using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>モニターを位置ではなく安定した識別子で指定する解決規則</b>（<see cref="MonitorSelection"/>）。
///
/// <para>
/// 画面キャプチャ要素にパスを受け取るプロパティは無く、<c>monitor-index</c>（位置依存）と
/// <c>monitor-handle</c>（保存できない実行時ハンドル）しかない。そのため
/// <c>monitor-device-path</c> は<b>アプリの擬似プロパティ</b>で、パイプラインを組む直前に
/// ハンドルへ解決されて消える ── 残ったまま <c>gst_parse_launch</c> へ渡ると
/// <c>no property</c> で落ちる。
/// </para>
/// <para>
/// <b>規則を純粋関数にしてあるのは、ここで全数を縛るためである。</b> 実機のモニター構成に
/// 依存する経路（<see cref="GstIntrospect.GetMonitors"/>）は開発機でも CI でも
/// 中身が保証されず、L2/L3 では「一致しなかった」と「そもそも列挙が空だった」を
/// 撃ち分けられない。
/// </para>
/// </summary>
public class MonitorSelectionTests
{
    /// <summary>実機で観測される形のデバイスパス（物理モニター＋端子ごとに安定する値）。</summary>
    private const string DevicePath =
        @"\\?\DISPLAY#DELA0C5#5&1c2f9a7a&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    /// <summary>
    /// パイプライン文字列の中での書かれ方。'\' を含むので
    /// <c>SrcPipelineBuilder.Assemble</c> は必ず引用し、内部の '\' は '\\' へエスケープする。
    /// </summary>
    private const string QuotedDevicePath =
        @"""\\\\?\\DISPLAY#DELA0C5#5&1c2f9a7a&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}""";

    private const ulong Handle = 0x12345UL; // 74565

    private static MonitorInfo Monitor(int index, string path, ulong handle = Handle) => new()
    {
        Index = index,
        Path = path,
        Handle = handle,
        Resolution = "3840x2160",
    };

    private static MonitorInfo[] Two(ulong handle = Handle) =>
    [
        Monitor(0, @"\\?\DISPLAY#OTHER#4&1&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", handle + 1),
        Monitor(1, DevicePath, handle),
    ];

    private static readonly MonitorInfo[] None = [];

    /// <summary>
    /// 前提の確認 ── 引用された形が実際に生のパスへ戻ること。
    /// ここが崩れると以降の一致判定がすべて無意味になる。
    /// </summary>
    [Fact]
    public void TheQuotedFormInAPipeline_ParsesBackToTheRawPath()
    {
        var parsed = SrcPipelineBuilder.Parse($"d3d12screencapturesrc monitor-device-path={QuotedDevicePath}");

        Assert.Equal(DevicePath, parsed.Properties["monitor-device-path"]);
    }

    // ---- 規則 1: 指定が無ければ何もしない ----

