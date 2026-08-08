using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="CampbellPalette"/> ── 配色の正本が実際に正本であることの検査。
///
/// <para>
/// 選択色は XAML（#AARRGGBB）と CSS（#RRGGBBAA）でアルファの位置が違い、
/// XAML 形式を xterm.js へそのまま渡すと「30% の白」が不透明の明るいシアンになる。
/// 2 形式を別々の定数で持つ以上、同じ色を指していることを機械的に守る。
/// </para>
/// </summary>
public class CampbellPaletteTests
{
    [Fact]
    public void TheCssSelectionColor_IsTheSameColorAsTheXamlForm()
    {
        // #AARRGGBB → #RRGGBBAA
        string xaml = CampbellPalette.SelectionBackground;
        string converted = "#" + xaml[3..] + xaml[1..3];

        Assert.Equal(CampbellPalette.SelectionBackgroundCss, converted, ignoreCase: true);
    }

    /// <summary>
    /// terminal.js は選択色をリテラルで持たない ── 色はすべてハンドシェイク
    /// （<c>LogTerminalView.SendInitialize</c>）経由で正本から受け取る。
    /// リテラルが復活すると、片方だけ直して食い違う事故が再発する。
    /// </summary>
    [Fact]
    public void TerminalJs_DoesNotHardcodeTheSelectionColors()
    {
        string js = TerminalJs();

        Assert.DoesNotContain(CampbellPalette.SelectionBackground, js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CampbellPalette.SelectionBackgroundCss, js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CampbellPalette.SelectionInactiveBackgroundCss, js, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>JS が読むフィールド位置を、色数から機械的に固定する。</b>
    /// ハンドシェイクの <c>i</c> は
    /// <c>i US scrollback US follow US fg US bg US c0..c15 US selection US selectionInactive</c>。
    /// 色より前に 1 つ足すと <c>slice(5, 21)</c> と <c>f[21]</c>/<c>f[22]</c> が無音でずれ、
    /// 選択色が色 15 になる ── 見た目だけ壊れるので L2/L3 でも拾えない。
    /// </summary>
    [Fact]
    public void TerminalJs_ReadsTheColorsAndSelectionAtTheExpectedOffsets()
    {
        string js = TerminalJs();

        const int firstColorIndex = 5;   // 0=タグ 1=scrollback 2=follow 3=fg 4=bg
        int afterColors = firstColorIndex + CampbellPalette.Colors.Length;

        Assert.Contains($"slice({firstColorIndex}, {afterColors})", js, StringComparison.Ordinal);
        Assert.Contains($"selectionBackground: f[{afterColors}]", js, StringComparison.Ordinal);
        Assert.Contains($"selectionInactiveBackground: f[{afterColors + 1}]", js, StringComparison.Ordinal);
    }

    private static string TerminalJs() => File.ReadAllText(
        RepositoryFiles.At("src", "ProcessRecorderApp", "Assets", "Terminal", "terminal.js"));
}
