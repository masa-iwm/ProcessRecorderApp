using ProcessRecorderApp.Components;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 録画ファイルの先頭だけを見る判定（<see cref="Fmp4Probe"/>）。
///
/// <para>
/// <b>入力は「途中までしか無い」のが正常である。</b> 呼び出し側は録画中のファイルの
/// 先頭 64KB を渡すので、箱が切れている・短すぎる・そもそも MP4 ではない、を
/// すべて false / null に畳めなければならない（例外を投げると一覧そのものが落ちる）。
/// </para>
/// <para>
/// <b>バイト列は手で組む。</b> ここで固定したいのは ISO-BMFF の読み方そのもので、
/// 実ファイルを置くと「そのファイルが読めること」しか言えない。
/// </para>
/// </summary>
public sealed class Fmp4ProbeTests
{
    /// <summary>size(4) ＋ type(4) ＋ 中身の箱を組む。</summary>
    private static byte[] Box(string type, params byte[][] children)
    {
        var payload = new List<byte>();
        foreach (byte[] child in children)
            payload.AddRange(child);

        byte[] box = new byte[8 + payload.Count];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (byte[] part in parts)
            all.AddRange(part);
        return [.. all];
    }

    /// <summary>version(1) ＋ profile ＋ compatibility ＋ level の <c>avcC</c>。</summary>
    private static byte[] AvcC(byte profile, byte compatibility, byte level)
        => Box("avcC", [1, profile, compatibility, level]);

    private static byte[] Ftyp() => Box("ftyp", Encoding.ASCII.GetBytes("isom"));

    private static byte[] U32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>
    /// <c>mdhd</c>。version 0 は creation / modification が 32bit、version 1 は 64bit で、
    /// <c>timescale</c> の位置がそのぶん動く。
    /// </summary>
    private static byte[] Mdhd(uint timescale, byte version = 0)
        => version == 0
            ? Box("mdhd", [0, 0, 0, 0], new byte[8], U32(timescale), new byte[4])
            : Box("mdhd", [1, 0, 0, 0], new byte[16], U32(timescale), new byte[8]);

    [Fact]
    public void AMoovWithMvex_IsFragmented()
    {
        byte[] file = Concat(
            Ftyp(),
            Box("moov", Box("mvhd", new byte[8]), Box("trak", new byte[4]), Box("mvex", Box("trex", new byte[8]))),
            Box("moof", new byte[4]));

        Assert.True(Fmp4Probe.IsFragmented(file));
    }

    [Fact]
    public void AMoovWithoutMvex_IsNotFragmented()
    {
        byte[] file = Concat(
            Ftyp(),
            Box("moov", Box("mvhd", new byte[8]), Box("trak", new byte[4])),
            Box("mdat", new byte[16]));

        Assert.False(Fmp4Probe.IsFragmented(file));
    }

    [Fact]
    public void WithoutAMoov_ItIsNotFragmented()
    {
        Assert.False(Fmp4Probe.IsFragmented(Concat(Ftyp(), Box("mdat", new byte[16]))));
    }

    /// <summary>録画が始まった直後は数バイトしか無い。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    public void TooShortIsNotFragmented(int length)
    {
        Assert.False(Fmp4Probe.IsFragmented(new byte[length]));
    }

