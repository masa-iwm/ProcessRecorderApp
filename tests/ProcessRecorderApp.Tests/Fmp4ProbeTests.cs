using ProcessRecorderApp.Components;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
}
