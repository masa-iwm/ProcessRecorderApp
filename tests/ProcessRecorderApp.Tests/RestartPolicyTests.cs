using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 自動復帰の間隔とエスカレーション基準（<see cref="RestartPolicy"/>）。
///
/// 「エラー1件ごとに 30 秒待って再試行するタスクを無条件に積む」形にすると、
/// 監視対象のモニタを抜いたとき数十件のエラーに対して数十本の復帰試行が並走する
/// （実際にあった欠陥）。ここで守るのは間隔の形と「諦めないこと」。
/// 多重化しないこと自体は <c>EventRecorder.ScheduleRestart</c> 側の責務で、
/// activity.log の試行時刻の間隔として L2 で確認する。
/// </summary>
public class RestartPolicyTests
{
    [Fact]
    public void Backoff_StartsShortSoBriefGlitchesRecoverQuickly()
    {
        // 一瞬の切断（ケーブルの接触・モード切替）で 30 秒待たされないこと。
        Assert.Equal(5_000, RestartPolicy.DelayForAttempt(1));
    }

    [Fact]
    public void Backoff_GrowsThenCaps()
    {
        Assert.Equal(5_000, RestartPolicy.DelayForAttempt(1));
        Assert.Equal(10_000, RestartPolicy.DelayForAttempt(2));
        Assert.Equal(30_000, RestartPolicy.DelayForAttempt(3));
        Assert.Equal(60_000, RestartPolicy.DelayForAttempt(4));
    }

    [Fact]
    public void Backoff_IsMonotonicallyNonDecreasing()
    {
        for (int i = 1; i < 50; i++)
            Assert.True(RestartPolicy.DelayForAttempt(i) <= RestartPolicy.DelayForAttempt(i + 1),
                $"attempt {i} -> {i + 1} で間隔が縮んでいる");
    }

    [Fact]
    public void Backoff_NeverGrowsWithoutBound()
    {
        // 試行回数は無制限なので、間隔が伸び続けると事実上の諦めになる。
        // 監視対象のモニタが1時間抜けていても、戻ってきたら1分以内に復帰すべき。
        Assert.Equal(60_000, RestartPolicy.DelayForAttempt(1000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Backoff_NonPositiveAttempt_IsTreatedAsTheFirst(int attempt)
    {
        // 0 や負値で 0ms が返ると、失敗し続けるソースに対してビジーループになる。
        Assert.Equal(5_000, RestartPolicy.DelayForAttempt(attempt));
    }

    [Fact]
    public void Escalation_HappensOnlyAfterSeveralFailures()
    {
        // 1回の失敗でパイプライン全体を作り直すのは高くつく（録画中なら中断する）。
        Assert.False(RestartPolicy.ShouldEscalate(1));
        Assert.False(RestartPolicy.ShouldEscalate(2));
        Assert.True(RestartPolicy.ShouldEscalate(RestartPolicy.EscalateAfterAttempts));
        Assert.True(RestartPolicy.ShouldEscalate(RestartPolicy.EscalateAfterAttempts + 10));
    }

    // ---- デバイス到着による早期復帰 ----

    /// <summary>
    /// <see cref="RestartPolicy.MaxDelayMs"/> は<b>バックオフ表の頭打ちと同じ値</b>であること。
    /// パイプラインを組めていない連鎖（<c>rebuildOnly</c>）はこちらを間隔に使うので、
    /// 表だけを伸ばすと 2 つの間隔が黙って食い違う。
    /// </summary>
    [Fact]
    public void MaxDelay_MatchesTheCapOfTheBackoffTable()
        => Assert.Equal(RestartPolicy.MaxDelayMs, RestartPolicy.DelayForAttempt(int.MaxValue));

    /// <summary>
    /// 落ち着き待ちは 0 であってはならない ── <b>列挙に出た＝開けるとは限らない</b>ので、
    /// 到着の瞬間に試すと確実に失敗する試行を 1 回消費する。
    /// </summary>
    [Fact]
    public void TheSettleAfterArrival_IsNotZero()
        => Assert.True(0 < RestartPolicy.EarlyWakeSettleMs);

    [Fact]
    public void AnEarlyArrival_GetsTheFullSettle()
        => Assert.Equal(
            RestartPolicy.EarlyWakeSettleMs,
            RestartPolicy.SettleAfterArrivalMs(fullDelayMs: 5_000, elapsedMs: 200));

    /// <summary>
    /// <b>元の待ちを超えない。</b> 到着が待ちの終わり際に来たときに落ち着き待ちを丸ごと足すと、
    /// 「早期復帰」が待ち切るより遅くなる ── 早めるための仕組みが遅らせる側に回る。
    /// </summary>
    [Theory]
    [InlineData(5_000, 4_500, 500)]
    [InlineData(5_000, 5_000, 0)]
    [InlineData(5_000, 6_000, 0)]       // 計測の揺れで超えることがある
    [InlineData(600, 0, 600)]           // 元の待ちが落ち着き待ちより短い
    public void TheSettle_NeverPushesPastTheOriginalDelay(int fullDelayMs, int elapsedMs, int expected)
        => Assert.Equal(expected, RestartPolicy.SettleAfterArrivalMs(fullDelayMs, elapsedMs));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void TheSettle_IsNeverNegative(int fullDelayMs, int elapsedMs)
        => Assert.True(0 <= RestartPolicy.SettleAfterArrivalMs(fullDelayMs, elapsedMs));
}
