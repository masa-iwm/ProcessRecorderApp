using System.Diagnostics;
using Xunit;
using Channel = ProcessRecorderApp.SingleInstance.SingleInstanceManager.CommandResultChannel;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>結果通知の相関 ID（実際にあった取り違え欠陥の回帰テスト）。</b>
///
/// <para>
/// ランチャーと常駐ワーカーが結果を受け渡すチャネル（名前付き MMF ＋ 名前付きイベント）は
/// <c>keyPrefix</c> ごとに1組しか無い。相関 ID を刻まないと、<b>結果待ちを打ち切った
/// （終了コード 2）コマンドの結果が、後から張られた次のコマンドのチャネルへ届き、
/// その次のコマンドの答えとして読まれる</b> ── 終了コードも標準出力も丸ごと入れ替わる。
/// <b>「遅い」ではなく「間違った答えを返す」壊れ方</b>なので、打ち切り時間の調整では直らない。
/// </para>
/// <para>
/// <b>この退行は E2E では検出できない。</b> 再現には「ワーカーが結果を返すのを
/// ランチャーの上限（60 秒）より遅らせた状態で、次のコマンドを撃つ」ことが必要で、
/// 上限は製品の <c>const</c>（<c>WorkerAcceptTimeoutMs</c>）なのでテストから短くできない。
/// 一方このチャネルは名前付きカーネルオブジェクトだけで動くので、
/// <b>1プロセス内でランチャー役とワーカー役を演じれば決定的に再現できる</b>
/// ── だからここは L1 に置いてある。<b>E2E の緑はこの相関 ID の証拠にはならない。</b>
/// </para>
/// </summary>
public class CommandResultChannelTests
{
    /// <summary>
    /// 「来ないこと」を確かめる待ち。<b>短くてよい</b> ── 検出したい退行では
    /// 古い結果が<b>既に書かれている</b>ので、待ちの長さに関係なく即座に配られる。
    /// </summary>
    private const int StaleWaitMs = 750;

    /// <summary>
    /// テストごとに固有の <c>keyPrefix</c>。名前付きオブジェクトはセッション共有なので、
    /// 使い回すと並列実行や前回の残骸と干渉する。
    /// </summary>
    private static string NewKeyPrefix(string test) => $"ProcessRecorderApp.Test.{test}.{Guid.NewGuid():N}";

    [Fact]
    public void AResultStampedWithTheRequestId_IsDelivered()
    {
        string prefix = NewKeyPrefix(nameof(AResultStampedWithTheRequestId_IsDelivered));
        var requestId = Guid.NewGuid();

        using var channel = Channel.CreateForRequest(prefix, requestId);

        // ワーカー役: リダイレクトが届いた瞬間に相関 ID を受け取る。
        Assert.Equal(requestId, Channel.ClaimRequestId(prefix));

        Channel.Publish(prefix, requestId, exitCode: 7, "出力", "エラー");

        Assert.True(channel.TryWaitForResult(5_000, out var result),
            "自分の相関 ID を刻んだ結果が配られませんでした（正常系が壊れています）。");
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("出力", result.ConsoleOutput);
        Assert.Equal("エラー", result.ConsoleError);
    }

    /// <summary>
    /// <b>これが取り違え欠陥そのものの回帰テスト。</b>
    /// 打ち切って消えた 1 本目の結果が、2 本目の答えになってはいけない。
    /// </summary>
    [Fact]
    public void AStaleResultFromAnAbandonedCommand_IsNotDeliveredToTheNextCommand()
    {
        string prefix = NewKeyPrefix(nameof(AStaleResultFromAnAbandonedCommand_IsNotDeliveredToTheNextCommand));

        // 1本目のランチャー: ワーカーはリダイレクトを受け取ったが、
        // 結果が返る前にランチャーが上限まで待って諦め、プロセスごと消えた。
        var abandoned = Guid.NewGuid();
        var first = Channel.CreateForRequest(prefix, abandoned);
        Assert.Equal(abandoned, Channel.ClaimRequestId(prefix));
        first.Dispose();

        // 2本目のランチャー: 同じ名前のチャネルを張り直す。
        var mine = Guid.NewGuid();
        using var second = Channel.CreateForRequest(prefix, mine);

        // 1本目のコマンドが「今になって」完了した。
        Channel.Publish(prefix, abandoned, exitCode: 42, "1本目の出力", null);

        Assert.False(second.TryWaitForResult(StaleWaitMs, out var stale),
            "取り残された 1 本目のコマンドの結果が、2 本目のコマンドの結果として配られました"
            + $"（終了コード {stale.ExitCode} / 出力 '{stale.ConsoleOutput}'）。"
            + Environment.NewLine
            + "結果には相関 ID を刻み、ランチャー側は一致しない通知を捨てて待ち直すこと"
            + "（CommandResultChannel）。これは「遅い」ではなく**間違った答えを返す**退行で、"
            + Environment.NewLine
            + "利用者から見れば `stop-recording` が前の `start-recording` の終了コードを返す。");

        // **捨てすぎていないこと。** 自分の結果はこの後ちゃんと受け取れる。
        Channel.Publish(prefix, mine, exitCode: 0, "2本目の出力", null);

        Assert.True(second.TryWaitForResult(5_000, out var result),
            "古い結果を捨てた後、自分の結果を受け取れませんでした"
            + "（＝待ち直しをせず、1回目の合図で諦めている）。");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2本目の出力", result.ConsoleOutput);
    }

