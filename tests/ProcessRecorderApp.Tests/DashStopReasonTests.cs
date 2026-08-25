using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <c>dash.stream-stop</c> の <c>reason=</c> の固定集合を、
/// <b><c>StopReason</c> の定数群・<c>DashPreviewStream.StopReasons</c>・
/// <c>src/README.md</c> の 2 箇所（停止理由の表とログイベント表の列挙）</b>で固定する。
///
/// <para>
/// <b>「この 10 個で全部」は運用への約束である。</b> 理由を増やしたのに表を直し忘れると、
/// 運用側は起きうる理由を数え上げられなくなる ── ログには出るのに文書に無い、
/// という形で静かに食い違う。逆に表の行だけが残ると、二度と起きない理由の待ち受けが残る。
/// </para>
/// <para>
/// 集合の外へ出る道は 2 つある。<c>StopReason</c> に定数を足して
/// <see cref="DashPreviewStream.StopReasons"/> へ入れ忘れる形（リフレクションで両側から見る）と、
/// <c>Teardown("…")</c> / <c>Fault(engine, "…")</c> へ文字列リテラルを直接書く形
/// （ソーステキストから拾って固定集合に属することを見る ── <c>Fault</c> が記録した理由も
/// <c>_faultReason</c> を通って <c>reason=</c> へ出る）。
/// </para>
/// </summary>
public sealed class DashStopReasonTests
{
    /// <summary>停止理由の表の直前に置かれている行（見出しではなく本文の 1 行）。</summary>
    private const string TableMarker = "停止理由（`dash.stream-stop` の `reason=`）";

    /// <summary>表の 1 列目の見出し。</summary>
    private const string ReasonHeader = "理由";

    /// <summary>ログイベント表の <c>dash.stream-stop</c> の行（同じ理由の列挙をもう 1 度持っている）。</summary>
    private const string LogEventRowPrefix = "| `dash.stream-stop` |";

    /// <summary>その行が持つ列挙と個数（<c>`reason=` は `a｜b｜…` の N 種</c>）。</summary>
    private static readonly Regex LogEventReasonListRegex =
        new(@"`reason=` は `([^`]+)` の (\d+) 種", RegexOptions.Compiled);

    /// <summary>
    /// <c>Teardown("…")</c> と <c>Fault(engine, "…")</c> の<b>直書きリテラル</b>。
    /// <c>Fault</c> の第 3 引数（<c>detail=</c>）は理由ではないので拾わない。
    /// </summary>
    private static readonly Regex StopReasonLiteralRegex =
        new(@"\b(?:Teardown\s*\(\s*|Fault\s*\(\s*\w+\s*,\s*)""([^""]*)""", RegexOptions.Compiled);

    /// <summary>
    /// <c>src/README.md</c> の停止理由の表の <c>reason</c> 列
    /// （読み方は終了コード表と同じ <see cref="MarkdownTable"/>）。
    /// </summary>
    private static List<string> ReadmeReasons()
    {
        var reasons = new List<string>();

        foreach (string[] cells in MarkdownTable.RowsAfter(
                     RepositoryFiles.At("src", "README.md"), TableMarker))
        {
            // 先頭と末尾は空（行が | で始まり | で終わるため）: ["", 理由, 起きる条件, ""]
            if (cells.Length < 4 || cells[1] == ReasonHeader)
                continue;

            reasons.Add(cells[1].Trim('`'));
        }

        return reasons;
    }

    /// <summary>
    /// <c>DashPreviewStream.cs</c> のソーステキストで理由として直書きされた文字列。
    /// <b>コメント行は除く</b> ── 素の走査は、その呼び出しを説明しているコメント自身に一致する。
    /// </summary>
    private static List<string> StopReasonLiterals()
    {
        string text = File.ReadAllText(
            RepositoryFiles.At("src", "GStreamer.GstSharpNet", "DashPreviewStream.cs"));

        return [.. StopReasonLiteralRegex.Matches(text)
            .Where(m => !SourceReferences.IsCommentLine(text, m.Index))
            .Select(m => m.Groups[1].Value)];
    }

    /// <summary><c>StopReason</c>（入れ子の private 型）の文字列定数の値。</summary>
    private static string[] StopReasonConstants()
    {
        var nested = typeof(DashPreviewStream).GetNestedType(
            "StopReason", BindingFlags.NonPublic | BindingFlags.Public);
        if (nested is null)
            return [];

        return [.. nested
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];
    }

