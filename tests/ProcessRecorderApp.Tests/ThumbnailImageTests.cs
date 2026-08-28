using ProcessRecorderApp.Components;
using System;
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
