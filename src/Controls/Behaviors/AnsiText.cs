using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using ProcessRecorderApp.Components;
using System.Collections.Generic;
using Windows.UI;

namespace ProcessRecorderApp.Behaviors
{
    /// <summary>
    /// ANSI エスケープ（SGR）を含む文字列を解釈し、<see cref="TextBlock"/> に
    /// 色・装飾付きで表示する添付プロパティ。
    ///
    /// 使用例: &lt;TextBlock lb:AnsiText.Source="{x:Bind}" /&gt;
    ///
    /// 配色は Windows Terminal 既定（Campbell スキーム）に固定：
    ///   - 前景色/太字/斜体 → Run の Foreground / FontWeight / FontStyle
    ///   - 下線/取り消し線 → Run の TextDecorations
    ///   - 淡色（Dim） → 前景色の減光
    ///   - 背景色/反転 → TextHighlighter（背景色ごとに1つ作成し文字範囲を指定）
    /// </summary>
    public sealed class AnsiText
    {
        private AnsiText() { }

        /// <summary>
        /// Windows Terminal 既定（Campbell スキーム）の 16 色パレット。
        /// 色そのものは <see cref="CampbellPalette"/> が正本 ──
        /// ログの表示経路は端末（xterm.js）とこの ListView の 2 つあり、
        /// 色を両方に書くと片方だけ直して見た目が食い違う
        /// </summary>
        private static readonly Color[] Palette = [.. CampbellPalette.Colors.Select(ParseHexColor)];

        /// <summary>Campbell スキームの既定前景色（#CCCCCC）</summary>
        private static readonly Color DefaultForeground = ParseHexColor(CampbellPalette.DefaultForeground);
        /// <summary>Campbell スキームの既定背景色（#0C0C0C）</summary>
        private static readonly Color DefaultBackground = ParseHexColor(CampbellPalette.DefaultBackground);

        /// <summary>"#RRGGBB" を不透明の <see cref="Color"/> にする</summary>
        private static Color ParseHexColor(string hex) => Color.FromArgb(
            0xFF,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));

        // Brush はイミュータブルに使うため色ごとにキャッシュする（UIスレッドからのみ参照）
        private static readonly Dictionary<Color, SolidColorBrush> BrushCache = [];

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.RegisterAttached("Source", typeof(string), typeof(AnsiText),
                new PropertyMetadata(null, OnSourceChanged));

        public static void SetSource(DependencyObject element, string? value) => element.SetValue(SourceProperty, value);
        public static string? GetSource(DependencyObject element) => (string?)element.GetValue(SourceProperty);

        [WinRT.DynamicWindowsRuntimeCast(typeof(TextBlock))]
        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock textBlock)
            {
                Render(textBlock, e.NewValue as string ?? string.Empty);
            }
        }

        /// <summary>ANSI を解釈して TextBlock の Inlines / TextHighlighters を構築する</summary>
        private static void Render(TextBlock textBlock, string text)
        {
            textBlock.TextHighlighters.Clear();

            // エスケープを含まない行は Text 直接設定の高速パス（Inlines は Text 設定で置き換わる）
            if (!AnsiEscape.Contains(text))
            {
                textBlock.Text = text;
                return;
            }

            textBlock.Inlines.Clear();
            Dictionary<Color, TextHighlighter>? highlighters = null;
            var offset = 0; // 全 Run 連結テキスト上の文字オフセット（TextHighlighter の範囲指定用）

            foreach (var segment in AnsiEscape.Parse(text))
            {
                if (segment.Text.Length == 0)
                {
                    continue;
                }

                var (foreground, background) = ResolveColors(segment);

                var run = new Run { Text = segment.Text };
                if (foreground is { } fg)
                {
                    run.Foreground = GetBrush(fg);
                }
                if (segment.Style.HasFlag(AnsiStyle.Bold))
                {
                    run.FontWeight = FontWeights.Bold;
                }
                if (segment.Style.HasFlag(AnsiStyle.Italic))
                {
                    run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                }
                var decorations = Windows.UI.Text.TextDecorations.None;
                if (segment.Style.HasFlag(AnsiStyle.Underline))
                {
                    decorations |= Windows.UI.Text.TextDecorations.Underline;
                }
                if (segment.Style.HasFlag(AnsiStyle.Strikethrough))
                {
                    decorations |= Windows.UI.Text.TextDecorations.Strikethrough;
                }
                if (decorations != Windows.UI.Text.TextDecorations.None)
                {
                    run.TextDecorations = decorations;
                }
                textBlock.Inlines.Add(run);

                if (background is { } bg)
                {
                    highlighters ??= [];
                    if (!highlighters.TryGetValue(bg, out var highlighter))
                    {
                        highlighter = new TextHighlighter { Background = GetBrush(bg) };
                        highlighters.Add(bg, highlighter);
                    }
                    highlighter.Ranges.Add(new TextRange { StartIndex = offset, Length = segment.Text.Length });
                }
                offset += segment.Text.Length;
            }

            if (highlighters is not null)
            {
                foreach (var highlighter in highlighters.Values)
                {
                    textBlock.TextHighlighters.Add(highlighter);
                }
            }
        }

        /// <summary>セグメントの実効前景色/背景色を解決する（null は既定色のまま＝装飾なし）</summary>
        private static (Color? Foreground, Color? Background) ResolveColors(in AnsiSegment segment)
        {
            Color? foreground = segment.ForegroundIndex >= 0 ? Palette[segment.ForegroundIndex] : null;
            Color? background = segment.BackgroundIndex >= 0 ? Palette[segment.BackgroundIndex] : null;

            if (segment.Style.HasFlag(AnsiStyle.Reverse))
            {
                // 反転：前景と背景を入れ替える。未指定側は Campbell 既定色で解決
                (foreground, background) = (background ?? DefaultBackground, foreground ?? DefaultForeground);
            }

            if (segment.Style.HasFlag(AnsiStyle.Dim))
            {
                // 淡色：前景色の輝度を 60% に落として表現（背景は固定の暗色のため乗算で近似できる）
                var color = foreground ?? DefaultForeground;
                foreground = Color.FromArgb(color.A,
                    (byte)(color.R * 6 / 10), (byte)(color.G * 6 / 10), (byte)(color.B * 6 / 10));
            }

            return (foreground, background);
        }

        private static SolidColorBrush GetBrush(Color color)
        {
            if (!BrushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                BrushCache.Add(color, brush);
            }
            return brush;
        }
    }
}
