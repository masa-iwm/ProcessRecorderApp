using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// ライブプレビューの配信契約（<see cref="PreviewSubscription"/>）。
///
/// <para>
/// <b>ここで固定しているのは「壊れ方」である。</b> MSE は init セグメントより前に
/// media を受け取ると復帰できず、init を途中で差し替えることもできない。
/// だから順序違反は例外、init の欠落・2 本目の init・遅すぎる購読者は
/// <b>すべて channel の完了</b>（＝クライアントの再接続）へ倒す、という 1 種類の
/// 壊れ方に集約してある。個別に握り潰すと、画面が黙って止まる形になる。
/// </para>
/// </summary>
public sealed class PreviewStreamContractTests
{
    private static PreviewSegment Segment(PreviewSegmentKind kind, long sequence)
        => new(kind, new byte[] { (byte)sequence }, sequence, StartsWithSync: false);

    private static PreviewSubscription NewSubscription(Action<PreviewSubscription>? onDispose = null)
        => new("recorder-0", onDispose);

    /// <summary>
    /// 残りを読み切ったうえで完了を確かめる。
    /// <c>ChannelReader.Completion</c> は<b>待ち行列が空になってから</b>完了するので、
    /// 読み切らずに見ると「完了していない」と誤判定する。
    /// </summary>
    private static void AssertCompleted(PreviewSubscription subscription)
    {
        while (subscription.Segments.TryRead(out _))
        {
        }

        Assert.True(subscription.Segments.Completion.Wait(TimeSpan.FromSeconds(5)),
            "channel が完了していない。契約では、この状態の購読者は再接続しなければならない。");
    }

    [Fact]
    public void TheFirstPublishedSegmentMustBeAnInit()
    {
        using var subscription = NewSubscription();

        Assert.Throws<ArgumentException>(() => subscription.Publish(Segment(PreviewSegmentKind.Media, 1)));
        Assert.False(subscription.HasReceivedInit);
    }

    [Fact]
    public void ASecondInitIsRefusedAndCompletesTheChannel()
    {
        using var subscription = NewSubscription();

        Assert.True(subscription.Publish(Segment(PreviewSegmentKind.Init, 0)));
        Assert.True(subscription.HasReceivedInit);
        Assert.True(subscription.Publish(Segment(PreviewSegmentKind.Media, 1)));

        Assert.False(subscription.Publish(Segment(PreviewSegmentKind.Init, 2)));
        AssertCompleted(subscription);
    }

    [Fact]
    public void AFullQueueDropsTheOldestAndCountsIt()
    {
        using var subscription = NewSubscription();

        Assert.True(subscription.Publish(Segment(PreviewSegmentKind.Init, 0)));
        Assert.True(subscription.Segments.TryRead(out _));   // init を先に読み出しておく

        for (long i = 1; i <= PreviewStreamLimits.QueueCapacity; i++)
            Assert.True(subscription.Publish(Segment(PreviewSegmentKind.Media, i)));

        Assert.Equal(0, subscription.DroppedSegments);

        Assert.True(subscription.Publish(Segment(PreviewSegmentKind.Media, PreviewStreamLimits.QueueCapacity + 1)));
        Assert.Equal(1, subscription.DroppedSegments);

        var received = new List<long>();
        while (subscription.Segments.TryRead(out var segment))
            received.Add(segment.Sequence);

        // 落ちたのは最も古い 1 件（2 以降が残る）。
        Assert.Equal(PreviewStreamLimits.QueueCapacity, received.Count);
        Assert.Equal(2, received[0]);
        Assert.Equal(PreviewStreamLimits.QueueCapacity + 1, received[^1]);
    }

    [Fact]
    public void DroppingTheInitCompletesTheChannelImmediately()
    {
        using var subscription = NewSubscription();

        // 一度も読まずに QueueCapacity + 1 件流すと、押し出されるのは init。
        // init は再送できないので、その場で切る（脱落上限を待たない）。
        Assert.True(subscription.Publish(Segment(PreviewSegmentKind.Init, 0)));
        for (long i = 1; i < PreviewStreamLimits.QueueCapacity; i++)
            Assert.True(subscription.Publish(Segment(PreviewSegmentKind.Media, i)));

        Assert.False(subscription.Publish(Segment(PreviewSegmentKind.Media, PreviewStreamLimits.QueueCapacity)));

        Assert.Equal(1, subscription.DroppedSegments);
        AssertCompleted(subscription);
    }

    [Fact]
    public void ExceedingTheDropBudgetRefusesAndCompletes()
    {
        using var subscription = NewSubscription();

        // init を先に読み出すのが要点 ── 読まないままだと、脱落 1 件目で init が落ちて
        // DroppingTheInitCompletesTheChannelImmediately の経路に入り、
        // 脱落上限の検査そのものが実行されない。
        Assert.True(subscription.Publish(Segment(PreviewSegmentKind.Init, 0)));
        Assert.True(subscription.Segments.TryRead(out _));

        int lastAccepted = PreviewStreamLimits.QueueCapacity + PreviewStreamLimits.MaxDroppedSegments;
        for (long i = 1; i <= lastAccepted; i++)
            Assert.True(subscription.Publish(Segment(PreviewSegmentKind.Media, i)));

        Assert.Equal(PreviewStreamLimits.MaxDroppedSegments, subscription.DroppedSegments);

        Assert.False(subscription.Publish(Segment(PreviewSegmentKind.Media, lastAccepted + 1)));
        Assert.Equal(PreviewStreamLimits.MaxDroppedSegments + 1, subscription.DroppedSegments);
        AssertCompleted(subscription);
    }

    [Fact]
    public void PublishAfterCompleteIsRefused()
    {
        using var subscription = NewSubscription();

        Assert.True(subscription.Publish(Segment(PreviewSegmentKind.Init, 0)));
        subscription.Complete();
        subscription.Complete();   // 複数回呼んでも安全

        Assert.False(subscription.Publish(Segment(PreviewSegmentKind.Media, 1)));
    }

    [Fact]
    public void DisposeReleasesTheSeatExactlyOnce()
    {
        int released = 0;
        var subscription = NewSubscription(_ => Interlocked.Increment(ref released));

        subscription.Dispose();
        subscription.Dispose();

        Assert.Equal(1, released);
        Assert.False(subscription.Publish(Segment(PreviewSegmentKind.Init, 0)));
    }

    [Fact]
    public void ConcurrentDisposeReleasesTheSeatExactlyOnce()
    {
        // 席を二重に返すと上限（MaxSubscribersPerRecorder / MaxTotalSubscribers）が
        // 事実上無くなる。HTTP の切断とレコーダーの停止は別スレッドから同時に来る。
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int released = 0;
            var subscription = NewSubscription(_ => Interlocked.Increment(ref released));

            Parallel.For(0, 8, _ => subscription.Dispose());

            Assert.Equal(1, released);
        }
    }
}