    /// <summary>
    /// 相関 ID は<b>1回しか claim できない</b>。
    ///
    /// <para>
    /// 消さずに残すと、<c>Activated</c> が想定外に余分に発火したときに
    /// <b>まだ生きているランチャーの ID を拾って偽の結果を刻める</b>
    /// ── 相関 ID を入れた意味が消える。1 つの ID につき発火は1回なので、
    /// 消しても正常系は何も失わない。
    /// </para>
    /// </summary>
    [Fact]
    public void TheRequestId_CanBeClaimedOnlyOnce()
    {
        string prefix = NewKeyPrefix(nameof(TheRequestId_CanBeClaimedOnlyOnce));
        var requestId = Guid.NewGuid();

        using var channel = Channel.CreateForRequest(prefix, requestId);

        Assert.Equal(requestId, Channel.ClaimRequestId(prefix));
        Assert.Equal(Guid.Empty, Channel.ClaimRequestId(prefix));

        // 無印（claim できなかった）結果は誰にも配られない。
        Channel.Publish(prefix, Guid.Empty, exitCode: 42, "偽の出力", null);

        Assert.False(channel.TryWaitForResult(StaleWaitMs, out var stale),
            $"相関 ID を持たない結果が配られました（終了コード {stale.ExitCode}）。");
    }

    /// <summary>
    /// 結果を待つランチャーが居ない状態（引数無しの起動など）で、
    /// ワーカー側の2つの入口が<b>例外を投げないこと</b>
    /// ── ここで投げると常駐ワーカーが落ちる。
    /// </summary>
    [Fact]
    public void WhenNoLauncherIsWaiting_ClaimingYieldsNothingAndPublishingIsHarmless()
    {
        string prefix = NewKeyPrefix(nameof(WhenNoLauncherIsWaiting_ClaimingYieldsNothingAndPublishingIsHarmless));

        Assert.Equal(Guid.Empty, Channel.ClaimRequestId(prefix));
        Channel.Publish(prefix, Guid.NewGuid(), exitCode: 0, "出力", "エラー");
    }

    /// <summary>
    /// 結果が来なければ<b>予算内で</b>諦めること。
    /// <c>timeoutMs</c> は締め切りであって1回の待ちの長さではないので、
    /// 古い結果を捨てるたびに測り直してはいけない（<c>LauncherMutex</c> の占有が伸びる）。
    /// </summary>
    [Fact]
    public void WhenNoResultArrives_TheWaitEndsWithinTheBudget()
    {
        string prefix = NewKeyPrefix(nameof(WhenNoResultArrives_TheWaitEndsWithinTheBudget));

        using var channel = Channel.CreateForRequest(prefix, Guid.NewGuid());

        var elapsed = Stopwatch.StartNew();
        Assert.False(channel.TryWaitForResult(400, out _));
        Assert.InRange(elapsed.ElapsedMilliseconds, 300, 5_000);
    }

    /// <summary>
    /// 容量を超える出力は切り詰める（<b>stdout を優先</b>）。例外にはしない
    /// ── 結果通知が例外で消えると、ランチャーは 60 秒待って終了コード 2 になる。
    /// </summary>
    [Fact]
    public void OutputLargerThanTheChannel_IsTruncatedWithStdoutFirst()
    {
        string prefix = NewKeyPrefix(nameof(OutputLargerThanTheChannel_IsTruncatedWithStdoutFirst));
        var requestId = Guid.NewGuid();

        using var channel = Channel.CreateForRequest(prefix, requestId);

        // ASCII なので「1文字 = 1バイト」（切り詰めが文字境界を割る話をここでは扱わない）。
        Channel.Publish(
            prefix, requestId, exitCode: 0,
            new string('o', Channel.PayloadCapacity + 1_000),
            new string('e', 1_000));

        Assert.True(channel.TryWaitForResult(5_000, out var result));
        Assert.Equal(Channel.PayloadCapacity, result.ConsoleOutput.Length);
        Assert.Equal(string.Empty, result.ConsoleError);
    }
}
