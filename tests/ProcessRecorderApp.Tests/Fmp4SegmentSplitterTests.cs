using ProcessRecorderApp.Components;
using ProcessRecorderApp.GStreamer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <c>mp4mux fragment-mode=dash-or-mss</c> の出力を Init / Media へ切る状態機械。
///
/// <para>
/// <b>箱はここで合成する。</b> 実 GStreamer を回すと GPU・同梱ランタイム・実時間に縛られ、
/// <c>largesize</c> や壊れた <c>size</c> のような「実装が正しく落ちること」を確かめる入力は
/// そもそも作れない。合成なら 1 バイトずつ流し込む等価性まで固定できる。
/// </para>
/// <para>
/// 同期判定の優先順（<c>trun.first_sample_flags</c> → <c>trun</c> の <c>sample_flags[0]</c> →
/// <c>tfhd</c> 既定 → <c>trex</c> 既定）は 4 段とも別々に検査する
/// ── 実測では同梱 mp4mux が <c>trun</c> の per-sample flags しか書かないので、
/// 残り 3 段は実ファイルでは踏まれず、間違っていても気付けない。
/// </para>
/// </summary>
public sealed class Fmp4SegmentSplitterTests
{
    private const uint NonSync = 0x00010000;

    // ---- 箱のビルダー -------------------------------------------------------

    private static byte[] Box(string type, params byte[][] payload)
    {
        int length = 8 + payload.Sum(p => p.Length);
        var box = new byte[length];
        WriteU32(box, 0, (uint)length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);

        int offset = 8;
        foreach (byte[] part in payload)
        {
            part.CopyTo(box, offset);
            offset += part.Length;
        }
        return box;
    }

    /// <summary><c>size==1</c> ＋ 64bit largesize の形。</summary>
    private static byte[] LargeBox(string type, params byte[][] payload)
    {
        int length = 16 + payload.Sum(p => p.Length);
        var box = new byte[length];
        WriteU32(box, 0, 1);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        WriteU64(box, 8, (ulong)length);

        int offset = 16;
        foreach (byte[] part in payload)
        {
            part.CopyTo(box, offset);
            offset += part.Length;
        }
        return box;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        WriteU32(bytes, 0, value);
        return bytes;
    }

