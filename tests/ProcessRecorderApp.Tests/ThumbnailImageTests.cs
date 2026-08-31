using ProcessRecorderApp.Components;
using ProcessRecorderApp.GStreamer;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 生フレーム → RGB のボックス平均縮小（<see cref="ThumbnailImage.TryCreate"/>）。
///
/// <para>
/// <b>期待色は変換式から作らない。</b> BT.709 limited range で黒 <c>(16,128,128)</c>・
/// 白 <c>(235,128,128)</c>・赤 <c>(63,102,240)</c> は、式を写さなくても RGB が判る
/// 3 点なので、これで縮小と色変換の両方を見る（許容は ±2）。
/// </para>
/// <para>
/// <b>合成フレームは既定レイアウトで作る。</b> stride のパディングには別の値を詰めて、
/// 実装がそこを画素として読んでいないことを確かめる。
/// </para>
/// <para>
/// <b>平面ごとの stride / offset を渡す多重定義は、それ用の合成フレームで見る</b>
/// （<c>*WithStride</c> / <c>*WithLayout</c>）。パディングを本当に踏んでいることは、
/// 同じバイト列を既定レイアウトで読んだときに絵が壊れることで固定する。
/// </para>
/// </summary>
public sealed class ThumbnailImageTests
{
    /// <summary>比較の許容（整数・float の丸めの差）。</summary>
    private const int Tolerance = 2;

    private static readonly (byte Y, byte U, byte V) YuvBlack = (16, 128, 128);
    private static readonly (byte Y, byte U, byte V) YuvWhite = (235, 128, 128);
    private static readonly (byte Y, byte U, byte V) YuvRed = (63, 102, 240);

    private static int RoundUp4(int value) => (value + 3) & ~3;

    private static int RoundUp2(int value) => (value + 1) & ~1;

    private static void AssertPixel(ThumbnailImage image, int x, int y, byte r, byte g, byte b)
    {
        int at = (((y * image.Width) + x) * 3);
        AssertNear(r, image.Rgb24[at], "R", x, y);
        AssertNear(g, image.Rgb24[at + 1], "G", x, y);
        AssertNear(b, image.Rgb24[at + 2], "B", x, y);
    }

    /// <summary>
    /// その画素が期待色**では無い**こと（stride を取り違えた読みが、絵として成り立たない
    /// ことを固定する側）。3 チャンネルのどれか 1 つでも許容の外にあればよい。
    /// </summary>
    private static void AssertNotPixel(ThumbnailImage image, int x, int y, byte r, byte g, byte b)
    {
        int at = (((y * image.Width) + x) * 3);
        byte actualR = image.Rgb24[at];
        byte actualG = image.Rgb24[at + 1];
        byte actualB = image.Rgb24[at + 2];

        Assert.True(
            Math.Abs(r - actualR) > Tolerance
            || Math.Abs(g - actualG) > Tolerance
            || Math.Abs(b - actualB) > Tolerance,
            $"({x},{y}) は {r},{g},{b} 以外のはずが {actualR},{actualG},{actualB} だった。");
    }

    private static void AssertNear(byte expected, byte actual, string channel, int x, int y)
    {
        Assert.True(Math.Abs(expected - actual) <= Tolerance,
            $"({x},{y}) の {channel} は {expected}±{Tolerance} のはずが {actual} だった。");
    }

    // ---- 合成フレーム ----

    /// <summary>4 バイト系（B,G,R,A の並び）。stride = width×4。</summary>
    private static byte[] Bgra(int width, int height, Func<int, int, (byte R, byte G, byte B)> color)
    {
        var frame = new byte[width * 4 * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = color(x, y);
                int at = (y * width * 4) + (x * 4);
                frame[at] = b;
                frame[at + 1] = g;
                frame[at + 2] = r;
                frame[at + 3] = 255;
            }
        }

