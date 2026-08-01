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

        /// <summary>Windows Terminal 既定（Campbell スキーム）の 16 色パレット</summary>
        private static readonly Color[] Palette =
        [
            Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C), // 0: Black
            Color.FromArgb(0xFF, 0xC5, 0x0F, 0x1F), // 1: Red
            Color.FromArgb(0xFF, 0x13, 0xA1, 0x0E), // 2: Green
            Color.FromArgb(0xFF, 0xC1, 0x9C, 0x00), // 3: Yellow
            Color.FromArgb(0xFF, 0x00, 0x37, 0xDA), // 4: Blue
            Color.FromArgb(0xFF, 0x88, 0x17, 0x98), // 5: Magenta
            Color.FromArgb(0xFF, 0x3A, 0x96, 0xDD), // 6: Cyan
            Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC), // 7: White
            Color.FromArgb(0xFF, 0x76, 0x76, 0x76), // 8: Bright Black
            Color.FromArgb(0xFF, 0xE7, 0x48, 0x56), // 9: Bright Red
            Color.FromArgb(0xFF, 0x16, 0xC6, 0x0C), // 10: Bright Green
            Color.FromArgb(0xFF, 0xF9, 0xF1, 0xA5), // 11: Bright Yellow
            Color.FromArgb(0xFF, 0x3B, 0x78, 0xFF), // 12: Bright Blue
            Color.FromArgb(0xFF, 0xB4, 0x00, 0x9E), // 13: Bright Magenta
            Color.FromArgb(0xFF, 0x61, 0xD6, 0xD6), // 14: Bright Cyan
            Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2), // 15: Bright White
        ];

        /// <summary>Campbell スキームの既定前景色（#CCCCCC）</summary>
        private static readonly Color DefaultForeground = Palette[7];
        /// <summary>Campbell スキームの既定背景色（#0C0C0C）</summary>
        private static readonly Color DefaultBackground = Palette[0];

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
