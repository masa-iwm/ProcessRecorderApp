using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// ライブ MPD の中身。<b>読み戻して属性で確かめる</b>（文字列の部分一致では、
/// 属性が別の要素へ移っても緑のままになる）。
///
/// <para>
/// <b>時刻の書式が効いている。</b> <c>"o"</c>（小数以下 7 桁）で書くと、
/// <c>xs:dateTime</c> としては正しいのに読めない実装がある ── 落ちるのは
/// ブラウザ側なので、ここで固定しないと気付けない。
/// </para>
/// </summary>
public sealed class DashManifestTests
{
    private static readonly XNamespace Mpd = "urn:mpeg:dash:schema:mpd:2011";

    private static DashManifestInput Input(
        IReadOnlyList<(ulong Time, ulong Duration)>? segments = null, int generation = 3)
        => new(
            Timescale: 90_000,
            Codecs: "avc1.64001F",
            Width: 1280,
            Height: 720,
            Fps: 15,
            BitrateKbps: 2500,
            AvailabilityStartTimeUtc: new DateTimeOffset(2026, 8, 25, 1, 2, 3, TimeSpan.Zero),
            PublishTimeUtc: new DateTimeOffset(2026, 8, 25, 1, 2, 9, TimeSpan.Zero),
            Generation: generation,
            PresentationTimeOffset: 90_000,
            Segments: segments ?? ((ulong Time, ulong Duration)[])[(90_000UL, 90_000UL), (180_000UL, 45_000UL)]);

    private static XElement Root(DashManifestInput input) => XDocument.Parse(DashManifest.Build(input)).Root!;

    [Fact]
    public void TheMpdCarriesTheLiveProfileAndTheTimingWindow()
    {
        var root = Root(Input());

        Assert.Equal(Mpd + "MPD", root.Name);
        Assert.Equal("dynamic", (string?)root.Attribute("type"));
        Assert.Equal("urn:mpeg:dash:profile:isoff-live:2011", (string?)root.Attribute("profiles"));
        Assert.Equal("PT1S", (string?)root.Attribute("minimumUpdatePeriod"));
        Assert.Equal("PT2S", (string?)root.Attribute("suggestedPresentationDelay"));
        Assert.Equal("PT6S", (string?)root.Attribute("timeShiftBufferDepth"));
        Assert.Equal("PT1S", (string?)root.Attribute("minBufferTime"));
    }

    /// <summary>
    /// 時刻は <c>yyyy-MM-ddTHH:mm:ssZ</c>（秒まで・UTC の <c>Z</c> 付き）。
    /// </summary>
    [Fact]
    public void TheTimesUseTheSecondPrecisionUtcFormat()
    {
        var root = Root(Input());

        Assert.Equal("2026-08-25T01:02:03Z", (string?)root.Attribute("availabilityStartTime"));
        Assert.Equal("2026-08-25T01:02:09Z", (string?)root.Attribute("publishTime"));
    }

    /// <summary>入力が別のオフセットで来ても UTC へ直して書くこと。</summary>
    [Fact]
    public void ANonUtcInputIsNormalisedToUtc()
    {
        var input = Input() with
        {
            AvailabilityStartTimeUtc = new DateTimeOffset(2026, 8, 25, 10, 2, 3, TimeSpan.FromHours(9)),
        };

        Assert.Equal("2026-08-25T01:02:03Z", (string?)Root(input).Attribute("availabilityStartTime"));
    }

    [Fact]
    public void ThePeriodIsIdentifiedByTheGeneration()
    {
        var period = Root(Input(generation: 7)).Element(Mpd + "Period")!;

        Assert.Equal("7", (string?)period.Attribute("id"));
        Assert.Equal("PT0S", (string?)period.Attribute("start"));
    }

    [Fact]
    public void TheAdaptationSetAndRepresentationCarryTheEncodedShape()
    {
        var adaptationSet = Root(Input()).Element(Mpd + "Period")!.Element(Mpd + "AdaptationSet")!;

        Assert.Equal("video/mp4", (string?)adaptationSet.Attribute("mimeType"));
        Assert.Equal("avc1.64001F", (string?)adaptationSet.Attribute("codecs"));
        Assert.Equal("true", (string?)adaptationSet.Attribute("segmentAlignment"));

        var representation = adaptationSet.Element(Mpd + "Representation")!;
        Assert.Equal("v", (string?)representation.Attribute("id"));
        Assert.Equal("2500000", (string?)representation.Attribute("bandwidth"));
        Assert.Equal("1280", (string?)representation.Attribute("width"));
        Assert.Equal("720", (string?)representation.Attribute("height"));
        Assert.Equal("15", (string?)representation.Attribute("frameRate"));
    }

    /// <summary>
    /// テンプレートは<b>相対 URL</b>（manifest と同じディレクトリ）。
    /// 絶対 URL を書くと、リバースプロキシ越しでホスト名が合わなくなる。
    /// </summary>
    [Fact]
    public void TheSegmentTemplateUsesRelativeUrls()
    {
        var template = Root(Input())
            .Element(Mpd + "Period")!.Element(Mpd + "AdaptationSet")!
            .Element(Mpd + "Representation")!.Element(Mpd + "SegmentTemplate")!;

        Assert.Equal("90000", (string?)template.Attribute("timescale"));
        Assert.Equal("init.mp4", (string?)template.Attribute("initialization"));
        Assert.Equal("seg-$Time$.m4s", (string?)template.Attribute("media"));
        Assert.Equal("90000", (string?)template.Attribute("presentationTimeOffset"));
    }

    /// <summary><c>SegmentTimeline</c> の <c>S</c> は入力と 1:1・同じ並び。</summary>
    [Fact]
    public void EverySegmentBecomesAnSElement()
    {
        var timeline = Root(Input(segments: ((ulong Time, ulong Duration)[])[(10UL, 20UL), (30UL, 40UL), (70UL, 5UL)]))
            .Descendants(Mpd + "SegmentTimeline").Single();

        var pairs = timeline.Elements(Mpd + "S")
            .Select(s => ((string?)s.Attribute("t"), (string?)s.Attribute("d")))
            .ToArray();

        Assert.Equal([("10", "20"), ("30", "40"), ("70", "5")], pairs);
    }

    /// <summary>
    /// <b>空の MPD は出さない。</b> セグメントが 1 つも無い MPD は
    /// 「そういう配信」として解釈されうる ── 呼び出し側が
    /// 「まだ始まっていない」と答えられるように例外にする。
    /// </summary>
    [Fact]
    public void AnEmptyTimelineThrows()
    {
        Assert.Throws<ArgumentException>(() => DashManifest.Build(Input(segments: ((ulong Time, ulong Duration)[])[])));
    }

    /// <summary>宣言の encoding は UTF-8（UTF-16 を名乗ると厳密なパーサーが落ちる）。</summary>
    [Fact]
    public void TheDeclarationAnnouncesUtf8()
    {
        Assert.Contains("encoding=\"utf-8\"", DashManifest.Build(Input()), StringComparison.OrdinalIgnoreCase);
    }
}
