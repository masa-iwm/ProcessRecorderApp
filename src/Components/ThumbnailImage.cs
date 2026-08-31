using System;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 生フレーム 1 枚を縮小した RGB 8bit の画像（<see cref="PngWriter"/> へそのまま渡せる形）。
/// </summary>
/// <param name="Width">幅（画素）。</param>
/// <param name="Height">高さ（画素）。</param>
/// <param name="Rgb24">行優先・パディング無しの RGB 画素列（長さは <c>Width×Height×3</c>）。</param>
public sealed record ThumbnailImage(int Width, int Height, byte[] Rgb24)
{
    /// <summary>
    /// この形式が使う平面の数（<b>対応しない形式は 0</b>）。
    /// stride / offset を読む側が、いくつ埋めればよいかを知るための口。
    /// </summary>
    /// <param name="format">caps の <c>format</c> 文字列（<c>NV12</c> 等）。</param>
    /// <returns>平面の数。対応しない形式は 0。</returns>
    public static int PlaneCount(string format) => format switch
    {
        "BGRA" or "BGRx" or "RGBA" or "RGBx" or "ARGB" or "xRGB" or "ABGR" or "xBGR"
            or "RGB" or "BGR" or "YUY2" or "UYVY" => 1,
        "NV12" or "NV21" => 2,
        "I420" or "YV12" => 3,
        _ => 0,
    };

    /// <summary>
    /// 生フレームを RGB へ変換しつつ縮小する（<b>既定レイアウトを仮定する</b>形）。
    ///
    /// <para>
    /// <c>gst_video_info_set_format</c> の既定レイアウト（4 バイト系は <c>width×4</c>、
    /// それ以外は 4 バイト境界へ切り上げ、平面は Y の直後に UV）で読む。
    /// <b>パディングの入った buffer はこの多重定義では正しく読めない</b> ──
    /// 平面ごとの stride / offset が判るなら、それを受ける多重定義へ渡すこと。
    /// </para>
    /// </summary>
    /// <param name="frame">生フレームの先頭。必要量より長い分は末尾のパディングとして無視する。</param>
    /// <param name="format">caps の <c>format</c> 文字列（<c>NV12</c> 等）。</param>
    /// <param name="width">ソースの幅。</param>
    /// <param name="height">ソースの高さ。</param>
    /// <param name="maxWidth">出力幅の上限。ソースがこれ以下なら等倍。</param>
    /// <param name="image">成功したときだけ非 <see langword="null"/>。</param>
    /// <returns>変換できたか。</returns>
    public static bool TryCreate(
        ReadOnlySpan<byte> frame, string format, int width, int height, int maxWidth,
        out ThumbnailImage? image)
        => TryCreate(frame, format, width, height, maxWidth, default, default, out image);

