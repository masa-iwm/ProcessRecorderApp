using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 到着の束ね（<see cref="DeviceArrivalWatcher.CoalesceWaitMs"/>）。
///
/// <para>
/// <b>連打は例外ではなく通常の形である。</b> モニターの抜き差し・解像度変更・
/// RDP の再接続はいずれも <c>WM_DISPLAYCHANGE</c> を数回続けて起こし、プロバイダは
/// そのたびに再 probe して差分を post する。1 件ごとに復帰の待ちを打ち切ると、
/// <b>まだ再構成の途中の機械へ試行を掛け</b>、失敗だけが積み上がる。
/// </para>
/// <para>
/// 束ねの規則は 2 つの締め切りの早い方 ── 「静穏が続いたら起こす」と
/// 「最初の 1 件からこれ以上は待たない」。<b>後者が無いと飢える</b>：
/// 静穏時間より短い間隔で到着が続く限り、永久に起こさないことになる。
/// </para>
/// </summary>
public class DeviceArrivalCoalescingTests
{
    private const long Start = 1_000_000;   // TickCount64 の代わり（相対値しか見ない）

    [Fact]
    public void RightAfterAnArrival_ItWaitsForTheQuietPeriod()
        => Assert.Equal(
            DeviceArrivalWatcher.ArrivalQuietMs,
            DeviceArrivalWatcher.CoalesceWaitMs(Start, lastArrivalTicks: Start, burstStartedTicks: Start));

    [Fact]
    public void OnceTheQuietPeriodHasPassed_ItSignals()
        => Assert.Equal(0, DeviceArrivalWatcher.CoalesceWaitMs(
            Start + DeviceArrivalWatcher.ArrivalQuietMs,
            lastArrivalTicks: Start,
            burstStartedTicks: Start));

    /// <summary>
    /// <b>連打の途中でも上限で打ち切る。</b> これが無いと、静穏時間より短い間隔で
    /// 到着が続く機械では一度も起こされず、早期復帰が丸ごと効かなくなる
    /// （タイマーだけの復帰へ静かに戻る）。
    /// </summary>
    [Fact]
    public void AContinuousStorm_IsCutOffByTheUpperBound()
    {
        long now = Start + DeviceArrivalWatcher.ArrivalMaxDeferMs;

        // 直前にも到着している＝静穏はまったく続いていない。
        Assert.Equal(0, DeviceArrivalWatcher.CoalesceWaitMs(
            now, lastArrivalTicks: now, burstStartedTicks: Start));
    }

    [Fact]
    public void BeforeTheUpperBound_ItStillWaitsButNeverLongerThanTheBound()
    {
        long now = Start + 100;
        int wait = DeviceArrivalWatcher.CoalesceWaitMs(now, lastArrivalTicks: now, burstStartedTicks: Start);

        Assert.True(0 < wait);
        Assert.True(wait <= DeviceArrivalWatcher.ArrivalMaxDeferMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10_000)]
    public void ItNeverReturnsANegativeWait(long elapsed)
        => Assert.True(0 <= DeviceArrivalWatcher.CoalesceWaitMs(
            Start + elapsed, lastArrivalTicks: Start, burstStartedTicks: Start));

    /// <summary>
    /// 静穏は上限より短いこと。逆なら上限が常に先に来て、静穏の判定が死ぬ。
    /// </summary>
    [Fact]
    public void TheQuietPeriod_IsShorterThanTheUpperBound()
        => Assert.True(DeviceArrivalWatcher.ArrivalQuietMs < DeviceArrivalWatcher.ArrivalMaxDeferMs);

    /// <summary>
    /// 束ねの遅れと落ち着き待ちの合計が、最初のバックオフ（5 秒）より十分に短いこと。
    /// ここが逆転すると「早期復帰」がタイマーより遅くなり、機能の意味が消える。
    /// </summary>
    [Fact]
    public void TheWorstCaseWakeLatency_StaysWellUnderTheFirstBackoff()
        => Assert.True(
            DeviceArrivalWatcher.ArrivalMaxDeferMs + RestartPolicy.EarlyWakeSettleMs
                < RestartPolicy.DelayForAttempt(1),
            "束ねの上限＋落ち着き待ちが最初のバックオフ以上になっている。"
            + "そうなると到着で起こす意味が無くなる（待ち切った方が早い）。");
}
