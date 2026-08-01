using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="SrcPipelineBuilder"/> の解析（<see cref="SrcPipelineBuilder.Parse"/>）と
/// 再生成（<see cref="SrcPipelineBuilder.Assemble"/>）。
///
/// 中心的な契約は「パイプライン編集ダイアログで開いて何も変えずに OK した場合、
/// 元の文字列と等価なものが返る」＝ Parse→Assemble のラウンドトリップである。
/// ここが壊れると、ユーザーがダイアログを開いただけで録画設定が黙って変わる。
///
/// <see cref="SrcPipelineBuilder.Sources"/> は表示名をローカライズリソースから取るため
/// <c>Lazy</c> 化されている。本テストがインプロセスで走ること自体が、
/// WinAppSDK 未ブートストラップのプロセスでもカタログを触れることの検証になっている。
/// </summary>
public class SrcPipelineBuilderTests
{
    // ---- カタログ ----

    [Theory]
    [InlineData("d3d12screencapturesrc")]
    [InlineData("mfvideosrc")]
    [InlineData("d3d12testsrc")]
    [InlineData("videotestsrc")]
    public void Sources_ContainsTheSupportedElement(string elementName)
        => Assert.NotNull(SrcPipelineBuilder.FindSource(elementName));

    [Fact]
    public void FindSource_UnknownElement_ReturnsNull()
        => Assert.Null(SrcPipelineBuilder.FindSource("v4l2src"));

    [Fact]
    public void FindSource_Null_ReturnsNull()
        => Assert.Null(SrcPipelineBuilder.FindSource(null));

    // ---- Parse ----

    [Fact]
    public void Parse_Null_ReturnsEmptyResult()
    {
        var parsed = SrcPipelineBuilder.Parse(null);
        Assert.Null(parsed.SourceElement);
        Assert.False(parsed.HasCaps);
        Assert.Empty(parsed.Properties);
        Assert.Empty(parsed.CapsFields);
    }

    [Fact]
    public void Parse_Whitespace_ReturnsEmptyResult()
        => Assert.Null(SrcPipelineBuilder.Parse("   ").SourceElement);

    [Fact]
    public void Parse_SourceWithProperties_ExtractsElementAndProperties()
    {
        var parsed = SrcPipelineBuilder.Parse("d3d12screencapturesrc monitor-index=1 show-cursor=true");

        Assert.Equal("d3d12screencapturesrc", parsed.SourceElement);
        Assert.Equal("1", parsed.Properties["monitor-index"]);
        Assert.Equal("true", parsed.Properties["show-cursor"]);
        Assert.False(parsed.HasCaps);
    }

    [Fact]
    public void Parse_Caps_ExtractsFieldsAndMemoryFeature()
    {
        var parsed = SrcPipelineBuilder.Parse(
            "d3d12testsrc is-live=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=1280, height=720, framerate=15/1");

        Assert.True(parsed.HasCaps);
        Assert.Equal("memory:D3D12Memory", parsed.MemoryFeature);
        Assert.Equal("NV12", parsed.CapsFields["format"]);
        Assert.Equal("1280", parsed.CapsFields["width"]);
        Assert.Equal("720", parsed.CapsFields["height"]);
        Assert.Equal("15/1", parsed.CapsFields["framerate"]);
    }

    [Fact]
    public void Parse_CapsWithoutMemoryFeature_LeavesMemoryFeatureNull()
    {
        var parsed = SrcPipelineBuilder.Parse("videotestsrc ! video/x-raw, format=I420");

        Assert.True(parsed.HasCaps);
        Assert.Null(parsed.MemoryFeature);
    }

    [Fact]
    public void Parse_MultipleCapsSegments_MergesFields()
    {
        var parsed = SrcPipelineBuilder.Parse(
            "videotestsrc ! video/x-raw, format=I420 ! video/x-raw, framerate=30/1");

        Assert.Equal("I420", parsed.CapsFields["format"]);
        Assert.Equal("30/1", parsed.CapsFields["framerate"]);
    }

    [Fact]
    public void Parse_QuotedValueContainingSpaces_IsUnquotedAsOneValue()
    {
        var parsed = SrcPipelineBuilder.Parse("mfvideosrc device-name=\"Integrated Camera (front)\"");

        Assert.Equal("Integrated Camera (front)", parsed.Properties["device-name"]);
    }

