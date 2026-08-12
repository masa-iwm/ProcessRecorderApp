using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// プレビューの構図補助線の幾何。
///
/// <para>
/// <b>この機能で自動的に守れるのはここだけである。</b> 線が実際に描かれていることも、
/// それが映像の縁と一致していることも UIA からは見えない（<c>docs/coverage-gaps.md</c>）。
/// したがってここでは「どこに引くべきか」という規則そのものを固定する。
/// </para>
/// <para>
/// 中核は <b>パネルではなく映像の矩形に引く</b>こと ── <c>d3d12swapchainsink</c> は
/// <c>force-aspect-ratio=true</c> でレターボックスを作るので、パネル全体に引くと
/// 線が黒帯の上へはみ出す。
/// </para>
/// </summary>
public class FramingGridGeometryTests
{
    private const double Tolerance = 1e-9;

    // ---- Fit（映像の矩形） ----

    [Fact]
    public void Fit_WhenTheAspectsMatch_FillsThePanel()
    {
        var rect = FramingGridGeometry.Fit(1600, 900, 1920, 1080);

        Assert.Equal(0, rect.X, Tolerance);
        Assert.Equal(0, rect.Y, Tolerance);
        Assert.Equal(1600, rect.Width, Tolerance);
        Assert.Equal(900, rect.Height, Tolerance);
    }

    [Fact]
    public void Fit_WhenTheVideoIsNarrowerThanThePanel_LeavesBarsOnTheSides()
    {
        // 4:3 の映像を 16:9 のパネルへ ── 左右に帯が出る。
        var rect = FramingGridGeometry.Fit(1600, 900, 640, 480);

        Assert.Equal(1200, rect.Width, Tolerance);
        Assert.Equal(900, rect.Height, Tolerance);
        Assert.Equal(200, rect.X, Tolerance);
        Assert.Equal(0, rect.Y, Tolerance);
    }

    [Fact]
    public void Fit_WhenTheVideoIsWiderThanThePanel_LeavesBarsAboveAndBelow()
    {
        var rect = FramingGridGeometry.Fit(800, 800, 1920, 1080);

        Assert.Equal(800, rect.Width, Tolerance);
        Assert.Equal(450, rect.Height, Tolerance);
        Assert.Equal(0, rect.X, Tolerance);
        Assert.Equal(175, rect.Y, Tolerance);
    }

    [Fact]
    public void Fit_NeverOverflowsThePanel()
    {
        var rect = FramingGridGeometry.Fit(1600, 900, 640, 480);

        Assert.True(rect.X >= 0);
        Assert.True(rect.Y >= 0);
        Assert.True(rect.Right <= 1600 + Tolerance);
        Assert.True(rect.Bottom <= 900 + Tolerance);
    }

    [Theory]
    [InlineData(0, 900, 1920, 1080)]
    [InlineData(1600, 0, 1920, 1080)]
    [InlineData(1600, 900, 0, 1080)]
    [InlineData(1600, 900, 1920, 0)]
    [InlineData(-1600, 900, 1920, 1080)]
    [InlineData(1600, 900, -1920, 1080)]
    [InlineData(double.NaN, 900, 1920, 1080)]
    public void Fit_WithADegenerateSize_ReturnsAnEmptyRect(double pw, double ph, double vw, double vh)
    {
        // 映像サイズが未知のあいだ（＝最初のフレームが来る前、レコーダー切替の直後）は
        // 0 が渡る。ここで例外にも「巨大な矩形」にもしないこと。
        var rect = FramingGridGeometry.Fit(pw, ph, vw, vh);

        Assert.True(rect.IsEmpty);
        Assert.Empty(FramingGridGeometry.Lines(FramingGridKind.Thirds, rect));
    }

    // ---- Lines（線の位置） ----

    [Fact]
    public void Lines_None_DrawsNothing()
    {
        var rect = FramingGridGeometry.Fit(1600, 900, 1600, 900);

        Assert.Empty(FramingGridGeometry.Lines(FramingGridKind.None, rect));
    }

