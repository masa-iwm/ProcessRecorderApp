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
/// <c>transcode.&lt;理由&gt;</c> のログイベント名の固定集合を、
/// <b><c>StopReason</c> の定数群・<c>TranscodeSession.StopReasons</c>・
/// <c>src/README.md</c> の停止理由の表</b>で固定する（<c>DashStopReasonTests</c> と同型）。
///
/// <para>
/// <b>「この 7 個で全部」は運用への約束である。</b> 理由を増やしたのに表を直し忘れると、
/// 運用側は起きうる理由を数え上げられなくなる ── ログには出るのに文書に無い、
/// という形で静かに食い違う。逆に表の行だけが残ると、二度と起きない理由の待ち受けが残る。
/// </para>
/// <para>
/// 集合の外へ出る道は 2 つある。<c>StopReason</c> に定数を足して
/// <see cref="TranscodeSession.StopReasons"/> へ入れ忘れる形（リフレクションで両側から見る）と、
/// <c>Close("…")</c> へ文字列リテラルを直接書く形（ソーステキストから拾う）。
/// </para>
/// </summary>
public sealed class TranscodeStopReasonTests
{
    /// <summary>停止理由の表の直前に置かれている行（見出しではなく本文の 1 行）。</summary>
    private const string TableMarker = "停止理由（ログイベント名 `transcode.<理由>`）";

    /// <summary>表の 1 列目の見出し。</summary>
    private const string ReasonHeader = "理由";

    /// <summary>
    /// <c>Close("…")</c> / <c>Close("…", …)</c> の<b>直書きリテラル</b>
    /// （第 2 引数の <c>detail=</c> は理由ではないので拾わない）。
    /// </summary>
    private static readonly Regex StopReasonLiteralRegex =
        new(@"\bClose\s*\(\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>停止理由を書きうるソース（session 本体と、それを畳む供給元）。</summary>
    private static readonly string[][] Sources =
    [
        ["src", "GStreamer.GstSharpNet", "TranscodeSession.cs"],
        ["src", "GStreamer.GstSharpNet", "TranscodeStreams.cs"],
    ];

    /// <summary><c>src/README.md</c> の停止理由の表の 1 列目。</summary>
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
    /// 理由として直書きされた文字列。<b>コメント行は除く</b>
    /// ── 素の走査は、その呼び出しを説明しているコメント自身に一致する。
    /// </summary>
    private static List<string> StopReasonLiterals()
    {
        var found = new List<string>();

        foreach (string[] segments in Sources)
        {
            string text = File.ReadAllText(RepositoryFiles.At(segments));
            found.AddRange(StopReasonLiteralRegex.Matches(text)
                .Where(m => !SourceReferences.IsCommentLine(text, m.Index))
                .Select(m => m.Groups[1].Value));
        }

        return found;
    }

    /// <summary><c>StopReason</c>（入れ子の private 型）の文字列定数の値。</summary>
    private static string[] StopReasonConstants()
    {
        var nested = typeof(TranscodeSession).GetNestedType(
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
            "src/README.md の録画トランスコードの停止理由の表を読めていない。"
            + "目印の行か表の形が変わった可能性がある。");

        string[] missing = [.. TranscodeSession.StopReasons
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
            .Where(r => !TranscodeSession.StopReasons.Contains(r, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];

        Assert.True(stale.Length == 0,
            $"TranscodeSession.StopReasons に無いのに src/README.md の表に在る reason:{Environment.NewLine}"
            + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    /// 定数群 ⇔ 配列。<b>定数を足して配列へ入れ忘れる</b>と落ちる
    /// （その理由は表と照合されないままログへ出てしまう）。
    /// </summary>
    [Fact]
    public void StopReasons_HoldsEveryStopReasonConstant()
    {
        string[] constants = StopReasonConstants();

        Assert.True(constants.Length > 0,
            "TranscodeSession の入れ子型 StopReason を読めていない（改名・削除された可能性がある）。");

        string[] missing = [.. constants
            .Where(c => !TranscodeSession.StopReasons.Contains(c, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];

        Assert.True(missing.Length == 0,
            $"StopReason の定数なのに StopReasons 配列に無い:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));

        string[] extra = [.. TranscodeSession.StopReasons
            .Where(r => !constants.Contains(r, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];

        Assert.True(extra.Length == 0,
            $"StopReason の定数に無いのに StopReasons 配列に在る:{Environment.NewLine}"
            + string.Join(Environment.NewLine, extra));

        Assert.True(constants.Length == TranscodeSession.StopReasons.Length,
            $"StopReason の定数 {constants.Length} 個に対して StopReasons は "
            + $"{TranscodeSession.StopReasons.Length} 個（同じ値の定数が 2 つある／配列に重複がある）。");
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
            .Where(r => !TranscodeSession.StopReasons.Contains(r, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];

        Assert.True(outside.Length == 0,
            "Close(\"…\") が StopReasons の外の文字列を渡している:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, outside)}");
    }

    /// <summary>
    /// <b>記録するイベント名は必ず <c>transcode.</c> で始まる。</b> 停止の記録が
    /// 別の接頭辞で出ると、運用の絞り込み（<c>dash.*</c> と同じ流儀）から外れる。
    /// </summary>
    [Fact]
    public void TheStopEventNameIsPrefixedWithTranscode()
    {
        string text = File.ReadAllText(
            RepositoryFiles.At("src", "GStreamer.GstSharpNet", "TranscodeSession.cs"));

        Assert.Contains("\"transcode.\" + reason", text, StringComparison.Ordinal);
    }
}