    [Fact]
    public void Parse_QuotedValueContainingComma_IsUnquotedAsOneValue()
    {
        var parsed = SrcPipelineBuilder.Parse("mfvideosrc device-name=\"Cam, rear\"");

        Assert.Equal("Cam, rear", parsed.Properties["device-name"]);
    }

    [Fact]
    public void Parse_ElementsAfterTheSource_AreReportedAsIntermediate()
    {
        var parsed = SrcPipelineBuilder.Parse("videotestsrc ! videoconvert ! videoscale");

        Assert.Equal("videotestsrc", parsed.SourceElement);
        Assert.Equal(new[] { "videoconvert", "videoscale" }, parsed.IntermediateElements);
    }

    [Fact]
    public void Parse_UnknownSourceElement_IsStillParsedButNotInTheCatalog()
    {
        var parsed = SrcPipelineBuilder.Parse("v4l2src device=/dev/video0");

        Assert.Equal("v4l2src", parsed.SourceElement);
        Assert.Equal("/dev/video0", parsed.Properties["device"]);
        Assert.Null(SrcPipelineBuilder.FindSource(parsed.SourceElement));
    }

    // ---- Assemble ----

    [Fact]
    public void Assemble_OmitsEmptyPropertyValues()
    {
        var def = SrcPipelineBuilder.FindSource("videotestsrc")!;

        string result = SrcPipelineBuilder.Assemble(
            def, capsEnabled: false, new[] { ("is-live", "true"), ("pattern", "") }, new Dictionary<string, string>());

        Assert.Equal("videotestsrc is-live=true", result);
    }

    [Fact]
    public void Assemble_QuotesValuesContainingSpaceOrComma()
    {
        var def = SrcPipelineBuilder.FindSource("mfvideosrc")!;

        string result = SrcPipelineBuilder.Assemble(
            def, capsEnabled: false, new[] { ("device-name", "Integrated Camera") }, new Dictionary<string, string>());

        Assert.Equal("mfvideosrc device-name=\"Integrated Camera\"", result);
    }

    [Fact]
    public void Assemble_DoesNotQuoteValuesWithoutSpaceOrComma()
    {
        var def = SrcPipelineBuilder.FindSource("mfvideosrc")!;

        string result = SrcPipelineBuilder.Assemble(
            def, capsEnabled: false, new[] { ("device-index", "0") }, new Dictionary<string, string>());

        Assert.Equal("mfvideosrc device-index=0", result);
    }

    [Fact]
    public void Assemble_ExpandsResolutionIntoWidthAndHeight()
    {
        var def = SrcPipelineBuilder.FindSource("videotestsrc")!;

        string result = SrcPipelineBuilder.Assemble(
            def, capsEnabled: true, [], new Dictionary<string, string> { ["resolution"] = "1280x720" });

        Assert.Equal("videotestsrc ! video/x-raw, width=1280, height=720", result);
    }

    [Fact]
    public void Assemble_EmitsTheMemoryFeatureEvenWhenNoCapsFieldIsSet()
    {
        var def = SrcPipelineBuilder.FindSource("d3d12screencapturesrc")!;

        string result = SrcPipelineBuilder.Assemble(
            def, capsEnabled: true, [], new Dictionary<string, string>());

        Assert.Equal("d3d12screencapturesrc ! video/x-raw(memory:D3D12Memory)", result);
    }

    [Fact]
    public void Assemble_WithoutMemoryFeatureAndWithoutFields_EmitsNoCaps()
    {
        var def = SrcPipelineBuilder.FindSource("videotestsrc")!;

        string result = SrcPipelineBuilder.Assemble(
            def, capsEnabled: true, [], new Dictionary<string, string>());

        Assert.Equal("videotestsrc", result);
    }

    [Fact]
    public void Assemble_CapsDisabled_EmitsNoCapsEvenWithValues()
    {
        var def = SrcPipelineBuilder.FindSource("d3d12testsrc")!;

        string result = SrcPipelineBuilder.Assemble(
            def, capsEnabled: false, [], new Dictionary<string, string> { ["format"] = "NV12" });

        Assert.Equal("d3d12testsrc", result);
    }

    // ---- Parse → Assemble ラウンドトリップ ----

