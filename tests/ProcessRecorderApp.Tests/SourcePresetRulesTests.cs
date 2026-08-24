using ProcessRecorderApp.Components;
using ProcessRecorderApp.GStreamer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// リモートから来たソースのテンプレートを受け付ける規則（<see cref="SourcePresetRules"/>）。
///
/// <para>
/// <b>ここが唯一の防波堤である。</b> <c>PUT /api/recorders/{id}/source</c> は
/// レコーダー設定の拒否リストを通らずに <c>SrcPipeline</c> を書く経路で、
/// 通す値を間違えると<b>LAN から任意の GStreamer パイプラインを実行できる</b>。
/// </para>
/// </summary>
public sealed class SourcePresetRulesTests
{
    // ---- 録画種別の導出 ----

    /// <summary>
    /// メモリ機能 → 録画種別。<b><c>D3D11Memory</c> は <c>System</c> 側</b>
    /// ── 「null でなければ D3D12」にすると、画面キャプチャ（D3D11）を選んだだけで
    /// 種別が変わる（D3D11 のメモリは CPU からマップできるので videoconvert が受ける）。
    /// </summary>
    [Theory]
    [InlineData(null, SourcePresetRules.RecordingTypeSystem)]
    [InlineData("", SourcePresetRules.RecordingTypeSystem)]
    [InlineData("memory:D3D11Memory", SourcePresetRules.RecordingTypeSystem)]
    [InlineData("memory:D3D12Memory", SourcePresetRules.RecordingTypeD3d12)]
    public void RecordingTypeFor_DerivesTheTypeFromTheMemoryFeature(string? memoryFeature, string expected)
        => Assert.Equal(expected, SourcePresetRules.RecordingTypeFor(memoryFeature));

    /// <summary>
    /// <b>カタログの全要素で導出が成立すること。</b> 導出は「含む／含まない」の 1 行なので、
    /// 表に無いメモリ機能が増えた日に黙って <c>System</c> へ落ちる
    /// ── 実際の値で確かめる。
    /// </summary>
    [Fact]
    public void RecordingTypeFor_AgreesWithTheCatalog()
    {
        var sources = SrcPipelineBuilder.Sources;
        Assert.True(3 < sources.Length, $"カタログが {sources.Length} 件しかありません。");

        foreach (var def in sources)
        {
            string expected = def.ElementName.StartsWith("d3d12", StringComparison.Ordinal)
                ? SourcePresetRules.RecordingTypeD3d12
                : SourcePresetRules.RecordingTypeSystem;

            Assert.Equal(expected, SourcePresetRules.RecordingTypeFor(def.MemoryFeature));
        }
    }

    /// <summary>
    /// <b>録画種別の綴りは <see cref="EventRecordingType"/> の列挙子名そのもの。</b>
    /// <c>PUT /api/recorders/{id}/source</c> はこの文字列を <c>Type</c> の値として書き込み、
    /// settings.json にも名前で入る ── 綴りがずれると「適用は 200 なのに種別だけ既定へ落ちる」
    /// という形で黙って壊れる（<c>kind</c> の綴りと同じ流儀で、こちらも実際の名前と照合する）。
    /// </summary>
    [Fact]
    public void RecordingTypeNames_MatchTheEnumMemberNames()
    {
        Assert.Equal(nameof(EventRecordingType.System), SourcePresetRules.RecordingTypeSystem);
        Assert.Equal(nameof(EventRecordingType.D3d12), SourcePresetRules.RecordingTypeD3d12);

        // 導出が答える値は、必ずそのどちらかである（列挙子が増えたら気付けるように件数も見る）。
        string[] fromEnum = [.. Enum.GetNames<EventRecordingType>().Order(StringComparer.Ordinal)];
        string[] fromRules =
        [
            .. new[] { SourcePresetRules.RecordingTypeSystem, SourcePresetRules.RecordingTypeD3d12 }
                .Order(StringComparer.Ordinal)
        ];
        Assert.Equal(string.Join(", ", fromEnum), string.Join(", ", fromRules));
    }

    // ---- 封筒に出る種別の綴り ----

    /// <summary>
    /// <b><c>SourcesDto</c> の <c>kind</c> は <see cref="SrcPropertyKind"/> の名前と
    /// 過不足なく一致する。</b> ずれると、画面は「文字列入力」として組んだ欄で
    /// 列挙を送ることになり、サーバーは 400 で断り続ける。
    /// </summary>
    [Fact]
    public void PropertyKinds_MatchTheCatalogEnumNames()
    {
        string[] fromEnum = [.. Enum.GetNames<SrcPropertyKind>().Order(StringComparer.Ordinal)];
        string[] fromRules = [.. SourcePresetRules.PropertyKinds.Order(StringComparer.Ordinal)];
        Assert.NotEmpty(fromEnum);
        // 連結して比べる ── 片方だけに在る綴りが失敗の文にそのまま出る。
        Assert.Equal(string.Join(", ", fromEnum), string.Join(", ", fromRules));
    }