    /// <summary>実装 → 表。理由を増やしたのに表を直し忘れると落ちる。</summary>
    [Fact]
    public void EveryStopReason_AppearsInTheReadmeTable()
    {
        var rows = ReadmeReasons();

        Assert.True(rows.Count > 0,
            "src/README.md の停止理由の表を読めていない。目印の行か表の形が変わった可能性がある。");

        string[] missing = [.. DashPreviewStream.StopReasons
            .Where(r => !rows.Contains(r, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];

        Assert.True(missing.Length == 0,
            $"src/README.md の停止理由の表に無い reason:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));
    }

    /// <summary>表 → 実装。理由を消した／改名したのに表に残っていると落ちる。</summary>
    [Fact]
    public void EveryReadmeTableRow_NamesAStopReason()
    {
        string[] stale = [.. ReadmeReasons()
            .Where(r => !DashPreviewStream.StopReasons.Contains(r, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];

        Assert.True(stale.Length == 0,
            $"DashPreviewStream.StopReasons に無いのに src/README.md の表に在る reason:{Environment.NewLine}"
            + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    /// 定数群 ⇔ 配列。<b>定数を足して配列へ入れ忘れる</b>と落ちる（その理由は表と照合されないまま
    /// <c>reason=</c> へ出てしまう）。逆に、配列に残った要素の定数を消しても落ちる。
    /// </summary>
    [Fact]
    public void StopReasons_HoldsEveryStopReasonConstant()
    {
        string[] constants = StopReasonConstants();

        Assert.True(constants.Length > 0,
            "DashPreviewStream の入れ子型 StopReason を読めていない（改名・削除された可能性がある）。");

        string[] missing = [.. constants
            .Where(c => !DashPreviewStream.StopReasons.Contains(c, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];

        Assert.True(missing.Length == 0,
            $"StopReason の定数なのに StopReasons 配列に無い:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));

        string[] extra = [.. DashPreviewStream.StopReasons
            .Where(r => !constants.Contains(r, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];

        Assert.True(extra.Length == 0,
            $"StopReason の定数に無いのに StopReasons 配列に在る:{Environment.NewLine}"
            + string.Join(Environment.NewLine, extra));

        Assert.True(constants.Length == DashPreviewStream.StopReasons.Length,
            $"StopReason の定数 {constants.Length} 個に対して StopReasons は "
            + $"{DashPreviewStream.StopReasons.Length} 個（同じ値の定数が 2 つある／配列に重複がある）。");
    }

    /// <summary>
    /// 理由として直書きされた文字列が固定集合の外へ出ていないこと。
    /// <b>現状は 1 件も無い</b>（すべて <c>StopReason</c> の定数を通す）ので、
    /// これは「リテラルで書き足された瞬間に落ちる」ための網である。
    /// </summary>
    [Fact]
    public void EveryStopReasonLiteral_IsOneOfTheStopReasons()
    {
        string[] outside = [.. StopReasonLiterals()
            .Where(r => !DashPreviewStream.StopReasons.Contains(r, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];

        Assert.True(outside.Length == 0,
            "Teardown(\"…\") / Fault(engine, \"…\") が StopReasons の外の文字列を渡している:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, outside)}");
    }

    /// <summary>
    /// ログイベント表の <c>dash.stream-stop</c> の行は、同じ理由の列挙と個数をもう 1 度持っている。
    /// <b>停止理由の表だけを直すと、こちらが古いまま残る。</b>
    /// </summary>
    [Fact]
    public void TheLogEventTableRow_ListsExactlyTheStopReasons()
    {
        string[] matched = [.. File.ReadAllLines(RepositoryFiles.At("src", "README.md"))
            .Where(l => l.StartsWith(LogEventRowPrefix, StringComparison.Ordinal))];

        Assert.True(matched.Length == 1,
            $"src/README.md のログイベント表で「{LogEventRowPrefix}」で始まる行が {matched.Length} 行ある（1 行のはず）。");

        var listed = LogEventReasonListRegex.Match(matched[0]);

        Assert.True(listed.Success,
            $"dash.stream-stop の行から理由の列挙を読めていない（書式が変わった可能性がある）:{Environment.NewLine}{matched[0]}");

        string[] fromRow = [.. listed.Groups[1].Value
            .Split('｜', StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)];
        string[] fromCode = [.. DashPreviewStream.StopReasons.Order(StringComparer.Ordinal)];

        Assert.True(fromRow.SequenceEqual(fromCode, StringComparer.Ordinal),
            $"dash.stream-stop の行の列挙が StopReasons と一致しない。{Environment.NewLine}"
            + $"行: {string.Join('｜', fromRow)}{Environment.NewLine}"
            + $"実装: {string.Join('｜', fromCode)}");

        Assert.True(int.Parse(listed.Groups[2].Value) == fromCode.Length,
            $"dash.stream-stop の行が「{listed.Groups[2].Value} 種」と書いているが、"
            + $"StopReasons は {fromCode.Length} 個ある。");
    }
}
