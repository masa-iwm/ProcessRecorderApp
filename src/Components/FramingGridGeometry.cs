using System;
using System.Collections.Generic;

namespace ProcessRecorderApp.Components;

/// <summary>プレビューへ重ねる構図補助線の種類（Windows 標準のカメラアプリに合わせてある）。</summary>
public enum FramingGridKind
{
    /// <summary>出さない。</summary>
    None,

    /// <summary>三分割（1/3・2/3 の縦横 2 本ずつ）。</summary>
    Thirds,

    /// <summary>黄金比（0.382・0.618 の縦横 2 本ずつ）。</summary>
    GoldenRatio,

    /// <summary>十字（中央の縦横 1 本ずつ）。</summary>
    Crosshair,

    /// <summary>正方形（中央に収まる 1:1 の枠）。</summary>
    Square,
}

/// <summary>矩形（左上と大きさ）。WinUI へ依存しないよう自前で持つ。</summary>
/// <param name="X">左端。</param>
/// <param name="Y">上端。</param>
/// <param name="Width">幅。</param>
/// <param name="Height">高さ。</param>
public readonly record struct GridRect(double X, double Y, double Width, double Height)
{
    /// <summary>面積を持たない（描くものが無い）矩形か。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>右端。</summary>
    public double Right => X + Width;

    /// <summary>下端。</summary>
    public double Bottom => Y + Height;
}

/// <summary>線分。</summary>
/// <param name="X1">始点の X。</param>
/// <param name="Y1">始点の Y。</param>
/// <param name="X2">終点の X。</param>
/// <param name="Y2">終点の Y。</param>
public readonly record struct GridLine(double X1, double Y1, double X2, double Y2);

/// <summary>
/// プレビューの構図補助線の幾何。<b>純粋関数だけを置く</b>（WinUI にも GStreamer にも依存しない）。
///
/// <para>
/// <b>線はパネル全体ではなく「映像の矩形」に合わせて引く。</b>
/// プレビューの <c>d3d12swapchainsink</c> は <c>force-aspect-ratio</c> の既定が <c>true</c> で、
/// スワップチェーンはパネル全面を占めるものの<b>映像はその中でアスペクトフィットされ、
/// 余った上下（または左右）は <c>border-color</c> で塗られる</b>（実測）。
/// パネル全体に線を引くと、その帯の上にも線が乗って構図の目安にならない。
/// </para>
/// <para>
/// <b>座標系は DIP（デバイス非依存ピクセル）。</b> 呼び出し側は
/// <c>SwapChainPanel</c> の <c>ActualWidth</c>/<c>ActualHeight</c> を渡すこと ──
/// <c>NativeSwapChainPanel</c> が高 DPI 対策で逆スケール行列を掛けているのは
/// <b>スワップチェーンだけ</b>で、その上に載る XAML の子は DIP のままである。
/// 物理ピクセル（<c>ActualWidth * CompositionScaleX</c>）を渡すと高 DPI 環境でずれる。
/// </para>
/// <para>
/// この機能で自動テストできるのはここだけである（線が実際に描かれていることは
/// UIA からは見えない。<c>docs/coverage-gaps.md</c>）。
/// </para>
/// </summary>
public static class FramingGridGeometry
{
    /// <summary>
    /// 黄金比の分割位置。1/φ² ＝ 0.381966…（もう一方はその補数）。
    /// <b>0.382 と直書きしない</b> ── 比そのものから出しておけば、
    /// 「なぜこの数字か」が式に残る。
    /// </summary>
    private static readonly double GoldenShorter = 2.0 - ((1.0 + Math.Sqrt(5.0)) / 2.0);

    /// <summary>
    /// 映像をパネルへアスペクトフィットさせた矩形（＝実際に映像が出ている範囲）を返す。
    /// どれかの寸法が 0 以下なら空の矩形を返す（映像サイズが未知のあいだは線を出さない）。
    /// </summary>
    /// <param name="panelWidth">パネルの幅（DIP）。</param>
    /// <param name="panelHeight">パネルの高さ（DIP）。</param>
    /// <param name="videoWidth">映像の表示幅（画素。PAR を掛けたあとの値）。</param>
    /// <param name="videoHeight">映像の表示高さ（画素）。</param>
    public static GridRect Fit(double panelWidth, double panelHeight, double videoWidth, double videoHeight)
    {
        if (!(panelWidth > 0) || !(panelHeight > 0) || !(videoWidth > 0) || !(videoHeight > 0))
            return default;

        // NaN / 無限大は上の比較（! (x > 0)）で既に弾いている。
        double scale = Math.Min(panelWidth / videoWidth, panelHeight / videoHeight);
        double width = videoWidth * scale;
        double height = videoHeight * scale;

        return new GridRect((panelWidth - width) / 2, (panelHeight - height) / 2, width, height);
    }

    /// <summary>
    /// 指定した種類の補助線を、<paramref name="rect"/>（＝<see cref="Fit"/> の結果）の中に引く。
    /// <see cref="FramingGridKind.None"/> と空の矩形では 0 本を返す。
    /// </summary>
    public static IReadOnlyList<GridLine> Lines(FramingGridKind kind, GridRect rect)
    {
        if (kind == FramingGridKind.None || rect.IsEmpty)
            return [];

        return kind switch
        {
            FramingGridKind.Thirds => Divisions(rect, 1.0 / 3.0, 2.0 / 3.0),
            FramingGridKind.GoldenRatio => Divisions(rect, GoldenShorter, 1.0 - GoldenShorter),
            FramingGridKind.Crosshair => Divisions(rect, 0.5),
            FramingGridKind.Square => SquareOutline(rect),
            _ => [],
        };
    }

    /// <summary>
    /// 指定した比の位置に縦線と横線を引く（比 1 つにつき縦横 1 本ずつ）。
    /// 比は矩形の左上を 0、右下を 1 とした相対位置。
    /// </summary>
    private static GridLine[] Divisions(GridRect rect, params double[] fractions)
    {
        var lines = new GridLine[fractions.Length * 2];
        for (int i = 0; i < fractions.Length; i++)
        {
            double f = fractions[i];
            double x = rect.X + (rect.Width * f);
            double y = rect.Y + (rect.Height * f);
            lines[i * 2] = new GridLine(x, rect.Y, x, rect.Bottom);            // 縦
            lines[(i * 2) + 1] = new GridLine(rect.X, y, rect.Right, y);       // 横
        }
        return lines;
    }

    /// <summary>
    /// 中央に収まる最大の正方形の枠（4 辺）。1:1 で切り出したときに何が残るかの目安。
    /// 映像がちょうど正方形なら枠は矩形の縁と重なる（0 本にはしない ── 種類を選んだのに
    /// 何も出ないと「壊れている」と見分けが付かない）。
    /// </summary>
    private static GridLine[] SquareOutline(GridRect rect)
    {
        double side = Math.Min(rect.Width, rect.Height);
        double left = rect.X + ((rect.Width - side) / 2);
        double top = rect.Y + ((rect.Height - side) / 2);
        double right = left + side;
        double bottom = top + side;

        return
        [
            new GridLine(left, top, right, top),
            new GridLine(right, top, right, bottom),
            new GridLine(right, bottom, left, bottom),
            new GridLine(left, bottom, left, top),
        ];
    }
}
