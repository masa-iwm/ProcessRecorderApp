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
    /// パイプラインを組めていない連鎖（<c>rebuildOnly</c>）は、到着があるまでこちらを
    /// 間隔に使い、到着の後は同じ表の梯子へ移る ── 表だけを伸ばすと、
    /// 梯子の最後の段と到着前の間隔が黙って食い違う。
    /// </summary>
    [Fact]
    public void MaxDelay_MatchesTheCapOfTheBackoffTable()
        => Assert.Equal(RestartPolicy.MaxDelayMs, RestartPolicy.DelayForAttempt(int.MaxValue));

    // ---- 作り直しだけの連鎖の間隔 ----

    /// <summary>
    /// <b>到着がまだ無いあいだは頭打ちのまま。</b> 何も変わっていない機械を短い間隔で
    /// 叩いても得るものが無い（作り直しの連鎖は要素単位の再開を試さない）。
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void TheRebuildInterval_StaysCappedUntilSomethingArrives(int failuresSinceArrival)
        => Assert.Equal(RestartPolicy.MaxDelayMs, RestartPolicy.RebuildDelayMs(failuresSinceArrival));

    /// <summary>
    /// <b>到着の後に失敗したら、短い梯子をやり直す。</b>
    ///
    /// <para>
    /// バックオフは「居ないデバイスを叩き続けない」ためにあるので、到着した時点で
    /// その理由は消えている。ここが頭打ちのままだと、到着で起きた試行が失敗した瞬間に
    /// 次の機会が丸 60 秒先になる ── RDP のセッション復帰のように<b>到着の直後は
    /// まだ撮れない</b>場合に、復帰がまるまる 1 分遅れる。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1, 5_000)]
    [InlineData(2, 10_000)]
    [InlineData(3, 30_000)]
    [InlineData(4, 60_000)]
    public void TheRebuildInterval_RestartsTheLadderAfterAnArrival(int failuresSinceArrival, int expected)
        => Assert.Equal(expected, RestartPolicy.RebuildDelayMs(failuresSinceArrival));

    /// <summary>
    /// 梯子は頭打ちで止まる ── 追いかけ続けても間隔が伸び続けないこと。
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void TheRebuildInterval_NeverGrowsPastTheCap(int failuresSinceArrival)
        => Assert.Equal(RestartPolicy.MaxDelayMs, RestartPolicy.RebuildDelayMs(failuresSinceArrival));

    /// <summary>
    /// <b>到着の直後は、到着前より必ず短いか同じ。</b> 到着が間隔を伸ばす側に回ったら
    /// 仕切り直しの意味が消える。
    /// </summary>
    [Fact]
    public void TheRebuildInterval_IsNeverSlowedDownByAnArrival()
    {
        for (int i = 1; i < 20; i++)
            Assert.True(RestartPolicy.RebuildDelayMs(i) <= RestartPolicy.RebuildDelayMs(-1),
                $"到着後 {i} 回目の間隔が、到着がまだ無いときより長い");
    }

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

    /// <summary>
    /// <b>到着で打ち切れる回数は、エスカレーションの予算より必ず少ないこと。</b>
    ///
    /// <para>
    /// 同じにすると、モニターの再構成のような到着の連打だけで予算を使い切れる ──
    /// 本来 5s + 10s + 30s の 45 秒に散っていた 3 回が数秒で尽き、
    /// <b>まだ落ち着いていない機械へパイプライン全再生成を掛ける</b>ことになる。
    /// 少なくとも 1 回はバックオフを待ち切る、というのがこの不等式の意味である。
    /// </para>
    /// </summary>
    [Fact]
    public void EarlyWakes_CannotConsumeTheWholeEscalationBudget()
        => Assert.True(RestartPolicy.MaxEarlyWakesPerChain < RestartPolicy.EscalateAfterAttempts,
            "早期ウェイクの上限がエスカレーションの予算以上になっている。"
            + "連打だけで全再生成へ跳べるようになる。");

    /// <summary>
    /// <b>実を待つ猶予は、次の試行までの間隔より必ず短いこと。</b>
    ///
    /// <para>
    /// 猶予（<see cref="RestartPolicy.SinkSampleGraceMs"/>）は待ちの<b>外側</b>に
    /// 足される ── 試行の間隔が 5s なら、実が来なかった回は 5s + 3s で次へ進む。
    /// ここが間隔以上になると、猶予だけで梯子が一段ぶん潰れ、エスカレーションまでの
    /// 時間が仕様（5s + 10s + 30s）から読めなくなる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheSinkSampleGrace_IsShorterThanTheFirstRetryDelay()
        => Assert.True(RestartPolicy.SinkSampleGraceMs < RestartPolicy.DelayForAttempt(1),
            "実を待つ猶予が最初の試行間隔以上になっている。"
            + "猶予は待ちの外側に足されるので、梯子の 1 段目が猶予に飲まれる。");

    [Fact]
    public void EarlyWakes_AreAllowedUntilTheCap()
    {
        Assert.True(RestartPolicy.MayWakeEarly(0));
        Assert.True(RestartPolicy.MayWakeEarly(RestartPolicy.MaxEarlyWakesPerChain - 1));
        Assert.False(RestartPolicy.MayWakeEarly(RestartPolicy.MaxEarlyWakesPerChain));
        Assert.False(RestartPolicy.MayWakeEarly(RestartPolicy.MaxEarlyWakesPerChain + 10));
    }
}