    /// <summary>
    /// ヘッダーより小さい size（＝壊れている）と、<c>size==0</c>（「以後ファイル末尾まで」）は
    /// <b>そこで読み終える</b>。回り続けたり、範囲の外を読んだりしてはいけない。
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(7u)]
    public void ABrokenBoxSizeEndsTheScan(uint size)
    {
        byte[] file = Concat(Ftyp(), Box("moov", Box("mvex", new byte[4])));
        // 先頭（ftyp）の size を壊す ── 以後の moov には到達できない。
        BinaryPrimitives.WriteUInt32BigEndian(file, size);

        Assert.False(Fmp4Probe.IsFragmented(file));
        Assert.Null(Fmp4Probe.CodecString(file));
    }

    /// <summary>
    /// <c>moov</c> が渡された範囲の途中で切れていても、読めたところに <c>mvex</c> が
    /// 在れば fragmented と答える（先頭 64KB しか渡されない）。
    /// </summary>
    [Fact]
    public void ATruncatedMoovStillAnswersFromWhatIsThere()
    {
        byte[] file = Concat(Ftyp(), Box("moov", Box("mvex", new byte[4]), Box("trak", new byte[64])));

        Assert.True(Fmp4Probe.IsFragmented(file[..^40]));
    }

    [Fact]
    public void TheCodecStringComesFromAvcC()
    {
        byte[] file = Concat(
            Ftyp(),
            Box("moov", Box("trak", Box("stsd", Box("avc1", AvcC(0x64, 0x00, 0x14)))), Box("mvex", new byte[4])));

        Assert.Equal("avc1.640014", Fmp4Probe.CodecString(file));
    }

    [Fact]
    public void WithoutAvcC_ThereIsNoCodecString()
    {
        Assert.Null(Fmp4Probe.CodecString(Concat(Ftyp(), Box("moov", Box("trak", new byte[16])))));
        Assert.Null(Fmp4Probe.CodecString(Concat(Ftyp(), Box("mdat", new byte[16]))));
        Assert.Null(Fmp4Probe.CodecString([]));
    }

    /// <summary>
    /// <b>16 進は 2 桁固定</b>（<c>avc1.42c01e</c> の <c>0e</c> が <c>e</c> に縮むと
    /// <c>isTypeSupported</c> が false を返す）。
    /// </summary>
    [Fact]
    public void EveryByteIsTwoHexDigits()
    {
        byte[] file = Concat(Ftyp(), Box("moov", Box("avc1", AvcC(0x42, 0xC0, 0x0E))));

        Assert.Equal("avc1.42c00e", Fmp4Probe.CodecString(file));
    }

    // ---- メディアの時間の単位 ----

    /// <summary>
    /// <c>timescale</c> は <c>moov</c> &gt; <c>trak</c> &gt; <c>mdia</c> &gt; <c>mdhd</c> から採る。
    /// <b>version 0 と 1 の両方を読む</b> ── 同梱の <c>mp4mux</c> がどちらを書くかは実装の都合で、
    /// 規格はどちらも許している。
    /// </summary>
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void TheMediaTimescaleComesFromMdhd(byte version)
    {
        byte[] file = Concat(
            Ftyp(),
            Box("moov",
                Box("mvhd", new byte[8]),
                Box("trak", Box("mdia", Mdhd(90000, version), Box("hdlr", new byte[16]))),
                Box("mvex", new byte[4])));

        Assert.True(Fmp4Probe.TryReadMediaTimescale(file, out uint timescale));
        Assert.Equal(90000u, timescale);
    }

    /// <summary>
    /// <c>mdhd</c> を持たない <c>trak</c> は飛ばして次を見る（規格は複数の trak を許す）。
    /// </summary>
    [Fact]
    public void ATrakWithoutMdhdDoesNotStopTheSearch()
    {
        byte[] file = Concat(
            Ftyp(),
            Box("moov",
                Box("trak", Box("tkhd", new byte[16])),
                Box("trak", Box("mdia", Mdhd(1000)))));

        Assert.True(Fmp4Probe.TryReadMediaTimescale(file, out uint timescale));
        Assert.Equal(1000u, timescale);
    }

    /// <summary>
    /// <c>moov</c> が無い・<c>mdhd</c> が無い・<c>timescale</c> が 0・箱が切れている、は
    /// すべて「読めなかった」に畳む（呼び出し側は 404 で答える）。
    /// </summary>
    [Fact]
    public void WithoutAReadableMdhd_ThereIsNoTimescale()
    {
        Assert.False(Fmp4Probe.TryReadMediaTimescale(Concat(Ftyp(), Box("mdat", new byte[16])), out _));
        Assert.False(Fmp4Probe.TryReadMediaTimescale(
            Concat(Ftyp(), Box("moov", Box("trak", Box("mdia", new byte[8])))), out _));
        Assert.False(Fmp4Probe.TryReadMediaTimescale(
            Concat(Ftyp(), Box("moov", Box("trak", Box("mdia", Mdhd(0))))), out _));
        Assert.False(Fmp4Probe.TryReadMediaTimescale([], out _));

        // mdhd が途中で切れている（timescale まで届いていない）。
        byte[] truncated = Concat(Ftyp(), Box("moov", Box("trak", Box("mdia", Mdhd(1000)))));
        Assert.False(Fmp4Probe.TryReadMediaTimescale(truncated[..^8], out _));
    }
    private static byte[] U64(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>
    /// <c>mvhd</c>。version 0 は creation / modification / duration が 32bit、
    /// version 1 は 64bit で <c>timescale</c> と <c>duration</c> の位置が動く。
    /// </summary>
    private static byte[] Mvhd(uint timescale, ulong duration, byte version = 0)
        => version == 0
            ? Box("mvhd", [0, 0, 0, 0], new byte[8], U32(timescale), U32((uint)duration))
            : Box("mvhd", [1, 0, 0, 0], new byte[16], U32(timescale), U64(duration));

    /// <summary>size(4) が 1 で、実長が largesize(8) に入る箱。</summary>
    private static byte[] LargeBox(string type, int payloadBytes)
    {
        byte[] box = new byte[16 + payloadBytes];
        BinaryPrimitives.WriteUInt32BigEndian(box, 1);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8), (ulong)box.Length);
        return box;
    }

    private static bool TryReadDuration(byte[] file, out long durationMs)
    {
        using var stream = new MemoryStream(file, writable: false);
        return Fmp4Probe.TryReadMovieDuration(stream, out durationMs);
    }

    [Fact]
    public void TheDurationIsReadFromAMoovAtTheFront()
    {
        // 単発録画は faststart=true で moov が ftyp の直後に来る。
        byte[] file = Concat(Ftyp(), Box("moov", Mvhd(1000, 2500)), Box("mdat", new byte[64]));

        Assert.True(TryReadDuration(file, out long durationMs));
        Assert.Equal(2500, durationMs);
    }

    [Fact]
    public void TheDurationIsReadFromAMoovAtTheEnd()
    {
        // 常時録画のセグメントは faststart 無しで moov が末尾に来る。
        // mdat は跳ぶだけで読まない。
        byte[] file = Concat(Ftyp(), Box("mdat", new byte[4096]), Box("moov", Mvhd(90_000, 315_000)));

        Assert.True(TryReadDuration(file, out long durationMs));
        Assert.Equal(3500, durationMs);
    }

    [Fact]
    public void ALargesizeBoxIsSkipped()
    {
        byte[] file = Concat(Ftyp(), LargeBox("mdat", 128), Box("moov", Mvhd(1000, 1234)));

        Assert.True(TryReadDuration(file, out long durationMs));
        Assert.Equal(1234, durationMs);
    }

    [Fact]
    public void TheVersion1MovieHeaderIsRead()
    {
        byte[] file = Concat(Ftyp(), Box("moov", Mvhd(1000, 7000, version: 1)));

        Assert.True(TryReadDuration(file, out long durationMs));
        Assert.Equal(7000, durationMs);
    }

    [Fact]
    public void AMvhdAfterOtherChildrenIsFound()
    {
        // mvhd は moov の最初の子であるのが普通だが、位置には依存しない。
        byte[] file = Concat(Ftyp(), Box("moov", Box("udta", new byte[16]), Mvhd(1000, 500)));

        Assert.True(TryReadDuration(file, out long durationMs));
        Assert.Equal(500, durationMs);
    }

    [Fact]
    public void AFragmentedMovieHasNoDuration()
    {
        // fragmented は mvhd の duration が 0 で、実際の尺は moof の側にある。
        byte[] file = Concat(
            Ftyp(),
            Box("moov", Mvhd(1000, 0), Box("mvex", Box("trex", new byte[8]))),
            Box("moof", new byte[4]));

        Assert.False(TryReadDuration(file, out long durationMs));
        Assert.Equal(0, durationMs);
    }

    [Fact]
    public void ATimescaleOfZeroIsRejected()
    {
        byte[] file = Concat(Ftyp(), Box("moov", Mvhd(0, 2500)));

        Assert.False(TryReadDuration(file, out _));
    }

    [Fact]
    public void AnUnknownDurationIsRejected()
    {
        // version 0 の 0xFFFFFFFF は「不明」の表明。
        byte[] file = Concat(Ftyp(), Box("moov", Mvhd(1000, uint.MaxValue)));

        Assert.False(TryReadDuration(file, out _));
    }

    [Fact]
    public void AVersion1DurationThatOverflowsInMillisecondsIsRejected()
    {
        // version 1 の duration は 64bit。ミリ秒へ直す掛け算は ulong で巻くので、
        // 巻いた結果（18446744073709552 * 1000 → 384）は long.MaxValue の検査を
        // すり抜ける ── 掛ける前に弾けていないと 384ms として通ってしまう。
        byte[] file = Concat(Ftyp(), Box("moov", Mvhd(1, 18_446_744_073_709_552, version: 1)));

        Assert.False(TryReadDuration(file, out _));
    }

    [Fact]
    public void ATruncatedMoovHasNoDuration()
    {
        byte[] whole = Concat(Ftyp(), Box("moov", Mvhd(1000, 2500)));

        Assert.False(TryReadDuration(whole[..(whole.Length - 8)], out _));
    }

    [Fact]
    public void AZeroSizedBoxEndsTheScan()
    {
        // size=0 は「以後ファイル末尾まで」なので、その後ろに兄弟は無い。
        byte[] mdat = new byte[8 + 32];
        Encoding.ASCII.GetBytes("mdat").CopyTo(mdat, 4);

        Assert.False(TryReadDuration(Concat(Ftyp(), mdat, Box("moov", Mvhd(1000, 2500))), out _));
    }

    [Fact]
    public void SomethingThatIsNotAnMp4HasNoDuration()
    {
        Assert.False(TryReadDuration(Encoding.ASCII.GetBytes("not an mp4 at all"), out _));
        Assert.False(TryReadDuration([], out _));
    }

    [Fact]
    public void AMoovWithoutAMvhdHasNoDuration()
    {
        byte[] file = Concat(Ftyp(), Box("moov", Box("trak", new byte[16])));

        Assert.False(TryReadDuration(file, out _));
    }
}
