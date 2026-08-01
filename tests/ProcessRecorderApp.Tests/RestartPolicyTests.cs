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
}