    /// <summary>
    /// 生フレームを RGB へ変換しつつ縮小する。<b>純マネージドで、対応できない入力は
    /// すべて <see langword="false"/> に畳む</b>（呼び出し側はログを 1 行残すだけでよい）。
    ///
    /// <para>
    /// <b>レイアウトは呼び出し側が渡す。</b> <paramref name="strides"/> と
    /// <paramref name="offsets"/> は平面ごとの 1 行のバイト数と先頭からのオフセットで、
    /// 形式が要る平面の数（RGB 系と packed YUV は 1、<c>NV12</c> / <c>NV21</c> は 2、
    /// <c>I420</c> / <c>YV12</c> は 3）だけ要る。<b>両方とも空なら
    /// <c>gst_video_info_set_format</c> の既定レイアウト</b>を使う。
    /// </para>
    /// <para>
    /// <b>読む位置は必ず <paramref name="frame"/> の中に収める。</b> 平面ごとの
    /// <c>offset + stride × 読む行数</c> が長さを超えるもの、stride が 1 行の画素に
    /// 足りないもの、負の値が混じるものは、すべて <see langword="false"/>。
    /// </para>
    /// <para>
    /// <b>YUV → RGB は BT.709 limited range 固定。</b> caps の <c>colorimetry</c> は読まない。
    /// pixel-aspect-ratio も見ないので、非正方画素のソースは縦横比が崩れる。
    /// </para>
    /// </summary>
    /// <param name="frame">生フレームの先頭。必要量より長い分は末尾のパディングとして無視する。</param>
    /// <param name="format">caps の <c>format</c> 文字列（<c>NV12</c> 等）。</param>
    /// <param name="width">ソースの幅。</param>
    /// <param name="height">ソースの高さ。</param>
    /// <param name="maxWidth">出力幅の上限。ソースがこれ以下なら等倍。</param>
    /// <param name="strides">平面ごとの 1 行のバイト数。空なら既定レイアウト。</param>
    /// <param name="offsets">平面ごとの先頭からのオフセット。空なら既定レイアウト。</param>
    /// <param name="image">成功したときだけ非 <see langword="null"/>。</param>
    /// <returns>変換できたか。</returns>
    public static bool TryCreate(
        ReadOnlySpan<byte> frame, string format, int width, int height, int maxWidth,
        ReadOnlySpan<int> strides, ReadOnlySpan<int> offsets,
        out ThumbnailImage? image)
    {
        image = null;

        if (width <= 0 || height <= 0 || maxWidth <= 0)
            return false;

        if (!TryDescribe(format, width, height, strides, offsets, out Layout layout))
            return false;

        if (frame.Length < layout.RequiredBytes)
            return false;

        int outWidth = Math.Min(maxWidth, width);

        // 四捨五入は AwayFromZero（整数演算）。切り捨てだと横長のソースで 1 行減る。
        int outHeight = Math.Max(1, (int)(((long)height * outWidth + (width / 2)) / width));

        var rgb = new byte[outWidth * outHeight * 3];
        int at = 0;

        for (int y = 0; y < outHeight; y++)
        {
            (int top, int bottom) = Block(y, height, outHeight);
            for (int x = 0; x < outWidth; x++)
            {
                (int left, int right) = Block(x, width, outWidth);
                Average(frame, in layout, left, right, top, bottom, out byte r, out byte g, out byte b);
                rgb[at++] = r;
                rgb[at++] = g;
                rgb[at++] = b;
            }
        }

        image = new ThumbnailImage(outWidth, outHeight, rgb);
        return true;
    }

    /// <summary>
    /// 出力画素 <paramref name="index"/> が覆うソースの区間 <c>[start, end)</c>。
    /// <b>空の区間は作らない</b>（丸めで潰れたら 1 画素へ広げる）。
    /// </summary>
    private static (int Start, int End) Block(int index, int source, int outSize)
    {
        int start = (int)((long)index * source / outSize);
        int end = (int)((long)(index + 1) * source / outSize);

        if (source <= start)
            start = source - 1;
        if (end <= start)
            end = start + 1;
        if (source < end)
            end = source;

        return (start, end);
    }

    /// <summary>4 の倍数へ切り上げる（平面の stride の既定）。</summary>
    private static int RoundUp4(int value) => (value + 3) & ~3;

    /// <summary>2 の倍数へ切り上げる（クロマを持つ形式の平面の高さ・幅の既定）。</summary>
    private static int RoundUp2(int value) => (value + 1) & ~1;

    /// <summary>画素の並べ方の種別。</summary>
    private enum Packing
    {
        /// <summary>1 画素が 3 または 4 バイトの RGB 系。</summary>
        Rgb,

        /// <summary>2 画素 4 バイトの YUV 4:2:2（YUY2 / UYVY）。</summary>
        PackedYuv422,

        /// <summary>Y 平面 ＋ クロマ交互の YUV 4:2:0（NV12 / NV21）。</summary>
        SemiPlanarYuv420,

        /// <summary>Y / U / V の 3 平面の YUV 4:2:0（I420 / YV12）。</summary>
        PlanarYuv420,
    }

