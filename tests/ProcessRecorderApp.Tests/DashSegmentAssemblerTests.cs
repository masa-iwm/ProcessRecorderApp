using System.Collections.Generic;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// fragment を DASH のセグメントへ集約する状態機械。
///
/// <para>
/// <b>ここが縛るのは 3 つ。</b> (1) セグメントの切れ目は必ず同期サンプルであること、
/// (2) 長さは<b>次</b>のセグメントの時刻との差であること、
/// (3) 壊れた入力（時刻の巻き戻し・伸び続ける GOP）を握り潰さないこと。
/// どれも崩れてもバイト列は出続けるので、<b>再生してみるまで気付けない</b>。
/// </para>
/// </summary>
public sealed class DashSegmentAssemblerTests
{
    private static Fmp4Segment Fragment(ulong decodeTime, bool sync, int length = 16)
        => new(PreviewSegmentKind.Media, new byte[length], sync, decodeTime);

    private static List<DashMediaSegment> Drain(DashSegmentAssembler assembler)
    {
        var segments = new List<DashMediaSegment>();
        while (assembler.TryDequeue(out var segment))
            segments.Add(segment);
        return segments;
    }

    /// <summary>
    /// GOP 1 秒・fragment 1 秒なら 1 fragment ＝ 1 セグメント。
    /// <b>長さは次の <c>t</c> との差</b>で、最後の 1 本はまだ確定しない。
    /// </summary>
    [Fact]
    public void OneSyncFragmentPerSegmentGivesOneSegmentEach()
    {
        var assembler = new DashSegmentAssembler();
        assembler.Push(Fragment(0, sync: true));
        assembler.Push(Fragment(1000, sync: true));
        assembler.Push(Fragment(2000, sync: true));

        var segments = Drain(assembler);

        Assert.Equal(2, segments.Count);
        Assert.Equal((0UL, 1000UL), (segments[0].Time, segments[0].Duration));
        Assert.Equal((1000UL, 1000UL), (segments[1].Time, segments[1].Duration));
        Assert.False(assembler.IsFaulted);
        Assert.Equal(0, assembler.DroppedLeading);
    }

    /// <summary>
    /// 非同期始まりの fragment は<b>保留へ連結する</b>（単独のセグメントにしない）。
    /// バイト列は連結され、<c>t</c> は先頭の fragment のものになる。
    /// </summary>
    [Fact]
    public void NonSyncFragmentsAreAppendedToThePendingSegment()
    {
        var assembler = new DashSegmentAssembler();
        assembler.Push(Fragment(0, sync: true, length: 10));
        assembler.Push(Fragment(500, sync: false, length: 20));
        assembler.Push(Fragment(1000, sync: false, length: 30));
        assembler.Push(Fragment(1500, sync: true, length: 40));

        var segment = Assert.Single(Drain(assembler));

        Assert.Equal(0UL, segment.Time);
        Assert.Equal(1500UL, segment.Duration);
        Assert.Equal(60, segment.Bytes.Length);
    }

    /// <summary>
    /// <b>最初の IDR より前は捨てる。</b> 非同期始まりのセグメントを出すと、
    /// そこから参加したクライアントは永久に絵が出ない。
    /// </summary>
    [Fact]
    public void FragmentsBeforeTheFirstSyncAreDropped()
    {
        var assembler = new DashSegmentAssembler();
        assembler.Push(Fragment(0, sync: false));
        assembler.Push(Fragment(500, sync: false));
        assembler.Push(Fragment(1000, sync: true));
        assembler.Push(Fragment(2000, sync: true));

        var segment = Assert.Single(Drain(assembler));

        Assert.Equal(1000UL, segment.Time);
        Assert.Equal(2, assembler.DroppedLeading);
        Assert.False(assembler.IsFaulted);
    }

    /// <summary>
    /// 時刻が進まない入力は fault。<b>握り潰して流すと <c>SegmentTimeline</c> が
    /// 単調でなくなり、クライアントは復帰できない。</b>
    /// </summary>
    [Theory]
    [InlineData(1000UL)]   // 同じ
    [InlineData(500UL)]    // 巻き戻し
    public void TimeThatDoesNotAdvanceFaults(ulong decodeTime)
    {
        var assembler = new DashSegmentAssembler();
        assembler.Push(Fragment(1000, sync: true));
        assembler.Push(Fragment(decodeTime, sync: true));

        Assert.True(assembler.IsFaulted);
        Assert.Equal("pts rewind", assembler.Fault);
        Assert.Empty(Drain(assembler));
    }

    /// <summary>
    /// 保留が上限に達したら fault。<b>GOP 1 秒・fragment 1 秒なら 1:1 なので
    /// これは安全網</b>で、踏んだということは IDR が来ていない（＝セグメントが
    /// 無限に伸びる）ということである。
    /// </summary>
    [Fact]
    public void APendingSegmentThatNeverEndsFaults()
    {
        var assembler = new DashSegmentAssembler();
        assembler.Push(Fragment(0, sync: true));

        for (ulong t = 1; !assembler.IsFaulted && t <= DashPreviewLimits.MaxPendingFragments + 4; t++)
            assembler.Push(Fragment(t * 1000, sync: false));

        Assert.True(assembler.IsFaulted);
        Assert.Equal("gop too long", assembler.Fault);
    }

    /// <summary>fault の後は何を押しても無視する（供給側は畳んで組み直す）。</summary>
    [Fact]
    public void PushIsIgnoredAfterAFault()
    {
        var assembler = new DashSegmentAssembler();
        assembler.Push(Fragment(1000, sync: true));
        assembler.Push(Fragment(500, sync: true));

        assembler.Push(Fragment(9000, sync: true));
        assembler.Push(Fragment(10000, sync: true));

        Assert.Empty(Drain(assembler));
    }
}
