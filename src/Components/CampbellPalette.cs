namespace ProcessRecorderApp.Components;

/// <summary>
/// Log 画面の配色（Windows Terminal 既定の Campbell スキーム）。
///
/// <para>
/// <b>ここが唯一の正本。</b> ログの表示経路は 2 つ（WebView2 の中の xterm.js と、
/// フォールバックの ListView）あり、色をそれぞれに書くと片方だけ直して見た目が食い違う。
/// 16 進表記で持つのは、xterm.js の <c>ITheme</c> がそのまま受け取れる形だから。
/// </para>
/// </summary>
public static class CampbellPalette
{
    /// <summary>ANSI の 16 色（0-7: 標準色、8-15: 明色）</summary>
    public static readonly string[] Colors =
    [
        "#0C0C0C", // 0: Black
        "#C50F1F", // 1: Red
        "#13A10E", // 2: Green
        "#C19C00", // 3: Yellow
        "#0037DA", // 4: Blue
        "#881798", // 5: Magenta
        "#3A96DD", // 6: Cyan
        "#CCCCCC", // 7: White
        "#767676", // 8: Bright Black
        "#E74856", // 9: Bright Red
        "#16C60C", // 10: Bright Green
        "#F9F1A5", // 11: Bright Yellow
        "#3B78FF", // 12: Bright Blue
        "#B4009E", // 13: Bright Magenta
        "#61D6D6", // 14: Bright Cyan
        "#F2F2F2", // 15: Bright White
    ];

    /// <summary>既定の前景色（<see cref="Colors"/> の 7 番）</summary>
    public const string DefaultForeground = "#CCCCCC";

    /// <summary>既定の背景色（<see cref="Colors"/> の 0 番）</summary>
    public const string DefaultBackground = "#0C0C0C";

    /// <summary>選択範囲の背景（半透明の白。背景が #0C0C0C 固定なのでシステム色では見えない）</summary>
    public const string SelectionBackground = "#4DFFFFFF";
}
