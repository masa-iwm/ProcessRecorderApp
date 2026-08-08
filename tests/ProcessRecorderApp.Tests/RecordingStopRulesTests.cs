using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>停止の結果を CLI がどう扱うかの規則。</b>
///
/// <para>
/// <b>この規則が L1 に居るのは、E2E では守れないから。</b>
/// <c>RecordingStopOutcome.NotFinalized</c>（排出が完了しなかった）を設定だけで
/// 決定論的に起こす方法が無い ── <b>較正を繰り返しても機械の速度に負けた</b>
/// （打ち切りを 1ms にしても 0 にしても、排出が間に合うかどうかはスケジューリング次第）。
/// <c>Stronger</c> に至っては
/// 「空かつ未確定」を同時に起こせないので<b>原理的に E2E で踏めない</b>
/// （押し込みが 0 なら排出するものが無く、排出は速く終わる）。
/// </para>
/// <para>
/// <c>RecordingCommandState</c> を同じ理由で切り出したのと同型
/// ── 規則を WinUI アプリのプロジェクトに書くと、L1 がそこを参照していないので
/// <b>誰も守れない</b>。
/// </para>
/// </summary>
public class RecordingStopRulesTests
{
    [Fact]
    public void OnlyOk_IsAUsableArtifact()
    {
        Assert.True(RecordingStopRules.IsUsableArtifact(RecordingStopOutcome.Ok));

        // **どちらも終了コード 0 で返してはいけない。** 呼び出し側のバッチは
        // %ERRORLEVEL% で成否を判定し、stop-recording の直後に copy する前提である。
        Assert.False(RecordingStopRules.IsUsableArtifact(RecordingStopOutcome.Empty),
            "1フレームも入っていない録画を「成功」で返すと、空のファイルが成果物として運ばれる。");
        Assert.False(RecordingStopRules.IsUsableArtifact(RecordingStopOutcome.NotFinalized),
            "確定していないファイルを「成功」で返すと、moov の無い MP4 が成果物として運ばれる。");
    }

    /// <summary>
    /// <b><c>-all</c> で結果が混ざったとき、弱い方へ倒さないこと。</b>
    ///
    /// <para>
    /// 「捨ててよい」（<c>Empty</c>）と「未確定なので触るな」（<c>NotFinalized</c>）では、
    /// 前者を返すと<b>救済できたはずのデータを捨てさせる</b>ことになり取り返しがつかない。
    /// 順序に依存しないことまで見る（<c>Aggregate</c> で畳むので、
    /// 並び順で答えが変わると <c>-all</c> の結果がレコーダーの登録順で変わってしまう）。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(RecordingStopOutcome.Ok, RecordingStopOutcome.Ok, RecordingStopOutcome.Ok)]
    [InlineData(RecordingStopOutcome.Ok, RecordingStopOutcome.Empty, RecordingStopOutcome.Empty)]
    [InlineData(RecordingStopOutcome.Empty, RecordingStopOutcome.Ok, RecordingStopOutcome.Empty)]
    [InlineData(RecordingStopOutcome.Ok, RecordingStopOutcome.NotFinalized, RecordingStopOutcome.NotFinalized)]
    [InlineData(RecordingStopOutcome.NotFinalized, RecordingStopOutcome.Ok, RecordingStopOutcome.NotFinalized)]
    [InlineData(RecordingStopOutcome.Empty, RecordingStopOutcome.NotFinalized, RecordingStopOutcome.NotFinalized)]
    [InlineData(RecordingStopOutcome.NotFinalized, RecordingStopOutcome.Empty, RecordingStopOutcome.NotFinalized)]
    public void Stronger_PrefersTheOutcomeThatConstrainsTheCallerMore(
        RecordingStopOutcome a, RecordingStopOutcome b, RecordingStopOutcome expected)
    {
        Assert.Equal(expected, RecordingStopRules.Stronger(a, b));
    }

    /// <summary>
    /// <c>Ok</c> を初期値にして畳んでも壊れないこと
    /// （<c>ActivationCommands</c> は <c>Aggregate(Ok, Stronger)</c> で畳む）。
    /// </summary>
    [Fact]
    public void Stronger_IsSafeAsAnAggregateSeed()
    {
        var all = new[] { RecordingStopOutcome.Ok, RecordingStopOutcome.Empty, RecordingStopOutcome.Ok };
        Assert.Equal(RecordingStopOutcome.Empty, all.Aggregate(RecordingStopOutcome.Ok, RecordingStopRules.Stronger));

        Assert.Equal(RecordingStopOutcome.Ok,
            Array.Empty<RecordingStopOutcome>().Aggregate(RecordingStopOutcome.Ok, RecordingStopRules.Stronger));
    }

    // ---- 排出結果の分類（StopDrainRules）----

    /// <summary>
    /// 排出待ちの結末と押し込み数から <c>result=</c> と結果を決める規則。
    /// この分類が終了コード 16/17 の分岐の中核で、L1 に居る理由は上と同じ
    /// （タイムアウト・エラー・「空かつ未確定」を E2E で決定論的に踏めない）。
    /// </summary>
    [Theory]
    [InlineData(StopDrainSignal.Eos, 10, "ok", RecordingStopOutcome.Ok)]
    [InlineData(StopDrainSignal.Eos, 0, "empty", RecordingStopOutcome.Empty)]
    [InlineData(StopDrainSignal.Timeout, 10, "timeout", RecordingStopOutcome.NotFinalized)]
    [InlineData(StopDrainSignal.Error, 10, "error", RecordingStopOutcome.NotFinalized)]
    public void Classify_MapsTheDrainSignalToResultAndOutcome(
        StopDrainSignal signal, int pushed, string expectedResult, RecordingStopOutcome expectedOutcome)
    {
        Assert.Equal((expectedResult, expectedOutcome), StopDrainRules.Classify(signal, pushed));
    }

    /// <summary>
    /// <b>「空」で「未確定」を潰さない。</b> 排出が完了していないなら、押し込みが 0 でも
    /// 「未確定なので触るな」が正しい答え ── 中身が無いと断定できるのは排出が
    /// 綺麗に終わった場合だけである（終了コード表の「両方に該当する場合は 17」）。
    /// </summary>
    [Theory]
    [InlineData(StopDrainSignal.Timeout)]
    [InlineData(StopDrainSignal.Error)]
    public void Classify_NeverDowngradesAnUnfinalizedDrainToEmpty(StopDrainSignal signal)
    {
        var (result, outcome) = StopDrainRules.Classify(signal, samplesPushed: 0);

        Assert.NotEqual("empty", result);
        Assert.Equal(RecordingStopOutcome.NotFinalized, outcome);
    }
}