    /// <summary>レイアウトから導いた、1 画素を読むのに要る値。</summary>
    private readonly struct Layout
    {
        public Packing Packing { get; init; }

        /// <summary>主平面（RGB / Y）の先頭からのオフセット。</summary>
        public int PlaneOffset { get; init; }

        /// <summary>主平面（RGB / Y）の 1 行のバイト数。</summary>
        public int Stride { get; init; }

        /// <summary>RGB 系の 1 画素のバイト数。</summary>
        public int PixelBytes { get; init; }

        /// <summary>packed YUV の 2 バイトの組の中の Y のオフセット。</summary>
        public int OffsetY { get; init; }

        /// <summary>RGB 系の画素内の R のオフセット。</summary>
        public int OffsetR { get; init; }

        public int OffsetG { get; init; }

        public int OffsetB { get; init; }

        /// <summary>U を含む平面の 1 行のバイト数。</summary>
        public int StrideU { get; init; }

        /// <summary>V を含む平面の 1 行のバイト数。</summary>
        public int StrideV { get; init; }

        /// <summary>U を含む平面の先頭からのオフセット。</summary>
        public int OffsetU { get; init; }

        /// <summary>V を含む平面の先頭からのオフセット。</summary>
        public int OffsetV { get; init; }

        /// <summary>このレイアウトで読む位置が収まっていなければならないバイト数。</summary>
        public long RequiredBytes { get; init; }
    }

    /// <summary>
    /// 対応する形式のレイアウトを引く。<b>対応しない形式と、収まらないレイアウトは
    /// <see langword="false"/></b>。
    /// </summary>
    private static bool TryDescribe(
        string format, int width, int height,
        ReadOnlySpan<int> strides, ReadOnlySpan<int> offsets, out Layout layout)
    {
        layout = default;

        switch (format)
        {
            // 4 バイト系: 既定の stride = width×4。
            case "BGRA":
            case "BGRx":
                return TryRgb(width, height, 4, r: 2, g: 1, b: 0, strides, offsets, out layout);
            case "RGBA":
            case "RGBx":
                return TryRgb(width, height, 4, r: 0, g: 1, b: 2, strides, offsets, out layout);
            case "ARGB":
            case "xRGB":
                return TryRgb(width, height, 4, r: 1, g: 2, b: 3, strides, offsets, out layout);
            case "ABGR":
            case "xBGR":
                return TryRgb(width, height, 4, r: 3, g: 2, b: 1, strides, offsets, out layout);

            // 3 バイト系: 既定の stride = RU4(width×3)。
            case "RGB":
                return TryRgb(width, height, 3, r: 0, g: 1, b: 2, strides, offsets, out layout);
            case "BGR":
                return TryRgb(width, height, 3, r: 2, g: 1, b: 0, strides, offsets, out layout);

            // 4:2:2 packed: 既定の stride = RU4(width×2)。2 画素 4 バイトの並びで U / V を共有する。
            case "YUY2":
                return TryPackedYuv(width, height, y: 0, u: 1, v: 3, strides, offsets, out layout);
            case "UYVY":
                return TryPackedYuv(width, height, y: 1, u: 0, v: 2, strides, offsets, out layout);

            // 4:2:0 semi-planar: Y 平面のあとにクロマが交互で続く（既定では stride も Y と同じ）。
            case "NV12":
                return TrySemiPlanarYuv(width, height, u: 0, v: 1, strides, offsets, out layout);
            case "NV21":
                return TrySemiPlanarYuv(width, height, u: 1, v: 0, strides, offsets, out layout);

            // 4:2:0 planar: Y の後ろに 2 つのクロマ平面（I420 は U→V、YV12 は V→U）。
            case "I420":
                return TryPlanarYuv(width, height, uFirst: true, strides, offsets, out layout);
            case "YV12":
                return TryPlanarYuv(width, height, uFirst: false, strides, offsets, out layout);

            default:
                return false;
        }
    }

    /// <summary>
    /// 平面 1 つの stride / offset を決める。<b>span が両方とも空なら既定値</b>で、
    /// そうでなければ渡された値を使い、扱えないもの（平面が足りない・負の offset・
    /// 1 行の画素に足りない stride）を弾く。
    /// </summary>
    private static bool TryPlane(
        int plane, int defaultStride, int defaultOffset, int minStride,
        ReadOnlySpan<int> strides, ReadOnlySpan<int> offsets,
        out int stride, out int offset)
    {
        if (strides.IsEmpty && offsets.IsEmpty)
        {
            stride = defaultStride;
            offset = defaultOffset;
            return true;
        }

        stride = 0;
        offset = 0;

        if (strides.Length <= plane || offsets.Length <= plane)
            return false;

        stride = strides[plane];
        offset = offsets[plane];

        // 負の stride（下から上へ並ぶ buffer）もここで落ちる。
        return minStride <= stride && 0 <= offset;
    }