    [Theory]
    [InlineData("d3d12screencapturesrc monitor-index=0 show-cursor=false ! video/x-raw(memory:D3D12Memory), framerate=30/1")]
    [InlineData("d3d11screencapturesrc ! video/x-raw(memory:D3D11Memory)")]
    [InlineData("videotestsrc is-live=true ! identity error-after=30 ! video/x-raw, width=320, height=240")]
    [InlineData("mfvideosrc device-name=\"Live! Cam Sync HD\" ! video/x-raw, format=NV12")]
    [InlineData("")]
    public void WithoutAPathProperty_TheStringIsReturnedUntouched(string pipeline)
    {
        var result = MonitorSelection.Resolve(pipeline, Two());

        // 入力の参照がそのまま返ること（＝分割も再構築もしていない）。
        Assert.Same(pipeline, result.Pipeline);
        Assert.Null(result.Warning);
        Assert.Null(result.Failure);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Null_IsReturnedAsNullWithoutFailing()
    {
        var result = MonitorSelection.Resolve(null, Two());

        Assert.Null(result.Pipeline);
        Assert.Null(result.Warning);
        Assert.Null(result.Failure);
    }

    /// <summary>
    /// <b>既に書かれている <c>monitor-handle</c> には触らない。</b> 手で書いた指定を
    /// 「パスが無いから」という理由で消してよい道理は無い。
    /// </summary>
    [Fact]
    public void AnExistingHandleProperty_IsNotTouched()
    {
        const string pipeline = "d3d12screencapturesrc monitor-handle=99 ! video/x-raw(memory:D3D12Memory)";

        Assert.Same(pipeline, MonitorSelection.Resolve(pipeline, Two()).Pipeline);
    }

    /// <summary>
    /// <b>引用された値の中身は解決の対象にならない。</b> 走査は解析と同じ
    /// <c>KeyValueRegex</c> の非重複マッチなので、引用の内側は独立したトークンにならない。
    /// </summary>
    [Fact]
    public void ThePropertyNameInsideAQuotedValue_IsNotResolved()
    {
        const string pipeline =
            "mfvideosrc device-name=\"monitor-device-path=x\" ! video/x-raw, format=NV12";

        var result = MonitorSelection.Resolve(pipeline, Two());

        Assert.Equal(pipeline, result.Pipeline);
        Assert.Null(result.Failure);
    }

    /// <summary>
    /// 値の無い <c>monitor-device-path=</c> は key=value として読めない。
    /// <b>中途半端に消さない</b> ── 打ち間違いは <c>gst_parse_launch</c> に
    /// 大きな声で落ちてもらう方がよい（黙って別の画面を録るよりまし）。
    /// </summary>
    [Fact]
    public void APathPropertyWithoutAValue_IsLeftForGStreamerToReject()
    {
        const string pipeline = "d3d12screencapturesrc monitor-device-path= ! video/x-raw(memory:D3D12Memory)";

        var result = MonitorSelection.Resolve(pipeline, Two());

        Assert.Equal(pipeline, result.Pipeline);
        Assert.Null(result.Failure);
    }

    // ---- 規則 2: 一致したらハンドルへ置き換え、番号は取り除く ----

    [Fact]
    public void AMatchingPath_BecomesTheHandleAndDropsTheIndex()
    {
        string pipeline =
            $"d3d12screencapturesrc monitor-index=0 monitor-device-path={QuotedDevicePath} show-cursor=true"
            + " ! video/x-raw(memory:D3D12Memory), width=3840, height=2160, framerate=30/1";

        var result = MonitorSelection.Resolve(pipeline, Two());

        Assert.Equal(
            "d3d12screencapturesrc monitor-handle=74565 show-cursor=true"
            + " ! video/x-raw(memory:D3D12Memory), width=3840, height=2160, framerate=30/1",
            result.Pipeline);
        Assert.Null(result.Warning);
        Assert.Null(result.Failure);
    }

    /// <summary>並びが逆でも同じ結果になること（番号が後ろに書かれていても取り除く）。</summary>
    [Fact]
    public void TheIndexIsDropped_WhereverItIsWritten()
    {
        string pipeline =
            $"d3d11screencapturesrc monitor-device-path={QuotedDevicePath} show-cursor=false monitor-index=2"
            + " ! video/x-raw(memory:D3D11Memory), framerate=15/1";

        var result = MonitorSelection.Resolve(pipeline, Two());

        Assert.Equal(
            "d3d11screencapturesrc monitor-handle=74565 show-cursor=false"
            + " ! video/x-raw(memory:D3D11Memory), framerate=15/1",
            result.Pipeline);
    }

    /// <summary>番号がそもそも書かれていない場合も、置き換えだけが起きること。</summary>
    [Fact]
    public void WithoutAnIndex_OnlyThePathTokenIsReplaced()
    {
        string pipeline = $"d3d12screencapturesrc monitor-device-path={QuotedDevicePath}";

        Assert.Equal("d3d12screencapturesrc monitor-handle=74565",
            MonitorSelection.Resolve(pipeline, Two()).Pipeline);
    }

    /// <summary>ハンドルは <c>guint64</c>。上限の値も 10 進のインバリアントで書けること。</summary>
    [Fact]
    public void TheHandleIsWrittenAsAnInvariantDecimal()
    {
        string pipeline = $"d3d12screencapturesrc monitor-device-path={QuotedDevicePath}";

        var result = MonitorSelection.Resolve(pipeline, (MonitorInfo[])[Monitor(0, DevicePath, ulong.MaxValue)]);

        Assert.Equal("d3d12screencapturesrc monitor-handle=18446744073709551615", result.Pipeline);
    }

    /// <summary>
    /// <b>ソース要素より後ろは 1 文字も変えない。</b> caps も中間要素も
    /// <c>SrcPipelineBuilder.Assemble</c> の往復では落ちるので、そこが唯一の実装制約になっている。
    /// </summary>
    [Fact]
    public void EverythingAfterTheSourceElement_IsBytewiseIdentical()
    {
        const string tail =
            " ! video/x-raw(memory:D3D12Memory), width=3840, height=2160, framerate=30/1"
            + " ! identity error-after=30 ! videoconvert ! video/x-raw, format=I420";
        string pipeline = $"d3d12screencapturesrc monitor-index=1 monitor-device-path={QuotedDevicePath}{tail}";

        var result = MonitorSelection.Resolve(pipeline, Two());

        Assert.NotNull(result.Pipeline);
        int cut = result.Pipeline.IndexOf('!');
        Assert.Equal(tail, result.Pipeline[(cut - 1)..]);
        Assert.Equal("d3d12screencapturesrc monitor-handle=74565", result.Pipeline[..(cut - 1)]);
    }

    /// <summary>
    /// ソース要素の他のプロパティは、値も並びも綴りもそのまま残ること
    /// （余分な空白を作らないことも含む）。
    /// </summary>
    [Fact]
    public void TheOtherPropertiesOfTheSource_KeepTheirOrderAndSpacing()
    {
        string pipeline =
            $"d3d12screencapturesrc show-cursor=true monitor-index=3 capture-api=wgc monitor-device-path={QuotedDevicePath}";

        Assert.Equal("d3d12screencapturesrc show-cursor=true capture-api=wgc monitor-handle=74565",
            MonitorSelection.Resolve(pipeline, Two()).Pipeline);
    }

    // ---- 規則 3: 列挙できたのに一致しない → 失敗 ----

    [Fact]
    public void AnUnknownPath_FailsAndNamesThePath()
    {
        string pipeline = $"d3d12screencapturesrc monitor-index=0 monitor-device-path={QuotedDevicePath}";

        var result = MonitorSelection.Resolve(
            pipeline, (MonitorInfo[])[Monitor(0, @"\\?\DISPLAY#OTHER#4&1&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Pipeline);
        Assert.NotNull(result.Failure);
        Assert.Contains(DevicePath, result.Failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>大文字小文字は同一視しない。</b> 一致は Ordinal ── 「似ているから」で
    /// 別の画面を録り始める余地を残さない。
    /// </summary>
    [Fact]
    public void TheComparisonIsOrdinal()
    {
        string pipeline = $"d3d12screencapturesrc monitor-device-path={QuotedDevicePath}";

        var result = MonitorSelection.Resolve(pipeline, (MonitorInfo[])[Monitor(0, DevicePath.ToUpperInvariant())]);

        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// 空の値（<c>monitor-device-path=""</c>）は「指定が無い」ではなく「一致しない指定」。
    /// 手で書ける以上、黙って番号へ倒さず失敗として見せる。
    /// </summary>
    [Fact]
    public void AnEmptyPathValue_IsTreatedAsASpecifiedPathThatMatchesNothing()
    {
        const string pipeline = "d3d12screencapturesrc monitor-device-path=\"\" monitor-index=0";

        var result = MonitorSelection.Resolve(pipeline, Two());

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    // ---- 規則 4: 列挙が空 → 番号が書かれていれば縮退＋警告、無ければ失敗 ----

    [Fact]
    public void WithoutAnyEnumeratedMonitor_ThePathIsDroppedAndTheIndexKept()
    {
        string pipeline =
            $"d3d12screencapturesrc monitor-index=2 monitor-device-path={QuotedDevicePath} show-cursor=false"
            + " ! video/x-raw(memory:D3D12Memory), framerate=30/1";

        var result = MonitorSelection.Resolve(pipeline, None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            "d3d12screencapturesrc monitor-index=2 show-cursor=false"
            + " ! video/x-raw(memory:D3D12Memory), framerate=30/1",
            result.Pipeline);
        Assert.NotNull(result.Warning);
        Assert.Contains(DevicePath, result.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// 縮退では、パスのトークンだけが消えて余分な空白も残らないこと
    /// （区切りの <c>'!'</c> の前が二重の空白にならない）。
    /// </summary>
    [Fact]
    public void TheDegradedStringDoesNotKeepAStraySeparator()
    {
        string pipeline =
            $"d3d12screencapturesrc monitor-index=0 monitor-device-path={QuotedDevicePath} ! video/x-raw(memory:D3D12Memory)";

        var result = MonitorSelection.Resolve(pipeline, None);

        Assert.Equal("d3d12screencapturesrc monitor-index=0 ! video/x-raw(memory:D3D12Memory)", result.Pipeline);
    }

    /// <summary>
    /// <b>戻せる先が無いなら縮退しない。</b> <c>monitor-index</c> が書かれていない指定で
    /// パスだけ取り除くと、選択プロパティが 1 つも無い文字列
    /// （<c>d3d12screencapturesrc capture-api=wgc ! …</c>）になり、要素は
    /// <b>既定のモニター（index 0）を黙って撮る</b> ── パス指定が防ごうとしている事故そのもの。
    /// 失敗させれば初期化が失敗し、デバイス到着の監視が拾い直して復帰する。
    /// </summary>
    [Fact]
    public void WithoutAnyEnumeratedMonitorAndWithoutAnIndex_ItFailsInsteadOfDegrading()
    {
        string pipeline =
            $"d3d12screencapturesrc capture-api=wgc monitor-device-path={QuotedDevicePath}"
            + " ! video/x-raw(memory:D3D12Memory), framerate=30/1";

        var result = MonitorSelection.Resolve(pipeline, None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Warning);
        Assert.NotNull(result.Failure);
        Assert.Contains(DevicePath, result.Failure, StringComparison.Ordinal);
        // 番号を書けば縮退できることが読み取れること（番号で構わない利用者の唯一の前進）。
        Assert.Contains("monitor-index", result.Failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>失敗のときはパイプライン文字列を書き換えて返さない。</b> 呼び出し側は投げるので
    /// 使われないが、中途半端な（＝既定のモニターを撮る）文字列を作らないこと自体を縛る。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFailureNeverProducesARewrittenPipeline(bool emptyEnumeration)
    {
        string pipeline = $"d3d12screencapturesrc monitor-device-path={QuotedDevicePath}";

        var result = MonitorSelection.Resolve(
            pipeline, emptyEnumeration ? None : (MonitorInfo[])[Monitor(0, DevicePath, handle: 0)]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Pipeline);
    }

    /// <summary>
    /// <b>規則 4/5 の失敗は規則 3 の失敗と区別できること。</b> 原因が違う
    /// （3 はモニターが繋がっていない、4 は列挙できない、5 はハンドルが読めない）ので、
    /// 同じ文言だと利用者が「挿し直す」という無効な対処へ誘導される。
    /// </summary>
    [Fact]
    public void TheThreeFailureReasons_AreDistinguishable()
    {
        string pipeline = $"d3d12screencapturesrc monitor-device-path={QuotedDevicePath}";

        string notConnected = MonitorSelection.Resolve(
            pipeline,
            (MonitorInfo[])[Monitor(0, @"\\?\DISPLAY#OTHER#4&1&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")])
            .Failure!;
        string notEnumerated = MonitorSelection.Resolve(pipeline, None).Failure!;
        string noHandle = MonitorSelection.Resolve(
            pipeline, (MonitorInfo[])[Monitor(0, DevicePath, handle: 0)]).Failure!;

        Assert.NotNull(notConnected);
        Assert.NotNull(notEnumerated);
        Assert.NotNull(noHandle);
        Assert.NotEqual(notConnected, notEnumerated);
        Assert.NotEqual(notConnected, noHandle);
        Assert.NotEqual(notEnumerated, noHandle);

        // 規則 3 の「繋がっていない」は 4/5 では言わない ── 挿し直しは対処にならない。
        Assert.Contains("is not connected", notConnected, StringComparison.Ordinal);
        Assert.DoesNotContain("is not connected", notEnumerated, StringComparison.Ordinal);
        Assert.DoesNotContain("is not connected", noHandle, StringComparison.Ordinal);

        // 逆に 4/5 だけが「戻せる先が無い」ことを言う。
        Assert.DoesNotContain("fall back", notConnected, StringComparison.Ordinal);
        Assert.Contains("fall back", notEnumerated, StringComparison.Ordinal);
        Assert.Contains("fall back", noHandle, StringComparison.Ordinal);
    }

    // ---- 規則 5: 一致したがハンドルが読めない → 4 と同じ扱い（番号の有無で分岐） ----

    [Fact]
    public void AMatchWithoutAReadableHandle_DegradesToTheIndexWithAWarning()
    {
        string pipeline =
            $"d3d12screencapturesrc monitor-index=1 monitor-device-path={QuotedDevicePath}"
            + " ! video/x-raw(memory:D3D12Memory), framerate=30/1";

        var result = MonitorSelection.Resolve(pipeline, (MonitorInfo[])[Monitor(0, DevicePath, handle: 0)]);

        Assert.True(result.Succeeded);
        Assert.Equal(
            "d3d12screencapturesrc monitor-index=1 ! video/x-raw(memory:D3D12Memory), framerate=30/1",
            result.Pipeline);
        Assert.NotNull(result.Warning);
        Assert.Contains(DevicePath, result.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// 規則 5 でも<b>戻せる先が無ければ縮退しない</b>（規則 4 と同じ判断）。
    /// パスが読めてハンドルだけ読めない構成で番号へ倒すと、番号が書かれていない以上
    /// 既定のモニターを撮ることになる。
    /// </summary>
    [Fact]
    public void AMatchWithoutAReadableHandleAndWithoutAnIndex_ItFailsInsteadOfDegrading()
    {
        string pipeline =
            $"d3d12screencapturesrc capture-api=wgc monitor-device-path={QuotedDevicePath}"
            + " ! video/x-raw(memory:D3D12Memory), framerate=30/1";

        var result = MonitorSelection.Resolve(pipeline, (MonitorInfo[])[Monitor(0, DevicePath, handle: 0)]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Warning);
        Assert.NotNull(result.Failure);
        Assert.Contains(DevicePath, result.Failure, StringComparison.Ordinal);
        Assert.Contains("monitor-index", result.Failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>縮退の結果に擬似プロパティが残らないこと</b>が規則 4/5 の本体である
    /// ── 残したまま渡すと <c>no property "monitor-device-path"</c> でパイプラインが組めない。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADegradedStringNeverKeepsThePseudoProperty(bool emptyEnumeration)
    {
        string pipeline = $"d3d12screencapturesrc monitor-index=0 monitor-device-path={QuotedDevicePath}";

        var result = MonitorSelection.Resolve(
            pipeline, emptyEnumeration ? None : (MonitorInfo[])[Monitor(0, DevicePath, handle: 0)]);

        Assert.NotNull(result.Pipeline);
        Assert.DoesNotContain(MonitorSelection.PathProperty, result.Pipeline, StringComparison.Ordinal);
    }

    // ---- 列挙を要求するかどうかの門 ----

    /// <summary>
    /// <b>列挙が要るのは画面キャプチャか、パス指定が在るときだけ。</b>
    ///
    /// <para>
    /// 画面キャプチャで無条件に要るのは<b>録る画面の実寸が録画ビットレートの式の材料</b>
    /// だからで（<see cref="MonitorSelectionResult.Monitor"/>）、パス指定の有無とは別の理由である。
    /// </para>
    /// <para>
    /// <b>それ以外は 1 回も列挙しない。</b> <c>InitializeCore</c> はレコーダーごと・復帰のたびに
    /// 走るので、無条件に列挙するとテストソースやカメラのレコーダーまでデバイスプロバイダを
    /// 起こし続けることになる。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("videotestsrc is-live=true", false)]
    [InlineData("mfvideosrc device-index=0", false)]
    // 画面キャプチャ ── 選択プロパティの書き方に依らず要る。
    [InlineData("d3d12screencapturesrc monitor-index=0", true)]
    [InlineData("d3d12screencapturesrc monitor-handle=99", true)]
    [InlineData("d3d11screencapturesrc", true)]
    [InlineData("d3d12screencapturesrc monitor-device-path=x", true)]
    // 画面キャプチャでなくても、パス指定が書かれていれば解決のために要る。
    [InlineData("somevendorsrc monitor-device-path=x", true)]
    public void RequiresMonitors_IsTrueForScreenCaptureAndForAnyWrittenPath(string? pipeline, bool expected)
        => Assert.Equal(expected, MonitorSelection.RequiresMonitors(pipeline));
    // ---- 当たったモニター（録画ビットレートの式へ渡す大きさの出所） ----

    /// <summary>
    /// <b>解決の結果には「実際に録るモニター」が付いてくる</b>
    /// （<see cref="MonitorSelectionResult.Monitor"/>）。
    ///
    /// <para>
    /// 画面キャプチャの <c>SrcPipeline</c> は解像度を caps に書かない構成が既定なので、
    /// 実際に流れる大きさはこのモニターの実寸でしか分からない ──
    /// <c>EventRecorder</c> が録画ビットレートの式へ渡す第 2 の出所である。
    /// <b>これが null に落ちると 4K の画面が 1080p ぶんの帯域で録られる</b>ので、
    /// 3 分岐（一致・番号へ縮退・分からない）を全部固定する。
    /// </para>
    /// </summary>
    [Fact]
    public void AResolvedPath_ReportsTheMatchedMonitor()
    {
        string pipeline = $"d3d12screencapturesrc monitor-device-path={QuotedDevicePath}";

        var result = MonitorSelection.Resolve(pipeline, Two());

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Monitor?.Index);
        Assert.Equal("3840x2160", result.Monitor?.Resolution);
    }

    /// <summary>
    /// <b>番号へ縮退したときは、書かれている番号のモニター</b>
    /// ── 実際に録られるのはパスで指した方ではなく番号の方である。
    /// </summary>
    [Fact]
    public void ADegradationToTheIndex_ReportsTheMonitorAtThatIndex()
    {
        string pipeline =
            $"d3d12screencapturesrc monitor-index=0 monitor-device-path={QuotedDevicePath}";

        // パスは一致するがハンドルが読めない（規則 5）。番号 0 は別のモニターを指す。
        var monitors = (MonitorInfo[])
        [
            Monitor(0, @"\\?\DISPLAY#OTHER#4&1&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"),
            Monitor(1, DevicePath, handle: 0),
        ];

        var result = MonitorSelection.Resolve(pipeline, monitors);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Warning);
        Assert.Equal(0, result.Monitor?.Index);
    }

    /// <summary>
    /// <b>パス指定の無い画面キャプチャでも、番号で選ばれるモニターを当てる。</b>
    /// これが<b>既定の構成</b>（設定画面が書くのは <c>monitor-index</c> だけ）なので、
    /// ここが null に落ちると <b>4K の画面が仮定値 1920x1080 ぶんの帯域で録られる</b> ──
    /// 「サイズに合わせた VBR」の本命が外れる。
    /// </summary>
    [Fact]
    public void AScreenCaptureWithoutAPath_StillReportsTheMonitorAtTheWrittenIndex()
    {
        var result = MonitorSelection.Resolve("d3d12screencapturesrc monitor-index=1", Two());

        Assert.True(result.Succeeded);
        Assert.Null(result.Warning);
        Assert.Equal(1, result.Monitor?.Index);
        Assert.Equal("3840x2160", result.Monitor?.Resolution);
    }

    /// <summary>
    /// 番号も書かれていなければ<b>要素の既定＝ 0 番</b>（それが実際に録られる画面である）。
    /// <c>d3d11screencapturesrc</c> でも同じ（同じ選択プロパティを持つ）。
    /// <b>文字列は 1 文字も変えない</b> ── 解決すべきものが何も無いので入力をそのまま返す。
    /// </summary>
    [Theory]
    [InlineData("d3d12screencapturesrc")]
    [InlineData("d3d11screencapturesrc")]
    public void AScreenCaptureWithNoSelectionAtAll_ReportsTheFirstMonitor(string element)
    {
        string pipeline = $"{element} show-cursor=true ! video/x-raw(memory:D3D12Memory), framerate=30/1";

        var result = MonitorSelection.Resolve(pipeline, Two());

        Assert.True(result.Succeeded);
        Assert.Equal(pipeline, result.Pipeline);
        Assert.Equal(0, result.Monitor?.Index);
    }

    /// <summary>
    /// <b>画面キャプチャでないソースは列挙もしないし、モニターも当てない。</b>
    /// <c>videotestsrc</c> やカメラのレコーダーまで 60 秒ごとにデバイスプロバイダを
    /// 起こしてはいけない（<see cref="MonitorSelection.RequiresMonitors"/>）。
    /// </summary>
    [Fact]
    public void ANonScreenCaptureSource_NeedsNoMonitorsAndGetsNone()
    {
        const string pipeline =
            "videotestsrc is-live=true ! videoconvert ! video/x-raw,format=I420,width=320,height=240,framerate=15/1";

        Assert.False(MonitorSelection.RequiresMonitors(pipeline));
        Assert.Null(MonitorSelection.Resolve(pipeline, Two()).Monitor);

        // 画面キャプチャならパス指定が無くても列挙が要る。
        Assert.True(MonitorSelection.RequiresMonitors("d3d12screencapturesrc monitor-index=0"));
        Assert.True(MonitorSelection.RequiresMonitors("d3d11screencapturesrc"));
    }

    /// <summary>
    /// <b>分からないときは null</b> ── 列挙が空（ヘッドレス・プロバイダ不在）／
    /// 解決に失敗した／書かれた番号が一覧の範囲外／ソースが画面キャプチャでない。
    /// 範囲外の番号で当てずっぽうに 1 台選ぶと、録られていない画面の実寸を帯域の根拠にしてしまう。
    /// </summary>
    [Fact]
    public void TheMonitorIsNullWhenItCannotBeKnown()
    {
        // 列挙が空（パス指定の有無に関わらず）。
        Assert.Null(MonitorSelection.Resolve("d3d12screencapturesrc monitor-index=0", None).Monitor);
        Assert.Null(MonitorSelection.Resolve(
            $"d3d12screencapturesrc monitor-index=0 monitor-device-path={QuotedDevicePath}", None).Monitor);

        // 一致しない（規則 3・失敗）。
        Assert.Null(MonitorSelection.Resolve(
            $"d3d12screencapturesrc monitor-device-path={QuotedDevicePath}",
            (MonitorInfo[])[Monitor(0, @"\\?\DISPLAY#OTHER#4&1&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")])
            .Monitor);

        // 書かれた番号が一覧の範囲外（パス指定の有無に関わらず）。
        Assert.Null(MonitorSelection.Resolve("d3d12screencapturesrc monitor-index=7", Two()).Monitor);
        Assert.Null(MonitorSelection.Resolve(
            $"d3d12screencapturesrc monitor-index=7 monitor-device-path={QuotedDevicePath}",
            (MonitorInfo[])[Monitor(0, DevicePath, handle: 0)]).Monitor);
    }
}
