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
    /// 生フレームを RGB へ変換しつつ縮小する。<b>純マネージドで、対応できない入力は
    /// すべて <see langword="false"/> に畳む</b>（呼び出し側はログを 1 行残すだけでよい）。
    ///
    /// <para>
    /// <b>stride は仮定である。</b> 使っている GStreamer のバインディングは平面ごとの
    /// stride / offset を数値で公開しないので、<c>gst_video_info_set_format</c> の既定
    /// レイアウト（下の各形式の説明）を仮定する。パディングの入った buffer が来ると
    /// 絵が斜めにずれるが、録画そのものには影響しない。
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
    /// <param name="image">成功したときだけ非 <see langword="null"/>。</param>
    /// <returns>変換できたか。</returns>
    public static bool TryCreate(
        ReadOnlySpan<byte> frame, string format, int width, int height, int maxWidth,
        out ThumbnailImage? image)
    {
        image = null;

        if (width <= 0 || height <= 0 || maxWidth <= 0)
            return false;

        if (!TryDescribe(format, width, height, out Layout layout))
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

    /// <summary>既定レイアウトから導いた、1 画素を読むのに要る値。</summary>
    private readonly struct Layout
    {
        public Packing Packing { get; init; }

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

        /// <summary>クロマ平面の 1 行のバイト数。</summary>
        public int ChromaStride { get; init; }

        /// <summary>U を含む平面の先頭からのオフセット。</summary>
        public int OffsetU { get; init; }

        /// <summary>V を含む平面の先頭からのオフセット。</summary>
        public int OffsetV { get; init; }

        /// <summary>この形式・寸法で最低限必要なバイト数。</summary>
        public int RequiredBytes { get; init; }
    }

    /// <summary>
    /// 対応する形式の既定レイアウトを引く。対応しない形式は <see langword="false"/>。
    /// </summary>
    private static bool TryDescribe(string format, int width, int height, out Layout layout)
    {
        layout = default;

        switch (format)
        {
            // 4 バイト系: stride = width×4。
            case "BGRA":
            case "BGRx":
                layout = Rgb(width, height, 4, r: 2, g: 1, b: 0);
                return true;
            case "RGBA":
            case "RGBx":
                layout = Rgb(width, height, 4, r: 0, g: 1, b: 2);
                return true;
            case "ARGB":
            case "xRGB":
                layout = Rgb(width, height, 4, r: 1, g: 2, b: 3);
                return true;
            case "ABGR":
            case "xBGR":
                layout = Rgb(width, height, 4, r: 3, g: 2, b: 1);
                return true;

            // 3 バイト系: stride = RU4(width×3)。
            case "RGB":
                layout = Rgb(width, height, 3, r: 0, g: 1, b: 2);
                return true;
            case "BGR":
                layout = Rgb(width, height, 3, r: 2, g: 1, b: 0);
                return true;

            // 4:2:2 packed: stride = RU4(width×2)。2 画素 4 バイトの並びで U / V を共有する。
            case "YUY2":
                layout = PackedYuv(width, height, y: 0, u: 1, v: 3);
                return true;
            case "UYVY":
                layout = PackedYuv(width, height, y: 1, u: 0, v: 2);
                return true;

            // 4:2:0 semi-planar: Y 平面の直後にクロマが交互で続く（stride は Y と同じ）。
            case "NV12":
                layout = SemiPlanarYuv(width, height, u: 0, v: 1);
                return true;
            case "NV21":
                layout = SemiPlanarYuv(width, height, u: 1, v: 0);
                return true;

            // 4:2:0 planar: Y の後ろに 2 つのクロマ平面（I420 は U→V、YV12 は V→U）。
            case "I420":
                layout = PlanarYuv(width, height, uFirst: true);
                return true;
            case "YV12":
                layout = PlanarYuv(width, height, uFirst: false);
                return true;

            default:
                return false;
        }
    }

    private static Layout Rgb(int width, int height, int pixelBytes, int r, int g, int b)
    {
        int stride = pixelBytes == 4 ? width * 4 : RoundUp4(width * 3);
        return new Layout
        {
            Packing = Packing.Rgb,
            Stride = stride,
            PixelBytes = pixelBytes,
            OffsetR = r,
            OffsetG = g,
            OffsetB = b,
            RequiredBytes = stride * height,
        };
    }

    private static Layout PackedYuv(int width, int height, int y, int u, int v)
    {
        int stride = RoundUp4(width * 2);
        return new Layout
        {
            Packing = Packing.PackedYuv422,
            Stride = stride,
            PixelBytes = 2,
            OffsetY = y,
            OffsetU = u,
            OffsetV = v,
            RequiredBytes = stride * height,
        };
    }

    private static Layout SemiPlanarYuv(int width, int height, int u, int v)
    {
        int stride = RoundUp4(width);
        int lumaBytes = stride * RoundUp2(height);
        return new Layout
        {
            Packing = Packing.SemiPlanarYuv420,
            Stride = stride,
            ChromaStride = stride,
            OffsetU = lumaBytes + u,
            OffsetV = lumaBytes + v,
            RequiredBytes = lumaBytes + (stride * (RoundUp2(height) / 2)),
        };
    }

    private static Layout PlanarYuv(int width, int height, bool uFirst)
    {
        int stride = RoundUp4(width);
        int lumaBytes = stride * RoundUp2(height);
        int chromaStride = RoundUp4(RoundUp2(width) / 2);
        int chromaBytes = chromaStride * (RoundUp2(height) / 2);

        return new Layout
        {
            Packing = Packing.PlanarYuv420,
            Stride = stride,
            ChromaStride = chromaStride,
            OffsetU = uFirst ? lumaBytes : lumaBytes + chromaBytes,
            OffsetV = uFirst ? lumaBytes + chromaBytes : lumaBytes,
            RequiredBytes = lumaBytes + (chromaBytes * 2),
        };
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
                    int row = y * layout.Stride;
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
                    int row = y * layout.Stride;
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
                    int row = y * layout.Stride;
                    int chromaRow = (y / 2) * layout.ChromaStride;
                    for (int x = left; x < right; x++)
                    {
                        sum0 += frame[row + x];

                        // 4:2:0 の最近傍は x/2, y/2。U と V は 1 バイトおきに交互。
                        int at = chromaRow + ((x / 2) * 2);
                        sum1 += frame[layout.OffsetU + at];
                        sum2 += frame[layout.OffsetV + at];
                    }
                }

                break;

            default:
                for (int y = top; y < bottom; y++)
                {
                    int row = y * layout.Stride;
                    int chromaRow = (y / 2) * layout.ChromaStride;
                    for (int x = left; x < right; x++)
                    {
                        sum0 += frame[row + x];

                        int at = chromaRow + (x / 2);
                        sum1 += frame[layout.OffsetU + at];
                        sum2 += frame[layout.OffsetV + at];
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
