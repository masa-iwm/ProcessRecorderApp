using ProcessRecorderApp.Components;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>録画トランスコードを提供できない実機での <c>TryOpen</c>。</b>
///
/// <para>
/// <b>この経路は L1 で押さえられる唯一の経路である。</b> 能力が true の側は
/// <c>ResolveEncoder</c> がネイティブのプローブへ降りるので、GStreamer の初期化なしには
/// 通せない ── ここで見るのは「断るときに枠を触らない」ことだけである。
/// </para>
/// <para>
/// <b>断り方を間違えると席が消える。</b> 提供できない実機で枠を取ってしまえば、
/// 同じ計数器を使うライブ画質の切り替えがその機械で永久に busy になる
/// （枠を返す経路は畳むときにしか無い）。
/// </para>
/// </summary>
public sealed class TranscodeStreamsTests
{
    /// <summary>ハードウェア H.264 デコーダーが無い実機の能力。</summary>
    private static TranscodeCapability Unavailable() => new(false, null);

    private static TranscodeOpen Open(string sessionId = "v1")
        => new(sessionId, @"C:\recordings\sample.mp4", 0.0, "360p", null);

    [Fact]
    public void OpeningWithoutTranscodeCapabilityIsRefusedWithoutTakingASlot()
    {
        var slots = new AuxiliaryEncoderSlots { Limit = 2 };
        using var streams = new TranscodeStreams(Unavailable, slots);

        Assert.False(streams.Capability.Transcode);
        Assert.False(streams.TryOpen(Open(), out var reader, out string? reason));

        Assert.Null(reader);
        Assert.Equal(TranscodeReasons.Unavailable, reason);
        Assert.Equal(0, slots.InUse);
        Assert.Equal(2, slots.Free);
    }

    /// <summary>
    /// 断りが繰り返されても計数器は動かない（<c>Free</c> が減っていくと、
    /// 提供できない実機ほど早くライブ画質が止まることになる）。
    /// </summary>
    [Fact]
    public void RepeatedRefusalsLeaveTheSlotsUntouched()
    {
        var slots = new AuxiliaryEncoderSlots { Limit = 1 };
        using var streams = new TranscodeStreams(Unavailable, slots);

        for (int i = 0; i < 5; i++)
            Assert.False(streams.TryOpen(Open("s" + i), out _, out _));

        Assert.Equal(0, slots.InUse);
        Assert.Equal(1, slots.Free);
    }

    /// <summary>
    /// <see cref="TranscodeStreams.Dispose"/> は冪等（<see cref="Controller"/> の破棄と
    /// 停止の経路が両方通っても落ちない）。
    /// </summary>
    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var slots = new AuxiliaryEncoderSlots { Limit = 2 };
        var streams = new TranscodeStreams(Unavailable, slots);

        streams.Dispose();
        streams.Dispose();

        Assert.Equal(0, slots.InUse);
        Assert.Equal(2, slots.Free);
    }

    /// <summary>
    /// 畳んだ後の要求も断られる。<b>この能力では答えているのは能力の関門である</b>
    /// （<c>_shutdown</c> の関門はその先で、能力 true でなければ通らない）
    /// ── それでも「畳んだ後に開けない」ことは呼び出し側への約束なので固定する。
    /// </summary>
    [Fact]
    public void OpeningAfterCloseAllIsRefused()
    {
        var slots = new AuxiliaryEncoderSlots { Limit = 2 };
        using var streams = new TranscodeStreams(Unavailable, slots);

        streams.CloseAll();

        Assert.False(streams.TryOpen(Open(), out var reader, out string? reason));
        Assert.Null(reader);
        Assert.Equal(TranscodeReasons.Unavailable, reason);
        Assert.Equal(0, slots.InUse);
    }
}