    /// <summary>平面 1 つが要る長さ（<c>int</c> で溢れないよう <c>long</c> で数える）。</summary>
    private static long Required(int offset, int stride, int rows) => offset + ((long)stride * rows);

    private static bool TryRgb(
        int width, int height, int pixelBytes, int r, int g, int b,
        ReadOnlySpan<int> strides, ReadOnlySpan<int> offsets, out Layout layout)
    {
        layout = default;

        int defaultStride = pixelBytes == 4 ? width * 4 : RoundUp4(width * 3);
        if (!TryPlane(0, defaultStride, 0, width * pixelBytes, strides, offsets,
                out int stride, out int offset))
            return false;

        layout = new Layout
        {
            Packing = Packing.Rgb,
            PlaneOffset = offset,
            Stride = stride,
            PixelBytes = pixelBytes,
            OffsetR = r,
            OffsetG = g,
            OffsetB = b,
            RequiredBytes = Required(offset, stride, height),
        };

        return true;
    }

    private static bool TryPackedYuv(
        int width, int height, int y, int u, int v,
        ReadOnlySpan<int> strides, ReadOnlySpan<int> offsets, out Layout layout)
    {
        layout = default;

        // 端の画素も 2 画素 4 バイトの組で持つので、1 行の最小は RU2(width)×2。
        if (!TryPlane(0, RoundUp4(width * 2), 0, RoundUp2(width) * 2, strides, offsets,
                out int stride, out int offset))
            return false;

        layout = new Layout
        {
            Packing = Packing.PackedYuv422,
            PlaneOffset = offset,
            Stride = stride,
            PixelBytes = 2,
            OffsetY = y,
            OffsetU = u,
            OffsetV = v,
            RequiredBytes = Required(offset, stride, height),
        };

        return true;
    }

    private static bool TrySemiPlanarYuv(
        int width, int height, int u, int v,
        ReadOnlySpan<int> strides, ReadOnlySpan<int> offsets, out Layout layout)
    {
        layout = default;

        int defaultStride = RoundUp4(width);
        if (!TryPlane(0, defaultStride, 0, width, strides, offsets,
                out int stride, out int offset))
            return false;

        // クロマの 1 行は RU2(width) バイト（U と V が 1 バイトおきに交互）。
        int chromaRows = RoundUp2(height) / 2;
        if (!TryPlane(1, defaultStride, defaultStride * RoundUp2(height), RoundUp2(width),
                strides, offsets, out int chromaStride, out int chromaOffset))
            return false;

        layout = new Layout
        {
            Packing = Packing.SemiPlanarYuv420,
            PlaneOffset = offset,
            Stride = stride,
            StrideU = chromaStride,
            StrideV = chromaStride,
            OffsetU = chromaOffset + u,
            OffsetV = chromaOffset + v,
            RequiredBytes = Math.Max(
                Required(offset, stride, height),
                Required(chromaOffset, chromaStride, chromaRows)),
        };

        return true;
    }

    private static bool TryPlanarYuv(
        int width, int height, bool uFirst,
        ReadOnlySpan<int> strides, ReadOnlySpan<int> offsets, out Layout layout)
    {
        layout = default;

        int defaultStride = RoundUp4(width);
        if (!TryPlane(0, defaultStride, 0, width, strides, offsets,
                out int stride, out int offset))
            return false;

        int defaultChromaStride = RoundUp4(RoundUp2(width) / 2);
        int chromaRows = RoundUp2(height) / 2;
        int defaultLumaBytes = defaultStride * RoundUp2(height);
        int minChromaStride = RoundUp2(width) / 2;

        // YV12 は平面 1 が V・平面 2 が U（I420 の 2 つを入れ替えたもの）。
        int uPlane = uFirst ? 1 : 2;
        int vPlane = uFirst ? 2 : 1;

        if (!TryPlane(uPlane, defaultChromaStride,
                defaultLumaBytes + ((uPlane - 1) * defaultChromaStride * chromaRows),
                minChromaStride, strides, offsets, out int strideU, out int offsetU))
            return false;

        if (!TryPlane(vPlane, defaultChromaStride,
                defaultLumaBytes + ((vPlane - 1) * defaultChromaStride * chromaRows),
                minChromaStride, strides, offsets, out int strideV, out int offsetV))
            return false;

        layout = new Layout
        {
            Packing = Packing.PlanarYuv420,
            PlaneOffset = offset,
            Stride = stride,
            StrideU = strideU,
            StrideV = strideV,
            OffsetU = offsetU,
            OffsetV = offsetV,
            RequiredBytes = Math.Max(
                Required(offset, stride, height),
                Math.Max(
                    Required(offsetU, strideU, chromaRows),
                    Required(offsetV, strideV, chromaRows))),
        };

        return true;
    }

