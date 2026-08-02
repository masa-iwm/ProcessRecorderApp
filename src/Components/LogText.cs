using System.Text;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 端末セマンティクスを持つ生ログを、端末以外（クリップボード・ListView）へ出すための整形。
/// 純粋関数のみ。
/// </summary>
public static class LogText
{
    /// <summary>
    /// <c>'\r'</c> を桁カーソルとして解釈し、上書き後の見た目に潰す
    /// （<c>"abcdef\rXY"</c> → <c>"XYcdef"</c>。実端末と同じ結果）。
    ///
    /// <para>
    /// <b>ANSI エスケープを除いた後に掛けること</b> ── エスケープは画面上 0 桁なのに
    /// 文字列としては桁を進めてしまい、上書き位置がずれる。
    /// </para>
    /// </summary>
    public static string FlattenCarriageReturns(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('\r'))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var line = new StringBuilder(80);
        var column = 0;

        foreach (var c in text)
        {
            switch (c)
            {
                case '\n':
                    result.Append(line).Append('\n');
                    line.Clear();
                    column = 0;
                    break;
                case '\r':
                    column = 0;
                    break;
                default:
                    if (column < line.Length)
                    {
                        line[column] = c;
                    }
                    else
                    {
                        line.Append(c);
                    }
                    column++;
                    break;
            }
        }

        return result.Append(line).ToString();
    }

    /// <summary>
    /// 最後の <c>'\r'</c> 以降だけを採る。ListView フォールバック用の簡略版
    /// ── ListView は行を上書きできないので、<b>短い上書きで前の行の末尾が残るケースは再現しない</b>。
    /// 既知の制約（端末表示なら正しく上書きされる）
    /// </summary>
    public static string TakeAfterLastCr(string line)
    {
        var index = line.LastIndexOf('\r');
        return index < 0 ? line : line[(index + 1)..];
    }
}
