using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 自動削除の間隔の上限を、<b>実装・設定画面の文言・<c>src/README.md</c></b> の3面で固定する
/// （<see cref="StopFinalizeBudgetTests"/> と同じ方式）。
///
/// <para>
/// 上限が要るのは <c>Task.Delay</c> が <c>Timer.MaxSupportedTimeout</c>（≒1,193 時間）を
/// 超える値で例外を投げるためで、超えると掃除の周回が例外死して<b>保持期限が恒久的・無音で
/// 効かなくなる</b>。定数だけ直して文言を直し忘れると、画面が古い上限を案内し続ける
/// ── 利用者から見える面がずれる方が、定数がずれるより悪い。
/// </para>
/// <para>
/// <c>RecordingCleanupScheduler</c> は WinUI アプリのプロジェクトにあり L1 から参照できないため、
/// 定数はソースからテキストで取り出す（<c>AppSettingsReloadTests</c> と同じ方式）。
/// </para>
/// </summary>
public class CleanupIntervalBudgetTests
{
    /// <summary><c>Task.Delay</c> が受け付ける上限（<c>Timer.MaxSupportedTimeout</c> = 4,294,967,294ms）。</summary>
    private const int TaskDelayCeilingHours = 1193;

    private static int MaximumIntervalHours()
    {
        string source = File.ReadAllText(RepositoryFiles.At(
            "src", "ProcessRecorderApp", "Services", "RecordingCleanupScheduler.cs"));

        var m = System.Text.RegularExpressions.Regex.Match(
            source, @"public const int MaximumIntervalHours\s*=\s*(\d+)\s*;");

        Assert.True(m.Success,
            "RecordingCleanupScheduler に MaximumIntervalHours が見つからない。"
            + "改名したなら、この検査も一緒に直すこと ── 見つからないまま緑にすると、検査そのものが消える。");

        return int.Parse(m.Groups[1].Value);
    }

    [Fact]
    public void TheMaximumInterval_StaysBelowWhatTaskDelayAccepts()
    {
        int maximum = MaximumIntervalHours();

        Assert.InRange(maximum, 1, TaskDelayCeilingHours - 1);
    }

    /// <summary>
    /// 上限の数字が、利用者に見えている文言（設定画面の説明）と一致すること。
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void TheMaximumInterval_AppearsInTheSettingsDescription(string locale)
    {
        string text = File.ReadAllText(
            RepositoryFiles.At("src", "ProcessRecorderApp", "Strings", locale, "Resources.resw"));

        int start = text.IndexOf("PropDesc_RecordingCleanupIntervalHours", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{locale} に PropDesc_RecordingCleanupIntervalHours が無い。");

        int end = text.IndexOf("</data>", start, StringComparison.Ordinal);
        string entry = text[start..end];

        Assert.Contains(MaximumIntervalHours().ToString(), entry, StringComparison.Ordinal);
    }

    /// <summary>同じ数字が <c>src/README.md</c> の設定表にも書いてあること。</summary>
    [Fact]
    public void TheMaximumInterval_AppearsInTheImplementationReadme()
    {
        string readme = File.ReadAllText(RepositoryFiles.At("src", "README.md"));

        int start = readme.IndexOf("`RecordingCleanupIntervalHours`", StringComparison.Ordinal);
        Assert.True(start >= 0, "src/README.md に RecordingCleanupIntervalHours の行が無い。");

        int end = readme.IndexOf('\n', start);
        string row = readme[start..(end < 0 ? readme.Length : end)];

        Assert.Contains(MaximumIntervalHours().ToString(), row, StringComparison.Ordinal);
    }
}
