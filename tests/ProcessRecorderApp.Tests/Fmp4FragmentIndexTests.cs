using ProcessRecorderApp.Components;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// fragmented MP4 の <c>moof</c> 索引（<see cref="Fmp4FragmentIndex"/>）。
///
/// <para>
/// <b>バイト列は手で組む。</b> 固定したいのは ISO-BMFF の読み方そのもので、
/// 実ファイルを置くと「そのファイルが読めること」しか言えない。
/// </para>
/// <para>
/// <b>末尾が書き掛けなのが正常な入力である。</b> 録画中のファイルを共有で読むので、
/// 最後の <c>moof</c> も <c>mdat</c> も途中までしか無いことがある ── そこで止まり、
/// その先頭を次の起点として返せなければ、差分の読み足しが成り立たない。
/// </para>
/// </summary>
public sealed class Fmp4FragmentIndexTests
{
    /// <summary><c>sample_is_non_sync_sample</c>。</summary>
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

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        WriteU32(bytes, 0, value);
        return bytes;
    }

    private static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        for (int i = 0; i < 8; i++)
            bytes[i] = (byte)(value >> (56 - (8 * i)));
        return bytes;
    }

    private static void WriteU32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

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

    private static byte[] Ftyp() => Box("ftyp", Encoding.ASCII.GetBytes("isomiso2"));

    /// <summary><c>moov{mvex{trex}}</c>。<paramref name="trexDefaultSampleFlags"/> が最後の砦。</summary>
    private static byte[] Moov(uint trexDefaultSampleFlags = 0)
        => Box("moov",
            Box("mvex", Box("trex",
                U32(0),                          // version / flags
                U32(1),                          // track_ID
                U32(1),                          // default_sample_description_index
                U32(0),                          // default_sample_duration
                U32(0),                          // default_sample_size
                U32(trexDefaultSampleFlags))));

    private static byte[] Tfhd(uint? defaultSampleDuration = null, uint? defaultSampleFlags = null)
    {
        uint flags = 0;
        var fields = new List<byte[]>();

        if (defaultSampleDuration is not null)
            flags |= 0x000008;
        if (defaultSampleFlags is not null)
            flags |= 0x000020;

        fields.Add(U32(flags));
        fields.Add(U32(1));                      // track_ID
        if (defaultSampleDuration is not null)
            fields.Add(U32(defaultSampleDuration.Value));
        if (defaultSampleFlags is not null)
            fields.Add(U32(defaultSampleFlags.Value));

        return Box("tfhd", [.. fields]);
    }

    /// <summary>version 1（64bit）の <c>tfdt</c>。同梱 <c>mp4mux</c> が書く形。</summary>
    private static byte[] Tfdt(ulong baseMediaDecodeTime)
        => Box("tfdt", U32(0x01000000), U64(baseMediaDecodeTime));

    /// <summary>
    /// <c>trun</c>。<paramref name="sampleDuration"/> を渡すと sample-duration-present で
    /// その値を <paramref name="sampleCount"/> 本並べる。
    /// <paramref name="sampleZeroFlags"/> は sample-size と sample-flags を並べ、
    /// <paramref name="compositionOffset"/> は sample-composition-time-offset を足す
    /// ── <b>並びの幅（stride）を作るためのもの</b>で、尺の総和はこの幅で拾える必要がある。
    /// </summary>
    private static byte[] Trun(
        uint sampleCount, uint? sampleDuration = null,
        uint? firstSampleFlags = null, uint? sampleZeroFlags = null, bool compositionOffset = false)
    {
        uint flags = 0x000001;                                   // data-offset-present
        var fields = new List<byte[]>();

        if (firstSampleFlags is not null)
            flags |= 0x000004;
        if (sampleDuration is not null)
            flags |= 0x000100;                                   // sample-duration-present
        if (sampleZeroFlags is not null)
            flags |= 0x000200 | 0x000400;                        // sample-size | sample-flags-present
        if (compositionOffset)
            flags |= 0x000800;                                   // sample-composition-time-offset

        fields.Add(U32(flags));
        fields.Add(U32(sampleCount));
        fields.Add(U32(0));                                      // data_offset

        if (firstSampleFlags is not null)
            fields.Add(U32(firstSampleFlags.Value));

        for (uint i = 0; i < sampleCount; i++)
        {
            if (sampleDuration is not null)
                fields.Add(U32(sampleDuration.Value));
            if (sampleZeroFlags is not null)
            {
                fields.Add(U32(4096));                           // sample_size
                fields.Add(U32(i == 0 ? sampleZeroFlags.Value : 0));
            }
            if (compositionOffset)
                fields.Add(U32(1));                              // sample_composition_time_offset
        }

        return Box("trun", [.. fields]);
    }

    /// <summary><c>moof</c> ＋ <c>mdat</c> の対 1 つぶん。</summary>
    private static byte[] Fragment(
        ulong decodeTime, uint sampleCount = 2, uint? sampleDuration = 500,
        uint? firstSampleFlags = null, byte[]? tfhd = null, int mdatPayload = 64, byte[]? trun = null)
        => Concat(
            Box("moof",
                Box("mfhd", U32(0), U32(1)),
                Box("traf",
                    tfhd ?? Tfhd(), Tfdt(decodeTime),
                    trun ?? Trun(sampleCount, sampleDuration, firstSampleFlags))),
            Box("mdat", new byte[mdatPayload]));

    private static Fmp4FragmentIndex.ScanResult Scan(
        byte[] file, long from = 0, IReadOnlyList<Fmp4FragmentIndex.Fragment>? previous = null,
        uint trexDefaultSampleFlags = 0)
    {
        using var stream = new MemoryStream(file, writable: false);
        return Fmp4FragmentIndex.Scan(stream, from, previous, trexDefaultSampleFlags);
    }

    // ---- 索引の中身 ---------------------------------------------------------

    /// <summary>
    /// 3 つの <c>moof</c> が、位置・大きさ・時刻・尺・同期の別まで揃って索引になること。
    /// <b>同期でないフラグメントが在るのが録画の形である</b>（フラグメント 1 秒・GOP 2 秒）。
    /// </summary>
    [Fact]
    public void EveryFragmentIsIndexedWithItsTimeDurationAndSyncFlag()
    {
        byte[] head = Concat(Ftyp(), Moov());
        byte[] first = Fragment(0, firstSampleFlags: 0);
        byte[] second = Fragment(1000, firstSampleFlags: NonSync);
        byte[] third = Fragment(2000, firstSampleFlags: 0);

        byte[] file = Concat(head, first, second, third);
        var result = Scan(file);

        Assert.Equal(3, result.Fragments.Count);
        Assert.Equal(head.Length, result.InitSize);
        Assert.Equal(file.Length, result.NextOffset);

        Assert.Equal<long[]>(
            [head.Length, head.Length + first.Length, head.Length + first.Length + second.Length],
            [.. result.Fragments.Select(f => f.Offset)]);
        Assert.Equal<int[]>(
            [first.Length, second.Length, third.Length], [.. result.Fragments.Select(f => f.Size)]);
        Assert.Equal<ulong[]>([0, 1000, 2000], [.. result.Fragments.Select(f => f.Time)]);

        // sample_duration 500 が 2 本。
        Assert.Equal<uint[]>([1000, 1000, 1000], [.. result.Fragments.Select(f => f.Duration)]);
        Assert.Equal<bool[]>([true, false, true], [.. result.Fragments.Select(f => f.Sync)]);
    }

    /// <summary>
    /// <c>trun</c> が <c>sample_duration</c> を持たないときは <c>tfhd</c> の
    /// <c>default_sample_duration</c> × <c>sample_count</c> になること。
    /// </summary>
    [Fact]
    public void WithoutPerSampleDurations_TheDefaultFromTfhdIsMultipliedOut()
    {
        byte[] file = Concat(
            Ftyp(), Moov(),
            Fragment(0, sampleCount: 4, sampleDuration: null, tfhd: Tfhd(defaultSampleDuration: 250)));

        var fragment = Assert.Single(Scan(file).Fragments);
        Assert.Equal(1000u, fragment.Duration);
    }

    /// <summary>
    /// 同期の判定は具体的なものから ── <c>first_sample_flags</c> が無ければ
    /// <c>tfhd</c> の <c>default_sample_flags</c>、それも無ければ <c>trex</c> のもの。
    /// </summary>
    [Fact]
    public void TheSyncFlagFallsBackFromTfhdToTrex()
    {
        byte[] fromTfhd = Concat(
            Ftyp(), Moov(),
            Fragment(0, tfhd: Tfhd(defaultSampleFlags: NonSync)));
        Assert.False(Assert.Single(Scan(fromTfhd).Fragments).Sync);

        byte[] fromTrex = Concat(Ftyp(), Moov(NonSync), Fragment(0));
        Assert.False(Assert.Single(Scan(fromTrex).Fragments).Sync);

        // どこにも無ければ同期扱い（Fmp4SegmentSplitter と同じ規則）。
        Assert.True(Assert.Single(Scan(Concat(Ftyp(), Moov(), Fragment(0))).Fragments).Sync);
    }

    /// <summary>
    /// <c>first_sample_flags</c> が無く <c>sample_flags[0]</c> だけが在る形でも同期が読めること。
    ///
    /// <para>
    /// <b>これは実在の形である</b> ── <c>first-sample-flags-present</c> を使わず、
    /// 先頭サンプルのフラグを並びの中に書く多重化器がある。<c>tfhd</c> も <c>trex</c> も
    /// 同期を言っていないので、<b>並びの中を読めなければ真（同期）と答えてしまい</b>、
    /// 同期でないフラグメントへ飛んで復号が始まらない。
    /// </para>
    /// </summary>
    [Fact]
    public void TheFirstSamplesFlagsInsideTheTableDecideTheSync()
    {
        byte[] nonSync = Concat(
            Ftyp(), Moov(),
            Fragment(0, trun: Trun(2, sampleDuration: 500, sampleZeroFlags: NonSync)));
        Assert.False(Assert.Single(Scan(nonSync).Fragments).Sync);

        byte[] sync = Concat(
            Ftyp(), Moov(NonSync),
            Fragment(0, trun: Trun(2, sampleDuration: 500, sampleZeroFlags: 0)));
        // 並びの中のフラグは trex の既定より具体的なので、そちらが勝つ。
        Assert.True(Assert.Single(Scan(sync).Fragments).Sync);
    }

    /// <summary>
    /// <c>sample_duration</c> の総和が、<b>並びの幅（stride）を跨いで</b>拾えること
    /// ── duration ＋ size ＋ flags ＋ composition offset の 4 語で 1 サンプルになる形。
    ///
    /// <para>
    /// 幅を取り違えると、隣の語（ここでは <c>sample_size</c> の 4096）を尺として足す
    /// ── 尺はシークバーの <c>max</c> そのものなので、桁ごと狂う。
    /// </para>
    /// </summary>
    [Fact]
    public void TheDurationsAreSummedAcrossTheFullSampleStride()
    {
        byte[] file = Concat(
            Ftyp(), Moov(),
            Fragment(0, trun: Trun(3, sampleDuration: 300, sampleZeroFlags: 0, compositionOffset: true)));

        var fragment = Assert.Single(Scan(file).Fragments);
        Assert.Equal(900u, fragment.Duration);
    }

    // ---- 書き掛けの末尾 -----------------------------------------------------

    /// <summary>
    /// 末尾の <c>moof</c> が途中で切れていたら、<b>その先頭</b>が次の起点になること。
    /// </summary>
    [Fact]
    public void ATruncatedTrailingMoofStopsAtItsStart()
    {
        byte[] head = Concat(Ftyp(), Moov());
        byte[] first = Fragment(0);
        byte[] second = Fragment(1000);
        byte[] third = Fragment(2000);

        long thirdOffset = head.Length + first.Length + second.Length;
        byte[] file = Concat(head, first, second, third)[..(int)(thirdOffset + 12)];

        var result = Scan(file);

        Assert.Equal(2, result.Fragments.Count);
        Assert.Equal(thirdOffset, result.NextOffset);
        Assert.Equal(head.Length, result.InitSize);
    }

    /// <summary>
    /// <c>mdat</c> が書き切られていないときも、<b><c>moof</c> の先頭</b>まで戻ること。
    /// <c>moof</c> と <c>mdat</c> は 1 つの単位で、片方だけを数えると尺も大きさも実在しない。
    /// </summary>
    [Fact]
    public void ATruncatedTrailingMdatStopsAtTheMoofBeforeIt()
    {
        byte[] head = Concat(Ftyp(), Moov());
        byte[] first = Fragment(0);
        byte[] second = Fragment(1000);

        long secondOffset = head.Length + first.Length;
        // 2 つ目の moof は丸ごと在るが、その mdat が数バイトしか無い。
        byte[] file = Concat(head, first, second)[..(int)(secondOffset + second.Length - 20)];

        var result = Scan(file);

        Assert.Single(result.Fragments);
        Assert.Equal(secondOffset, result.NextOffset);
    }

    /// <summary>
    /// <b>差分走査へ <c>trex</c> の既定フラグが持ち越されること。</b> 続きから読む走査は
    /// <c>moov</c> を通らないので、渡さないと同期の判定の最後の拠り所が 0（＝同期）に戻り、
    /// <b>同じフラグメントの <c>Sync</c> が全走査と食い違う</b>
    /// ── ブラウザは同期でない位置へ飛び、復号が始まらない。
    /// </summary>
    [Fact]
    public void TheTrexDefaultIsCarriedIntoTheIncrementalScan()
    {
        byte[] head = Concat(Ftyp(), Moov(NonSync));
        byte[] first = Fragment(0);
        byte[] second = Fragment(1000);

        var half = Scan(Concat(head, first));
        Assert.False(Assert.Single(half.Fragments).Sync);
        Assert.Equal(NonSync, half.TrexDefaultSampleFlags);

        byte[] whole = Concat(head, first, second);
        var resumed = Scan(whole, half.NextOffset, half.Fragments, half.TrexDefaultSampleFlags);

        Assert.Equal<bool[]>([false, false], [.. resumed.Fragments.Select(f => f.Sync)]);
        // 全走査と同じ答えであること（食い違わないのが要点）。
        Assert.Equal<bool[]>([false, false], [.. Scan(whole).Fragments.Select(f => f.Sync)]);
    }

    /// <summary>
    /// 続きは前回の <c>NextOffset</c> から読み足せること（録画中のファイルの差分走査）。
    /// </summary>
    [Fact]
    public void TheScanResumesFromWhereItStopped()
    {
        byte[] head = Concat(Ftyp(), Moov());
        byte[] first = Fragment(0);
        byte[] second = Fragment(1000);

        byte[] partial = Concat(head, first, second)[..(head.Length + first.Length + 12)];
        var half = Scan(partial);
        Assert.Single(half.Fragments);

        byte[] whole = Concat(head, first, second);
        var full = Scan(whole, half.NextOffset, half.Fragments);

        Assert.Equal(2, full.Fragments.Count);
        Assert.Equal(whole.Length, full.NextOffset);
        // 差分走査では moov を見ないので、init の大きさは前回のものを引き継ぐ。
        Assert.Equal(head.Length, full.InitSize);
        Assert.Equal<ulong[]>([0, 1000], [.. full.Fragments.Select(f => f.Time)]);
    }

    /// <summary><c>from</c> より手前のフラグメントは索引に載らないこと。</summary>
    [Fact]
    public void ScanningFromAnOffsetDropsWhatIsBeforeIt()
    {
        byte[] head = Concat(Ftyp(), Moov());
        byte[] first = Fragment(0);
        byte[] file = Concat(head, first, Fragment(1000));

        var result = Scan(file, head.Length + first.Length);

        var only = Assert.Single(result.Fragments);
        Assert.Equal(1000ul, only.Time);
        Assert.Equal(file.Length, result.NextOffset);
    }

    // ---- フラグメントではない箱 ---------------------------------------------

    /// <summary>
    /// 確定時に末尾へ足される <c>mfra</c> と 2 つ目の <c>moov</c>、および
    /// <c>free</c> / <c>sidx</c> / <c>styp</c> は索引に入らないこと。
    /// </summary>
    [Fact]
    public void MfraAndASecondMoovAndPaddingAreIgnored()
    {
        byte[] head = Concat(Ftyp(), Moov());
        byte[] file = Concat(
            head,
            Box("free", new byte[16]),
            Box("styp", Encoding.ASCII.GetBytes("msdh")),
            Fragment(0),
            Box("sidx", new byte[24]),
            Fragment(1000),
            Box("mfra", Box("mfro", U32(0), U32(16))),
            Moov());

        var result = Scan(file);

        Assert.Equal(2, result.Fragments.Count);
        Assert.Equal(file.Length, result.NextOffset);
        // init の大きさは最初の moof の位置 ── free と styp もそこに含まれる。
        Assert.Equal(head.Length + 24 + 12, result.InitSize);
    }

    /// <summary><c>moof</c> が 1 つも無いファイルは、空の索引で終わること。</summary>
    [Fact]
    public void AFileWithoutFragmentsYieldsAnEmptyIndex()
    {
        byte[] file = Concat(Ftyp(), Moov());
        var result = Scan(file);

        Assert.Empty(result.Fragments);
        Assert.Equal(0, result.InitSize);
        Assert.Equal(file.Length, result.NextOffset);
    }

    /// <summary>
    /// 壊れた size（ヘッダーより小さい・<c>0</c>）はそこで読み終えること
    /// ── 回り続けたり、範囲の外を読んだりしてはいけない。
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(7u)]
    public void ABrokenBoxSizeEndsTheScan(uint size)
    {
        byte[] file = Concat(Ftyp(), Moov(), Fragment(0));
        WriteU32(file, 0, size);

        var result = Scan(file);

        Assert.Empty(result.Fragments);
        Assert.Equal(0, result.NextOffset);
    }

    /// <summary><c>size==1</c>（<c>largesize</c>）の箱のヘッダーだけ。宣言だけが巨大で中身は無い。</summary>
    private static byte[] LargeBoxHeader(string type, ulong largesize)
        => Concat(U32(1), Encoding.ASCII.GetBytes(type), U64(largesize));

    /// <summary>
    /// <b><c>largesize</c> が桁外れでも読み終わるだけであること。</b>
    ///
    /// <para>
    /// 宣言された大きさを「位置 ＋ 大きさ」の形で足すと 64bit を回って負になり、
    /// 範囲の検査を素通りする ── その先で負の位置を読みに行き、
    /// <c>Stream.Position</c> が投げる（<c>ArgumentOutOfRangeException</c> は
    /// 走査が捕まえている種類ではないので、要求は 500 になる）。
    /// 録画中のファイルは末尾が書き掛けなので、この数語は素性の知れないバイト列である。
    /// </para>
    /// </summary>
    [Fact]
    public void ABoxWhoseDeclaredSizeOverflowsEndsTheScan()
    {
        byte[] head = Concat(Ftyp(), Moov());
        byte[] first = Fragment(0);
        byte[] file = Concat(head, first, LargeBoxHeader("free", long.MaxValue));

        var result = Scan(file);

        Assert.Single(result.Fragments);
        Assert.Equal(head.Length + first.Length, result.NextOffset);
    }

    // ---- キャッシュ（Fmp4FragmentIndexCache） -------------------------------

    private static readonly DateTime CacheStamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Fmp4FragmentIndex.ScanResult Get(
        Fmp4FragmentIndexCache cache, string path, byte[] file, DateTime? lastWriteUtc = null)
    {
        using var stream = new MemoryStream(file, writable: false);
        return cache.Get(path, stream, file.Length, lastWriteUtc ?? CacheStamp);
    }

    /// <summary>
    /// 伸びたファイルを読み足しても、<c>trex</c> の既定フラグが効いたままであること
    /// ── 続きを覚えているのはキャッシュなので、持ち越しが切れるならここで切れる。
    /// </summary>
    [Fact]
    public void TheCacheCarriesTheTrexDefaultIntoWhatWasAppended()
    {
        byte[] head = Concat(Ftyp(), Moov(NonSync));
        byte[] first = Fragment(0);
        byte[] second = Fragment(1000);

        var cache = new Fmp4FragmentIndexCache();
        Assert.False(Assert.Single(Get(cache, "a.mp4", Concat(head, first)).Fragments).Sync);

        var grown = Get(cache, "a.mp4", Concat(head, first, second), CacheStamp.AddSeconds(1));

        Assert.Equal(2, grown.Fragments.Count);
        Assert.Equal<bool[]>([false, false], [.. grown.Fragments.Select(f => f.Sync)]);
    }

    /// <summary>
    /// <b>縮んだファイルは覚えている続きを捨てて全部を辿り直すこと。</b>
    /// 同じ名前が別の実体に差し替わったということなので、覚えている位置は意味を失う
    /// ── 読み足すと、実在しない位置を指す索引になる。
    /// </summary>
    [Fact]
    public void AShrunkFileIsScannedFromItsBeginningAgain()
    {
        byte[] head = Concat(Ftyp(), Moov());
        byte[] three = Concat(head, Fragment(0), Fragment(1000), Fragment(2000));
        byte[] one = Concat(head, Fragment(0));

        var cache = new Fmp4FragmentIndexCache();
        Assert.Equal(3, Get(cache, "a.mp4", three).Fragments.Count);

        var after = Get(cache, "a.mp4", one, CacheStamp.AddSeconds(1));

        Assert.Single(after.Fragments);
        Assert.Equal(one.Length, after.NextOffset);
        Assert.Equal(head.Length, after.InitSize);
    }

    /// <summary>
    /// 覚えているのは <see cref="Fmp4FragmentIndexCache.MaxEntries"/> 件までで、
    /// 溢れたものは辿り直しになること（正しさは変わらず、仕事の量だけが戻る）。
    ///
    /// <para>
    /// <b>証人は「同じ長さで中身の違うファイル」である</b> ── 長さと更新時刻が同じなら
    /// 覚えている答えがそのまま返るので、返った答えがどちらの中身のものかで
    /// 読み直したかどうかが分かる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheCacheRemembersUntilItOverflows()
    {
        byte[] indexed = Concat(Ftyp(), Moov(), Fragment(0), Fragment(1000));
        byte[] plain = Box("free", new byte[indexed.Length - 8]);
        Assert.Equal(indexed.Length, plain.Length);

        var kept = new Fmp4FragmentIndexCache();
        Assert.Equal(2, Get(kept, "a.mp4", indexed).Fragments.Count);
        Assert.Equal(2, Get(kept, "a.mp4", plain).Fragments.Count);

        var overflowed = new Fmp4FragmentIndexCache();
        Assert.Equal(2, Get(overflowed, "a.mp4", indexed).Fragments.Count);
        for (int i = 0; i < Fmp4FragmentIndexCache.MaxEntries; i++)
            Get(overflowed, "other" + i.ToString(CultureInfo.InvariantCulture) + ".mp4", indexed);

        Assert.Empty(Get(overflowed, "a.mp4", plain).Fragments);
    }
}
