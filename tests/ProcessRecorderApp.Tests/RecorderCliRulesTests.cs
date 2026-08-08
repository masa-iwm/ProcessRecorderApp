using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="RecorderCliRules"/> ── CLI がレコーダー集合へ適用する選択と整形の規則。
///
/// <para>
/// この規則が壊れると「引数が別のレコーダーを指す」「TAB 区切りの列がずれる」という形で
/// バッチ側が静かに壊れる。従来は発行物 E2E でしか動かせない死角だった
/// （実際にその死角で成立した欠陥が 2 件見つかっている）。
/// </para>
/// </summary>
public class RecorderCliRulesTests
{
    private static readonly string[] Names = ["R1", "R2", "Recorder #1"];

    // ---- 対象解決 ----

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2", 2)]
    [InlineData("3", -1)]   // 範囲外
    [InlineData("-1", -1)]  // 負のインデックス
    public void ResolveTargetIndex_NumericTarget_IsAnIndex(string target, int expected)
        => Assert.Equal(expected, RecorderCliRules.ResolveTargetIndex(Names, target));

    [Theory]
    [InlineData("R2", 1)]
    [InlineData("Recorder #1", 2)]   // 空白を含む名前も1トークンなら解決できる
    [InlineData("r2", -1)]           // 序数比較（大文字小文字を区別する）
    [InlineData("R", -1)]            // 前方一致はしない（完全一致のみ）
    [InlineData("", -1)]
    public void ResolveTargetIndex_NameTarget_IsAnOrdinalExactMatch(string target, int expected)
        => Assert.Equal(expected, RecorderCliRules.ResolveTargetIndex(Names, target));

    [Fact]
    public void ResolveTargetIndex_DuplicateNames_FirstOneWins()
        => Assert.Equal(0, RecorderCliRules.ResolveTargetIndex(new[] { "R", "R" }, "R"));

    /// <summary>
    /// <b>数値は名前へフォールバックしない。</b> 「1」という名前のレコーダーがいても、
    /// 引数 "1" はインデックス 1 を指す ── 両様に解釈すると、同じ引数が構成しだいで
    /// 別のレコーダーを指してしまう。
    /// </summary>
    [Fact]
    public void ResolveTargetIndex_NumericLookingName_IsNotReachableByName()
        => Assert.Equal(1, RecorderCliRules.ResolveTargetIndex(new[] { "1", "other" }, "1"));

    // ---- 出力整形 ----

    [Fact]
    public void FormatRecorderLine_IsNameTabFilename()
        => Assert.Equal("R1\tC:\\rec\\a.mp4", RecorderCliRules.FormatRecorderLine("R1", "C:\\rec\\a.mp4"));

    [Fact]
    public void FormatStatusLine_HasFiveTabSeparatedColumns_WithTheFreeTextLast()
    {
        string line = RecorderCliRules.FormatStatusLine("R1", true, false, "C:\\rec\\a.mp4", "boom");

        Assert.Equal(new[] { "R1", "True", "False", "C:\\rec\\a.mp4", "boom" }, line.Split('\t'));
    }

    /// <summary>
    /// 自由記述の「直近の障害」に TAB・改行が混ざっても、1 レコーダー＝1 行・
    /// 5 列を崩さない（<c>ActivityLog</c> と同じ規約）。
    /// </summary>
    [Fact]
    public void FormatStatusLine_FlattensTabsAndNewlinesInTheError()
    {
        string line = RecorderCliRules.FormatStatusLine(
            "R1", false, false, null, "ERROR: line1\r\nline2\tcolumn");

        Assert.Equal(5, line.Split('\t').Length);
        Assert.DoesNotContain('\n', line);
        Assert.Equal("ERROR: line1 line2 column", line.Split('\t')[4]);
    }

    // ---- 健全判定 ----

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "  ", true)]     // 空白だけの障害文は「障害なし」
    [InlineData(true, "boom", false)]
    [InlineData(false, null, false)]   // 未初期化は障害が無くても不健全
    public void IsHealthy_RequiresInitializedAndNoLastError(bool initialized, string? lastError, bool expected)
        => Assert.Equal(expected, RecorderCliRules.IsHealthy(initialized, lastError));
}
