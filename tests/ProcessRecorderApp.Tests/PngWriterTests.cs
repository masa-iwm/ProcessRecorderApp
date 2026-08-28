using ProcessRecorderApp.Components;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 自前の PNG 符号化器（<see cref="PngWriter"/>）。
///
/// <para>
/// <b>読み返しは独立した復号器で行う。</b> 自前で書いて自前で読むと、両側が同じ
/// 勘違いをしていても緑になる ── ここでは <c>System.Drawing</c>（GDI+）に読ませて
/// 画素を突き合わせ、加えて chunk の構造と CRC を手で辿る。
/// </para>
/// </summary>
public sealed class PngWriterTests
{
    /// <summary>PNG の署名。</summary>
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>行ごとに違う値が並ぶ画像（フィルタが効く形）。</summary>
    private static byte[] Gradient(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 3;
                rgb[at] = (byte)(x * 17);
                rgb[at + 1] = (byte)(y * 29);
                rgb[at + 2] = (byte)((x * y) + 7);
            }
        }

        return rgb;
    }

    private static byte[] Encode(int width, int height, byte[] rgb)
    {
        using var stream = new MemoryStream();
        PngWriter.Write(stream, width, height, rgb);
        return stream.ToArray();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(1, 3)]
    [InlineData(7, 5)]
    [InlineData(320, 180)]
    public void EveryPixelSurvivesTheRoundTripThroughAnIndependentDecoder(int width, int height)
    {
        byte[] rgb = Gradient(width, height);

        using var stream = new MemoryStream(Encode(width, height, rgb));
        using var bitmap = new Bitmap(stream);

        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                int at = ((y * width) + x) * 3;

                Assert.Equal((rgb[at], rgb[at + 1], rgb[at + 2]), (pixel.R, pixel.G, pixel.B));
            }
        }
    }

    /// <summary>
    /// 構造は仕様どおりか ── 署名・<c>IHDR</c> の値・chunk ごとの CRC・
    /// <c>IDAT</c> がちょうど 1 つ・末尾が空の <c>IEND</c>。
    /// </summary>
    [Fact]
    public void TheFileIsASignatureThenIhdrThenOneIdatThenIend()
    {
        byte[] png = Encode(7, 5, Gradient(7, 5));

        Assert.Equal(Signature, png[..8]);

        List<Chunk> chunks = ReadChunks(png);
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, chunks.ConvertAll(c => c.Type));

        Chunk header = chunks[0];
        Assert.Equal(13, header.Data.Length);
        Assert.Equal(7, BinaryPrimitives.ReadInt32BigEndian(header.Data.AsSpan(0, 4)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32BigEndian(header.Data.AsSpan(4, 4)));
        Assert.Equal(8, header.Data[8]);  // bit depth
        Assert.Equal(2, header.Data[9]);  // color type = truecolor
        Assert.Equal(0, header.Data[10]); // compression
        Assert.Equal(0, header.Data[11]); // filter
        Assert.Equal(0, header.Data[12]); // interlace

        Assert.Empty(chunks[2].Data);
    }

    /// <summary>
    /// <b>各 scanline のフィルタ種別は Paeth（4）である。</b> 復号器は 5 種類すべてを
    /// 受けるので、種別を取り違えたまま書いても読み返しは一致してしまう
    /// ── 出す側が何を宣言しているかは展開して直接見る。
    /// </summary>
    [Fact]
    public void EveryScanlineDeclaresThePaethFilter()
    {
        const int width = 7;
        const int height = 5;

        List<Chunk> chunks = ReadChunks(Encode(width, height, Gradient(width, height)));
        Chunk data = Assert.Single(chunks.FindAll(c => c.Type == "IDAT"));

        using var compressed = new MemoryStream(data.Data);
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        byte[] scanlines = raw.ToArray();

        // 長さを先に見る ── 途中で切れた展開でも、拾えた行だけなら 4 が並びうる。
        int rowBytes = 1 + (width * 3);
        Assert.Equal(height * rowBytes, scanlines.Length);

        for (int y = 0; y < height; y++)
            Assert.Equal(4, scanlines[y * rowBytes]);
    }

    /// <summary>長さが合わない画素列は受け取らない（黙って壊れた PNG を書かない）。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(-3)]
    public void APixelBufferOfTheWrongLengthIsRejected(int delta)
    {
        int correct = 7 * 5 * 3;
        var rgb = new byte[correct + delta];

        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => PngWriter.Write(stream, 7, 5, rgb));
    }

    private sealed record Chunk(string Type, byte[] Data);

    /// <summary>chunk を辿りながら CRC を検算する。</summary>
    private static List<Chunk> ReadChunks(byte[] png)
    {
        var chunks = new List<Chunk>();

        int at = 8;
        while (at < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at, 4));
            string type = Encoding.ASCII.GetString(png, at + 4, 4);
            byte[] data = png[(at + 8)..(at + 8 + length)];

            uint stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length, 4));
            uint computed = Crc32(png.AsSpan(at + 4, 4 + length));
            Assert.Equal(computed, stored);

            chunks.Add(new Chunk(type, data));
            at += 12 + length;
        }

        Assert.Equal(png.Length, at);
        return chunks;
    }

    /// <summary>PNG の CRC32（多項式 0xEDB88320）を素朴に計算する。</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
