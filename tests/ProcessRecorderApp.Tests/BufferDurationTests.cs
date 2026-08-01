using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 事前バッファ長(ms)の検証。
///
/// 検証がないと <c>(ulong)BufferDuration * 1_000_000</c> が負値で巨大な符号なし値になり、
/// リングバッファが一切退避されなくなる（＝設定値ひとつでメモリを枯渇させられる）。
/// </summary>
public class BufferDurationClampTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(10_000, 10_000)]
    [InlineData(600_000, 600_000)]
    public void ValueInRange_IsUnchanged(int input, int expected)
        => Assert.Equal(expected, EventRecorderSettings.ClampBufferDuration(input));

    [Theory]
    [InlineData(-1)]
    [InlineData(-10_000)]
    [InlineData(int.MinValue)]
    public void NegativeValue_IsClampedToMinimum(int input)
        => Assert.Equal(EventRecorderSettings.MinBufferDuration, EventRecorderSettings.ClampBufferDuration(input));

    [Theory]
    [InlineData(600_001)]
    [InlineData(9_999_999)]
    [InlineData(int.MaxValue)]
    public void TooLargeValue_IsClampedToMaximum(int input)
        => Assert.Equal(EventRecorderSettings.MaxBufferDuration, EventRecorderSettings.ClampBufferDuration(input));

    [Fact]
    public void ClampedValue_IsAlwaysSafeForUnsignedNanosecondConversion()
    {
        // リングバッファの退避条件で使う (ulong)value * 1_000_000 が
        // アンダーフロー/オーバーフローしないことの確認
        foreach (var input in new[] { int.MinValue, -1, 0, 10_000, 600_000, int.MaxValue })
        {
            var clamped = EventRecorderSettings.ClampBufferDuration(input);
            Assert.InRange(clamped, EventRecorderSettings.MinBufferDuration, EventRecorderSettings.MaxBufferDuration);
            ulong ns = (ulong)clamped * 1_000_000UL;
            Assert.True(ns <= (ulong)EventRecorderSettings.MaxBufferDuration * 1_000_000UL);
        }
    }

    // ---- 設定オブジェクト側でも丸められること（3箇所すべてで丸める設計） ----

    [Fact]
    public void Settings_Setter_ClampsNegative()
    {
        var settings = new EventRecorderSettings { BufferDuration = -5 };
        Assert.Equal(EventRecorderSettings.MinBufferDuration, settings.BufferDuration);
    }

    [Fact]
    public void Settings_Setter_ClampsTooLarge()
    {
        var settings = new EventRecorderSettings { BufferDuration = int.MaxValue };
        Assert.Equal(EventRecorderSettings.MaxBufferDuration, settings.BufferDuration);
    }

    [Fact]
    public void Settings_DefaultIs10Seconds()
        => Assert.Equal(10_000, new EventRecorderSettings().BufferDuration);

    [Fact]
    public void Settings_Setter_RaisesPropertyChangedWithTheClampedValue()
    {
        var settings = new EventRecorderSettings();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.BufferDuration = int.MaxValue;

        Assert.Contains(nameof(EventRecorderSettings.BufferDuration), raised);
        Assert.Equal(EventRecorderSettings.MaxBufferDuration, settings.BufferDuration);
    }

    [Fact]
    public void Settings_SettingTheSameClampedValueTwice_RaisesPropertyChangedOnlyOnce()
    {
        var settings = new EventRecorderSettings { BufferDuration = EventRecorderSettings.MaxBufferDuration };
        int count = 0;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EventRecorderSettings.BufferDuration))
                count++;
        };

        // どちらも上限に丸められるので、値としては変化しない
        settings.BufferDuration = 700_000;
        settings.BufferDuration = 800_000;

        Assert.Equal(0, count);
    }
}
