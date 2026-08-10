using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 常時録画のセグメント長の範囲を、<b>実装・設定画面の文言・<c>src/README.md</c></b> の
/// 3 面で固定する（<see cref="CleanupIntervalBudgetTests"/> と同じ方式）。
///
/// <para>
/// 上限が要るのは、セグメントが長いほど<b>異常終了で失う範囲が広がる</b>ため
/// ── 書き込み中のファイルは常に未確定で、<c>moov</c> が書かれるのは切り替えか終了のときだけ。
/// 下限が要るのは、切り替えのたびに書き出しパイプラインを作り直すため。
/// 定数だけ直して文言を直し忘れると、画面が古い範囲を案内し続ける。
/// </para>
/// </summary>
public class ContinuousSegmentBudgetTests
{
    [Fact]
    public void TheRangeIsOrderedAndTheDefaultSitsInsideIt()
    {
        Assert.True(
            EventRecorderSettings.MinContinuousSegmentSeconds
                < EventRecorderSettings.MaxContinuousSegmentSeconds);

        int fallback = new EventRecorderSettings().ContinuousSegmentSeconds;
        Assert.InRange(
            fallback,
            EventRecorderSettings.MinContinuousSegmentSeconds,
            EventRecorderSettings.MaxContinuousSegmentSeconds);
    }

    /// <summary>
    /// 丸めは 3 箇所（設定・モデル・VM）すべてで同じ関数を通す規約。
    /// ここでは正本である <c>ClampContinuousSegmentSeconds</c> 自体を縛る。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ValuesBelowTheFloor_AreClampedUp(int value)
    {
        Assert.Equal(
            EventRecorderSettings.MinContinuousSegmentSeconds,
            EventRecorderSettings.ClampContinuousSegmentSeconds(value));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    public void ValuesAboveTheCeiling_AreClampedDown(int value)
    {
        Assert.Equal(
            EventRecorderSettings.MaxContinuousSegmentSeconds,
            EventRecorderSettings.ClampContinuousSegmentSeconds(value));
    }

    [Fact]
    public void TheSettingsPropertyRoundsInsteadOfStoringOutOfRangeValues()
    {
        var settings = new EventRecorderSettings { ContinuousSegmentSeconds = int.MaxValue };
        Assert.Equal(EventRecorderSettings.MaxContinuousSegmentSeconds, settings.ContinuousSegmentSeconds);

        settings.ContinuousSegmentSeconds = 0;
        Assert.Equal(EventRecorderSettings.MinContinuousSegmentSeconds, settings.ContinuousSegmentSeconds);
    }

    /// <summary>範囲の数字が、利用者に見えている文言（設定画面の説明）と一致すること。</summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void TheRange_AppearsInTheSettingsDescription(string locale)
    {
        string text = File.ReadAllText(
            RepositoryFiles.At("src", "ProcessRecorderApp", "Strings", locale, "Resources.resw"));

        int start = text.IndexOf("PropDesc_Rec_ContinuousSegmentSeconds", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{locale} に PropDesc_Rec_ContinuousSegmentSeconds が無い。");

        int end = text.IndexOf("</data>", start, StringComparison.Ordinal);
        string entry = text[start..end];

        Assert.Contains(
            EventRecorderSettings.MinContinuousSegmentSeconds.ToString(), entry, StringComparison.Ordinal);
        Assert.Contains(
            EventRecorderSettings.MaxContinuousSegmentSeconds.ToString(), entry, StringComparison.Ordinal);
    }

    /// <summary>同じ数字が <c>src/README.md</c> の設定表にも書いてあること。</summary>
    [Fact]
    public void TheRange_AppearsInTheImplementationReadme()
    {
        string readme = File.ReadAllText(RepositoryFiles.At("src", "README.md"));

        int start = readme.IndexOf("`ContinuousSegmentSeconds`", StringComparison.Ordinal);
        Assert.True(start >= 0, "src/README.md に ContinuousSegmentSeconds の行が無い。");

        int end = readme.IndexOf('\n', start);
        string row = readme[start..(end < 0 ? readme.Length : end)];

        Assert.Contains(
            EventRecorderSettings.MinContinuousSegmentSeconds.ToString(), row, StringComparison.Ordinal);
        Assert.Contains(
            EventRecorderSettings.MaxContinuousSegmentSeconds.ToString(), row, StringComparison.Ordinal);
    }
}
