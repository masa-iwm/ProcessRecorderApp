using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="BusMessageThrottle"/> の契約。
///
/// 守っているのは「洪水でログが潰れないこと」と「潰さないために間引いた結果、
/// 障害そのものが見えなくならないこと」の両立。
/// GPU 実機の <c>nvh264enc</c> では <c>h264parse</c> が捨てた NAL 1個ごとに
/// Warning を出しており、素通しにすると 1MB のローテーションを数秒で食い潰して
/// **原因を突き止めるためのログが原因自身に流し去られる**。
/// </summary>
public class BusMessageThrottleTests
{
    [Fact]
    public void FirstMessage_IsAlwaysEmitted()
    {
        var throttle = new BusMessageThrottle();

        var (emit, repeated) = throttle.Observe("h264parse broken nal");

        Assert.True(emit);
        Assert.Equal(0, repeated);
    }

    [Fact]
    public void IdenticalMessagesInARow_AreSuppressed()
    {
        var throttle = new BusMessageThrottle();
        throttle.Observe("h264parse broken nal");

        for (int i = 0; i < 50; i++)
            Assert.False(throttle.Observe("h264parse broken nal").Emit);

        Assert.Equal(50, throttle.SuppressedCount);
    }

    [Fact]
    public void DifferentMessage_IsEmittedAndReportsTheSuppressedCount()
    {
        var throttle = new BusMessageThrottle();
        throttle.Observe("h264parse broken nal");
        for (int i = 0; i < 7; i++)
            throttle.Observe("h264parse broken nal");

        var (emit, repeated) = throttle.Observe("filesink disk full");

        Assert.True(emit);
        // 抑制していた7件を取りこぼさず、次の行に添えて報告する
        Assert.Equal(7, repeated);
    }

    [Fact]
    public void LongRunOfIdenticalMessages_BreaksSilenceAtTheCap()
    {
        // 延々と沈黙し続けると「まだ続いているのか、止まったのか」が分からなくなる。
        var throttle = new BusMessageThrottle();
        int emitted = 0;

        for (int i = 0; i < BusMessageThrottle.MaxSuppressedInARow * 3; i++)
            if (throttle.Observe("same").Emit)
                emitted++;

        // 素通しなら 600 行。抑制しっぱなしなら 1 行。どちらでもないことを確かめる。
        Assert.InRange(emitted, 2, 5);
    }

    [Fact]
    public void AlternatingMessages_AreNotSuppressed()
    {
        // 「直前と同じ」でしか畳まない。交互に出る2種類の障害は両方見えるべき。
        var throttle = new BusMessageThrottle();
        int emitted = 0;

        for (int i = 0; i < 10; i++)
        {
            if (throttle.Observe("A").Emit) emitted++;
            if (throttle.Observe("B").Emit) emitted++;
        }

        Assert.Equal(20, emitted);
    }

    [Fact]
    public void Flush_ReturnsThePendingCountAndResets()
    {
        var throttle = new BusMessageThrottle();
        throttle.Observe("x");
        throttle.Observe("x");
        throttle.Observe("x");

        Assert.Equal(2, throttle.Flush());
        Assert.Equal(0, throttle.SuppressedCount);

        // Flush 後は「直前と同じ」の記憶も消える（次の同一メッセージは出力される）
        Assert.True(throttle.Observe("x").Emit);
    }

    [Fact]
    public void Flush_WithNothingPending_ReturnsZero()
    {
        var throttle = new BusMessageThrottle();
        Assert.Equal(0, throttle.Flush());

        throttle.Observe("x");
        Assert.Equal(0, throttle.Flush());
    }
}
