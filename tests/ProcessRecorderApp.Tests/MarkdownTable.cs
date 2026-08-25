namespace ProcessRecorderApp.Tests;

/// <summary>
/// 文書の Markdown 表を「目印の行の後に続く最初の表」として読む。
///
/// <para>
/// 表を読む流儀をここ 1 つに置く ── 表と実装を突き合わせるテストが増えるたびに
/// 自前の走査や正規表現を書くと、<b>表の書式を変えたときに直す場所が増える</b>。
/// </para>
/// </summary>
internal static class MarkdownTable
{
    /// <summary>
    /// <paramref name="path"/> の中で <paramref name="marker"/> で始まる行より後の
    /// 最初の表を、行ごとのセル配列で返す（区切り行 <c>|---|</c> だけ除く。
    /// <b>見出し行は含む</b> ── 列の構成は呼び出し側の関心なので、そちらで弾く）。
    ///
    /// <para>
    /// セルは <c>|</c> で区切ったままなので<b>先頭と末尾は空</b>
    /// （行が <c>|</c> で始まり <c>|</c> で終わるため）── 1 列目は <c>cells[1]</c>。
    /// 目印も表も見つからなければ空を返す（「読めていない」の判定は呼び出し側で行う）。
    /// </para>
    /// </summary>
    internal static List<string[]> RowsAfter(string path, string marker)
    {
        var rows = new List<string[]>();
        bool inTable = false;

        foreach (string line in File.ReadAllLines(path))
        {
            if (!inTable)
            {
                inTable = line.StartsWith(marker, StringComparison.Ordinal);
                continue;
            }
            if (!line.StartsWith('|'))
            {
                if (rows.Count > 0)
                    break; // 表の終わり
                continue;
            }

            string[] cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (2 <= cells.Length && cells[1].StartsWith("---", StringComparison.Ordinal))
                continue;

            rows.Add(cells);
        }

        return rows;
    }
}