    private static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        WriteU64(bytes, 0, value);
        return bytes;
    }

    private static void WriteU32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WriteU64(byte[] target, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
            target[offset + i] = (byte)(value >> (56 - (8 * i)));
    }

    private static byte[] Ftyp() => Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isomiso2"));

    /// <summary><c>moov{mvex{trex}}</c>。<paramref name="trexDefaultSampleFlags"/> が最後の砦。</summary>
    private static byte[] Moov(uint trexDefaultSampleFlags = 0)
    {
        byte[] trex = Box("trex",
            U32(0),                          // version / flags
            U32(1),                          // track_ID
            U32(1),                          // default_sample_description_index
            U32(0),                          // default_sample_duration
            U32(0),                          // default_sample_size
            U32(trexDefaultSampleFlags));
        return Box("moov", Box("mvex", trex));
    }

    /// <summary>
    /// <c>tfhd</c>。<paramref name="defaultSampleFlags"/> を渡すと
    /// <c>default-sample-duration/size</c> も併せて立て、<b>飛ばす長さの計算</b>を踏ませる。
    /// </summary>
    private static byte[] Tfhd(uint? defaultSampleFlags)
    {
        if (defaultSampleFlags is null)
            return Box("tfhd", U32(0), U32(1));

        return Box("tfhd",
            U32(0x00000008 | 0x00000010 | 0x00000020),   // version(0) / default-sample-duration|size|flags-present
            U32(1),                                      // track_ID
            U32(3000),                                   // default_sample_duration
            U32(1234),                                   // default_sample_size
            U32(defaultSampleFlags.Value));
    }

    private static byte[] Tfdt(uint baseMediaDecodeTime)
        => Box("tfdt", U32(0), U32(baseMediaDecodeTime));

    /// <summary>version 1（64bit）の <c>tfdt</c>。version(1)=0x01 ＋ flags(3)=0。</summary>
    private static byte[] Tfdt64(ulong baseMediaDecodeTime)
        => Box("tfdt", U32(0x01000000), U64(baseMediaDecodeTime));

    /// <summary><c>tfdt</c> を差し替えた（null なら省いた）<c>moof</c>。</summary>
    private static byte[] MoofWithTfdt(byte[]? tfdt)
        => tfdt is null
            ? Box("moof", Box("mfhd", U32(0), U32(1)), Box("traf", Tfhd(null), Trun()))
            : Box("moof", Box("mfhd", U32(0), U32(1)), Box("traf", Tfhd(null), tfdt, Trun()));

    /// <summary>
    /// <c>trun</c>。<paramref name="firstSampleFlags"/> か <paramref name="sampleZeroFlags"/> の
    /// どちらを立てるかで、状態機械が使う経路が変わる。
    /// </summary>
    private static byte[] Trun(uint sampleCount = 1, uint? firstSampleFlags = null, uint? sampleZeroFlags = null)
    {
        uint flags = 0x000001;                                   // data-offset-present
        var fields = new List<byte[]>();

        if (firstSampleFlags is not null)
            flags |= 0x000004;
        if (sampleZeroFlags is not null)
            flags |= 0x000200 | 0x000400;                        // sample-size-present | sample-flags-present

        fields.Add(U32(flags));
        fields.Add(U32(sampleCount));
        fields.Add(U32(0));                                      // data_offset

        if (firstSampleFlags is not null)
            fields.Add(U32(firstSampleFlags.Value));

        if (sampleZeroFlags is not null)
        {
            for (uint i = 0; i < sampleCount; i++)
            {
                fields.Add(U32(4096));                           // sample_size
                fields.Add(U32(i == 0 ? sampleZeroFlags.Value : 0));
            }
        }

        return Box("trun", [.. fields]);
    }

    private static byte[] Moof(byte[]? tfhd = null, byte[]? trun = null, uint sequenceNumber = 1)
        => Box("moof",
            Box("mfhd", U32(0), U32(sequenceNumber)),
            Box("traf", tfhd ?? Tfhd(null), Tfdt(sequenceNumber * 1000), trun ?? Trun()));

    private static byte[] Mdat(int payloadLength = 32)
        => Box("mdat", new byte[payloadLength]);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    // ---- 実行 ---------------------------------------------------------------

    private static List<Fmp4Segment> Split(byte[] stream)
    {
        var splitter = new Fmp4SegmentSplitter();
        splitter.Push(stream);
        return Drain(splitter);
    }

    private static List<Fmp4Segment> Drain(Fmp4SegmentSplitter splitter)
    {
        var segments = new List<Fmp4Segment>();
        while (splitter.TryDequeue(out var segment))
            segments.Add(segment);
        return segments;
    }

    // ---- テスト -------------------------------------------------------------

    [Fact]
    public void ByteAtATimePushProducesTheSameSegmentsAsOnePush()
    {
        byte[] stream = Concat(Ftyp(), Moov(), Moof(), Mdat(48), Moof(sequenceNumber: 2), Mdat(64));

        var whole = Split(stream);

        var incremental = new Fmp4SegmentSplitter();
        foreach (byte value in stream)
            incremental.Push([value]);
        var byteAtATime = Drain(incremental);

        Assert.False(incremental.IsFaulted);
        Assert.Equal(whole.Count, byteAtATime.Count);
        for (int i = 0; i < whole.Count; i++)
        {
            Assert.Equal(whole[i].Kind, byteAtATime[i].Kind);
            Assert.Equal(whole[i].StartsWithSync, byteAtATime[i].StartsWithSync);
            Assert.Equal(whole[i].Bytes, byteAtATime[i].Bytes);
        }
    }

    [Fact]
    public void TheInitSegmentRunsFromTheStartThroughTheMoov()
    {
        byte[] ftyp = Ftyp();
        byte[] moov = Moov();

        var segments = Split(Concat(ftyp, moov, Moof(), Mdat()));

        Assert.Equal(PreviewSegmentKind.Init, segments[0].Kind);
        Assert.Equal(Concat(ftyp, moov), segments[0].Bytes);
        Assert.False(segments[0].StartsWithSync);
    }

    [Fact]
    public void OneInitIsFollowedByOneMediaSegmentPerMoofMdatPair()
    {
        byte[] moof2 = Moof(sequenceNumber: 2);
        byte[] mdat2 = Mdat(64);

        var segments = Split(Concat(
            Ftyp(), Moov(),
            Moof(), Mdat(48),
            moof2, mdat2,
            Moof(sequenceNumber: 3), Mdat(80)));

        Assert.Equal(4, segments.Count);
        Assert.Equal(PreviewSegmentKind.Init, segments[0].Kind);
        Assert.All(segments.Skip(1), s => Assert.Equal(PreviewSegmentKind.Media, s.Kind));

        // Media は moof と mdat をこの順で連結したもの（間に何も足さない）。
        Assert.Equal(Concat(moof2, mdat2), segments[2].Bytes);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(NonSync, false)]
    public void FirstSampleFlagsDecidesSync(uint sampleFlags, bool expected)
    {
        var segments = Split(Concat(
            Ftyp(), Moov(trexDefaultSampleFlags: expected ? NonSync : 0),
            Moof(trun: Trun(firstSampleFlags: sampleFlags)), Mdat()));

        Assert.Equal(expected, segments[1].StartsWithSync);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(NonSync, false)]
    public void SampleFlagsOfTheFirstSampleDecidesSyncWhenFirstSampleFlagsIsAbsent(uint sampleFlags, bool expected)
    {
        // 実測では同梱 mp4mux がこの経路を使う（per-sample の non-sync ビットだけを書く）。
        var segments = Split(Concat(
            Ftyp(), Moov(trexDefaultSampleFlags: expected ? NonSync : 0),
            Moof(tfhd: Tfhd(expected ? NonSync : 0), trun: Trun(sampleCount: 3, sampleZeroFlags: sampleFlags)),
            Mdat()));

        Assert.Equal(expected, segments[1].StartsWithSync);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(NonSync, false)]
    public void TfhdDefaultSampleFlagsIsUsedWhenTheTrunSaysNothing(uint defaultFlags, bool expected)
    {
        var segments = Split(Concat(
            Ftyp(), Moov(trexDefaultSampleFlags: expected ? NonSync : 0),
            Moof(tfhd: Tfhd(defaultFlags)), Mdat()));

        Assert.Equal(expected, segments[1].StartsWithSync);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(NonSync, false)]
    public void TrexDefaultSampleFlagsIsTheLastFallback(uint defaultFlags, bool expected)
    {
        var segments = Split(Concat(
            Ftyp(), Moov(trexDefaultSampleFlags: defaultFlags),
            Moof(), Mdat()));

        Assert.Equal(expected, segments[1].StartsWithSync);
    }

    [Fact]
    public void AFragmentWithoutATrunIsNotTreatedAsSyncStarting()
    {
        byte[] moof = Box("moof", Box("mfhd", U32(0), U32(1)), Box("traf", Tfhd(null), Tfdt(0)));

        var segments = Split(Concat(Ftyp(), Moov(), moof, Mdat()));

        Assert.Equal(PreviewSegmentKind.Media, segments[1].Kind);
        Assert.False(segments[1].StartsWithSync);
    }

    [Fact]
    public void ASampleCountOfZeroIsNotTreatedAsSyncStarting()
    {
        var segments = Split(Concat(
            Ftyp(), Moov(),
            Moof(trun: Trun(sampleCount: 0, firstSampleFlags: 0)), Mdat()));

        Assert.False(segments[1].StartsWithSync);
    }

    [Fact]
    public void LargesizeBoxesAreWalked()
    {
        byte[] mdat = LargeBox("mdat", new byte[96]);

        var segments = Split(Concat(Ftyp(), Moov(), Moof(), mdat, Moof(sequenceNumber: 2), Mdat()));

        Assert.Equal(3, segments.Count);
        Assert.Equal(Concat(Moof(), mdat), segments[1].Bytes);
    }

    [Fact]
    public void ASizeOfZeroFaults()
    {
        // 「以後ストリーム末尾まで」はライブでは決着しない。
        var splitter = new Fmp4SegmentSplitter();
        byte[] openEnded = Box("mdat", new byte[16]);
        WriteU32(openEnded, 0, 0);

        splitter.Push(Concat(Ftyp(), Moov(), openEnded));

        Assert.True(splitter.IsFaulted);
        Assert.NotNull(splitter.Fault);
    }

    [Fact]
    public void ABoxOverTheSizeLimitFaultsWithoutWaitingForItsBody()
    {
        var splitter = new Fmp4SegmentSplitter();
        splitter.Push(Concat(Ftyp(), Moov()));

        var oversized = new byte[8];
        WriteU32(oversized, 0, (uint)Fmp4SegmentSplitter.MaxBoxSize + 1);
        Encoding.ASCII.GetBytes("mdat").CopyTo(oversized, 4);
        splitter.Push(oversized);

        Assert.True(splitter.IsFaulted);
    }

    [Fact]
    public void AMoofBeforeTheMoovFaults()
    {
        var splitter = new Fmp4SegmentSplitter();

        splitter.Push(Concat(Ftyp(), Moof(), Mdat()));

        Assert.True(splitter.IsFaulted);
        Assert.Empty(Drain(splitter));
    }

    [Fact]
    public void AMoofThatIsNotFollowedByAnMdatFaults()
    {
        var splitter = new Fmp4SegmentSplitter();

        splitter.Push(Concat(Ftyp(), Moov(), Moof(), Box("free", new byte[8])));

        Assert.True(splitter.IsFaulted);
        Assert.Single(Drain(splitter));   // init だけが出ている
    }

    [Fact]
    public void AnMdatWithoutAMoofFaults()
    {
        var splitter = new Fmp4SegmentSplitter();

        splitter.Push(Concat(Ftyp(), Moov(), Mdat()));

        Assert.True(splitter.IsFaulted);
    }

    [Fact]
    public void BoxesThatAreNeitherMoofNorMdatAreDroppedAfterTheInit()
    {
        // EOS のときに mp4mux は末尾へ mfra と確定した moov を追記する。
        // 捨てるだけで、後続の fragment は続く。
        var segments = Split(Concat(
            Ftyp(), Moov(),
            Moof(), Mdat(),
            Box("mfra", new byte[16]),
            Moov(),
            Box("free", new byte[4]),
            Box("styp", Encoding.ASCII.GetBytes("msdh")),
            Moof(sequenceNumber: 2), Mdat()));

        Assert.Equal(3, segments.Count);
        Assert.Equal(PreviewSegmentKind.Init, segments[0].Kind);
        Assert.Equal(PreviewSegmentKind.Media, segments[1].Kind);
        Assert.Equal(PreviewSegmentKind.Media, segments[2].Kind);
    }

    [Fact]
    public void PushIsIgnoredOnceFaulted()
    {
        var splitter = new Fmp4SegmentSplitter();
        splitter.Push(Concat(Ftyp(), Moov(), Mdat()));   // moof の無い mdat で fault
        Assert.True(splitter.IsFaulted);

        string? fault = splitter.Fault;
        var afterFault = Drain(splitter);
        splitter.Push(Concat(Moof(), Mdat()));

        Assert.Equal(fault, splitter.Fault);
        Assert.Empty(Drain(splitter));
        Assert.Single(afterFault);                       // fault 前に出た init は残る
    }

    [Theory]
    [InlineData(0u, NonSync, true)]
    [InlineData(NonSync, 0u, false)]
    public void FirstSampleFlagsWinsOverTheFirstPerSampleFlags(uint firstSampleFlags, uint sampleZeroFlags, bool expected)
    {
        // 両方書かれていたら first_sample_flags が勝つ。逆順に読むと、
        // 先頭が同期でない fragment を同期扱いにして MSE が復帰できなくなる。
        var segments = Split(Concat(
            Ftyp(), Moov(),
            Moof(trun: Trun(sampleCount: 3, firstSampleFlags: firstSampleFlags, sampleZeroFlags: sampleZeroFlags)),
            Mdat()));

        Assert.Equal(expected, segments[1].StartsWithSync);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(7u)]
    public void ASizeSmallerThanTheHeaderFaults(uint size)
    {
        // size が 8 未満だと箱の長さが自分のヘッダーより短い。進めないので待っても決着しない。
        var splitter = new Fmp4SegmentSplitter();
        splitter.Push(Concat(Ftyp(), Moov()));

        var undersized = new byte[8];
        WriteU32(undersized, 0, size);
        Encoding.ASCII.GetBytes("mdat").CopyTo(undersized, 4);
        splitter.Push(undersized);

        Assert.True(splitter.IsFaulted);
        Assert.NotNull(splitter.Fault);
    }

    [Fact]
    public void ALargesizeSmallerThanItsHeaderFaults()
    {
        var splitter = new Fmp4SegmentSplitter();
        splitter.Push(Concat(Ftyp(), Moov()));

        // size==1 の箱のヘッダーは 16 バイト。largesize がそれ未満なら壊れている。
        var undersized = new byte[16];
        WriteU32(undersized, 0, 1);
        Encoding.ASCII.GetBytes("mdat").CopyTo(undersized, 4);
        WriteU64(undersized, 8, 8);

        // 16 バイト全部を渡す ── 足りないと largesize を読む前に待たれて fault にならない。
        splitter.Push(undersized);

        Assert.True(splitter.IsFaulted);
        Assert.NotNull(splitter.Fault);
    }

    [Fact]
    public void SegmentsThatAreNeverDequeuedFault()
    {
        // 引き取られないセグメントを溜め続けると使用量が青天井になる。
        var splitter = new Fmp4SegmentSplitter();
        splitter.Push(Concat(Ftyp(), Moov()));

        for (uint i = 1; i <= Fmp4SegmentSplitter.MaxPendingSegments + 8; i++)
            splitter.Push(Concat(Moof(sequenceNumber: i), Mdat()));

        Assert.True(splitter.IsFaulted);

        // 上限までは積まれたまま残る（捨てると穴の開いた列になる）。
        Assert.Equal(Fmp4SegmentSplitter.MaxPendingSegments, Drain(splitter).Count);
    }

    [Fact]
    public void BytesPilingUpWithoutAMoovFault()
    {
        // init はストリームの先頭から moov までなので、moov が来ないかぎり
        // 緩衝の先頭は切り落とせない。1 箱ずつは上限内でも溜まり続ける。
        var splitter = new Fmp4SegmentSplitter();
        splitter.Push(Ftyp());

        byte[] filler = Box("free", new byte[8 * 1024 * 1024]);
        for (int i = 0; i < 5 && !splitter.IsFaulted; i++)
            splitter.Push(filler);

        Assert.True(splitter.IsFaulted);
        Assert.NotNull(splitter.Fault);
        Assert.Empty(Drain(splitter));
    }

    /// <summary>
    /// <c>tfdt</c> version 0（32bit）の <c>baseMediaDecodeTime</c> が
    /// <c>DecodeTime</c> に出ること。<b>Init は常に 0</b>。
    /// </summary>
    [Fact]
    public void AVersion0TfdtGivesTheDecodeTime()
    {
        var segments = Split(Concat(Ftyp(), Moov(), MoofWithTfdt(Tfdt(90_000)), Mdat()));

        Assert.Equal(0UL, segments.Single(s => s.Kind == PreviewSegmentKind.Init).DecodeTime);
        Assert.Equal(90_000UL, segments.Single(s => s.Kind == PreviewSegmentKind.Media).DecodeTime);
    }

    /// <summary>
    /// <c>tfdt</c> version 1（64bit）。<b>32bit に収まらない値</b>で試す ──
    /// version を見ずに 32bit で読むと、ここで黙って下位 32bit だけを拾う。
    /// </summary>
    [Fact]
    public void AVersion1TfdtGivesTheFull64BitDecodeTime()
    {
        const ulong DecodeTime = 0x0000_0001_0000_0007UL;

        var segments = Split(Concat(Ftyp(), Moov(), MoofWithTfdt(Tfdt64(DecodeTime)), Mdat()));

        Assert.Equal(DecodeTime, segments.Single(s => s.Kind == PreviewSegmentKind.Media).DecodeTime);
    }

    /// <summary>
    /// <c>tfdt</c> が無ければ 0。<b>切り出しそのものは成功させる</b>
    /// ── 時刻が無いことは壊れた入力ではない（DASH の集約側が判断する）。
    /// </summary>
    [Fact]
    public void AMissingTfdtLeavesTheDecodeTimeAtZero()
    {
        var segments = Split(Concat(Ftyp(), Moov(), MoofWithTfdt(null), Mdat()));

        var media = segments.Single(s => s.Kind == PreviewSegmentKind.Media);
        Assert.Equal(0UL, media.DecodeTime);
        Assert.NotEmpty(media.Bytes);
    }
}
