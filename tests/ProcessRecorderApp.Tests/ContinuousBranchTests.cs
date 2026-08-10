using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>常時録画の枝（<c>tee</c> の 3 本目）の組み立て。</b>
///
/// <para>
/// パイプライン文字列は実行しないと分からない部分が多いが、
/// <b>「入れてはいけないものが入っていない」「外してはいけないものが外れていない」</b>
/// はここで機械的に守れる。特に <c>videorate</c> は「上書きが空なら出さない」が崩れると、
/// 別途入れた GStreamer にその要素が無い利用者の初期化を巻き添えで壊す。
/// </para>
/// </summary>
public class ContinuousBranchTests
{
    private const string Encoder = "x264enc bitrate=2000 tune=zerolatency key-int-max=15";

    [Fact]
    public void AnEmptyEncoder_ProducesNoBranchAtAll()
    {
        Assert.Equal("", ContinuousBranch.Build(EventRecordingType.D3d12, "", false, "", ""));
    }

    [Fact]
    public void TheBranchHangsOffTheSameTeeAndEndsInAnAppsink()
    {
        string branch = ContinuousBranch.Build(EventRecordingType.System, Encoder, false, "", "");

        Assert.StartsWith("t. ! ", branch);
        Assert.EndsWith($"appsink name={ContinuousBranch.AppSinkName} async=false", branch);
    }

    /// <summary>
    /// <c>async=false</c> が外れると、低いフレームレートのときこの枝が
    /// パイプライン全体の <c>PLAYING</c> 到達を握る（イベント録画ごと道連れになる）。
    /// </summary>
    [Fact]
    public void TheAppsinkDoesNotGatePreroll()
    {
        string branch = ContinuousBranch.Build(EventRecordingType.D3d12, Encoder, false, "", "");
        Assert.Contains("async=false", branch);
    }

    /// <summary>
    /// 常時枝の queue は <c>leaky=downstream</c>。詰まったときに tee を止めるのは
    /// イベント録画の枝の役目であって、常時録画がイベント録画を道連れにしてはならない。
    /// バイト・時間の上限を外すのはプレビュー枝と同じ理由（解像度に依存させない）。
    /// </summary>
    [Fact]
    public void TheBranchQueueLeaksInsteadOfStallingTheTee()
    {
        string branch = ContinuousBranch.Build(EventRecordingType.System, Encoder, false, "", "");

        Assert.Contains("queue leaky=downstream", branch);
        Assert.Contains("max-size-bytes=0", branch);
        Assert.Contains("max-size-time=0", branch);
    }

    [Theory]
    [InlineData(EventRecordingType.System)]
    [InlineData(EventRecordingType.D3d12)]
    public void NoFramerateOverride_MeansNoVideorate(EventRecordingType type)
    {
        string branch = ContinuousBranch.Build(type, Encoder, false, "", "");

        Assert.DoesNotContain(ContinuousBranch.VideorateFactory, branch);
        Assert.False(ContinuousBranch.RequiresVideorate(""));
        Assert.False(ContinuousBranch.RequiresVideorate(null));
        Assert.False(ContinuousBranch.RequiresVideorate("   "));
    }

    /// <summary>
    /// <b>D3d12 経路の framerate capsfilter からメモリ機能を落とさない。</b>
    /// <c>video/x-raw, framerate=X</c> と書くとシステムメモリを要求してしまい、
    /// 上流に <c>d3d12download</c> が無いのでリンクに失敗して初期化ごと落ちる。
    /// </summary>
    [Fact]
    public void TheFramerateCapsKeepsTheD3d12MemoryFeature()
    {
        string branch = ContinuousBranch.Build(EventRecordingType.D3d12, Encoder, false, "5/1", "");

        Assert.True(ContinuousBranch.RequiresVideorate("5/1"));
        Assert.Contains("videorate ! video/x-raw(memory:D3D12Memory), framerate=5/1", branch);
    }

    [Fact]
    public void TheSystemPathUsesPlainRawCapsForTheFramerate()
    {
        string branch = ContinuousBranch.Build(EventRecordingType.System, Encoder, false, "5/1", "");

        Assert.Contains("videorate ! video/x-raw, framerate=5/1", branch);
        Assert.DoesNotContain("D3D12Memory", branch);
    }