    /// <summary>
    /// 4 ソースそれぞれについて、既定値どおりに組んだ文字列が
    /// Parse → Assemble を通しても同一文字列に戻ることを確認する。
    /// </summary>
    [Theory]
    [InlineData("d3d12screencapturesrc monitor-index=0 show-cursor=false ! video/x-raw(memory:D3D12Memory), framerate=15/1")]
    [InlineData("mfvideosrc device-index=0 ! video/x-raw, format=NV12, width=1920, height=1080, framerate=15/1")]
    [InlineData("d3d12testsrc is-live=true do-timestamp=true pattern=smpte ! video/x-raw(memory:D3D12Memory), format=NV12, width=1280, height=720, framerate=15/1")]
    [InlineData("videotestsrc is-live=true do-timestamp=true pattern=smpte ! video/x-raw, format=I420, width=1280, height=720, framerate=15/1")]
    public void RoundTrip_PreservesTheOriginalPipeline(string pipeline)
        => Assert.Equal(pipeline, RoundTrip(pipeline));

    [Fact]
    public void RoundTrip_PreservesQuotedValues()
    {
        const string pipeline = "mfvideosrc device-index=1 device-name=\"Integrated Camera\" ! video/x-raw, format=NV12";

        Assert.Equal(pipeline, RoundTrip(pipeline));
    }

    [Fact]
    public void RoundTrip_PreservesNonDefaultPropertyValues()
    {
        const string pipeline = "d3d12screencapturesrc monitor-index=2 show-cursor=true ! video/x-raw(memory:D3D12Memory), framerate=30/1";

        Assert.Equal(pipeline, RoundTrip(pipeline));
    }

    /// <summary>
    /// ダイアログが行う「解析 → 既存値をそのまま採用 → 再生成」を再現する。
    /// UI 側（PipelineBuilderDialog）と同じ手順であることが本テストの前提。
    /// </summary>
    private static string RoundTrip(string pipeline)
    {
        var parsed = SrcPipelineBuilder.Parse(pipeline);
        var def = SrcPipelineBuilder.FindSource(parsed.SourceElement);
        Assert.NotNull(def);

        // 要素プロパティ: カタログ定義の順に、解析できたものだけを採用する
        var properties = def.Properties
            .Where(p => parsed.Properties.ContainsKey(p.Name))
            .Select(p => (p.Name, parsed.Properties[p.Name]))
            .ToList();

        // caps フィールド: resolution は width/height から合成し直す
        var capsValues = new Dictionary<string, string>();
        foreach (var field in def.CapsFields)
        {
            if (field.IsResolution)
            {
                parsed.CapsFields.TryGetValue("width", out string? w);
                parsed.CapsFields.TryGetValue("height", out string? h);
                if (SrcPipelineBuilder.JoinResolution(w, h) is { } resolution)
                    capsValues[field.Name] = resolution;
            }
            else if (parsed.CapsFields.TryGetValue(field.Name, out string? value))
            {
                capsValues[field.Name] = value;
            }
        }

        return SrcPipelineBuilder.Assemble(def, parsed.HasCaps, properties, capsValues);
    }

    // ---- 解像度の分解／合成 ----

    [Theory]
    [InlineData("1920x1080", "1920", "1080")]
    [InlineData("1280X720", "1280", "720")]
    [InlineData("640×480", "640", "480")]
    [InlineData(" 800x600 ", "800", "600")]
    public void SplitResolution_ParsesSupportedSeparators(string value, string width, string height)
        => Assert.Equal((width, height), SrcPipelineBuilder.SplitResolution(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1920")]
    [InlineData("1920*1080")]
    [InlineData("axb")]
    public void SplitResolution_InvalidValue_ReturnsNulls(string? value)
        => Assert.Equal((null, null), SrcPipelineBuilder.SplitResolution(value));

    [Fact]
    public void JoinResolution_BothPresent_JoinsWithX()
        => Assert.Equal("1920x1080", SrcPipelineBuilder.JoinResolution("1920", "1080"));

    [Theory]
    [InlineData(null, "1080")]
    [InlineData("1920", null)]
    [InlineData("", "1080")]
    [InlineData("1920", "")]
    public void JoinResolution_MissingComponent_ReturnsNull(string? width, string? height)
        => Assert.Null(SrcPipelineBuilder.JoinResolution(width, height));
}
