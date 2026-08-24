using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// プレビュー配信の 4 設定（幅・高さ・フレームレート・ビットレート）の丸め。
///
/// <para>
/// <b>丸めるのは設定オブジェクトの setter である</b> ── リモートの PATCH は
/// 範囲を検査せず、そのまま代入して 200 を返す（<c>BufferDuration</c> と同じ経路）。
/// ここが効かないと、範囲外の値がそのまま配信側の caps とエンコーダーへ渡る。
/// </para>
/// </summary>
public class PreviewSettingsClampTests
{
    [Theory]
    [InlineData(160, 160)]
    [InlineData(1280, 1280)]
    [InlineData(3840, 3840)]
    [InlineData(159, 160)]
    [InlineData(0, 160)]
    [InlineData(int.MinValue, 160)]
    [InlineData(3841, 3840)]
    [InlineData(int.MaxValue, 3840)]
    public void Width_IsClampedToItsRange(int input, int expected)
        => Assert.Equal(expected, EventRecorderSettings.ClampPreviewWidth(input));

    [Theory]
    [InlineData(120, 120)]
    [InlineData(720, 720)]
    [InlineData(2160, 2160)]
    [InlineData(119, 120)]
    [InlineData(int.MinValue, 120)]
    [InlineData(2161, 2160)]
    [InlineData(int.MaxValue, 2160)]
    public void Height_IsClampedToItsRange(int input, int expected)
        => Assert.Equal(expected, EventRecorderSettings.ClampPreviewHeight(input));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(15, 15)]
    [InlineData(60, 60)]
    [InlineData(0, 1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(61, 60)]
    [InlineData(999, 60)]
    [InlineData(int.MaxValue, 60)]
    public void Fps_IsClampedToItsRange(int input, int expected)
        => Assert.Equal(expected, EventRecorderSettings.ClampPreviewFps(input));

    [Theory]
    [InlineData(100, 100)]
    [InlineData(2000, 2000)]
    [InlineData(20_000, 20_000)]
    [InlineData(99, 100)]
    [InlineData(int.MinValue, 100)]
    [InlineData(20_001, 20_000)]
    [InlineData(int.MaxValue, 20_000)]
    public void BitrateKbps_IsClampedToItsRange(int input, int expected)
        => Assert.Equal(expected, EventRecorderSettings.ClampPreviewBitrateKbps(input));

    [Fact]
    public void TheBoundsAreTheOnesTheDescriptionsPromise()
    {
        Assert.Equal(160, EventRecorderSettings.MinPreviewWidth);
        Assert.Equal(3840, EventRecorderSettings.MaxPreviewWidth);
        Assert.Equal(120, EventRecorderSettings.MinPreviewHeight);
        Assert.Equal(2160, EventRecorderSettings.MaxPreviewHeight);
        Assert.Equal(1, EventRecorderSettings.MinPreviewFps);
        Assert.Equal(60, EventRecorderSettings.MaxPreviewFps);
        Assert.Equal(100, EventRecorderSettings.MinPreviewBitrateKbps);
        Assert.Equal(20_000, EventRecorderSettings.MaxPreviewBitrateKbps);
    }

    [Fact]
    public void Defaults_Are1280x720At15FpsAnd2000Kbps()
    {
        var settings = new EventRecorderSettings();
        Assert.Equal(1280, settings.PreviewWidth);
        Assert.Equal(720, settings.PreviewHeight);
        Assert.Equal(15, settings.PreviewFps);
        Assert.Equal(2000, settings.PreviewBitrateKbps);
    }

    // ---- 設定オブジェクトの setter でも丸まること（3 箇所すべてで丸める設計） ----

    [Fact]
    public void Settings_Setters_ClampOutOfRangeValues()
    {
        var settings = new EventRecorderSettings
        {
            PreviewWidth = int.MaxValue,
            PreviewHeight = -1,
            PreviewFps = 999,
            PreviewBitrateKbps = 1,
        };

        Assert.Equal(EventRecorderSettings.MaxPreviewWidth, settings.PreviewWidth);
        Assert.Equal(EventRecorderSettings.MinPreviewHeight, settings.PreviewHeight);
        Assert.Equal(EventRecorderSettings.MaxPreviewFps, settings.PreviewFps);
        Assert.Equal(EventRecorderSettings.MinPreviewBitrateKbps, settings.PreviewBitrateKbps);
    }

    /// <summary>
    /// <b>丸めた値で PropertyChanged が飛ぶこと。</b> <c>[ObservableProperty]</c> の
    /// <c>OnXChanged</c> で丸め直す形にすると、丸め後の値が現在値と一致した場合に
    /// 通知が飛ばず、UI が範囲外の値を表示したまま残る（<c>BufferDuration</c> と同じ罠）。
    /// </summary>
    [Fact]
    public void Settings_Setter_RaisesPropertyChangedWithTheClampedValue()
    {
        var settings = new EventRecorderSettings();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.PreviewFps = 999;

        Assert.Contains(nameof(EventRecorderSettings.PreviewFps), raised);
        Assert.Equal(EventRecorderSettings.MaxPreviewFps, settings.PreviewFps);
    }

    /// <summary>
    /// <b>プレビューの 4 設定は「再初期化が要る」一覧に載せない。</b>
    /// 録画パイプラインの組み立てには一切関わらないので、載せると
    /// 「今の録画には効かない」という嘘の助言が PATCH の応答に出る。
    /// </summary>
    [Fact]
    public void ThePreviewSettings_DoNotRequireReinitialize()
    {
        foreach (string name in new[]
                 {
                     nameof(EventRecorderSettings.PreviewWidth),
                     nameof(EventRecorderSettings.PreviewHeight),
                     nameof(EventRecorderSettings.PreviewFps),
                     nameof(EventRecorderSettings.PreviewBitrateKbps),
                 })
        {
            Assert.DoesNotContain(name,
                EventRecorderSettings.PropertiesRequiringReinitialize, StringComparer.Ordinal);
        }
    }
}