    /// <summary>
    /// 解像度はソースの caps ではなく<b>変換段</b>で効かせる
    /// ── 画面キャプチャの src caps はモニター解像度に固定されているため。
    /// </summary>
    [Fact]
    public void TheResolutionIsAppliedByTheConverterOnTheD3d12Path()
    {
        string branch = ContinuousBranch.Build(EventRecordingType.D3d12, Encoder, false, "", "1280x720");

        Assert.Contains("d3d12convert ! video/x-raw(memory:D3D12Memory), format=NV12, width=1280, height=720", branch);
    }

    [Fact]
    public void TheResolutionIsAppliedByVideoscaleOnTheSystemPath()
    {
        string branch = ContinuousBranch.Build(EventRecordingType.System, Encoder, false, "", "640x360");

        Assert.Contains("videoscale ! video/x-raw, width=640, height=360", branch);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1280")]
    [InlineData("1280x")]
    [InlineData("axb")]
    [InlineData("0x720")]
    [InlineData("-1280x720")]
    public void AnUnreadableResolution_IsNotAppliedAtAll(string? resolution)
    {
        Assert.False(ContinuousBranch.TryParseResolution(resolution, out _, out _));

        string branch = ContinuousBranch.Build(EventRecordingType.System, Encoder, false, "", resolution);
        Assert.DoesNotContain("videoscale", branch);
        Assert.DoesNotContain("width=", branch);
    }

    /// <summary>
    /// エンコーダーの直前の <c>videoconvert</c> は必須（<c>parse_launch</c> は変換要素を
    /// 自動挿入しないので、画素形式が合わないとリンクに失敗する）。D3d12 でシステムメモリを
    /// 要求するエンコーダーには <c>d3d12download</c> も要る ── イベント枝と同じ規則。
    /// </summary>
    [Fact]
    public void AnEncoderThatNeedsSystemMemory_GetsTheDownloadAndTheConvert()
    {
        string branch = ContinuousBranch.Build(EventRecordingType.D3d12, Encoder, true, "", "");

        Assert.Contains("d3d12download ! video/x-raw(memory:SystemMemory) ! videoconvert ! " + Encoder, branch);
    }

    [Fact]
    public void AnEncoderOnSystemMemory_AlwaysGetsAConvertInFront()
    {
        string branch = ContinuousBranch.Build(EventRecordingType.System, Encoder, false, "", "");
        Assert.Contains("videoconvert ! " + Encoder, branch);
    }

    /// <summary>
    /// <c>config-interval=-1</c> が無いと 2 本目以降のセグメントにパラメータセットが入らず、
    /// 単体では再生できないファイルが黙って残る。<c>alignment=au</c> は
    /// 「1 バッファ＝1 フレーム」の前提（PTS の張り替えがこれに依存する）。
    /// </summary>
    [Fact]
    public void TheBranchRepeatsTheParameterSetsAndKeepsOneBufferPerFrame()
    {
        string branch = ContinuousBranch.Build(EventRecordingType.D3d12, Encoder, false, "", "");

        Assert.Contains("h264parse config-interval=-1", branch);
        Assert.Contains("alignment=au", branch);
    }

    /// <summary>
    /// セグメントの書き出しに <c>faststart=true</c> を付けない
    /// ── EOS のたびにファイル全体を書き直すので、数分ごとの切り替えでは I/O が跳ねる。
    /// </summary>
    [Fact]
    public void TheSegmentWriterDoesNotRewriteEveryFileOnClose()
    {
        Assert.Contains("mp4mux", ContinuousBranch.SegmentWriterPipeline);
        Assert.DoesNotContain("faststart", ContinuousBranch.SegmentWriterPipeline);
    }

    /// <summary>常時録画を切っている構成のパイプライン文字列は 1 文字も変わらない。</summary>
    [Fact]
    public void WithoutABranch_TheSinkPipelineIsUnchanged()
    {
        string withoutArgument = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, "d3d12testsrc", Encoder, false);
        string withEmptyBranch = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, "d3d12testsrc", Encoder, false, "");

        Assert.Equal(withoutArgument, withEmptyBranch);
        Assert.DoesNotContain(ContinuousBranch.AppSinkName + " ", withoutArgument);
    }

    [Fact]
    public void WithABranch_TheSinkPipelineGetsExactlyOneMoreAppsink()
    {
        string branch = ContinuousBranch.Build(EventRecordingType.D3d12, Encoder, false, "5/1", "640x360");
        string pipeline = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, "d3d12testsrc", Encoder, false, branch);

        Assert.Contains("name=preview", pipeline);
        Assert.Contains("appsink name=sink", pipeline);
        Assert.Contains($"appsink name={ContinuousBranch.AppSinkName}", pipeline);
        Assert.EndsWith(branch, pipeline);
    }
}
