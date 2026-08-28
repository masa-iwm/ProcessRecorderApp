using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 一覧 API の問い合わせ規則（<see cref="RecordingQuery"/>）。
///
/// <para>
/// <b>純関数なので HTTP を立てずに固定できる。</b> 絞り込みと日付集計の境界
/// （<c>to</c> は排他・<c>tz</c> はローカル日付）が API の応答をそのまま決める。
/// </para>
/// </summary>
public sealed class RecordingQueryTests
{
    private static RecordingEntry Entry(string path, string recorder, DateTime startUtc)
        => new(path, 1024, startUtc, false, false, startUtc, recorder, 1000, 1920, 1080, false);

    private static DateTime Utc(int year, int month, int day, int hour, int minute)
        => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static IReadOnlyList<RecordingEntry> Sample() => new RecordingEntry[]
    {
        Entry("c.mp4", "cam2", Utc(2026, 8, 28, 23, 0)),
        Entry("b.mp4", "cam1", Utc(2026, 8, 28, 10, 0)),
        Entry("a.mp4", "cam1", Utc(2026, 8, 27, 10, 0)),
    };

    [Fact]
    public void NoConditionReturnsEverything()
    {
        var entries = Sample();

        Assert.Same(entries, RecordingQuery.Filter(entries, null, null, null));
        Assert.Same(entries, RecordingQuery.Filter(entries, null, null, ""));
    }

    [Fact]
    public void FromIsInclusiveAndToIsExclusive()
    {
        var entries = Sample();

        var justFrom = RecordingQuery.Filter(
            entries, new DateTimeOffset(Utc(2026, 8, 28, 10, 0)), null, null);
        Assert.Equal(new[] { "c.mp4", "b.mp4" }, justFrom.Select(static e => e.RelativePath).ToArray());

        var justTo = RecordingQuery.Filter(
            entries, null, new DateTimeOffset(Utc(2026, 8, 28, 10, 0)), null);
        Assert.Equal(new[] { "a.mp4" }, justTo.Select(static e => e.RelativePath).ToArray());
    }

    [Fact]
    public void TheOffsetOfTheBoundIsHonoured()
    {
        var entries = Sample();

        // 2026-08-28T19:00:00+09:00 == 10:00 UTC。
        var from = new DateTimeOffset(2026, 8, 28, 19, 0, 0, TimeSpan.FromHours(9));

        Assert.Equal(2, RecordingQuery.Filter(entries, from, null, null).Count);
    }

    [Fact]
    public void TheRecorderMatchesExactly()
    {
        var entries = Sample();

        Assert.Equal(2, RecordingQuery.Filter(entries, null, null, "cam1").Count);
        Assert.Empty(RecordingQuery.Filter(entries, null, null, "cam"));
        Assert.Empty(RecordingQuery.Filter(entries, null, null, "CAM1"));
    }

    [Fact]
    public void ThePageKeepsTheOrder()
    {
        var entries = Sample();

        Assert.Equal(new[] { "c.mp4" }, RecordingQuery.Page(entries, 1, 0).Select(static e => e.RelativePath).ToArray());
        Assert.Equal(new[] { "b.mp4", "a.mp4" }, RecordingQuery.Page(entries, 2, 1).Select(static e => e.RelativePath).ToArray());
        // limit 無指定は残り全部（従来の応答と同じ）。
        Assert.Equal(3, RecordingQuery.Page(entries, null, 0).Count);
    }

    [Fact]
    public void APageBeyondTheEndIsEmpty()
    {
        var entries = Sample();

        Assert.Empty(RecordingQuery.Page(entries, 10, 3));
        Assert.Empty(RecordingQuery.Page(entries, 10, 99));
        Assert.Equal(3, RecordingQuery.Page(entries, 10, -5).Count);
    }

    [Fact]
    public void TheDaysAreCountedInUtcByDefault()
    {
        var days = RecordingQuery.CountDays(Sample(), TimeZoneInfo.Utc);

        Assert.Equal(new[] { "2026-08-27", "2026-08-28" }, days.Select(static d => d.Date).ToArray());
        Assert.Equal(new[] { 1, 2 }, days.Select(static d => d.Count).ToArray());
    }

    [Fact]
    public void TheDaysFollowTheRequestedOffset()
    {
        // +09:00 では 23:00 UTC が翌日になる。
        Assert.True(RecordingQuery.TryResolveTimeZone("+09:00", out TimeZoneInfo? zone));

        var days = RecordingQuery.CountDays(Sample(), zone!);

        Assert.Equal(new[] { "2026-08-27", "2026-08-28", "2026-08-29" }, days.Select(static d => d.Date).ToArray());
        Assert.Equal(new[] { 1, 1, 1 }, days.Select(static d => d.Count).ToArray());
    }

    [Fact]
    public void AnEmptyTimeZoneIsUtc()
    {
        Assert.True(RecordingQuery.TryResolveTimeZone(null, out TimeZoneInfo? fromNull));
        Assert.Equal(TimeZoneInfo.Utc, fromNull);

        Assert.True(RecordingQuery.TryResolveTimeZone("", out TimeZoneInfo? fromEmpty));
        Assert.Equal(TimeZoneInfo.Utc, fromEmpty);
    }

    [Fact]
    public void ANegativeOffsetIsAccepted()
    {
        Assert.True(RecordingQuery.TryResolveTimeZone("-05:30", out TimeZoneInfo? zone));
        Assert.Equal(TimeSpan.FromMinutes(-330), zone!.BaseUtcOffset);
    }

    [Fact]
    public void AWindowsTimeZoneIdIsAccepted()
    {
        // InvariantGlobalization=true でも Windows の ID は解決できる（IANA は解決できない）。
        Assert.True(RecordingQuery.TryResolveTimeZone("Tokyo Standard Time", out TimeZoneInfo? zone));
        Assert.Equal(TimeSpan.FromHours(9), zone!.BaseUtcOffset);
    }

    [Theory]
    [InlineData("+9")]
    [InlineData("09:00")]
    [InlineData("+09:60")]
    [InlineData("+15:00")]
    [InlineData("-15:00")]
    [InlineData("+ab:cd")]
    [InlineData("Nowhere Standard Time")]
    public void AnUnresolvableTimeZoneIsRejected(string tz)
        => Assert.False(RecordingQuery.TryResolveTimeZone(tz, out _));
}
