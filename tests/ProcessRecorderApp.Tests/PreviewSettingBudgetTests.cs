using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// プレビュー配信の 4 設定（<c>PreviewWidth</c> / <c>PreviewHeight</c> / <c>PreviewFps</c> /
/// <c>PreviewBitrateKbps</c>）の範囲を、<b>実装・設定画面の文言・<c>src/README.md</c></b> の
/// 3 面で固定する（<see cref="ContinuousSegmentBudgetTests"/> と同じ方式）。
///
/// <para>
/// 4 つとも上下限が <c>Min*</c> / <c>Max*</c> 定数として在るのに、resw（en/ja）と
/// <c>src/README.md</c> には数値リテラルで書かれている。定数だけ動かすと、画面と文書が
/// 古い範囲を案内し続ける ── 入力は黙って丸められるので、利用者からは
/// 「入れた値が効かない」形で現れる。
/// </para>
/// </summary>
public class PreviewSettingBudgetTests
{
    /// <summary>1 設定ぶんの縛り（下限・上限・丸める関数・文言のキー・README の行頭）。</summary>
    private sealed record Setting(
        int Min, int Max, Func<int, int> Clamp, Func<EventRecorderSettings, int> Read, string ResourceKey, string ReadmeKey);

    /// <summary>
    /// 4 設定の一覧。<b>ここに載っていない設定は無検査である</b>
    /// ── プレビューの設定を増やしたら行を足すこと。
    /// </summary>
    public static TheoryData<string> Names => new()
    {
        "PreviewWidth", "PreviewHeight", "PreviewFps", "PreviewBitrateKbps",
    };

    private static Setting Of(string name) => name switch
    {
        "PreviewWidth" => new Setting(
            EventRecorderSettings.MinPreviewWidth, EventRecorderSettings.MaxPreviewWidth,
            EventRecorderSettings.ClampPreviewWidth, s => s.PreviewWidth,
            "PropDesc_Rec_PreviewWidth", "`PreviewWidth`"),
        "PreviewHeight" => new Setting(
            EventRecorderSettings.MinPreviewHeight, EventRecorderSettings.MaxPreviewHeight,
            EventRecorderSettings.ClampPreviewHeight, s => s.PreviewHeight,
            "PropDesc_Rec_PreviewHeight", "`PreviewHeight`"),
        "PreviewFps" => new Setting(
            EventRecorderSettings.MinPreviewFps, EventRecorderSettings.MaxPreviewFps,
            EventRecorderSettings.ClampPreviewFps, s => s.PreviewFps,
            "PropDesc_Rec_PreviewFps", "`PreviewFps`"),
        "PreviewBitrateKbps" => new Setting(
            EventRecorderSettings.MinPreviewBitrateKbps, EventRecorderSettings.MaxPreviewBitrateKbps,
            EventRecorderSettings.ClampPreviewBitrateKbps, s => s.PreviewBitrateKbps,
            "PropDesc_Rec_PreviewBitrateKbps", "`PreviewBitrateKbps`"),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown preview setting"),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void TheRangeIsOrderedAndTheDefaultSitsInsideIt(string name)
    {
        var setting = Of(name);

        Assert.True(setting.Min < setting.Max, $"{name}: 下限が上限以上になっている。");
        Assert.InRange(setting.Read(new EventRecorderSettings()), setting.Min, setting.Max);
    }

    /// <summary>丸めの正本（<c>Clamp*</c>）そのものを両端で縛る。</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ValuesOutsideTheRange_AreClampedToTheNearerBound(string name)
    {
        var setting = Of(name);

        Assert.Equal(setting.Min, setting.Clamp(int.MinValue));
        Assert.Equal(setting.Min, setting.Clamp(setting.Min - 1));
        Assert.Equal(setting.Max, setting.Clamp(int.MaxValue));
        Assert.Equal(setting.Max, setting.Clamp(setting.Max + 1));
    }

    /// <summary>
    /// 設定のプロパティが範囲外の値を保持せずに丸めること
    /// （3 箇所の手書きミラーのうち、正本である <see cref="EventRecorderSettings"/> の面）。
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void TheSettingsPropertyRoundsInsteadOfStoringOutOfRangeValues(string name)
    {
        var setting = Of(name);
        var settings = new EventRecorderSettings();

        Set(settings, name, int.MaxValue);
        Assert.Equal(setting.Max, setting.Read(settings));

        Set(settings, name, int.MinValue);
        Assert.Equal(setting.Min, setting.Read(settings));
    }

    private static void Set(EventRecorderSettings settings, string name, int value)
    {
        switch (name)
        {
            case "PreviewWidth": settings.PreviewWidth = value; break;
            case "PreviewHeight": settings.PreviewHeight = value; break;
            case "PreviewFps": settings.PreviewFps = value; break;
            case "PreviewBitrateKbps": settings.PreviewBitrateKbps = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(name), name, "unknown preview setting");
        }
    }

    /// <summary>範囲の数字が、利用者に見えている文言（設定画面の説明）と一致すること。</summary>
    [Theory]
    [InlineData("en-US", "PreviewWidth")]
    [InlineData("en-US", "PreviewHeight")]
    [InlineData("en-US", "PreviewFps")]
    [InlineData("en-US", "PreviewBitrateKbps")]
    [InlineData("ja-JP", "PreviewWidth")]
    [InlineData("ja-JP", "PreviewHeight")]
    [InlineData("ja-JP", "PreviewFps")]
    [InlineData("ja-JP", "PreviewBitrateKbps")]
    public void TheRange_AppearsInTheSettingsDescription(string locale, string name)
    {
        var setting = Of(name);

        string text = File.ReadAllText(
            RepositoryFiles.At("src", "ProcessRecorderApp", "Strings", locale, "Resources.resw"));

        int start = text.IndexOf(setting.ResourceKey, StringComparison.Ordinal);
        Assert.True(0 <= start, $"{locale} に {setting.ResourceKey} が無い。");

        int end = text.IndexOf("</data>", start, StringComparison.Ordinal);
        string entry = text[start..end];

        Assert.Contains(setting.Min.ToString(), entry, StringComparison.Ordinal);
        Assert.Contains(setting.Max.ToString(), entry, StringComparison.Ordinal);
    }

    /// <summary>同じ数字が <c>src/README.md</c> の設定表にも書いてあること。</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void TheRange_AppearsInTheImplementationReadme(string name)
    {
        var setting = Of(name);

        string readme = File.ReadAllText(RepositoryFiles.At("src", "README.md"));

        int start = readme.IndexOf(setting.ReadmeKey, StringComparison.Ordinal);
        Assert.True(0 <= start, $"src/README.md に {setting.ReadmeKey} の行が無い。");

        int end = readme.IndexOf('\n', start);
        string row = readme[start..(end < 0 ? readme.Length : end)];

        Assert.Contains(setting.Min.ToString(), row, StringComparison.Ordinal);
        Assert.Contains(setting.Max.ToString(), row, StringComparison.Ordinal);
    }
}
