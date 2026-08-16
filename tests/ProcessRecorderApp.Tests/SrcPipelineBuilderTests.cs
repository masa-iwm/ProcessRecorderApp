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
    [InlineData("d3d11screencapturesrc")]
    [InlineData("mfvideosrc")]
    [InlineData("d3d12testsrc")]
    [InlineData("videotestsrc")]
    public void Sources_ContainsTheSupportedElement(string elementName)
        => Assert.NotNull(SrcPipelineBuilder.FindSource(elementName));

    /// <summary>
    /// <b><c>capture-api</c> は画面キャプチャ 2 種にだけ、条件付きで載っていること。</b>
    ///
    /// <para>
    /// このプロパティは GStreamer の <c>conditionally available</c> ── Windows Graphics
    /// Capture を組み込んだビルドにしか登録されない（同梱ランタイムの MSVC 版には在り、
    /// MinGW 版には無い。実測）。したがって
    /// <see cref="SrcPropertyDef.ConditionallyAvailable"/> が立っていなければならない
    /// ── 立っていないと UI は無条件に行を出し、<b>MinGW 版のランタイムで
    /// <c>no property "capture-api"</c> のパイプラインを組み立ててしまう</b>。
    /// </para>
    /// <para>
    /// <b>2 種に同じ綴りで載っていること</b>も固定する ── 片方だけだと、
    /// ソースを D3D12 ⇔ D3D11 で切り替えた瞬間に <see cref="SrcPipelineBuilder.CarryOver"/>
    /// が名前照合で落とす。カメラやテストソースには存在しないので、載っていたら誤り。
    /// </para>
    /// <para>
    /// <b>「実行時に無いときに値を持ち越す」ことはここでは見られない</b>
    /// ── その分岐は <c>PipelineBuilderViewModel</c>（WinUI アプリ側）に在り、
    /// L1 からは参照できない。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("d3d12screencapturesrc", true)]
    [InlineData("d3d11screencapturesrc", true)]
    [InlineData("mfvideosrc", false)]
    [InlineData("d3d12testsrc", false)]
    [InlineData("videotestsrc", false)]
    public void CaptureApi_IsDeclaredConditionallyOnTheScreenCaptureSourcesOnly(string elementName, bool expected)
    {
        SrcElementDef source = Assert.IsType<SrcElementDef>(SrcPipelineBuilder.FindSource(elementName));
        SrcPropertyDef? def = source.Properties.FirstOrDefault(p => p.Name == "capture-api");

        if (!expected)
        {
            Assert.Null(def);
            return;
        }

        Assert.NotNull(def);
        Assert.True(def.ConditionallyAvailable,
            $"{elementName} の capture-api に ConditionallyAvailable が立っていない。"
            + "WGC 非対応のランタイムで存在しないプロパティを書き込むことになる。");
        Assert.Equal(SrcPropertyKind.Enum, def.Kind);
        Assert.Equal("dxgi", def.DefaultValue);
        Assert.Equal(["dxgi", "wgc"], Assert.IsType<string[]>(def.EnumChoices));
    }

    /// <summary>
    /// D3D11 版の画面キャプチャは <c>memory:D3D11Memory</c> を名乗ること。
    ///
    /// <para>
    /// このメモリ機能は <b>録画種別の両方で通ることを実測して選んでいる</b> ──
    /// <c>Type=D3d12</c> は <c>d3d12upload</c> が D3D11Memory を受け、<c>Type=System</c> は
    /// D3D11 のメモリが CPU からマップできるので <c>videoconvert</c> が受ける。
    /// ここをシステムメモリ（機能なし）へ変えると、画面全体を毎フレーム
    /// CPU へ読み戻す経路に黙って変わるので、値を固定しておく。
    /// </para>
    /// </summary>
    [Fact]
    public void D3d11ScreenCapture_UsesD3d11Memory()
    {
        var def = SrcPipelineBuilder.FindSource("d3d11screencapturesrc");

        Assert.NotNull(def);
        Assert.Equal("memory:D3D11Memory", def.MemoryFeature);
        Assert.Contains(def.Properties, p => p.Name == "show-cursor");
        Assert.Contains(def.Properties, p => p.Name == "monitor-index" && p.DynamicKey == "monitor-index");
        Assert.Contains(def.CapsFields, c => c.Name == "resolution" && c.DynamicKey == "monitor-resolution");
    }

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
    public void Parse_QuotedValueContainingBang_IsNotSplitAtTheBang()
    {
        // '!' は引用値の中に実在するデバイス表示名（例: Creative "Live! Cam" 系）経由で入る。
        // gst_parse_launch は引用内の '!' を値として扱うので、ここで割ると
        // 「録画は通るのにビルダーで開き直すと設定が壊れる」形になる。
        var parsed = SrcPipelineBuilder.Parse(
            "mfvideosrc device-name=\"Live! Cam Sync HD\" ! video/x-raw, width=640, height=480");

        Assert.Equal("mfvideosrc", parsed.SourceElement);
        Assert.Equal("Live! Cam Sync HD", parsed.Properties["device-name"]);
        Assert.True(parsed.HasCaps);
        Assert.Equal("640", parsed.CapsFields["width"]);
        Assert.Empty(parsed.IntermediateElements);
    }

    [Fact]
    public void Assemble_DoesNotQuoteOrdinaryCapsValues()
    {
        // 数値・分数・列挙は引用対象にならない（従来の出力と1バイトも変わらないこと）
        var def = SrcPipelineBuilder.FindSource("videotestsrc")!;

        string result = SrcPipelineBuilder.Assemble(
            def, capsEnabled: true, [],
            new Dictionary<string, string> { ["format"] = "I420", ["framerate"] = "15/1" });

        Assert.Equal("videotestsrc ! video/x-raw, format=I420, framerate=15/1", result);
    }

    [Fact]
    public void RoundTrip_CapsValueContainingASeparator_IsPreserved()
    {
        // caps 値を無引用で連結すると、以降のトークンの意味が変わって
        // パイプラインの構造そのものが壊れる（要素プロパティと同じ規則で引用する）
        var def = SrcPipelineBuilder.FindSource("videotestsrc")!;

        string assembled = SrcPipelineBuilder.Assemble(
            def, capsEnabled: true, [],
            new Dictionary<string, string> { ["format"] = "odd value" });
        var parsed = SrcPipelineBuilder.Parse(assembled);

        Assert.Equal("videotestsrc", parsed.SourceElement);
        Assert.Empty(parsed.IntermediateElements);
        Assert.Equal("odd value", parsed.CapsFields["format"]);
    }

    [Fact]
    public void RoundTrip_DeviceNameWithBangQuotesAndBackslash_IsPreserved()
    {
        var def = SrcPipelineBuilder.FindSource("mfvideosrc")!;
        const string name = "Live! \"Cam\" \\ Sync";

        string assembled = SrcPipelineBuilder.Assemble(
            def, capsEnabled: false, new[] { ("device-name", name) }, new Dictionary<string, string>());
        var parsed = SrcPipelineBuilder.Parse(assembled);

        Assert.Equal("mfvideosrc", parsed.SourceElement);
        Assert.Equal(name, parsed.Properties["device-name"]);
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

    // ---- ソース切り替え時の引き継ぎ（CarryOver） ----

    /// <summary>
    /// 画面キャプチャの D3D12 版 → D3D11 版。<b>項目の顔ぶれが同じなので全部運ぶ。</b>
    /// ここが落ちると、ソースを切り替えるたびにモニター番号と解像度を入れ直すことになる。
    /// </summary>
    [Fact]
    public void CarryOver_BetweenTheTwoScreenCaptures_KeepsEveryField()
    {
        var target = SrcPipelineBuilder.FindSource("d3d11screencapturesrc");
        Assert.NotNull(target);

        var carried = SrcPipelineBuilder.CarryOver(
            target,
            new Dictionary<string, string> { ["monitor-index"] = "2", ["show-cursor"] = "true" },
            new Dictionary<string, string> { ["resolution"] = "3840x2160", ["framerate"] = "30/1" },
            capsEnabled: true);

        Assert.Equal("d3d11screencapturesrc", carried.SourceElement);
        Assert.Equal("2", carried.Properties["monitor-index"]);
        Assert.Equal("true", carried.Properties["show-cursor"]);
        Assert.Equal("3840", carried.CapsFields["width"]);
        Assert.Equal("2160", carried.CapsFields["height"]);
        Assert.Equal("30/1", carried.CapsFields["framerate"]);
        Assert.True(carried.HasCaps);
    }

    /// <summary>
    /// 引き継ぎ先に無い項目は運ばない。<b>その要素では意味を成さない値が残ると
    /// パイプラインが黙って壊れる</b>ため、名前の一致だけを条件にする。
    /// </summary>
    [Fact]
    public void CarryOver_DropsFieldsTheTargetDoesNotHave()
    {
        var target = SrcPipelineBuilder.FindSource("d3d12screencapturesrc");
        Assert.NotNull(target);

        var carried = SrcPipelineBuilder.CarryOver(
            target,
            new Dictionary<string, string> { ["device-name"] = "HD Pro Webcam C920", ["monitor-index"] = "1" },
            new Dictionary<string, string> { ["format"] = "NV12", ["framerate"] = "15/1" },
            capsEnabled: true);

        Assert.False(carried.Properties.ContainsKey("device-name"));
        Assert.Equal("1", carried.Properties["monitor-index"]);
        // d3d12screencapturesrc の caps は解像度と framerate だけ（format は持たない）
        Assert.False(carried.CapsFields.ContainsKey("format"));
        Assert.Equal("15/1", carried.CapsFields["framerate"]);
    }

    /// <summary>
    /// 読めない解像度は運ばない。<b>幅だけが入った中途半端な状態</b>を作らないため。
    /// </summary>
    [Fact]
    public void CarryOver_UnparsableResolution_IsNotCarried()
    {
        var target = SrcPipelineBuilder.FindSource("d3d11screencapturesrc");
        Assert.NotNull(target);

        var carried = SrcPipelineBuilder.CarryOver(
            target,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["resolution"] = "3840" },
            capsEnabled: true);

        Assert.False(carried.CapsFields.ContainsKey("width"));
        Assert.False(carried.CapsFields.ContainsKey("height"));
    }

    /// <summary>caps を出さない設定は引き継ぎでも保たれる。</summary>
    [Fact]
    public void CarryOver_KeepsTheCapsEnabledState()
    {
        var target = SrcPipelineBuilder.FindSource("d3d11screencapturesrc");
        Assert.NotNull(target);

        var carried = SrcPipelineBuilder.CarryOver(
            target, new Dictionary<string, string>(), new Dictionary<string, string>(), capsEnabled: false);

        Assert.False(carried.HasCaps);
    }

    /// <summary>
    /// 引き継いだ結果をそのまま組み直すと、<b>要素名だけが変わった同じ設定</b>になること。
    /// ダイアログが行う「切り替え → 再生成」を通しで再現する。
    /// </summary>
    [Fact]
    public void CarryOver_ThenAssemble_ChangesOnlyTheElementName()
    {
        const string before = "d3d12screencapturesrc monitor-index=2 show-cursor=true ! video/x-raw(memory:D3D12Memory), width=3840, height=2160, framerate=30/1";
        var parsed = SrcPipelineBuilder.Parse(before);
        var target = SrcPipelineBuilder.FindSource("d3d11screencapturesrc");
        Assert.NotNull(target);

        var capsValues = new Dictionary<string, string>
        {
            ["resolution"] = SrcPipelineBuilder.JoinResolution(
                parsed.CapsFields["width"], parsed.CapsFields["height"]) ?? "",
            ["framerate"] = parsed.CapsFields["framerate"],
        };
        var carried = SrcPipelineBuilder.CarryOver(target, parsed.Properties, capsValues, capsEnabled: true);

        string after = SrcPipelineBuilder.Assemble(
            target,
            capsEnabled: carried.HasCaps,
            properties: target.Properties.Select(p => (p.Name, carried.Properties.TryGetValue(p.Name, out var v) ? v : "")),
            capsValues: new Dictionary<string, string>
            {
                ["resolution"] = SrcPipelineBuilder.JoinResolution(
                    carried.CapsFields["width"], carried.CapsFields["height"]) ?? "",
                ["framerate"] = carried.CapsFields["framerate"],
            });

        Assert.Equal(
            "d3d11screencapturesrc monitor-index=2 show-cursor=true ! video/x-raw(memory:D3D11Memory), width=3840, height=2160, framerate=30/1",
            after);
    }

    // ---- Parse → Assemble ラウンドトリップ ----

    /// <summary>
    /// 5 ソースそれぞれについて、既定値どおりに組んだ文字列が
    /// Parse → Assemble を通しても同一文字列に戻ることを確認する。
    /// </summary>
    [Theory]
    [InlineData("d3d12screencapturesrc monitor-index=0 show-cursor=false ! video/x-raw(memory:D3D12Memory), framerate=15/1")]
    [InlineData("d3d11screencapturesrc monitor-index=0 show-cursor=true ! video/x-raw(memory:D3D11Memory), width=3840, height=2160, framerate=15/1")]
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
