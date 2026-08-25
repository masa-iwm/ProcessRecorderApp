using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace ProcessRecorderApp.Components;

/// <summary>
/// MPD 1 枚を組むのに要る値。<b>すべて呼び出し側が決めた実測値で、既定は無い。</b>
/// </summary>
/// <param name="Timescale">1 秒あたりの刻み数（<c>mdhd</c>）。</param>
/// <param name="Codecs"><c>avc1.PPCCLL</c>。</param>
/// <param name="Width">幅(px)。</param>
/// <param name="Height">高さ(px)。</param>
/// <param name="Fps">フレームレート(fps)。</param>
/// <param name="BitrateKbps">ビットレート(kbit/sec)。<c>bandwidth</c> は これ×1000。</param>
/// <param name="AvailabilityStartTimeUtc">この連続体が始まった時刻。</param>
/// <param name="PublishTimeUtc">この MPD を組んだ時刻。</param>
/// <param name="Generation">連続体の通し番号（<c>Period</c> の id になる）。</param>
/// <param name="PresentationTimeOffset">最初のセグメントの <c>t</c>。</param>
/// <param name="Segments">セグメントの (開始時刻, 長さ)（古い順）。</param>
public sealed record DashManifestInput(
    uint Timescale,
    string Codecs,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    DateTimeOffset AvailabilityStartTimeUtc,
    DateTimeOffset PublishTimeUtc,
    int Generation,
    ulong PresentationTimeOffset,
    IReadOnlyList<(ulong Time, ulong Duration)> Segments);

/// <summary>
/// ライブ用の MPD（<c>profiles=urn:mpeg:dash:profile:isoff-live:2011</c>）を組む純関数。
///
/// <para>
/// <b><c>SegmentTemplate</c> ＋ <c>SegmentTimeline</c> の形にする。</b>
/// <c>$Number$</c> と <c>duration</c> の形は「セグメントが等長」を前提にするが、
/// ここのセグメントは IDR の間隔で切れるので等長にならない ── 時刻を明示しないと、
/// クライアントは存在しないセグメントを要求し続ける。
/// </para>
/// <para>
/// <b>URL は相対</b>（manifest と同じディレクトリ）。絶対 URL を書くと、
/// リバースプロキシ越しに配ったときにホスト名が合わなくなる。
/// </para>
/// <para>
/// <b>空の MPD は出さない。</b> セグメントが 1 つも無い MPD は「そういう配信」として
/// 解釈されうるので、呼び出し側が「まだ始まっていない」と答えられるように例外にする。
/// </para>
/// </summary>
public static class DashManifest
{
    /// <summary>初期化セグメントの相対 URL（<c>SegmentTemplate.initialization</c>）。</summary>
    public const string InitializationTemplate = "init.mp4";

    /// <summary>メディアセグメントの相対 URL（<c>$Time$</c> は DASH の識別子置換）。</summary>
    public const string MediaTemplate = "seg-$Time$.m4s";

    /// <summary>
    /// 時刻の書式。<b><c>"o"</c> は使わない</b> ── 小数以下 7 桁が付き、
    /// DASH の実装によっては読めない（<c>xs:dateTime</c> としては正しいのに落ちる）。
    /// </summary>
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>MPD 1 枚を組む。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> が null。</exception>
    /// <exception cref="ArgumentException">セグメントが 1 つも無い。</exception>
    public static string Build(DashManifestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Segments.Count == 0)
            throw new ArgumentException("a dynamic MPD needs at least one segment", nameof(input));

        // **バイト列を経由する。** StringWriter へ書くと XML 宣言が utf-16 を名乗り、
        // それを UTF-8 で配ると厳密なパーサーはそこで落ちる。
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        using var bytes = new MemoryStream();
        using (var writer = XmlWriter.Create(bytes, settings))
        {
            writer.WriteStartElement("MPD", "urn:mpeg:dash:schema:mpd:2011");
            writer.WriteAttributeString("type", "dynamic");
            writer.WriteAttributeString("profiles", "urn:mpeg:dash:profile:isoff-live:2011");
            writer.WriteAttributeString("minimumUpdatePeriod", "PT1S");
            writer.WriteAttributeString("suggestedPresentationDelay", "PT2S");
            writer.WriteAttributeString("timeShiftBufferDepth", "PT6S");
            writer.WriteAttributeString("minBufferTime", "PT1S");
            writer.WriteAttributeString("availabilityStartTime", Format(input.AvailabilityStartTimeUtc));
            writer.WriteAttributeString("publishTime", Format(input.PublishTimeUtc));

            writer.WriteStartElement("Period");
            writer.WriteAttributeString("id", Number(input.Generation));
            writer.WriteAttributeString("start", "PT0S");

            writer.WriteStartElement("AdaptationSet");
            writer.WriteAttributeString("mimeType", "video/mp4");
            writer.WriteAttributeString("codecs", input.Codecs);
            writer.WriteAttributeString("segmentAlignment", "true");

            writer.WriteStartElement("Representation");
            writer.WriteAttributeString("id", "v");
            writer.WriteAttributeString("bandwidth", Number((long)input.BitrateKbps * 1000));
            writer.WriteAttributeString("width", Number(input.Width));
            writer.WriteAttributeString("height", Number(input.Height));
            writer.WriteAttributeString("frameRate", Number(input.Fps));

            writer.WriteStartElement("SegmentTemplate");
            writer.WriteAttributeString("timescale", Number(input.Timescale));
            writer.WriteAttributeString("initialization", InitializationTemplate);
            writer.WriteAttributeString("media", MediaTemplate);
            writer.WriteAttributeString("presentationTimeOffset", Number(input.PresentationTimeOffset));

            writer.WriteStartElement("SegmentTimeline");
            foreach ((ulong time, ulong duration) in input.Segments)
            {
                writer.WriteStartElement("S");
                writer.WriteAttributeString("t", Number(time));
                writer.WriteAttributeString("d", Number(duration));
                writer.WriteEndElement();
            }
            writer.WriteEndElement();   // SegmentTimeline

            writer.WriteEndElement();   // SegmentTemplate
            writer.WriteEndElement();   // Representation
            writer.WriteEndElement();   // AdaptationSet
            writer.WriteEndElement();   // Period
            writer.WriteEndElement();   // MPD
        }

        return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
    }

    private static string Format(DateTimeOffset value)
        => value.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}
