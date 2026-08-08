using ProcessRecorderApp.SingleInstance;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// ランチャーの時間予算の関係式。<b>これらは独立に決めてよい値ではない</b>
/// ── <c>StopFinalizeBudgetTests</c> が排出待ちとランチャーの結果待ちの関係を守っているのと
/// 同じ方式で、<c>launcherMutex</c> の順番待ち上限とその根拠（正当な保持時間の最悪値）の
/// 算術を機械的に守る。片方だけ動かすと、<b>正当に順番待ちしている CLI が
/// <c>ExitCode_LauncherBusy</c>（契約上「正常な待ち行列でここへ来ることは無い」）を踏む</b>。
/// </summary>
public class LauncherBudgetTests
{
    /// <summary>
    /// 正当な保持時間の最悪値: 受理待ちを使い切ってからコールドスタートへ倒れる経路
    /// （受理 60 秒 ＋ 起動完了待ち 120 秒 ＋ 転送 30 秒 ＋ 結果待ち 60 秒 ＝ 約 270 秒）。
    /// </summary>
    private static int WorstCaseHoldMs =>
        SingleInstanceManager.WorkerAcceptTimeoutMs
        + SingleInstanceManager.ColdStartRegistrationTimeoutMs
        + SingleInstanceManager.RedirectTimeoutMs
        + SingleInstanceManager.WorkerAcceptTimeoutMs;

    [Fact]
    public void TheMutexTimeout_AbsorbsTwoWorstCaseHolds()
    {
        // doc コメントの主張の下側（「2本積んでもまだ届かない」）。
        Assert.True(2 * WorstCaseHoldMs < SingleInstanceManager.LauncherMutexTimeoutMs,
            $"launcherMutex の順番待ち上限（{SingleInstanceManager.LauncherMutexTimeoutMs}ms）が、"
            + $"病的な保持 2 本分（{2 * WorstCaseHoldMs}ms）を吸収できない。"
            + "コールドスタートの 120 秒・結果待ちの 60 秒・転送の 30 秒のどれかを動かしたなら、"
            + "LauncherMutexTimeoutMs とその doc コメントも一緒に見直すこと。");
    }

    /// <summary>
    /// <b>上側も固定する。</b> 下側だけだと、上限を大きくする方向（例: 30 分）が緑のまま通り、
    /// doc コメントが根拠にしている「3 本目は届かない」が黙って偽になる
    /// ── 10 分という値を正当化しているのはこの上側の主張である。
    /// </summary>
    [Fact]
    public void TheMutexTimeout_DoesNotAbsorbThreeWorstCaseHolds()
    {
        Assert.True(3 * WorstCaseHoldMs > SingleInstanceManager.LauncherMutexTimeoutMs,
            $"launcherMutex の順番待ち上限（{SingleInstanceManager.LauncherMutexTimeoutMs}ms）が、"
            + $"病的な保持 3 本分（{3 * WorstCaseHoldMs}ms）まで吸収してしまう。"
            + "doc コメントの「3 本目は届かない」が偽になっているので、"
            + "値とコメントのどちらを直すか決めること。");
    }
}
