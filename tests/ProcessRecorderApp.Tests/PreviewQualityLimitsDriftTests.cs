using ProcessRecorderApp.Components;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="PreviewQualityPresets"/> の上下限 ⇔ <see cref="EventRecorderSettings"/> の
/// <c>Min/MaxPreview*</c>。
///
/// <para>
/// <b>プリセットの解決値も設定値と同じ範囲に収まらなければならない。</b> 解決は
/// <c>Components</c> の中で完結する（設定側の <c>Clamp*</c> を呼べない ── あちらは
/// GStreamer 側の型である）ので、範囲がリテラルで 2 か所に書かれている。
/// 片方だけ動かすと、<b>設定では入らない値をプリセットが配れる</b>形になり、
/// エンコーダーの拒否として現れる。
/// </para>
/// </summary>
public sealed class PreviewQualityLimitsDriftTests
{
    [Fact]
    public void TheResolvedSizeStaysInsideTheSettingRange()
    {
        Assert.Equal(EventRecorderSettings.MinPreviewWidth, PreviewQualityPresets.MinWidth);
        Assert.Equal(EventRecorderSettings.MaxPreviewWidth, PreviewQualityPresets.MaxWidth);
        Assert.Equal(EventRecorderSettings.MinPreviewHeight, PreviewQualityPresets.MinHeight);
        Assert.Equal(EventRecorderSettings.MaxPreviewHeight, PreviewQualityPresets.MaxHeight);
    }

    [Fact]
    public void TheResolvedFramerateStaysInsideTheSettingRange()
    {
        Assert.Equal(EventRecorderSettings.MinPreviewFps, PreviewQualityPresets.MinFps);
        Assert.Equal(EventRecorderSettings.MaxPreviewFps, PreviewQualityPresets.MaxFps);
    }

    /// <summary>
    /// 表のビットレートも設定の範囲に収まること。<b>解決はビットレートを縮めない</b>ので、
    /// 表そのものが範囲外なら誰も丸めない。
    /// </summary>
    [Fact]
    public void EveryPresetBitrateFitsTheSettingRange()
    {
        foreach (var preset in PreviewQualityPresets.All)
        {
            Assert.InRange(
                preset.BitrateKbps,
                EventRecorderSettings.MinPreviewBitrateKbps,
                EventRecorderSettings.MaxPreviewBitrateKbps);
        }
    }
}
