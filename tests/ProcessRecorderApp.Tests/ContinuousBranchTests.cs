using ProcessRecorderApp.GStreamer;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// 枝の文字列だけを取り出す薄い包み。既定で「ソースの幅・高さは固定済み」とするのは、
    /// 解像度の上書きが**効く**ことを前提にした検査が大半だから
    /// ── 固定されていないときの規則は専用の検査で見る。
    /// </summary>
    private static string Build(
        EventRecordingType type, string encoder, bool needsSystemMemory,
        string? framerate, string? resolution, bool sourceSizeIsPinned = true)
        => ContinuousBranch.Plan(type, encoder, needsSystemMemory, framerate, resolution, sourceSizeIsPinned).Branch;

    [Fact]
    public void AnEmptyEncoder_ProducesNoBranchAtAll()
    {
        Assert.Equal("", Build(EventRecordingType.D3d12, "", false, "", ""));
    }

    /// <summary>
    /// <b>フレームレートは分数へ正規化する。</b> caps の <c>framerate</c> は
    /// <c>GstFraction</c> なので、<c>5</c> と書くと <c>(int)5</c> として読まれ、
    /// <b>どの要素も扱えない caps になってリンクに失敗する</b>
    /// （実測: <c>could not link videorate0 to ..., can't handle caps
    /// video/x-raw, framerate=(int)5</c>）。設定画面から素の数字を書く利用者がいる以上、
    /// 受け付けて正規化する。
    /// </summary>
    [Theory]
    [InlineData("5", "framerate=5/1")]
    [InlineData("5/1", "framerate=5/1")]
    [InlineData(" 30 ", "framerate=30/1")]
    [InlineData("30000/1001", "framerate=30000/1001")]
    public void ABareNumberFramerate_IsNormalisedToAFraction(string input, string expected)
    {
        string branch = Build(EventRecordingType.D3d12, Encoder, false, input, "");

        Assert.Contains(expected, branch);
        // **必ず分数として出る**こと（整数のまま出ると caps が (int) になって壊れる）
        Assert.Matches(@"framerate=\d+/\d+", branch);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0/1")]
    [InlineData("5/0")]
    public void AnUnreadableFramerate_IsDroppedInsteadOfProducingBrokenCaps(string framerate)
    {
        var plan = ContinuousBranch.Plan(
            EventRecordingType.D3d12, Encoder, false, framerate, "", sourceSizeIsPinned: true);

        Assert.DoesNotContain(ContinuousBranch.VideorateFactory, plan.Branch);
        Assert.NotNull(plan.DroppedOverride);
        Assert.Contains("ContinuousFramerate", plan.DroppedOverride!);
    }

    /// <summary>
    /// <b>ソースの幅・高さが固定されていないときは解像度の上書きを捨てる。</b>
    ///
    /// <para>
    /// 枝の capsfilter が要求する幅・高さは、拡縮できる要素を素通りして <c>tee</c> を越え、
    /// <b>ソースまで伝播する</b>。ソースが任意の大きさを出せると（<c>d3d12screencapturesrc</c> は
    /// <c>width:[1,MAX]</c> を名乗る）、<b>プレビューもイベント録画も一緒に縮む</b>
    /// ── 実測で 3840x2160 のキャプチャが常時枝の 960x540 に引きずられた。
    /// 常時録画のために本線の録画を壊すことは許されないので、上書きの方を捨てる。
    /// </para>
    /// </summary>
    [Fact]
    public void AResolutionOverride_IsDroppedWhenTheSourceSizeIsNotPinned()
    {
        var plan = ContinuousBranch.Plan(
            EventRecordingType.D3d12, Encoder, false, "", "1280x720", sourceSizeIsPinned: false);

        Assert.DoesNotContain("width=1280", plan.Branch);
        Assert.DoesNotContain("d3d12convert", plan.Branch);
        Assert.NotNull(plan.DroppedOverride);
        // 直し方が書いてあること（「効かない」だけでは利用者は動けない）
        Assert.Contains("SrcPipeline", plan.DroppedOverride!);
    }

    [Fact]
    public void AResolutionOverride_IsAppliedWhenTheSourceSizeIsPinned()
    {
        var plan = ContinuousBranch.Plan(
            EventRecordingType.D3d12, Encoder, false, "", "1280x720", sourceSizeIsPinned: true);

        Assert.Contains("width=1280, height=720", plan.Branch);
        Assert.Null(plan.DroppedOverride);
    }

    /// <summary>
    /// 固定されているかの判定は <c>SrcPipeline</c> の caps を読むだけ
    /// （解析は <c>SrcPipelineBuilder</c> に任せる ── 引用符の中の <c>!</c> 等の規則を
    /// 2 か所に書かないため）。
    /// </summary>
    [Theory]
    [InlineData("d3d12screencapturesrc monitor-index=0 ! video/x-raw(memory:D3D12Memory), framerate=30/1", false)]
    [InlineData("d3d12screencapturesrc monitor-index=0 ! video/x-raw(memory:D3D12Memory), framerate=30/1, width=3840, height=2160", true)]
    [InlineData("videotestsrc ! video/x-raw,format=I420,width=320,height=240,framerate=15/1", true)]
    [InlineData("videotestsrc ! video/x-raw,format=I420,width=320,framerate=15/1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SourceSizeIsPinned_ReadsTheCapsOfTheSourcePipeline(string? srcPipeline, bool expected)
        => Assert.Equal(expected, ContinuousBranch.SourceSizeIsPinned(srcPipeline));

    /// <summary>
    /// <b>枝の中で拡縮するときは <c>tee</c> の手前も固定する。</b>
    ///
    /// <para>
    /// D3d12 経路は <c>tee</c> の手前に <c>d3d12convert</c> が居る。ここを固定しないと、
    /// 枝の capsfilter が要求する小さい大きさをその変換が吸収し、
    /// <b>プレビューとイベント録画まで縮む</b> ── ソースの caps を固定していても起こる
    /// （実測: ソース 1920x1080 固定・枝 960x540 でプレビューが 960x540 になり、
    /// この固定を足すと 1920x1080 に戻った）。
    /// </para>
    /// </summary>
    [Fact]
    public void ScalingInTheBranch_PinsTheSizeInFrontOfTheTee()
    {
        string branch = ContinuousBranch.Plan(
            EventRecordingType.D3d12, Encoder, false, "", "960x540", sourceSizeIsPinned: true).Branch;

        string withPin = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, "d3d12testsrc", Encoder, false, branch, "1920x1080");

        // tee の手前（d3d12convert の出力 caps）に固定が入っていること
        Assert.Contains(
            "d3d12upload ! d3d12convert ! video/x-raw(memory:D3D12Memory), format=NV12, width=1920, height=1080",
            withPin);
        // 枝の側は縮んだままであること
        Assert.Contains("width=960, height=540", withPin);
    }

    /// <summary>
    /// 拡縮しない構成では手前を固定しない ── 常時録画を切っている構成の
    /// パイプライン文字列を、この機能を入れる前から 1 文字も変えないため。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-size")]
    public void WithoutScaling_TheSinkPipelineIsNotPinned(string pinnedResolution)
    {
        string plain = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, "d3d12testsrc", Encoder, false);
        string same = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, "d3d12testsrc", Encoder, false, "", pinnedResolution);

        Assert.Equal(plain, same);
    }

    [Theory]
    [InlineData("videotestsrc ! video/x-raw,format=I420,width=320,height=240,framerate=15/1", "320x240")]
    [InlineData("d3d12screencapturesrc ! video/x-raw(memory:D3D12Memory), framerate=30/1", "")]
    [InlineData("videotestsrc ! video/x-raw,width=0,height=240", "")]
    public void SourceSize_ReadsTheCapsOfTheSourcePipeline(string srcPipeline, string expected)
        => Assert.Equal(expected, ContinuousBranch.SourceSize(srcPipeline));

    [Fact]
    public void TheBranchHangsOffTheSameTeeAndEndsInAnAppsink()
    {
        string branch = Build(EventRecordingType.System, Encoder, false, "", "");

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
        string branch = Build(EventRecordingType.D3d12, Encoder, false, "", "");
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
        string branch = Build(EventRecordingType.System, Encoder, false, "", "");

        Assert.Contains("queue leaky=downstream", branch);
        Assert.Contains("max-size-bytes=0", branch);
        Assert.Contains("max-size-time=0", branch);
    }

    [Theory]
    [InlineData(EventRecordingType.System)]
    [InlineData(EventRecordingType.D3d12)]
    public void NoFramerateOverride_MeansNoVideorate(EventRecordingType type)
    {
        string branch = Build(type, Encoder, false, "", "");

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
        string branch = Build(EventRecordingType.D3d12, Encoder, false, "5/1", "");

        Assert.True(ContinuousBranch.RequiresVideorate("5/1"));
        Assert.Contains("videorate ! video/x-raw(memory:D3D12Memory), framerate=5/1", branch);
    }

    [Fact]
    public void TheSystemPathUsesPlainRawCapsForTheFramerate()
    {
        string branch = Build(EventRecordingType.System, Encoder, false, "5/1", "");

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
        string branch = Build(EventRecordingType.D3d12, Encoder, false, "", "1280x720");

        Assert.Contains("d3d12convert ! video/x-raw(memory:D3D12Memory), format=NV12, width=1280, height=720", branch);
    }

    [Fact]
    public void TheResolutionIsAppliedByVideoscaleOnTheSystemPath()
    {
        string branch = Build(EventRecordingType.System, Encoder, false, "", "640x360");

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

        string branch = Build(EventRecordingType.System, Encoder, false, "", resolution);
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
        string branch = Build(EventRecordingType.D3d12, Encoder, true, "", "");

        Assert.Contains("d3d12download ! video/x-raw(memory:SystemMemory) ! videoconvert ! " + Encoder, branch);
    }

    [Fact]
    public void AnEncoderOnSystemMemory_AlwaysGetsAConvertInFront()
    {
        string branch = Build(EventRecordingType.System, Encoder, false, "", "");
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
        string branch = Build(EventRecordingType.D3d12, Encoder, false, "", "");

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
        string branch = Build(EventRecordingType.D3d12, Encoder, false, "5/1", "640x360");
        string pipeline = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, "d3d12testsrc", Encoder, false, branch);

        Assert.Contains("name=preview", pipeline);
        Assert.Contains("appsink name=sink", pipeline);
        Assert.Contains($"appsink name={ContinuousBranch.AppSinkName}", pipeline);
        Assert.EndsWith(branch, pipeline);
    }
}
