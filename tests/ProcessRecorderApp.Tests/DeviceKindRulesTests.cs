using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="DeviceKindRules"/> ── <c>SrcPipeline</c> の映像源が、どの種類のデバイス到着で
/// 復帰しうるかの判定。
///
/// <para>
/// <b>この判定が緩むと、到着の監視が要らないところで走る。</b> テストソースを
/// カメラと判定すると、<c>mfdeviceprovider</c> を起動したうえで
/// 決して来ない到着を待つことになる（実害は無いが、無駄にプロバイダを started に保つ）。
/// 逆に厳しすぎると ── カメラを <see cref="DeviceKind.None"/> と判定すると ──
/// <b>早期復帰も、初期化失敗からの復帰も、丸ごと効かなくなる</b>。
/// </para>
/// <para>
/// <b>カタログとの過不足もここで縛る。</b> 判定表は
/// <see cref="SrcPipelineBuilder.Sources"/> とは別に持っている（あちらは表示名を
/// ローカライズリソースから取る <c>Lazy</c> なので、GStreamer のスレッドから引きたくない）。
/// 二重管理になる以上、<b>片方だけ増えたら赤くなる</b>ようにしておく。
/// </para>
/// </summary>
public class DeviceKindRulesTests
{
    // ---- 要素名からの判定 ----

    // 期待値は activity.log の綴り（DeviceKindRules.LogName）で書く ──
    // DeviceKind は internal なので、public なテストメソッドの引数にはできない。
    [Theory]
    [InlineData("mfvideosrc", "camera")]
    [InlineData("d3d12screencapturesrc", "monitor")]
    [InlineData("d3d11screencapturesrc", "monitor")]
    [InlineData("d3d12testsrc", "none")]
    [InlineData("videotestsrc", "none")]
    public void EverySupportedSource_HasItsKind(string elementName, string expected)
        => Assert.Equal(expected, DeviceKindRules.LogName(DeviceKindRules.ForElement(elementName)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("filesrc")]
    [InlineData("MFVIDEOSRC")]      // 要素名は大文字小文字を区別する
    public void AnUnknownSource_IsNotWatched(string? elementName)
        => Assert.Equal(DeviceKind.None, DeviceKindRules.ForElement(elementName));

    // ---- SrcPipeline 文字列からの判定 ----

    [Fact]
    public void ACameraPipeline_IsClassifiedAsACamera()
        => Assert.Equal(
            DeviceKind.Camera,
            DeviceKindRules.Classify(
                "mfvideosrc device-name=\"Live! Cam Sync HD\" ! "
                + "video/x-raw, format=NV12, width=1920, height=1080, framerate=30/1"));

    [Fact]
    public void AScreenCapturePipeline_IsClassifiedAsAMonitor()
        => Assert.Equal(
            DeviceKind.Monitor,
            DeviceKindRules.Classify(
                "d3d12screencapturesrc monitor-index=1 show-cursor=false ! "
                + "video/x-raw(memory:D3D12Memory), framerate=30/1"));

    /// <summary>
    /// <b>E2E の障害注入がそのまま通ること。</b> <c>identity error-after=N</c> を挟んだ
    /// テストソースは <see cref="DeviceKind.None"/> のままでなければならない ──
    /// ここが変わると <c>ErrorReportingTests</c> の意味（タイマーだけの復帰の観測）が
    /// 静かに変わる。
    /// </summary>
    [Fact]
    public void TheFaultInjectionPipeline_StaysUnwatched()
        => Assert.Equal(
            DeviceKind.None,
            DeviceKindRules.Classify(
                "videotestsrc is-live=true do-timestamp=true ! identity error-after=30 ! videoconvert ! "
                + "video/x-raw,format=I420,width=320,height=240,framerate=15/1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! not a pipeline")]
    public void AnUnparsablePipeline_IsNotWatched(string? pipeline)
        => Assert.Equal(DeviceKind.None, DeviceKindRules.Classify(pipeline));

    // ---- カタログとの過不足 ----

    /// <summary>
    /// カタログに載っている映像源は<b>全部</b>、種別表にも明示的に載っていること。
    /// 新しいソース要素を足したときに種別を決め忘れると、
    /// <see cref="DeviceKindRules.ForElement"/> は黙って <see cref="DeviceKind.None"/> を返し、
    /// <b>そのソースだけ早期復帰が効かない</b>という気付きにくい欠落になる。
    /// </summary>
    [Fact]
    public void EverySourceInTheCatalog_HasAnExplicitKind()
    {
        foreach (SrcElementDef source in SrcPipelineBuilder.Sources)
        {
            Assert.True(
                DeviceKindRules.IsKnownElement(source.ElementName),
                $"'{source.ElementName}' が DeviceKindRules の種別表に無い。"
                + "カタログへソースを足したら、到着の監視が要るかどうかを必ず決めること"
                + "（要らないなら DeviceKind.None を明示する）。");
        }
    }

    // ---- プロバイダ名 ----

    /// <summary>
    /// <b>モニターの監視は D3D11 の映像源でも D3D12 のプロバイダで行う。</b>
    /// 見ているのは <c>WM_DISPLAYCHANGE</c> であって src 要素ではなく、
    /// <c>d3d11screencapturedeviceprovider</c> は <c>probe</c> しか実装していないので
    /// 通知を一切出さない（docs/environment-facts.md）。
    /// </summary>
    [Fact]
    public void TheMonitorProvider_IsTheD3d12One()
    {
        Assert.Equal("d3d12screencapturedeviceprovider", DeviceKindRules.ProviderName(DeviceKind.Monitor));
        Assert.Equal("mfdeviceprovider", DeviceKindRules.ProviderName(DeviceKind.Camera));
        Assert.Null(DeviceKindRules.ProviderName(DeviceKind.None));
    }

    /// <summary>activity.log の綴りは E2E が <c>Contains</c> で拾うので固定する。</summary>
    [Fact]
    public void TheLogName_IsStable()
    {
        Assert.Equal("camera", DeviceKindRules.LogName(DeviceKind.Camera));
        Assert.Equal("monitor", DeviceKindRules.LogName(DeviceKind.Monitor));
        Assert.Equal("none", DeviceKindRules.LogName(DeviceKind.None));
    }
}