    [Fact]
    public void Lines_Thirds_DrawsTwoVerticalsAndTwoHorizontalsAtOneAndTwoThirds()
    {
        var rect = new GridRect(0, 0, 300, 300);

        var lines = FramingGridGeometry.Lines(FramingGridKind.Thirds, rect);

        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => IsVerticalAt(l, 100, rect));
        Assert.Contains(lines, l => IsVerticalAt(l, 200, rect));
        Assert.Contains(lines, l => IsHorizontalAt(l, 100, rect));
        Assert.Contains(lines, l => IsHorizontalAt(l, 200, rect));
    }

    [Fact]
    public void Lines_GoldenRatio_DrawsAtTheGoldenSectionsNotAtThirds()
    {
        var rect = new GridRect(0, 0, 1000, 1000);

        var lines = FramingGridGeometry.Lines(FramingGridKind.GoldenRatio, rect);

        Assert.Equal(4, lines.Count);
        // 1/φ² = 0.381966… / その補数 0.618034…
        Assert.Contains(lines, l => IsVerticalAt(l, 381.966011, rect, 1e-4));
        Assert.Contains(lines, l => IsVerticalAt(l, 618.033989, rect, 1e-4));
        // 三分割と取り違えていないこと（見た目が近いので、実装を混同しても気付きにくい）。
        Assert.DoesNotContain(lines, l => IsVerticalAt(l, 1000.0 / 3.0, rect, 1e-4));
    }

    [Fact]
    public void Lines_Crosshair_DrawsOneVerticalAndOneHorizontalThroughTheCentre()
    {
        var rect = new GridRect(0, 0, 400, 200);

        var lines = FramingGridGeometry.Lines(FramingGridKind.Crosshair, rect);

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => IsVerticalAt(l, 200, rect));
        Assert.Contains(lines, l => IsHorizontalAt(l, 100, rect));
    }

    [Fact]
    public void Lines_Square_DrawsACentredSquareThatFitsInside()
    {
        var rect = new GridRect(10, 20, 400, 200);

        var lines = FramingGridGeometry.Lines(FramingGridKind.Square, rect);

        Assert.Equal(4, lines.Count);
        // 一辺は短い方（200）。中央に置くので左端は 10 + (400-200)/2 = 110。
        Assert.All(lines, l =>
        {
            Assert.InRange(l.X1, 110 - Tolerance, 310 + Tolerance);
            Assert.InRange(l.X2, 110 - Tolerance, 310 + Tolerance);
            Assert.InRange(l.Y1, 20 - Tolerance, 220 + Tolerance);
            Assert.InRange(l.Y2, 20 - Tolerance, 220 + Tolerance);
        });
        Assert.Contains(lines, l => IsHorizontalAt(l, 20, rect, Tolerance, expectFullWidth: false));
    }

    /// <summary>
    /// <b>すべての線が映像の矩形の中に収まること。</b> これがこの機能の中核契約
    /// ── はみ出すと黒帯の上に線が乗り、構図の目安にならない。
    /// </summary>
    [Theory]
    [InlineData(FramingGridKind.Thirds)]
    [InlineData(FramingGridKind.GoldenRatio)]
    [InlineData(FramingGridKind.Crosshair)]
    [InlineData(FramingGridKind.Square)]
    public void Lines_AlwaysStayInsideTheVideoRect(FramingGridKind kind)
    {
        // 4:3 の映像を 16:9 のパネルへ入れた、帯のある構成。
        var rect = FramingGridGeometry.Fit(1600, 900, 640, 480);

        foreach (var line in FramingGridGeometry.Lines(kind, rect))
        {
            Assert.InRange(line.X1, rect.X - Tolerance, rect.Right + Tolerance);
            Assert.InRange(line.X2, rect.X - Tolerance, rect.Right + Tolerance);
            Assert.InRange(line.Y1, rect.Y - Tolerance, rect.Bottom + Tolerance);
            Assert.InRange(line.Y2, rect.Y - Tolerance, rect.Bottom + Tolerance);
        }
    }

    /// <summary>
    /// 帯があるとき、線がパネルの原点ではなく<b>映像の左上</b>を基準にしていること。
    /// パネル基準で書いてしまう退行は、帯の無い構成（アスペクトが一致）では
    /// まったく同じ結果になるので、こちらの構成でしか捕まらない。
    ///
    /// <para>
    /// <b>十字では捕まらない。</b> 中央線は定義上つねにパネル中央と一致する
    /// （矩形を中央に置くのだから）── 分割線で見ること。
    /// </para>
    /// </summary>
    [Fact]
    public void Lines_AreRelativeToTheVideoRect_NotToThePanelOrigin()
    {
        // 16:9 の映像を 1000x1000 のパネルへ: 幅 1000・高さ 562.5・Y=218.75
        var rect = FramingGridGeometry.Fit(1000, 1000, 1920, 1080);
        Assert.Equal(218.75, rect.Y, 1e-6);

        var lines = FramingGridGeometry.Lines(FramingGridKind.Thirds, rect);

        // 映像基準: 218.75 + 562.5/3 = 406.25
        Assert.Contains(lines, l => IsHorizontalAt(l, 406.25, rect, 1e-6));
        // パネル基準（退行の形）: 1000/3 = 333.33…
        Assert.DoesNotContain(lines, l => IsHorizontalAt(l, 1000.0 / 3.0, rect, 1e-6));

        // 縦は帯が無い方向なので一致する（＝この向きでは退行を検出できない）ことも固定しておく。
        Assert.Contains(lines, l => IsVerticalAt(l, 1000.0 / 3.0, rect, 1e-6));
    }

    private static bool IsVerticalAt(GridLine line, double x, GridRect rect, double tolerance = Tolerance)
        => Math.Abs(line.X1 - x) < tolerance
           && Math.Abs(line.X2 - x) < tolerance
           && Math.Abs(line.Y1 - rect.Y) < Tolerance
           && Math.Abs(line.Y2 - rect.Bottom) < Tolerance;

    private static bool IsHorizontalAt(GridLine line, double y, GridRect rect,
                                       double tolerance = Tolerance, bool expectFullWidth = true)
        => Math.Abs(line.Y1 - y) < tolerance
           && Math.Abs(line.Y2 - y) < tolerance
           && (!expectFullWidth
               || (Math.Abs(line.X1 - rect.X) < Tolerance && Math.Abs(line.X2 - rect.Right) < Tolerance));
}
