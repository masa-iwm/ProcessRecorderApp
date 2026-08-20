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
    public void FormatStatusLine_HasEightTabSeparatedColumns_WithTheFreeTextLast()
    {
        string line = RecorderCliRules.FormatStatusLine(
            "R1", true, false, false, "C:\\rec\\a.mp4",
            RecorderCliRules.ContinuousOn, "C:\\rec\\a_c00001.mp4", "boom");

        Assert.Equal(
            new[] { "R1", "True", "False", "False", "C:\\rec\\a.mp4", "on", "C:\\rec\\a_c00001.mp4", "boom" },
            line.Split('\t'));
    }

    /// <summary>
    /// <b>「録画中」と「復帰待ち」は別の列であること。</b>
    ///
    /// <para>
    /// デバイスが抜けているあいだは「録画中=False・復帰待ち=True」になる ──
    /// この 2 つを 1 列に畳むと、機械からは「いまフレームが録れているか」と
    /// 「復帰したら録り直すか」を区別できなくなる。畳んだ値を持っているのは
    /// ビューモデル側（トグルを切れる状態に保つため）で、CLI はそちらを読まない。
    /// </para>
    /// </summary>
    [Fact]
    public void FormatStatusLine_KeepsRecordingAndTheResumeApart()
    {
        string[] cells = RecorderCliRules.FormatStatusLine(
            "R1", isInitialized: false, isRecording: false, isAwaitingRecoveryResume: true,
            "C:\\rec\\a.mp4", RecorderCliRules.ContinuousOff, null, "boom").Split('\t');

        Assert.Equal("False", cells[2]);   // 録画中
        Assert.Equal("True", cells[3]);    // 復帰待ち
    }

    /// <summary>
    /// 自由記述の「直近の障害」に TAB・改行が混ざっても、1 レコーダー＝1 行・
    /// 8 列を崩さない（<c>ActivityLog</c> と同じ規約）。
    /// </summary>
    [Fact]
    public void FormatStatusLine_FlattensTabsAndNewlinesInTheError()
    {
        string line = RecorderCliRules.FormatStatusLine(
            "R1", false, false, false, null, RecorderCliRules.ContinuousOff, null,
            "ERROR: line1\r\nline2\tcolumn");

        Assert.Equal(8, line.Split('\t').Length);
        Assert.DoesNotContain('\n', line);
        // **末尾から取る。** 契約は「自由記述は最後の列」なので、
        // 列が増えても壊れない書き方にしておく。
        Assert.Equal("ERROR: line1 line2 column", line.Split('\t')[^1]);
    }

    /// <summary>
    /// 常時録画の列は 1 語に畳む（自由記述の列を 2 つ持てないため）。
    /// 「有効なのに回っていない」を <c>off</c> と言ってはいけない ── 起動直後の窓と
    /// 「そもそも設定していない」は別のことである。
    /// </summary>
    [Theory]
    [InlineData(false, false, null, RecorderCliRules.ContinuousOff)]
    [InlineData(false, false, "boom", RecorderCliRules.ContinuousOff)]
    [InlineData(true, true, null, RecorderCliRules.ContinuousOn)]
    [InlineData(true, false, null, RecorderCliRules.ContinuousPending)]
    [InlineData(true, true, "boom", RecorderCliRules.ContinuousError)]
    [InlineData(true, false, "boom", RecorderCliRules.ContinuousError)]
    [InlineData(true, true, "   ", RecorderCliRules.ContinuousOn)]
    public void ContinuousState_IsOneWord(bool enabled, bool running, string? lastError, string expected)
        => Assert.Equal(expected, RecorderCliRules.ContinuousState(enabled, running, lastError));

    /// <summary>
    /// <b>常時録画の障害は健全判定に効かない（隔離契約）。</b> 効かせると、常時録画の
    /// 設定ミスだけで <c>status</c> が終了コード 15 を返し始め、既存のスクリプトが壊れる。
    /// </summary>
    [Fact]
    public void ContinuousTrouble_DoesNotMakeTheRecorderUnhealthy()
        => Assert.True(RecorderCliRules.IsHealthy(isInitialized: true, lastError: null));

    // ---- 健全判定 ----

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "  ", true)]     // 空白だけの障害文は「障害なし」
    [InlineData(true, "boom", false)]
    [InlineData(false, null, false)]   // 未初期化は障害が無くても不健全
    public void IsHealthy_RequiresInitializedAndNoLastError(bool initialized, string? lastError, bool expected)
        => Assert.Equal(expected, RecorderCliRules.IsHealthy(initialized, lastError));
}
