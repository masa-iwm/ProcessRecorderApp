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

    // ---- 規則 4: 列挙が空 → 番号へ縮退＋警告 ----

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

    /// <summary>番号が書かれていない縮退では、パスだけが消えて余分な空白も残らないこと。</summary>
    [Fact]
    public void TheDegradedStringDoesNotKeepAStraySeparator()
    {
        string pipeline = $"d3d12screencapturesrc monitor-device-path={QuotedDevicePath} ! video/x-raw(memory:D3D12Memory)";

        var result = MonitorSelection.Resolve(pipeline, None);

        Assert.Equal("d3d12screencapturesrc ! video/x-raw(memory:D3D12Memory)", result.Pipeline);
    }

    // ---- 規則 5: 一致したがハンドルが読めない → 4 と同じ縮退＋警告 ----

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
    /// <b>列挙は指定が在るときだけ。</b> <c>InitializeCore</c> はレコーダーごと・復帰のたびに
    /// 走るので、無条件に列挙するとテストソースのレコーダーまでデバイスプロバイダを
    /// 起こし続けることになる。
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("videotestsrc is-live=true", false)]
    [InlineData("d3d12screencapturesrc monitor-index=0", false)]
    [InlineData("d3d12screencapturesrc monitor-handle=99", false)]
    [InlineData("d3d12screencapturesrc monitor-device-path=x", true)]
    public void RequiresMonitors_IsTrueExactlyWhenTheNameCanBeFound(string? pipeline, bool expected)
        => Assert.Equal(expected, MonitorSelection.RequiresMonitors(pipeline));
}