        return frame;
    }

    /// <summary>3 バイト系（R,G,B の並び）。stride = RU4(width×3)、余りは 0xA5 で埋める。</summary>
    private static byte[] Rgb(int width, int height, Func<int, int, (byte R, byte G, byte B)> color)
    {
        int stride = RoundUp4(width * 3);
        var frame = new byte[stride * height];
        Array.Fill(frame, (byte)0xA5);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = color(x, y);
                int at = (y * stride) + (x * 3);
                frame[at] = r;
                frame[at + 1] = g;
                frame[at + 2] = b;
            }
        }

        return frame;
    }

    /// <summary>YUY2 / UYVY。stride = RU4(width×2)、クロマは 2 画素で共有。</summary>
    private static byte[] Packed422(
        int width, int height, bool yFirst, Func<int, int, (byte Y, byte U, byte V)> color)
    {
        int stride = RoundUp4(width * 2);
        var frame = new byte[stride * height];
        Array.Fill(frame, (byte)0xA5);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte luma, byte u, byte v) = color(x, y);
                int row = y * stride;
                frame[row + (x * 2) + (yFirst ? 0 : 1)] = luma;

                if (x % 2 != 0)
                    continue;

                int pair = row + ((x / 2) * 4);
                frame[pair + (yFirst ? 1 : 0)] = u;
                frame[pair + (yFirst ? 3 : 2)] = v;
            }
        }

        return frame;
    }

    /// <summary>NV12 / NV21。Y 平面の直後にクロマが交互で続く。</summary>
    private static byte[] SemiPlanar420(
        int width, int height, bool uFirst, Func<int, int, (byte Y, byte U, byte V)> color)
    {
        int stride = RoundUp4(width);
        int lumaBytes = stride * RoundUp2(height);
        var frame = new byte[lumaBytes + (stride * (RoundUp2(height) / 2))];
        Array.Fill(frame, (byte)0xA5);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte luma, byte u, byte v) = color(x, y);
                frame[(y * stride) + x] = luma;

                if (x % 2 != 0 || y % 2 != 0)
                    continue;

                int at = lumaBytes + ((y / 2) * stride) + ((x / 2) * 2);
                frame[at + (uFirst ? 0 : 1)] = u;
                frame[at + (uFirst ? 1 : 0)] = v;
            }
        }

        return frame;
    }

    /// <summary>I420 / YV12。Y の後ろに 2 つのクロマ平面（I420 は U→V）。</summary>
    private static byte[] Planar420(
        int width, int height, bool uFirst, Func<int, int, (byte Y, byte U, byte V)> color)
    {
        int stride = RoundUp4(width);
        int lumaBytes = stride * RoundUp2(height);
        int chromaStride = RoundUp4(RoundUp2(width) / 2);
        int chromaBytes = chromaStride * (RoundUp2(height) / 2);

        var frame = new byte[lumaBytes + (chromaBytes * 2)];
        Array.Fill(frame, (byte)0xA5);

        int uPlane = uFirst ? lumaBytes : lumaBytes + chromaBytes;
        int vPlane = uFirst ? lumaBytes + chromaBytes : lumaBytes;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte luma, byte u, byte v) = color(x, y);
                frame[(y * stride) + x] = luma;

                if (x % 2 != 0 || y % 2 != 0)
                    continue;

                int at = ((y / 2) * chromaStride) + (x / 2);
                frame[uPlane + at] = u;
                frame[vPlane + at] = v;
            }
        }

        return frame;
    }

    /// <summary>左半分が赤・右半分が白（4:2:0 の境界に合う位置で割る）。</summary>
    private static (byte Y, byte U, byte V) RedThenWhite(int x, int width)
        => x < width / 2 ? YuvRed : YuvWhite;

    // ---- RGB 系 ----

    /// <summary>
    /// 既知色の領域が、縮小後もその色で残ること（ボックス平均なので境界をまたがない限り不変）。
    /// </summary>
    [Fact]
    public void BgraShrinksEachRegionToItsOwnColour()
    {
        byte[] frame = Bgra(8, 4, (x, _) => x < 4 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255));

        Assert.True(ThumbnailImage.TryCreate(frame, "BGRA", 8, 4, 2, out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 1, 0, 0, 0, 255);
    }

    /// <summary>
    /// <b>混じったブロックは平均になる</b> ── 一様色だけで見ていると、最近傍で
    /// 1 画素を拾うだけの実装でも同じ色が出て通ってしまう。0 と 255 を 1 つの出力画素へ
    /// 畳んで、値が端ではなく中間に来ることを固定する。
    /// </summary>
    [Fact]
    public void ABlockOfTwoColoursBecomesTheirAverage()
    {
        byte[] frame = Bgra(2, 1, (x, _) => x == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));

        Assert.True(ThumbnailImage.TryCreate(frame, "BGRA", 2, 1, 1, out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);

        // 最近傍なら 0 か 255 のどちらかになる。
        AssertPixel(image, 0, 0, 127, 127, 127);
    }

    /// <summary>4 バイト系はどの並びでも同じ色になる。</summary>
    [Theory]
    [InlineData("BGRA", 2, 1, 0)]
    [InlineData("BGRx", 2, 1, 0)]
    [InlineData("RGBA", 0, 1, 2)]
    [InlineData("RGBx", 0, 1, 2)]
    [InlineData("ARGB", 1, 2, 3)]
    [InlineData("xRGB", 1, 2, 3)]
    [InlineData("ABGR", 3, 2, 1)]
    [InlineData("xBGR", 3, 2, 1)]
    public void EveryFourByteOrderIsReadAtItsOwnOffsets(string format, int r, int g, int b)
    {
        var frame = new byte[2 * 4 * 2];
        for (int pixel = 0; pixel < 4; pixel++)
        {
            frame[(pixel * 4) + r] = 10;
            frame[(pixel * 4) + g] = 120;
            frame[(pixel * 4) + b] = 240;
        }

        Assert.True(ThumbnailImage.TryCreate(frame, format, 2, 2, 320, out ThumbnailImage? image));
        Assert.NotNull(image);
        AssertPixel(image, 0, 0, 10, 120, 240);
        AssertPixel(image, 1, 1, 10, 120, 240);
    }

    /// <summary>
    /// 幅が 4 の倍数でない 3 バイト系。<b>stride は RU4(width×3)</b> なので、
    /// 行末のパディングを画素として読むと色がずれる。
    /// </summary>
    [Fact]
    public void AnOddWidthRgbFrameUsesTheRoundedUpStride()
    {
        byte[] frame = Rgb(5, 3, (x, y) => ((byte)(x * 10), (byte)(y * 20), (byte)200));

        Assert.True(ThumbnailImage.TryCreate(frame, "RGB", 5, 3, 320, out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(5, image.Width);
        Assert.Equal(3, image.Height);

        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
                AssertPixel(image, x, y, (byte)(x * 10), (byte)(y * 20), 200);
        }
    }

    /// <summary>BGR は 3 バイト系の並び違い。</summary>
    [Fact]
    public void BgrIsReadInReverseOrder()
    {
        byte[] frame = Rgb(2, 2, (_, _) => ((byte)240, (byte)120, (byte)10));

        Assert.True(ThumbnailImage.TryCreate(frame, "BGR", 2, 2, 320, out ThumbnailImage? image));
        Assert.NotNull(image);

        // Rgb() は R,G,B の順に詰めるので、BGR として読むと R と B が入れ替わる。
        AssertPixel(image, 0, 0, 10, 120, 240);
    }

    // ---- YUV 系 ----

    [Theory]
    [InlineData("YUY2")]
    [InlineData("UYVY")]
    public void PackedYuvSharesChromaAcrossThePair(string format)
    {
        byte[] frame = Packed422(4, 2, yFirst: format == "YUY2", (x, _) => RedThenWhite(x, 4));

        Assert.True(ThumbnailImage.TryCreate(frame, format, 4, 2, 2, out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 1, 0, 255, 255, 255);
    }

    [Theory]
    [InlineData("NV12")]
    [InlineData("NV21")]
    public void SemiPlanarYuvReadsTheInterleavedChromaPlane(string format)
    {
        byte[] frame = SemiPlanar420(4, 4, uFirst: format == "NV12", (x, _) => RedThenWhite(x, 4));

        Assert.True(ThumbnailImage.TryCreate(frame, format, 4, 4, 2, out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 1, 0, 255, 255, 255);
        AssertPixel(image, 0, 1, 255, 0, 0);
        AssertPixel(image, 1, 1, 255, 255, 255);
    }

    [Theory]
    [InlineData("I420")]
    [InlineData("YV12")]
    public void PlanarYuvReadsTheTwoChromaPlanesInTheRightOrder(string format)
    {
        byte[] frame = Planar420(4, 4, uFirst: format == "I420", (x, _) => RedThenWhite(x, 4));

        Assert.True(ThumbnailImage.TryCreate(frame, format, 4, 4, 2, out ThumbnailImage? image));
        Assert.NotNull(image);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 1, 0, 255, 255, 255);
    }

    /// <summary>黒と白は変換式を写さなくても値が判る 2 点（limited range の両端）。</summary>
    [Fact]
    public void TheLimitedRangeEndpointsBecomeBlackAndWhite()
    {
        byte[] black = Planar420(2, 2, uFirst: true, (_, _) => YuvBlack);
        byte[] white = Planar420(2, 2, uFirst: true, (_, _) => YuvWhite);

        Assert.True(ThumbnailImage.TryCreate(black, "I420", 2, 2, 320, out ThumbnailImage? dark));
        Assert.True(ThumbnailImage.TryCreate(white, "I420", 2, 2, 320, out ThumbnailImage? bright));
        Assert.NotNull(dark);
        Assert.NotNull(bright);

        AssertPixel(dark, 0, 0, 0, 0, 0);
        AssertPixel(bright, 0, 0, 255, 255, 255);
    }

    /// <summary>
    /// <b>Y も平均してから変換する</b>（RGB 系と同じく、最近傍では通らない形で見る）。
    /// 黒 <c>Y=16</c> と白 <c>Y=235</c> を 1 画素へ畳むと平均は 125.5 ──
    /// limited range の伸長で <c>1.164×(125.5−16) ≒ 127</c> の灰色になる。
    /// クロマはどちらも 128 なので無彩色のまま。
    /// </summary>
    [Fact]
    public void PlanarLumaIsAveragedBeforeTheConversion()
    {
        byte[] frame = Planar420(2, 2, uFirst: true, (x, _) => x == 0 ? YuvBlack : YuvWhite);

        Assert.True(ThumbnailImage.TryCreate(frame, "I420", 2, 2, 1, out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);

        // 最近傍なら 0 か 255 のどちらかになる。
        AssertPixel(image, 0, 0, 127, 127, 127);
    }

    /// <summary>幅も高さも奇数の 4:2:0（平面の高さ・クロマ幅の切り上げが効く）。</summary>
    [Fact]
    public void AnOddSizedPlanarFrameIsAccepted()
    {
        byte[] frame = Planar420(5, 3, uFirst: true, (_, _) => YuvWhite);

        Assert.True(ThumbnailImage.TryCreate(frame, "I420", 5, 3, 320, out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(5, image.Width);
        Assert.Equal(3, image.Height);
        AssertPixel(image, 4, 2, 255, 255, 255);
    }

    // ---- 平面ごとの stride / offset ----

    /// <summary>4 バイト系を任意の stride / offset で詰める（隙間と行末の余りは 0xA5）。</summary>
    private static byte[] BgraWithLayout(
        int width, int height, int stride, int offset,
        Func<int, int, (byte R, byte G, byte B)> color)
    {
        var frame = new byte[offset + (stride * height)];
        Array.Fill(frame, (byte)0xA5);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = color(x, y);
                int at = offset + (y * stride) + (x * 4);
                frame[at] = b;
                frame[at + 1] = g;
                frame[at + 2] = r;
                frame[at + 3] = 255;
            }
        }

        return frame;
    }

    /// <summary>3 バイト系を任意の stride / offset で詰める（隙間は 0xA5）。</summary>
    private static byte[] RgbWithLayout(
        int width, int height, int stride, int offset,
        Func<int, int, (byte R, byte G, byte B)> color)
    {
        var frame = new byte[offset + (stride * height)];
        Array.Fill(frame, (byte)0xA5);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = color(x, y);
                int at = offset + (y * stride) + (x * 3);
                frame[at] = r;
                frame[at + 1] = g;
                frame[at + 2] = b;
            }
        }

        return frame;
    }

    /// <summary>YUY2 / UYVY を任意の stride / offset で詰める（隙間は 0xA5）。</summary>
    private static byte[] Packed422WithLayout(
        int width, int height, bool yFirst, int stride, int offset,
        Func<int, int, (byte Y, byte U, byte V)> color)
    {
        var frame = new byte[offset + (stride * height)];
        Array.Fill(frame, (byte)0xA5);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte luma, byte u, byte v) = color(x, y);
                int row = offset + (y * stride);
                frame[row + (x * 2) + (yFirst ? 0 : 1)] = luma;

                if (x % 2 != 0)
                    continue;

                int pair = row + ((x / 2) * 4);
                frame[pair + (yFirst ? 1 : 0)] = u;
                frame[pair + (yFirst ? 3 : 2)] = v;
            }
        }

        return frame;
    }

    /// <summary>NV12 / NV21 を任意の stride / offset で詰める（隙間は 0xA5）。</summary>
    private static byte[] SemiPlanar420WithLayout(
        int width, int height, bool uFirst, int lumaStride, int chromaStride,
        int lumaOffset, int chromaOffset, Func<int, int, (byte Y, byte U, byte V)> color)
    {
        int chromaRows = RoundUp2(height) / 2;
        var frame = new byte[Math.Max(
            lumaOffset + (lumaStride * height),
            chromaOffset + (chromaStride * chromaRows))];
        Array.Fill(frame, (byte)0xA5);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte luma, byte u, byte v) = color(x, y);
                frame[lumaOffset + (y * lumaStride) + x] = luma;

                if (x % 2 != 0 || y % 2 != 0)
                    continue;

                int at = chromaOffset + ((y / 2) * chromaStride) + ((x / 2) * 2);
                frame[at + (uFirst ? 0 : 1)] = u;
                frame[at + (uFirst ? 1 : 0)] = v;
            }
        }

        return frame;
    }

    /// <summary>I420 / YV12 を任意の stride / offset で詰める（隙間は 0xA5）。</summary>
    private static byte[] Planar420WithLayout(
        int width, int height, int lumaStride, int uStride, int vStride,
        int lumaOffset, int uOffset, int vOffset, Func<int, int, (byte Y, byte U, byte V)> color)
    {
        int chromaRows = RoundUp2(height) / 2;
        var frame = new byte[Math.Max(
            lumaOffset + (lumaStride * height),
            Math.Max(
                uOffset + (uStride * chromaRows),
                vOffset + (vStride * chromaRows)))];
        Array.Fill(frame, (byte)0xA5);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte luma, byte u, byte v) = color(x, y);
                frame[lumaOffset + (y * lumaStride) + x] = luma;

                if (x % 2 != 0 || y % 2 != 0)
                    continue;

                frame[uOffset + ((y / 2) * uStride) + (x / 2)] = u;
                frame[vOffset + ((y / 2) * vStride) + (x / 2)] = v;
            }
        }

        return frame;
    }

    /// <summary>
    /// <b>stride にパディングのある 4 バイト系。</b> 幅 96（既定なら 384 バイト）を
    /// stride 512 で詰めたものを、渡したレイアウトどおりに読むこと。
    /// </summary>
    [Fact]
    public void APaddedFourByteFrameIsReadWithTheGivenStride()
    {
        byte[] frame = BgraWithLayout(96, 8, 512, 0,
            (x, _) => x < 48 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "BGRA", 96, 8, 320, [512], [0], out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(96, image.Width);
        Assert.Equal(8, image.Height);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 0, 7, 255, 0, 0);
        AssertPixel(image, 95, 0, 0, 0, 255);
        AssertPixel(image, 95, 7, 0, 0, 255);
    }

    /// <summary>
    /// <b>同じバイト列を既定レイアウトで読むと絵にならない</b> ── パディングを
    /// 本当に踏んでいることを固定する（踏んでいなければ上のテストは無検査になる）。
    /// 2 行目の先頭は、既定の stride 384 では 1 行目の行末のパディング（0xA5）に当たる。
    /// </summary>
    [Fact]
    public void TheDefaultLayoutReadsThePaddingOfAPaddedFourByteFrame()
    {
        byte[] frame = BgraWithLayout(96, 8, 512, 0,
            (x, _) => x < 48 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255));

        Assert.True(ThumbnailImage.TryCreate(frame, "BGRA", 96, 8, 320, out ThumbnailImage? image));
        Assert.NotNull(image);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 0, 1, 0xA5, 0xA5, 0xA5);
    }

    /// <summary>
    /// <b>フレームの先頭が buffer の先頭とは限らない。</b> offset を無視すると、
    /// 頭の 1024 バイト（0xA5 で埋めてある）を画素として読むことになる。
    /// </summary>
    [Fact]
    public void AFourByteFrameIsReadFromTheGivenOffset()
    {
        byte[] frame = BgraWithLayout(96, 8, 512, 1024,
            (x, _) => x < 48 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "BGRA", 96, 8, 320, [512], [1024], out ThumbnailImage? image));
        Assert.NotNull(image);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 0, 7, 255, 0, 0);
        AssertPixel(image, 95, 0, 0, 0, 255);
        AssertPixel(image, 95, 7, 0, 0, 255);
    }

    /// <summary>
    /// <b>3 バイト系のパディングと offset。</b> 既定は stride 16・offset 0 なので、
    /// どちらを無視しても 0xA5 の埋め草か隣の行を読むことになる。
    /// </summary>
    [Fact]
    public void APaddedThreeByteFrameIsReadWithTheGivenStrideAndOffset()
    {
        byte[] frame = RgbWithLayout(5, 3, 64, 32, (x, y) => ((byte)(x * 10), (byte)(y * 20), (byte)200));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "RGB", 5, 3, 320, [64], [32], out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(5, image.Width);
        Assert.Equal(3, image.Height);

        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
                AssertPixel(image, x, y, (byte)(x * 10), (byte)(y * 20), 200);
        }
    }

    /// <summary>
    /// <b>packed YUV のパディングと offset。</b> 既定は stride 16・offset 0。
    /// </summary>
    [Fact]
    public void APaddedPackedYuvFrameIsReadWithTheGivenStrideAndOffset()
    {
        byte[] frame = Packed422WithLayout(8, 4, yFirst: true, 40, 100, (x, _) => RedThenWhite(x, 8));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "YUY2", 8, 4, 320, [40], [100], out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(8, image.Width);
        Assert.Equal(4, image.Height);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 3, 3, 255, 0, 0);
        AssertPixel(image, 4, 0, 255, 255, 255);
        AssertPixel(image, 7, 3, 255, 255, 255);
    }

    /// <summary>
    /// <b>平面ごとに stride も offset も違う 4:2:0 planar。</b> Y の直後にクロマが
    /// 続かない（平面のあいだに隙間がある）並びでも、渡したとおりに読むこと。
    /// </summary>
    [Fact]
    public void APaddedPlanarFrameIsReadWithTheGivenStridesAndOffsets()
    {
        byte[] frame = Planar420WithLayout(
            8, 8, lumaStride: 16, uStride: 8, vStride: 8, lumaOffset: 0, uOffset: 200, vOffset: 300,
            (x, _) => RedThenWhite(x, 8));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "I420", 8, 8, 320, [16, 8, 8], [0, 200, 300], out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 3, 7, 255, 0, 0);
        AssertPixel(image, 4, 0, 255, 255, 255);
        AssertPixel(image, 7, 7, 255, 255, 255);
    }

    /// <summary>
    /// <b>U と V で stride が違う 4:2:0 planar。</b> V の平面だけパディングが広い並びでも、
    /// 平面ごとの stride で読むこと（片方の stride をもう片方に流用しない）。
    /// </summary>
    [Fact]
    public void APlanarFrameWithUnequalChromaStridesIsReadWithEachPlanesStride()
    {
        byte[] frame = Planar420WithLayout(
            8, 8, lumaStride: 16, uStride: 8, vStride: 12, lumaOffset: 0, uOffset: 200, vOffset: 300,
            (x, _) => RedThenWhite(x, 8));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "I420", 8, 8, 320, [16, 8, 12], [0, 200, 300], out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 3, 7, 255, 0, 0);
        AssertPixel(image, 4, 0, 255, 255, 255);
        AssertPixel(image, 7, 7, 255, 255, 255);
    }

    /// <summary>
    /// <b>U の stride を V に流用すると V がパディングに当たる</b> ── 上のテストが
    /// 「U と V の stride を取り違えても通る」無検査にならないことを固定する。
    /// V を stride 8 で読むと クロマ 2 行目の先頭は 300+8=308 で、V の 1 行目の行末
    /// パディング（304..311 = 0xA5。実際の 2 行目は 312）に当たる。
    /// </summary>
    [Fact]
    public void ReusingTheUStrideForVReadsThePaddingOfAPlanarFrame()
    {
        byte[] frame = Planar420WithLayout(
            8, 8, lumaStride: 16, uStride: 8, vStride: 12, lumaOffset: 0, uOffset: 200, vOffset: 300,
            (x, _) => RedThenWhite(x, 8));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "I420", 8, 8, 320, [16, 8, 8], [0, 200, 300], out ThumbnailImage? image));
        Assert.NotNull(image);

        // クロマの 1 行目（y=0,1）はどちらの stride でも同じ位置なので、崩れるのは y≥2。
        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertNotPixel(image, 0, 2, 255, 0, 0);
    }

    /// <summary>
    /// <b>YV12 は平面 1 が V・平面 2 が U</b> ── offset を入れ替えて渡しても、
    /// U と V が入れ替わらないこと。
    /// </summary>
    [Fact]
    public void APaddedYv12FrameReadsItsChromaPlanesInTheRightOrder()
    {
        byte[] frame = Planar420WithLayout(
            8, 8, lumaStride: 16, uStride: 8, vStride: 8, lumaOffset: 0, uOffset: 300, vOffset: 200,
            (x, _) => RedThenWhite(x, 8));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "YV12", 8, 8, 320, [16, 8, 8], [0, 200, 300], out ThumbnailImage? image));
        Assert.NotNull(image);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 7, 7, 255, 255, 255);
    }

    /// <summary>
    /// <b>平面ごとに stride も offset も違う 4:2:0 semi-planar。</b>
    /// </summary>
    [Fact]
    public void APaddedSemiPlanarFrameIsReadWithTheGivenStridesAndOffsets()
    {
        byte[] frame = SemiPlanar420WithLayout(
            8, 8, uFirst: true, lumaStride: 20, chromaStride: 20, lumaOffset: 0, chromaOffset: 200,
            (x, _) => RedThenWhite(x, 8));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "NV12", 8, 8, 320, [20, 20], [0, 200], out ThumbnailImage? image));
        Assert.NotNull(image);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 3, 7, 255, 0, 0);
        AssertPixel(image, 4, 0, 255, 255, 255);
        AssertPixel(image, 7, 7, 255, 255, 255);
    }

    /// <summary>
    /// <b>輝度とクロマで stride が違う 4:2:0 semi-planar。</b> クロマの平面だけ
    /// パディングが広い並びでも、平面ごとの stride で読むこと
    /// （平面 0 の stride をクロマに流用しない）。
    /// </summary>
    [Fact]
    public void ASemiPlanarFrameWithAWiderChromaStrideIsReadWithEachPlanesStride()
    {
        byte[] frame = SemiPlanar420WithLayout(
            8, 8, uFirst: true, lumaStride: 20, chromaStride: 24, lumaOffset: 0, chromaOffset: 200,
            (x, _) => RedThenWhite(x, 8));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "NV12", 8, 8, 320, [20, 24], [0, 200], out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 3, 7, 255, 0, 0);
        AssertPixel(image, 4, 0, 255, 255, 255);
        AssertPixel(image, 7, 7, 255, 255, 255);
    }

    /// <summary>
    /// <b>輝度の stride をクロマに流用するとパディングに当たる</b> ── 上のテストが
    /// 「平面 0 の stride で両方を読んでも通る」無検査にならないことを固定する。
    /// クロマを stride 20 で読むと 2 行目の先頭は 200+20=220 で、クロマ 1 行目の行末
    /// パディング（208..223 = 0xA5。実際の 2 行目は 224）に当たる。
    /// </summary>
    [Fact]
    public void ReusingTheLumaStrideForChromaReadsThePaddingOfASemiPlanarFrame()
    {
        byte[] frame = SemiPlanar420WithLayout(
            8, 8, uFirst: true, lumaStride: 20, chromaStride: 24, lumaOffset: 0, chromaOffset: 200,
            (x, _) => RedThenWhite(x, 8));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "NV12", 8, 8, 320, [20, 20], [0, 200], out ThumbnailImage? image));
        Assert.NotNull(image);

        // クロマの 1 行目（y=0,1）はどちらの stride でも同じ位置なので、崩れるのは y≥2。
        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertNotPixel(image, 0, 2, 255, 0, 0);
    }

    /// <summary>
    /// <b>幅も高さも奇数の 4:2:0 を、既定とは違う stride / offset で。</b>
    /// 既定なら Y は stride 100・offset 0、クロマは stride 52・offset 600 / 756 なので、
    /// stride と offset のどちらを無視しても平面のどこか別の場所を読むことになる。
    /// </summary>
    [Fact]
    public void AnOddSizedPlanarFrameIsReadWithTheGivenStridesAndOffsets()
    {
        byte[] frame = Planar420WithLayout(
            97, 5, lumaStride: 128, uStride: 72, vStride: 72, lumaOffset: 256, uOffset: 1024, vOffset: 2048,
            (x, _) => RedThenWhite(x, 97));

        Assert.True(ThumbnailImage.TryCreate(
            frame, "I420", 97, 5, 320, [128, 72, 72], [256, 1024, 2048], out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(97, image.Width);
        Assert.Equal(5, image.Height);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 47, 4, 255, 0, 0);
        AssertPixel(image, 48, 0, 255, 255, 255);
        AssertPixel(image, 96, 4, 255, 255, 255);
    }

    // ---- 最終行が stride まで埋まっていない buffer ----

    /// <summary>
    /// <b><c>d3d12download</c> が出す NV12 の実レイアウト。</b> 1920x1080・
    /// 平面ごとの stride 2048・クロマの offset 2211840 で、最終行が stride まで
    /// 埋まっていない buffer（実長 3,317,632 ＝ 2211840 ＋ 2048×539 ＋ 1920）を
    /// そのまま撮れること。読み手が触る最大位置＋1 がちょうどこの長さである。
    /// </summary>
    [Fact]
    public void AnNv12FrameWhoseLastRowIsNotPaddedToTheStrideIsAccepted()
    {
        byte[] frame = SemiPlanar420WithLayout(
            1920, 1080, uFirst: true, lumaStride: 2048, chromaStride: 2048,
            lumaOffset: 0, chromaOffset: 2211840, (x, _) => RedThenWhite(x, 1920));

        Assert.True(ThumbnailImage.TryCreate(
            frame.AsSpan(0, 3317632), "NV12", 1920, 1080, 1920, [2048, 2048], [0, 2211840],
            out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(1920, image.Width);
        Assert.Equal(1080, image.Height);

        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 1919, 1079, 255, 255, 255);
    }

    /// <summary>
    /// 上の buffer が 1 バイト短ければ撮らない ── 緩めたのは「最終行の後ろの
    /// パディング」だけで、最終行そのものは全部読めなければならない。
    /// </summary>
    [Fact]
    public void AnNv12FrameOneByteShorterThanItsLastRowIsRejected()
    {
        byte[] frame = SemiPlanar420WithLayout(
            1920, 1080, uFirst: true, lumaStride: 2048, chromaStride: 2048,
            lumaOffset: 0, chromaOffset: 2211840, (x, _) => RedThenWhite(x, 1920));

        Assert.False(ThumbnailImage.TryCreate(
            frame.AsSpan(0, 3317631), "NV12", 1920, 1080, 1920, [2048, 2048], [0, 2211840],
            out ThumbnailImage? image));
        Assert.Null(image);
    }

    /// <summary>
    /// <b>4:2:0 planar の最終行 unpadded。</b> V 平面の最終行の行バイト数まで
    /// （120 ＋ 6×3 ＋ 4 ＝ 142）でちょうど足り、141 では足りないこと。
    /// </summary>
    [Fact]
    public void APlanarFrameThatEndsAtItsLastRowIsAcceptedAndOneByteShorterIsNot()
    {
        byte[] frame = Planar420WithLayout(
            8, 8, lumaStride: 12, uStride: 6, vStride: 6, lumaOffset: 0, uOffset: 96, vOffset: 120,
            (x, _) => RedThenWhite(x, 8));

        Assert.True(ThumbnailImage.TryCreate(
            frame.AsSpan(0, 142), "I420", 8, 8, 320, [12, 6, 6], [0, 96, 120],
            out ThumbnailImage? image));
        Assert.NotNull(image);
        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 7, 7, 255, 255, 255);

        Assert.False(ThumbnailImage.TryCreate(
            frame.AsSpan(0, 141), "I420", 8, 8, 320, [12, 6, 6], [0, 96, 120],
            out ThumbnailImage? tooShort));
        Assert.Null(tooShort);
    }

    /// <summary>
    /// <b>packed（4 バイト系）の最終行 unpadded。</b> 幅 8・stride 40 なら
    /// 40×7 ＋ 32 ＝ 312 でちょうど足り、311 では足りないこと。
    /// </summary>
    [Fact]
    public void AFourByteFrameThatEndsAtItsLastRowIsAcceptedAndOneByteShorterIsNot()
    {
        byte[] frame = BgraWithLayout(
            8, 8, stride: 40, offset: 0,
            (x, _) => x < 4 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255));

        Assert.True(ThumbnailImage.TryCreate(
            frame.AsSpan(0, 312), "BGRA", 8, 8, 320, [40], [0], out ThumbnailImage? image));
        Assert.NotNull(image);
        AssertPixel(image, 0, 0, 255, 0, 0);
        AssertPixel(image, 7, 7, 0, 0, 255);

        Assert.False(ThumbnailImage.TryCreate(
            frame.AsSpan(0, 311), "BGRA", 8, 8, 320, [40], [0], out ThumbnailImage? tooShort));
        Assert.Null(tooShort);
    }

    /// <summary>
    /// 既定レイアウトの多重定義は、同じ値を書き下したレイアウトと同じ絵になる
    /// （既定は「レイアウトを渡さない」だけで、別の経路ではない）。
    /// </summary>
    [Fact]
    public void TheDefaultLayoutAgreesWithTheSameLayoutSpelledOut()
    {
        byte[] frame = Planar420(8, 8, uFirst: true, (x, _) => RedThenWhite(x, 8));

        Assert.True(ThumbnailImage.TryCreate(frame, "I420", 8, 8, 320, out ThumbnailImage? assumed));
        Assert.True(ThumbnailImage.TryCreate(
            frame, "I420", 8, 8, 320, [8, 4, 4], [0, 64, 80], out ThumbnailImage? given));
        Assert.NotNull(assumed);
        Assert.NotNull(given);

        Assert.Equal(assumed.Rgb24, given.Rgb24);
    }

    /// <summary>
    /// <b>読む位置が buffer を出るレイアウトは撮らない。</b> 長さ・stride の下限・
    /// 負の値・平面の数のどれが欠けても false。
    /// </summary>
    [Theory]
    // 最終行が 1 バイト足りない（8 行目の 32 バイトまでで 64×7＋32＝480 要る）。
    [InlineData("BGRA", 479, new[] { 64 }, new[] { 0 })]
    // 1 行の画素（8×4＝32 バイト）に足りない stride。
    [InlineData("BGRA", 4096, new[] { 31 }, new[] { 0 })]
    // 負の stride（下から上へ並ぶ buffer）は扱わない。
    [InlineData("BGRA", 4096, new[] { -32 }, new[] { 0 })]
    // offset が負。
    [InlineData("BGRA", 4096, new[] { 32 }, new[] { -1 })]
    // 平面が足りない（I420 は 3 平面）。
    [InlineData("I420", 4096, new[] { 8, 4 }, new[] { 0, 64 })]
    // クロマ平面が buffer の外へ出る。
    [InlineData("I420", 100, new[] { 8, 4, 4 }, new[] { 0, 64, 4000 })]
    public void ALayoutThatDoesNotFitTheFrameIsRejected(
        string format, int length, int[] strides, int[] offsets)
    {
        var frame = new byte[length];

        Assert.False(ThumbnailImage.TryCreate(
            frame, format, 8, 8, 320, strides, offsets, out ThumbnailImage? image));
        Assert.Null(image);
    }

    /// <summary>
    /// <see cref="ThumbnailImage.PlaneCount"/> は実装が読む平面の数と一致する
    /// ── 1 つ少ないレイアウトは必ず拒まれる（**片方だけ動かすと、呼び出し側が
    /// 埋める数が足りないまま既定レイアウトへ落ちる**）。
    /// </summary>
    [Theory]
    [InlineData("BGRA")]
    [InlineData("RGB")]
    [InlineData("YUY2")]
    [InlineData("NV12")]
    [InlineData("NV21")]
    [InlineData("I420")]
    [InlineData("YV12")]
    public void ThePlaneCountIsTheNumberOfPlanesTheLayoutNeeds(string format)
    {
        int planes = ThumbnailImage.PlaneCount(format);
        Assert.InRange(planes, 1, 3);

        // どの形式でも収まる、十分に広いレイアウト。
        int[] strides = [32, 16, 16];
        int[] offsets = [0, 1024, 2048];
        var frame = new byte[8192];

        Assert.True(ThumbnailImage.TryCreate(
            frame, format, 8, 8, 320, strides.AsSpan(0, planes), offsets.AsSpan(0, planes),
            out ThumbnailImage? image));
        Assert.NotNull(image);

        // 最後の平面だけ壊す ── 実装がそこまで読んでいなければ、これが通ってしまう。
        int[] broken = [.. strides.AsSpan(0, planes)];
        broken[planes - 1] = 0;

        Assert.False(ThumbnailImage.TryCreate(
            frame, format, 8, 8, 320, broken, offsets.AsSpan(0, planes),
            out ThumbnailImage? unreadable));
        Assert.Null(unreadable);

        // 1 つ少ない span も拒む（空の span だけが「既定レイアウト」の合図）。
        if (1 < planes)
        {
            Assert.False(ThumbnailImage.TryCreate(
                frame, format, 8, 8, 320,
                strides.AsSpan(0, planes - 1), offsets.AsSpan(0, planes - 1),
                out ThumbnailImage? missing));
            Assert.Null(missing);
        }
    }

    [Theory]
    [InlineData("GRAY8")]
    [InlineData("Y444")]
    [InlineData("")]
    public void AnUnsupportedFormatHasNoPlanes(string format)
        => Assert.Equal(0, ThumbnailImage.PlaneCount(format));

    /// <summary>
    /// <c>EventRecorder.ThumbnailMaxPlanes</c>（レイアウトを受ける span の長さ）は
    /// <see cref="ThumbnailImage.PlaneCount"/> が返しうる最大（<b>3</b>）と一致する。
    ///
    /// <para>
    /// <b>形式は <c>PlaneCount</c> の switch から取り出す</b> ── 平面のより多い形式を
    /// 足したときに、この検査が自動で当たるようにする。span が足りないと
    /// <c>ReadFrameLayout</c> は実レイアウトを読めないまま既定レイアウトへ落ち、
    /// <b>絵は撮れてしまう</b>（斜行するだけで、ログにも一覧にも異常が出ない）。
    /// </para>
    /// </summary>
    [Fact]
    public void TheLayoutSpansHoldEveryPlaneAFormatCanNeed()
    {
        string source = File.ReadAllText(
            RepositoryFiles.At("src", "Components", "ThumbnailImage.cs"));
        int at = source.IndexOf("public static int PlaneCount(", StringComparison.Ordinal);
        Assert.True(0 <= at, "PlaneCount の switch が見つからない。");

        string body = source[at..source.IndexOf("};", at, StringComparison.Ordinal)];

        // アンダースコア入りの形式名（`A420_10LE` 等）も拾う ── 落とすと、その形式が
        // 黙って列挙から漏れる。
        MatchCollection formats = Regex.Matches(body, "\"([A-Za-z0-9_]+)\"");

        // **件数も断定する。** 部分的にしか拾えていない正規表現は、並びの偶然で
        // 最大が合ってしまい、値の検査だけでは赤にならない。
        Assert.Equal(16, formats.Count);

        int max = formats.Max(m => ThumbnailImage.PlaneCount(m.Groups[1].Value));

        Assert.Equal(3, max);

        FieldInfo? span = typeof(EventRecorder).GetField(
            "ThumbnailMaxPlanes", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(span);
        Assert.Equal(max, (int)span!.GetRawConstantValue()!);
    }

    // ---- 寸法と拒否 ----

    /// <summary>縦横比は維持する（<c>pixel-aspect-ratio</c> は見ない）。</summary>
    [Theory]
    [InlineData(640, 360, 320, 320, 180)]
    [InlineData(1920, 1080, 320, 320, 180)]
    [InlineData(1000, 333, 320, 320, 107)]
    [InlineData(100, 4000, 320, 100, 4000)]
    public void TheOutputKeepsTheAspectRatio(int width, int height, int maxWidth, int outWidth, int outHeight)
    {
        byte[] frame = Bgra(width, height, (_, _) => ((byte)1, (byte)2, (byte)3));

        Assert.True(ThumbnailImage.TryCreate(frame, "BGRA", width, height, maxWidth, out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(outWidth, image.Width);
        Assert.Equal(outHeight, image.Height);
        Assert.Equal(outWidth * outHeight * 3, image.Rgb24.Length);
    }

    /// <summary>上限以下のソースは等倍（拡大はしない）。</summary>
    [Fact]
    public void ASourceNarrowerThanTheLimitIsCopiedAtFullSize()
    {
        byte[] frame = Bgra(100, 50, (x, y) => ((byte)x, (byte)y, (byte)7));

        Assert.True(ThumbnailImage.TryCreate(frame, "BGRA", 100, 50, 320, out ThumbnailImage? image));
        Assert.NotNull(image);
        Assert.Equal(100, image.Width);
        Assert.Equal(50, image.Height);
        AssertPixel(image, 99, 49, 99, 49, 7);
    }

    /// <summary>末尾の余りは無視する（buffer が必要量より大きいことは普通にある）。</summary>
    [Fact]
    public void TrailingPaddingIsIgnored()
    {
        byte[] frame = Bgra(4, 4, (_, _) => ((byte)9, (byte)8, (byte)7));
        var padded = new byte[frame.Length + 4096];
        frame.CopyTo(padded, 0);

        Assert.True(ThumbnailImage.TryCreate(padded, "BGRA", 4, 4, 320, out ThumbnailImage? image));
        Assert.NotNull(image);
        AssertPixel(image, 3, 3, 9, 8, 7);
    }

    /// <summary>足りない buffer は読まない（想定外のレイアウトを黙って絵にしない）。</summary>
    [Theory]
    [InlineData("BGRA")]
    [InlineData("RGB")]
    [InlineData("YUY2")]
    [InlineData("NV12")]
    [InlineData("I420")]
    public void AFrameShorterThanTheDefaultLayoutIsRejected(string format)
    {
        // 8x8 で最も小さい必要量（4:2:0 の 96 バイト）にも届かない長さ。
        var frame = new byte[64];

        Assert.False(ThumbnailImage.TryCreate(frame, format, 8, 8, 320, out ThumbnailImage? image));
        Assert.Null(image);
    }

    [Theory]
    [InlineData("GRAY8")]
    [InlineData("NV16")]
    [InlineData("Y444")]
    [InlineData("bgra")]
    [InlineData("")]
    public void AnUnsupportedFormatIsRejected(string format)
    {
        var frame = new byte[8 * 8 * 4];

        Assert.False(ThumbnailImage.TryCreate(frame, format, 8, 8, 320, out ThumbnailImage? image));
        Assert.Null(image);
    }

    [Theory]
    [InlineData(0, 8, 320)]
    [InlineData(8, 0, 320)]
    [InlineData(-1, 8, 320)]
    [InlineData(8, -1, 320)]
    [InlineData(8, 8, 0)]
    [InlineData(8, 8, -1)]
    public void NonPositiveDimensionsAreRejected(int width, int height, int maxWidth)
    {
        var frame = new byte[8 * 8 * 4];

        Assert.False(ThumbnailImage.TryCreate(frame, "BGRA", width, height, maxWidth, out ThumbnailImage? image));
        Assert.Null(image);
    }
}
