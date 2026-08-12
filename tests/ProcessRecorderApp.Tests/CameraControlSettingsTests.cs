using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// カメラ制御の設定文字列（レコーダー設定 <c>CameraControls</c>）の書式。
///
/// <para>
/// <b>この機能で自動的に守れるのはここだけである。</b> 実際にカメラへ値が届くかは
/// 開発機にカメラが無いので確かめられず（<c>docs/coverage-gaps.md</c>）、
/// COM 経路はカメラのある機械での手動確認になる。
/// したがってここでは「文字列 ⇔ 値」の規則そのものを固定する。
/// </para>
/// </summary>
public class CameraControlSettingsTests
{
    [Fact]
    public void Parse_ReadsValuesAndAutoFlags()
    {
        var settings = CameraControlSettings.Parse("brightness=128;focus=30;focus-auto=false");

        Assert.Equal(128, settings.Get("brightness").Value);
        Assert.Null(settings.Get("brightness").Auto);
        Assert.Equal(30, settings.Get("focus").Value);
        Assert.False(settings.Get("focus").Auto);
    }

    [Fact]
    public void Parse_IgnoresWhitespaceAndEmptySegments()
    {
        var settings = CameraControlSettings.Parse("  brightness = 128 ;; focus=30;  ");

        Assert.Equal(128, settings.Get("brightness").Value);
        Assert.Equal(30, settings.Get("focus").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_OfNothing_IsEmpty(string? text)
    {
        var settings = CameraControlSettings.Parse(text);

        Assert.True(settings.IsEmpty);
        Assert.Equal("", settings.Format());
    }

    /// <summary>
    /// <b>自動フラグと手動値は同居できる。</b> UI は「自動を外したら最後の手動値へ戻す」
    /// という動きをするので、自動にしているあいだも手動値を覚えておく必要がある。
    /// </summary>
    [Fact]
    public void AutoAndManualValue_CoexistOnTheSameItem()
    {
        var settings = CameraControlSettings.Parse("exposure=-6;exposure-auto=true");

        Assert.Equal(-6, settings.Get("exposure").Value);
        Assert.True(settings.Get("exposure").Auto);
        Assert.Equal("exposure=-6;exposure-auto=true", settings.Format());
    }

    [Fact]
    public void Format_UsesTheCatalogOrder_NotTheInputOrder()
    {
        // 入力は逆順（focus はカタログでは brightness より後ろ）
        var settings = CameraControlSettings.Parse("focus=30;brightness=128");

        // 並びが安定しないと settings.json の差分が毎回揺れる
        Assert.Equal("brightness=128;focus=30", settings.Format());
    }

    [Fact]
    public void RoundTrip_IsStable()
    {
        const string text = "brightness=128;white-balance=4000;white-balance-auto=true;focus=30;focus-auto=false";

        string once = CameraControlSettings.Parse(text).Format();
        string twice = CameraControlSettings.Parse(once).Format();

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// <b>知らないキーも読めない値も捨てない。</b> 新しい版で増えた項目を古い版で開いて
    /// 保存し直したときに消えると、利用者からは「設定が勝手に消えた」としか見えない
    /// （<c>AppSettings.ExtensionData</c> と同じ判断）。
    /// </summary>
    [Theory]
    [InlineData("brightness=128;future-thing=7")]
    [InlineData("brightness=128;focus=not-a-number")]
    [InlineData("brightness=128;focus-auto=maybe")]
    [InlineData("brightness=128;garbage")]
    public void UnknownOrUnreadableEntries_SurviveTheRoundTrip(string text)
    {
        string formatted = CameraControlSettings.Parse(text).Format();

        // 既知の値は正しく読めていること
        Assert.Equal(128, CameraControlSettings.Parse(text).Get("brightness").Value);
        // 読めなかった断片が残っていること
        string tail = text["brightness=128;".Length..];
        Assert.Contains(tail, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_WithAnEmptyValue_RemovesTheItem()
    {
        var settings = CameraControlSettings.Parse("brightness=128;focus=30");

        settings.Set("focus", default);

        Assert.Equal("brightness=128", settings.Format());
        Assert.Null(settings.Get("focus").Value);
    }

    [Fact]
    public void SpecifiedItems_ListsOnlyWhatWasGiven()
    {
        var settings = CameraControlSettings.Parse("focus=30;brightness=128");

        Assert.Equal((string[])["brightness", "focus"], settings.SpecifiedItems.Select(d => d.Key));
    }

    /// <summary>
    /// キーの一致は大文字小文字を区別しない（手で書ける道を残しているため）。
    /// ただし書き出しは常にカタログの表記に揃える。
    /// </summary>
    [Fact]
    public void Keys_AreCaseInsensitiveOnInput_ButCanonicalOnOutput()
    {
        var settings = CameraControlSettings.Parse("Brightness=128;FOCUS-AUTO=true");

        Assert.Equal(128, settings.Get("brightness").Value);
        Assert.Equal("brightness=128;focus-auto=true", settings.Format());
    }

    // ---- Merge（編集 UI が確定するときの合成） ----
    //
    // **ここが無いと UI 経路の取りこぼしを縛れない。** ダイアログは「そのカメラが申告した
    // 項目」しか行に持たないので、確定を新しいインスタンスから組み立てると、未知キーと
    // 「このカメラには無いが設定には書いてある項目」が黙って消える ── 実際に 1 度そうなっていた。

    [Fact]
    public void Merge_KeepsUnknownKeysThatTheDialogNeverSaw()
    {
        // ダイアログが触れるのは brightness だけ。future-thing は行にすら出ない。
        string result = CameraControlSettings.Merge(
            "brightness=64;future-thing=7",
            ((string Key, int Value, bool? Auto)[])[("brightness", 128, (bool?)null)]);

        Assert.Contains("brightness=128", result, StringComparison.Ordinal);
        Assert.Contains("future-thing=7", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>そのカメラが申告しない項目の指定も残す。</b> 別のカメラ用に入れてあった値が、
    /// いま繋がっているカメラで設定画面を開いて OK しただけで消えてはならない。
    /// </summary>
    [Fact]
    public void Merge_KeepsSettingsForItemsTheCameraDoesNotReport()
    {
        // このカメラは focus を持たない（行に出ない）が、設定には書いてある。
        string result = CameraControlSettings.Merge(
            "brightness=64;focus=30;focus-auto=false",
            ((string Key, int Value, bool? Auto)[])[("brightness", 128, (bool?)null)]);

        Assert.Contains("focus=30", result, StringComparison.Ordinal);
        Assert.Contains("focus-auto=false", result, StringComparison.Ordinal);
        Assert.Contains("brightness=128", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_OverwritesTheEditedItems()
    {
        string result = CameraControlSettings.Merge(
            "brightness=64;brightness-auto=true",
            ((string Key, int Value, bool? Auto)[])[("brightness", 200, (bool?)false)]);

        var parsed = CameraControlSettings.Parse(result);
        Assert.Equal(200, parsed.Get("brightness").Value);
        Assert.False(parsed.Get("brightness").Auto);
    }

    [Fact]
    public void Merge_WithNoOriginal_JustWritesTheEditedItems()
    {
        string result = CameraControlSettings.Merge(null, ((string Key, int Value, bool? Auto)[])[("focus", 30, (bool?)true)]);

        Assert.Equal("focus=30;focus-auto=true", result);
    }

    /// <summary>自動を扱えない項目は auto を書かない（null を渡す意味）。</summary>
    [Fact]
    public void Merge_WithNullAuto_DoesNotWriteAnAutoFlag()
    {
        string result = CameraControlSettings.Merge(null, ((string Key, int Value, bool? Auto)[])[("gain", 5, (bool?)null)]);

        Assert.Equal("gain=5", result);
    }

    /// <summary>
    /// カタログの中身そのもののピン。
    /// <b>番号は DirectShow の定義値</b>で、間違えると「隣の意味」の設定が動く
    /// ── しかも動くこと自体は正常に見えるので気付けない。
    /// </summary>
    [Fact]
    public void Catalog_CoversBothInterfaces_WithTheDocumentedPropertyIds()
    {
        var procAmp = CameraControlCatalog.All
            .Where(d => d.Domain == CameraControlDomain.VideoProcAmp).ToArray();
        var cameraControl = CameraControlCatalog.All
            .Where(d => d.Domain == CameraControlDomain.CameraControl).ToArray();

        Assert.Equal(10, procAmp.Length);
        Assert.Equal(7, cameraControl.Length);

        // それぞれの系統で 0 から連番であること（DirectShow の列挙そのもの）
        Assert.Equal(Enumerable.Range(0, 10), procAmp.Select(d => d.PropertyId));
        Assert.Equal(Enumerable.Range(0, 7), cameraControl.Select(d => d.PropertyId));

        // 代表的な対応（取り違えると隣の設定が動く）
        Assert.Equal(0, CameraControlCatalog.Find("brightness")!.PropertyId);
        Assert.Equal(7, CameraControlCatalog.Find("white-balance")!.PropertyId);
        Assert.Equal(6, CameraControlCatalog.Find("focus")!.PropertyId);
        Assert.Equal(4, CameraControlCatalog.Find("exposure")!.PropertyId);

        // キーは重複しない（重複するとダイアログの行が潰れる）
        Assert.Equal(
            CameraControlCatalog.All.Count,
            CameraControlCatalog.All.Select(d => d.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