    /// <summary>
    /// 写す側（<c>RemoteControlBackend.KindName</c>）が<b>列挙子ごとに 1 本のアーム</b>を
    /// 持つこと。<b>ソーステキストで読む</b> ── あちらは WinUI アプリ側にあり、
    /// L1 からは型として参照できない（<c>RemoteApiRulesDriftTests</c> と同じ形）。
    ///
    /// <para>
    /// <b>収集件数を先に assert する。</b> 正規表現が空振りしたまま「全部在る」を
    /// 通すと、検査そのものが消える。
    /// </para>
    /// </summary>
    [Fact]
    public void TheBackend_MapsEveryKindToItsOwnName()
    {
        string text = File.ReadAllText(
            RepositoryFiles.At("src", "ProcessRecorderApp", "Services", "RemoteControlBackend.cs"));

        var arms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in
                 Regex.Matches(text, @"SrcPropertyKind\.(\w+)\s*=>\s*SourcePresetRules\.Kind(\w+)"))
        {
            arms[match.Groups[1].Value] = match.Groups[2].Value;
        }

        Assert.Equal(Enum.GetNames<SrcPropertyKind>().Length, arms.Count);
        foreach (string name in Enum.GetNames<SrcPropertyKind>())
        {
            Assert.True(arms.TryGetValue(name, out string? mapped), $"{name} のアームがありません。");
            Assert.Equal(name, mapped);
        }
    }

    // ---- 解像度の形 ----

    /// <summary>
    /// <b><see cref="SourcePresetRules.IsResolutionValue"/> は
    /// <c>SrcPipelineBuilder.SplitResolution</c> と同じ集合を受ける。</b>
    /// 規則が <c>Components</c> に写してあるのは、あちらが
    /// <c>GStreamer.GstSharpNet</c> にあって参照できないため
    /// ── 一致は<b>両方を呼んで</b>確かめる。
    /// </summary>
    [Theory]
    [InlineData("1280x720")]
    [InlineData("1280X720")]
    [InlineData("3840 x 2160")]
    [InlineData(" 1280x720 ")]
    [InlineData("1280×720")]
    [InlineData("")]
    [InlineData("1280")]
    [InlineData("1280x")]
    [InlineData("x720")]
    [InlineData("1280x720x30")]
    [InlineData("-1x-1")]
    [InlineData("1280x720 ! filesink")]
    public void IsResolutionValue_AgreesWithSplitResolution(string value)
    {
        var (width, height) = SrcPipelineBuilder.SplitResolution(value);
        Assert.Equal(width is not null && height is not null, SourcePresetRules.IsResolutionValue(value));
    }

    // ---- 検証 ----

    /// <summary>videotestsrc を模した記述（実カタログの形と同じ顔ぶれ）。</summary>
    private static SourceSpec TestSource()
    {
        SourcePropertySpec[] properties =
        [
            new SourcePropertySpec("is-live", SourcePresetRules.KindBool, null),
            new SourcePropertySpec("index", SourcePresetRules.KindInt, null),
            new SourcePropertySpec("pattern", SourcePresetRules.KindEnum, (string[])["smpte", "snow"]),
            new SourcePropertySpec("name", SourcePresetRules.KindString, null),
        ];
        SourceCapsSpec[] capsFields =
        [
            new SourceCapsSpec("format", IsResolution: false),
            new SourceCapsSpec("resolution", IsResolution: true),
        ];
        return new SourceSpec("videotestsrc", properties, capsFields);
    }

    private static Dictionary<string, string> One(string key, string value) => new() { [key] = value };

    private static string Reject(
        IReadOnlyDictionary<string, string>? properties,
        IReadOnlyDictionary<string, string>? caps = null)
    {
        Assert.False(
            SourcePresetRules.Validate(TestSource(), properties, caps, out string? error),
            "受け付けてはいけない値が通りました。");
        return error!;
    }

    private static void Accept(
        IReadOnlyDictionary<string, string>? properties,
        IReadOnlyDictionary<string, string>? caps = null)
    {
        Assert.True(
            SourcePresetRules.Validate(TestSource(), properties, caps, out string? error),
            $"受け付けるべき値が断られました: {error}");
    }

    /// <summary>カタログに無い要素は「未知の要素」で断る（400 の理由がそのまま応答に出る）。</summary>
    [Fact]
    public void AnUnknownElement_IsRefused()
    {
        Assert.False(SourcePresetRules.Validate(null, null, null, out string? error));
        Assert.Equal(SourcePresetRules.UnknownElementError, error);
    }

    /// <summary>定義に無いプロパティ名は<b>名前を添えて</b>断る。</summary>
    [Fact]
    public void AnUnknownProperty_IsRefusedByName()
        => Assert.Contains("location", Reject(One("location", "C:\\x.mp4")), StringComparison.Ordinal);

    /// <summary>定義に無い caps フィールドも同じく断る。</summary>
    [Fact]
    public void AnUnknownCapsField_IsRefusedByName()
        => Assert.Contains("bitrate", Reject(null, One("bitrate", "1")), StringComparison.Ordinal);

    /// <summary>列挙は<b>選択肢のいずれか</b>だけ（選択肢が無ければ何も通さない）。</summary>
    [Fact]
    public void AnEnum_OnlyAcceptsItsChoices()
    {
        Accept(One("pattern", "smpte"));
        Assert.Contains("pattern", Reject(One("pattern", "ball")), StringComparison.Ordinal);

        // 選択肢を持たない列挙（動的候補が読めなかった場合）は、どの値も通さない。
        SourcePropertySpec[] properties = [new SourcePropertySpec("pattern", SourcePresetRules.KindEnum, null)];
        var noChoices = new SourceSpec("videotestsrc", properties, System.Array.Empty<SourceCapsSpec>());
        Assert.False(SourcePresetRules.Validate(noChoices, One("pattern", "smpte"), null, out _));
    }

    /// <summary>真偽値は <c>true</c> / <c>false</c> の 2 語だけ（<c>True</c> も <c>1</c> も通さない）。</summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("True", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    public void ABool_OnlyAcceptsTheTwoWords(string value, bool expected)
        => Assert.Equal(expected, SourcePresetRules.Validate(TestSource(), One("is-live", value), null, out _));

    /// <summary>整数は符号付きの十進だけ。<b>範囲は見ない</b>（カタログが上下限を持たない）。</summary>
    [Theory]
    [InlineData("0", true)]
    [InlineData("-1", true)]
    [InlineData("999999999999", true)]
    [InlineData("1.5", false)]
    [InlineData("0x10", false)]
    [InlineData("", false)]
    public void AnInt_OnlyAcceptsDecimalDigits(string value, bool expected)
        => Assert.Equal(expected, SourcePresetRules.Validate(TestSource(), One("index", value), null, out _));

    /// <summary>解像度の caps は <c>幅x高さ</c> だけ。</summary>
    [Fact]
    public void AResolutionCapsField_NeedsWidthByHeight()
    {
        Accept(null, One("resolution", "1280x720"));
        Assert.Contains("resolution", Reject(null, One("resolution", "1280")), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>パイプラインの構文へ漏れる文字は、種別に関わらず先に落とす。</b>
    /// <c>Assemble</c> の引用に頼らないのが要点 ── 引用の実装が変わった日に、
    /// 値がパイプラインの<b>構造</b>として解釈される形へ戻らないため。
    /// </summary>
    [Theory]
    [InlineData("x ! filesink location=C:\\\\x.mp4")]
    [InlineData("x\nfilesink")]
    [InlineData("x\rfilesink")]
    [InlineData("x\0y")]
    // **引用を免れる形も同じく落とす。** Assemble の引用は caps のリストとレンジを
    // 素通しするので、先頭が '{' / '[' の値は空白ごとパイプラインの構文へ出る
    // （device-name={a} fakesink name=z} で要素を足せる）。
    [InlineData("{a} fakesink name=z}")]
    [InlineData("  {a} fakesink name=z}")]
    [InlineData("[ 1, 30 ]")]
    public void ValuesThatLeakIntoThePipelineSyntax_AreRefused(string value)
    {
        // 自由入力（String）でも断る ── ここを通すと引用だけが最後の砦になる。
        Assert.Contains("name", Reject(One("name", value)), StringComparison.Ordinal);
        // caps 側にも同じ規律が掛かる。
        Assert.Contains("format", Reject(null, One("format", value)), StringComparison.Ordinal);
    }

    /// <summary>何も指定しない要求は通る（＝要素だけのパイプライン）。</summary>
    [Fact]
    public void AnEmptyPreset_IsAccepted()
    {
        Accept(null);
        Accept(new Dictionary<string, string>(), new Dictionary<string, string>());
    }
}
