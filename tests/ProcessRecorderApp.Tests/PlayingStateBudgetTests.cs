using System.Text.RegularExpressions;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b><c>PLAYING</c> 到達待ちと、起動直後のコマンドが待てる時間の関係を固定する。</b>
///
/// <para>
/// <c>EventRecorder.PlayingStateTimeoutMs</c> は、パイプラインが実際に <c>PLAYING</c> へ
/// 到達するのを待つ上限（<c>SetState</c> の <c>ASYNC</c> を成功扱いしないための検出器）。
/// この待ちは<b>起動時のレコーダー初期化＝UI スレッド</b>で消費され、
/// <c>GstControllerViewModel.IsReady</c> が立つ<b>前</b>に起きる。
/// 一方その間に届いた CLI コマンドは <c>ActivationCommands.ReadyWaitTimeout</c> しか待たない
/// ── ここが逆転すると、<b>1本のレコーダーが引っ掛かっただけで、
/// 起動直後のコマンドが「録画エンジンが利用できない」で失敗し始める。</b>
/// </para>
/// <para>
/// <b>2つの定数は別アセンブリにある</b>（<c>GStreamer.GirCore</c> と
/// <c>ProcessRecorderApp</c>）。しかも L1 テストは WinUI 側のプロジェクトを参照していないので、
/// <c>StopFinalizeBudgetTests</c> のように型で突き合わせることができない
/// ── <c>AppSettingsReloadTests</c> と同じくソースをテキストとして読む。
/// </para>
/// <para>
/// <b>これは「片方だけ動かしても何も落ちない」を潰すための表明。</b>
/// 同じ理由の表明が <c>StopFinalizeBudgetTests</c> にもある
/// （60000 / 50000 / 5000 が3つの場所に散っている関係を固定する）。
/// </para>
/// </summary>
public class PlayingStateBudgetTests
{
    /// <summary>
    /// <c>ActivationCommands.ReadyWaitTimeout</c> の秒数（ソースから読む）。
    /// </summary>
    private static readonly Regex ReadyWaitRegex = new(
        @"ReadyWaitTimeout\s*=\s*TimeSpan\.FromSeconds\(\s*(\d+)\s*\)", RegexOptions.Compiled);

    [Fact]
    public void ThePlayingStateWait_FitsInsideTheCommandReadyWait()
    {
        string path = RepositoryFiles.At("src", "ProcessRecorderApp", "ActivationCommands.cs");
        string text = File.ReadAllText(path);

        // コメント中の言及（doc コメントがこの名前を何度も出す）を実装と数えない。
        // 素の IndexOf でコメント行を拾って「注入したのに落ちない」を起こした前例がある
        // （WorkerAcceptingEventOrderTests。docs/test-harness.md「テストの有効性検証の原則」）。
        var match = ReadyWaitRegex.Matches(text)
            .FirstOrDefault(m => !SourceReferences.IsCommentLine(text, m.Index));

        Assert.True(match is not null,
            "ActivationCommands.cs に ReadyWaitTimeout = TimeSpan.FromSeconds(...) が見つからない。"
            + Environment.NewLine
            + "書き方を変えたなら、この正規表現も一緒に直すこと"
            + "（見つからないまま緑にすると、検査そのものが消える）。");

        int readyWaitMs = int.Parse(match!.Groups[1].Value) * 1000;

        Assert.True(EventRecorder.PlayingStateTimeoutMs < readyWaitMs,
            $"PLAYING 到達待ちが {EventRecorder.PlayingStateTimeoutMs}ms あるのに、"
            + $"起動直後のコマンドが待てるのは {readyWaitMs}ms しかない。"
            + Environment.NewLine
            + "この状態だと、レコーダーが1本でも PLAYING に到達しないだけで、"
            + Environment.NewLine
            + "起動直後の CLI コマンドが「録画エンジンが利用できない」で失敗し始める。"
            + Environment.NewLine
            + "どちらかを動かすなら、もう一方（ActivationCommands.ReadyWaitTimeout /"
            + Environment.NewLine
            + "EventRecorder.PlayingStateTimeoutMs）も一緒に見直すこと。");
    }

    /// <summary>
    /// <b>健全なパイプラインの実測に対して十分な余裕があること。</b>
    /// 実測（GPU 無しの開発機・320x240〜3840x2160）は 0.39〜0.67 秒。
    /// ここを詰めすぎると<b>動いているレコーダーを壊す</b>方向の退行になるので、
    /// 下限を明示しておく。
    /// </summary>
    [Fact]
    public void ThePlayingStateWait_LeavesRoomOverTheMeasuredHealthyCase()
    {
        const int MeasuredWorstHealthyMs = 670;

        Assert.True(EventRecorder.PlayingStateTimeoutMs >= MeasuredWorstHealthyMs * 4,
            $"PLAYING 到達待ちが {EventRecorder.PlayingStateTimeoutMs}ms しかない。"
            + Environment.NewLine
            + $"実測の健全な最悪値は {MeasuredWorstHealthyMs}ms で、余裕が 4 倍を切っている。"
            + Environment.NewLine
            + "この待ちは超えたら候補失敗として扱われるので、短すぎると"
            + Environment.NewLine
            + "正常に動いていたレコーダーが初期化できなくなる。");
    }
}