    /// <summary>
    /// ソースの矩形 <c>[left, right) × [top, bottom)</c> をボックス平均して 1 画素にする。
    /// <b>YUV は Y / U / V を別々に平均してから 1 回だけ RGB へ変換する</b>
    /// （RGB 化してから平均するとクロマの最近傍サンプルが何度も効いてしまう）。
    /// </summary>
    private static void Average(
        ReadOnlySpan<byte> frame, in Layout layout,
        int left, int right, int top, int bottom,
        out byte r, out byte g, out byte b)
    {
        int count = (right - left) * (bottom - top);
        long sum0 = 0;
        long sum1 = 0;
        long sum2 = 0;

        switch (layout.Packing)
        {
            case Packing.Rgb:
                for (int y = top; y < bottom; y++)
                {
                    int row = layout.PlaneOffset + (y * layout.Stride);
                    for (int x = left; x < right; x++)
                    {
                        int at = row + (x * layout.PixelBytes);
                        sum0 += frame[at + layout.OffsetR];
                        sum1 += frame[at + layout.OffsetG];
                        sum2 += frame[at + layout.OffsetB];
                    }
                }

                r = (byte)(sum0 / count);
                g = (byte)(sum1 / count);
                b = (byte)(sum2 / count);
                return;

            case Packing.PackedYuv422:
                for (int y = top; y < bottom; y++)
                {
                    int row = layout.PlaneOffset + (y * layout.Stride);
                    for (int x = left; x < right; x++)
                    {
                        sum0 += frame[row + (x * 2) + layout.OffsetY];

                        // クロマは対応する輝度位置の最近傍（4:2:2 は x/2）。
                        int pair = row + ((x / 2) * 4);
                        sum1 += frame[pair + layout.OffsetU];
                        sum2 += frame[pair + layout.OffsetV];
                    }
                }

                break;

            case Packing.SemiPlanarYuv420:
                for (int y = top; y < bottom; y++)
                {
                    int row = layout.PlaneOffset + (y * layout.Stride);
                    int rowU = layout.OffsetU + ((y / 2) * layout.StrideU);
                    int rowV = layout.OffsetV + ((y / 2) * layout.StrideV);
                    for (int x = left; x < right; x++)
                    {
                        sum0 += frame[row + x];

                        // 4:2:0 の最近傍は x/2, y/2。U と V は 1 バイトおきに交互。
                        int at = (x / 2) * 2;
                        sum1 += frame[rowU + at];
                        sum2 += frame[rowV + at];
                    }
                }

                break;

            default:
                for (int y = top; y < bottom; y++)
                {
                    int row = layout.PlaneOffset + (y * layout.Stride);
                    int rowU = layout.OffsetU + ((y / 2) * layout.StrideU);
                    int rowV = layout.OffsetV + ((y / 2) * layout.StrideV);
                    for (int x = left; x < right; x++)
                    {
                        sum0 += frame[row + x];

                        int at = x / 2;
                        sum1 += frame[rowU + at];
                        sum2 += frame[rowV + at];
                    }
                }

                break;
        }

        YuvToRgb(sum0 / (float)count, sum1 / (float)count, sum2 / (float)count, out r, out g, out b);
    }

    /// <summary>BT.709 limited range の逆変換。</summary>
    private static void YuvToRgb(float y, float u, float v, out byte r, out byte g, out byte b)
    {
        float luma = 1.164f * (y - 16f);
        float du = u - 128f;
        float dv = v - 128f;

        r = Clamp(luma + (1.793f * dv));
        g = Clamp(luma - (0.213f * du) - (0.533f * dv));
        b = Clamp(luma + (2.112f * du));
    }

    private static byte Clamp(float value)
    {
        int rounded = (int)MathF.Round(value, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp(rounded, 0, 255);
    }
}
