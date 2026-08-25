using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// Init セグメントから MPD へ持ち出す 2 つの値（timescale と codecs）の読み取り。
///
/// <para>
/// <b>箱はここで合成する。</b> 実 GStreamer の Init は同梱 mp4mux が書いた
/// 1 通りしか出てこないので、<c>mdhd</c> version 1・音声が先に並んだ moov・
/// <c>avcC</c> の無い Init といった「読み違えると黙って壊れる」入力は実物では作れない。
/// </para>
/// <para>
/// <b><c>codecs</c> は 2 桁大文字 16 進</b>（<c>avc1.64001F</c> の形）。
/// 桁を落とすと MSE の <c>isTypeSupported</c> が false になり、
/// 再生が始まらないだけで理由はどこにも出ない。
/// </para>
/// </summary>
public sealed class Fmp4InitInfoTests
{
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

    private static byte[] Ftyp()
        => Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isomiso2"));

    /// <summary><c>hdlr</c>: version/flags(4) pre_defined(4) handler_type(4) ＋ 予約と名前。</summary>
    private static byte[] Hdlr(string handlerType)
        => Box("hdlr",
            U32(0),
            U32(0),
            Encoding.ASCII.GetBytes(handlerType),
            U32(0), U32(0), U32(0),
            [0]);

    /// <summary>
    /// <c>mdhd</c>。version 0 は時刻が 32bit、version 1 は 64bit で、
    /// <b>timescale の位置がそれだけずれる</b>。
    /// </summary>
    private static byte[] Mdhd(uint timescale, byte version)
        => version == 0
            ? Box("mdhd", U32(0), U32(0), U32(0), U32(timescale), U32(0), U32(0x55C40000))
            : Box("mdhd",
                U32(0x01000000),
                U32(0), U32(0),                      // creation_time(64)
                U32(0), U32(0),                      // modification_time(64)
                U32(timescale),
                U32(0), U32(0),                      // duration(64)
                U32(0x55C40000));

    /// <summary>
    /// <c>avc1</c>（VisualSampleEntry）。<b>固定部は 78 バイト</b>で、
    /// その後ろに <c>avcC</c> などの子の箱が並ぶ。
    /// </summary>
    private static byte[] Avc1(byte[]? avcc)
    {
        var fixedPart = new byte[78];
        fixedPart[7] = 1;                            // data_reference_index
        return avcc is null
            ? Box("avc1", fixedPart)
            : Box("avc1", fixedPart, avcc);
    }

    /// <summary><c>avcC</c>: configurationVersion(1) profile(1) compatibility(1) level(1) ＋ 残り。</summary>
    private static byte[] AvcC(byte profile, byte compatibility, byte level)
        => Box("avcC", [1, profile, compatibility, level, 0xFF, 0xE0]);

    private static byte[] VideoTrack(uint timescale = 90_000, byte mdhdVersion = 0, byte[]? avcc = null)
        => Track("vide", timescale, mdhdVersion, avcc ?? AvcC(0x64, 0x00, 0x1F));

    private static byte[] Track(string handlerType, uint timescale, byte mdhdVersion, byte[]? avcc)
        => Box("trak",
            Box("mdia",
                Mdhd(timescale, mdhdVersion),
                Hdlr(handlerType),
                Box("minf", Box("stbl", Box("stsd", U32(0), U32(1), Avc1(avcc))))));

    private static byte[] Init(params byte[][] traks)
        => Concat(Ftyp(), Box("moov", [.. new List<byte[]> { Box("mvex", Box("trex", U32(0), U32(1), U32(1), U32(0), U32(0), U32(0))) }.Concat(traks)]));

    // ---- テスト -------------------------------------------------------------

    [Fact]
    public void AVersion0MdhdGivesTheTimescaleAndCodecs()
    {
        Assert.True(Fmp4InitInfo.TryParse(Init(VideoTrack(timescale: 90_000, mdhdVersion: 0)), out var info));

        Assert.Equal(90_000u, info.Timescale);
        Assert.Equal("avc1.64001F", info.Codecs);
    }

    /// <summary>
    /// version 1 の <c>mdhd</c> でも同じ値が読めること。<b>version を見ずに
    /// 決め打ちの位置から読むと、ここで creation_time の一部を timescale にする</b>。
    /// </summary>
    [Fact]
    public void AVersion1MdhdGivesTheSameTimescale()
    {
        Assert.True(Fmp4InitInfo.TryParse(Init(VideoTrack(timescale: 30_000, mdhdVersion: 1)), out var info));

        Assert.Equal(30_000u, info.Timescale);
    }

    /// <summary>
    /// <c>codecs</c> は profile / constraint flags / level を 2 桁大文字 16 進で並べたもの。
    /// </summary>
    [Theory]
    [InlineData(0x64, 0x00, 0x1F, "avc1.64001F")]
    [InlineData(0x4D, 0x40, 0x1F, "avc1.4D401F")]
    [InlineData(0x42, 0xC0, 0x0A, "avc1.42C00A")]
    public void TheCodecsStringIsTwoDigitUppercaseHex(int profile, int compatibility, int level, string expected)
    {
        Assert.True(Fmp4InitInfo.TryParse(
            Init(VideoTrack(avcc: AvcC((byte)profile, (byte)compatibility, (byte)level))), out var info));

        Assert.Equal(expected, info.Codecs);
    }

    /// <summary>
    /// <c>avcC</c> が無ければ false。<b>推測で埋めない</b> ── 間違った
    /// <c>codecs</c> を書いた MPD は、ブラウザが黙って再生しないだけになる。
    /// </summary>
    [Fact]
    public void AnInitWithoutAvcCIsRejected()
    {
        Assert.False(Fmp4InitInfo.TryParse(Init(Track("vide", 90_000, 0, avcc: null)), out var info));
        Assert.Null(info.Codecs);
    }

    /// <summary>
    /// <b>video でないトラックは飛ばす。</b> 先頭の <c>trak</c> をそのまま採ると、
    /// 音声が先に並んだ Init で<b>音声の timescale で映像の時間軸を書く</b>ことになる。
    /// </summary>
    [Fact]
    public void ANonVideoTrackIsSkipped()
    {
        byte[] audio = Track("soun", timescale: 48_000, mdhdVersion: 0, avcc: null);
        byte[] init = Init(audio, VideoTrack(timescale: 90_000));

        Assert.True(Fmp4InitInfo.TryParse(init, out var info));
        Assert.Equal(90_000u, info.Timescale);
        Assert.Equal("avc1.64001F", info.Codecs);
    }

    /// <summary><c>moov</c> が無い（Media だけ）入力は false。</summary>
    [Fact]
    public void AnInitWithoutAMoovIsRejected()
    {
        Assert.False(Fmp4InitInfo.TryParse(Ftyp(), out _));
    }
}
