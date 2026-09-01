using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>録画ビットレートの式へ渡す「ソースの大きさ」の決め方</b>
/// （<see cref="EventRecorder.RecordingSizeFor"/>）。
///
/// <para>
/// 出所は 3 つ（caps ／ 当たったモニターの実寸 ／ 仮定値）で、この優先順は固定である。
/// <b>順序が入れ替わると帯域が静かに変わる</b> ── 4K の画面キャプチャは caps に
/// 解像度を書かない構成が既定なので、モニターの出所が落ちるとその録画だけが
/// 1080p ぶんの目標で符号化され、録画物を測るまで気付けない。
/// </para>
/// <para>
/// <b>出所の名前もテストの対象である。</b> ログ（<c>gst.encoder selected</c> の
/// <c>size-source=</c>）にそのまま出る文字列で、値だけを見ても
/// 「小さいソースだから低い」のか「読めずに仮定値へ落ちた」のかが区別できない。
/// </para>
/// </summary>
public class RecordingSizeTests
{
    private static MonitorInfo MonitorWith(string resolution) => new()
    {
        Index = 0,
        Path = @"\\?\DISPLAY#DELA0C5#5&1c2f9a7a&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
        Handle = 0x12345UL,
        Resolution = resolution,
    };

    /// <summary>caps が幅・高さを固定しているなら、それが変換段の出力そのものである。</summary>
    [Fact]
    public void CapsWin_WhenTheSourcePinsTheSize()
    {
        var size = EventRecorder.RecordingSizeFor(
            "videotestsrc is-live=true ! videoconvert ! video/x-raw,format=I420,width=1280,height=720,framerate=30/1",
            MonitorWith("3840x2160"),
            out string source);

        Assert.Equal("caps", source);
        Assert.Equal((1280, 720), size);
    }

    /// <summary>
    /// caps に大きさが無ければ、当たったモニターの実寸を使う ──
    /// 画面キャプチャの既定はまさにこの形で、<b>ここが落ちると 4K が 1080p 扱いになる</b>。
    /// </summary>
    [Fact]
    public void TheMonitorIsUsed_WhenTheCapsDoNotPinTheSize()
    {
        var size = EventRecorder.RecordingSizeFor(
            "d3d12screencapturesrc monitor-handle=74565 ! video/x-raw(memory:D3D12Memory), framerate=30/1",
            MonitorWith("3840x2160"),
            out string source);

        Assert.Equal("monitor", source);
        Assert.Equal((3840, 2160), size);
    }

    /// <summary>
    /// どちらも読めなければ仮定値。<b>モニターの実寸が空の場合も同じ</b>
    /// ── 読めないビルドでは <see cref="MonitorInfo.Resolution"/> が空文字になる。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void TheAssumedSizeIsTheLastResort(string? monitorResolution)
    {
        var size = EventRecorder.RecordingSizeFor(
            "d3d12screencapturesrc monitor-index=0 ! video/x-raw(memory:D3D12Memory), framerate=30/1",
            monitorResolution is null ? null : MonitorWith(monitorResolution),
            out string source);

        Assert.Equal("assumed", source);
        Assert.Equal((EncoderCatalog.AssumedWidth, EncoderCatalog.AssumedHeight), size);
        Assert.Equal((1920, 1080), size);
    }

    /// <summary>
    /// 出所ごとの帯域が実際に違うこと ── 優先順が入れ替わっても
    /// 上の 3 つは「どれか 1 つが選ばれた」としか言わないので、
    /// <b>式まで通して値の違いを見る</b>。
    /// </summary>
    [Fact]
    public void EachSourceOfTheSizeProducesADifferentBitrate()
    {
        int caps = EncoderCatalog.BitrateKbpsFor(1280, 720, 30);
        int monitor = EncoderCatalog.BitrateKbpsFor(3840, 2160, 30);
        int assumed = EncoderCatalog.BitrateKbpsFor(
            EncoderCatalog.AssumedWidth, EncoderCatalog.AssumedHeight, EncoderCatalog.AssumedFps);

        Assert.Equal(2765, caps);
        Assert.Equal(24883, monitor);
        Assert.Equal(6221, assumed);
    }
}
