using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <b>常時録画（ContinuousRecording）。</b>
///
/// <para>
/// イベント録画とは別に tee の 3 本目の枝を回し、一定時間ごとにファイルを切り替える。
/// 分割は C# 側で書き出しパイプラインを作り直して行う（splitmuxsink は同梱ランタイムに無い）。
/// L1 では規則（SegmentRotationRules / ContinuousBranch）しか見られないので、
/// <b>本当にファイルが分かれて、1 本ずつ再生可能で、イベント録画を邪魔しない</b>ことは
/// 発行物を動かさないと分からない。
/// </para>
/// <para>
/// <b>どのテストも <c>FragmentedOutput = false</c> を明示する。</b> ここの表明は
/// <c>moov</c> の <c>mvhd</c>／<c>stsz</c>（尺・サンプル数）を読むので、fragmented MP4
/// （製品の既定）では尺 0・サンプル数 0 になり、分割やフレームレートの検査が成立しない。
/// fMP4 側のセグメントは <c>RecordingDeliveryTests</c> が見る。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class ContinuousRecordingTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>製品の下限。これより短くはできない。</summary>
    private const int SegmentSeconds = 5;

    /// <summary>
    /// <b>正常終了させる。</b> 常時録画の最後のセグメントが確定するのは終了経路だけなので、
    /// 強制終了（KillWorkers）では moov の書かれていないファイルが残る
    /// ── ここで見たいのは「終了すれば全部使える」ことである。
    /// 正常終了の手段は Ctrl+閉じる だけ（CLI に終了コマンドは無い）。
    /// </summary>
    private static void CloseGracefully(AppInstance instance)
    {
        using var ui = AppUi.Activate(instance);
        ui.CloseWindow(holdControl: true);
        Assert.True(ui.WaitForProcessExit(ExitBudget),
            "Ctrl+閉じる でプロセスが終了しませんでした。" + Environment.NewLine + instance.DiagnosticDump());
    }

    private static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(420);

    /// <summary>
    /// <b>常時録画の解像度がイベント録画を縮めないこと。</b>
    ///
    /// <para>
    /// 枝の capsfilter が要求する幅・高さは、拡縮できる要素を素通りして <c>tee</c> を越え、
    /// <b>ソースまで伝播する</b> ── ソースが任意の大きさを出せる場合、ソースは小さい方を選び、
    /// プレビューもイベント録画も一緒に縮む（実機の 3840x2160 のキャプチャが常時枝の
    /// 960x540 に引きずられた）。出来上がった MP4 は「妥当」なままなので、
    /// <b>大きさを直接読む以外に検出できない</b>。
    /// </para>
    /// <para>
    /// 製品は、ソースの caps が幅・高さを固定していないときは上書きの方を捨てる。
    /// ここではその結果として<b>イベント録画がソースの大きさのまま</b>であることを見る。
    /// </para>
    /// </summary>
    [Fact]
    public void AResolutionOverride_NeverShrinksTheEventRecording()
    {
        var settings = new SettingsFile();
        settings.FragmentedOutput = false;
        var recorder = settings.AddRecorder("R1").WithContinuous(SegmentSeconds);
        // **幅・高さを書かないソース。** videotestsrc は既定の 320x240 を出すが、
        // 下流が小さい大きさを要求すればそれに合わせてしまう（画面キャプチャと同じ性質）。
        recorder.SrcPipeline =
            "videotestsrc is-live=true do-timestamp=true ! videoconvert ! video/x-raw,format=I420,framerate=15/1";
        recorder.ContinuousResolution = "160x120";

        using var instance = AppInstance.Create(app, settings);

        Assert.Equal(0, instance.Run("start-recording", "R1").ExitCode);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Assert.Equal(0, instance.Run("stop-recording", "R1").ExitCode);

        string eventFile = instance.ListRecordings().Single(p => !Path.GetFileName(p).Contains("_c0"));
        var probe = Mp4File.Probe(eventFile);
        output.WriteLine(probe.ToString());

        Assert.True(probe.IsValid, probe.ToString());
        Assert.NotEqual(160, probe.FrameWidth);
        Assert.Equal(320, probe.FrameWidth);
        Assert.Equal(240, probe.FrameHeight);

        // 上書きを捨てたことは黙って済ませない。
        var log = instance.ReadActivityLog();
        Assert.NotEmpty(ActivityLogFile.Events(log, "recorder.continuous-init fail"));
        // イベント録画は無傷（レコーダー自体の初期化は成功している）
        Assert.Empty(ActivityLogFile.Events(log, "recorder.init fail"));

        CloseGracefully(instance);
    }

    /// <summary>
    /// <b>D3d12 経路でも、常時録画の解像度がイベント録画を縮めないこと。</b>
    ///
    /// <para>
    /// こちらは<b>ソースの caps が幅・高さを固定している</b>のに壊れる経路である
    /// ── D3d12 では <c>tee</c> の手前に <c>d3d12convert</c> が居り、枝の要求を
    /// そこで吸収してしまうため（実測でプレビューが 960x540 に落ちた）。
    /// 製品は枝で拡縮するときに <c>tee</c> の手前も固定する。
    /// </para>
    /// </summary>
    [Fact]
    public void OnTheD3d12Path_AResolutionOverride_NeverShrinksTheEventRecording()
    {
        var settings = new SettingsFile();
        settings.FragmentedOutput = false;
        var recorder = settings.AddRecorder("R1").WithContinuous(SegmentSeconds);
        recorder.Type = EventRecordingType.D3d12;
        recorder.SrcPipeline =
            "d3d12testsrc is-live=true do-timestamp=true ! "
            + "video/x-raw(memory:D3D12Memory), format=NV12, width=640, height=480, framerate=15/1";
        recorder.ContinuousResolution = "320x240";

        using var instance = AppInstance.Create(app, settings);

        Assert.Equal(0, instance.Run("start-recording", "R1").ExitCode);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Assert.Equal(0, instance.Run("stop-recording", "R1").ExitCode);

        string eventFile = instance.ListRecordings().Single(p => !Path.GetFileName(p).Contains("_c0"));
        var probe = Mp4File.Probe(eventFile);
        output.WriteLine(probe.ToString());

        Assert.True(probe.IsValid, probe.ToString());
        Assert.Equal(640, probe.FrameWidth);
        Assert.Equal(480, probe.FrameHeight);

        CloseGracefully(instance);

        // 常時録画の方は指定どおり縮んでいること（固定が効きすぎて上書きが死んでいない）
        var segments = Segments(instance, "R1");
        Assert.NotEmpty(segments);
        var segment = Mp4File.Probe(segments[0]);
        output.WriteLine(segment.ToString());
        Assert.Equal(320, segment.FrameWidth);
        Assert.Equal(240, segment.FrameHeight);

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "recorder.continuous-init fail"));
    }

    private static IReadOnlyList<string> Segments(AppInstance instance, string recorder)
        => [.. Directory.GetFiles(instance.RecordingsDir, recorder + "_c*.mp4")
            .OrderBy(File.GetCreationTimeUtc)];

    /// <summary>
    /// <b>一定時間でファイルが分かれ、1 本ずつが単体で使える。</b>
    ///
    /// <para>
    /// StartsOnASyncSample がこの機能の核心 ── セグメントの先頭がキーフレームでなければ、
    /// そのファイルは先頭が壊れて見える。「MP4 として妥当か」だけでは絶対に検出できない。
    /// </para>
    /// </summary>
    [Fact]
    public void TheContinuousRecordingIsCutIntoUsableFiles()
    {
        var settings = new SettingsFile();
        settings.FragmentedOutput = false;
        settings.AddRecorder("R1").WithContinuous(SegmentSeconds);

        using var instance = AppInstance.Create(app, settings);

        // セグメント 3 本ぶん＋切り替えの余裕。イベント録画は一度も開始しない。
        Thread.Sleep(TimeSpan.FromSeconds(SegmentSeconds * 3 + 4));

        // 終了させて最後のセグメントを確定させる。
        CloseGracefully(instance);

        var segments = Segments(instance, "R1");
        output.WriteLine($"segments: {segments.Count}");
        Assert.True(2 <= segments.Count,
            $"常時録画が分割されていません（{segments.Count} 本）{Environment.NewLine}{instance.DiagnosticDump()}");

        foreach (string path in segments)
        {
            Assert.True(Mp4File.IsClosedByWriter(path), $"書き込み側がまだ掴んでいます: {path}");
            var probe = Mp4File.Probe(path);
            output.WriteLine(probe.ToString());
            Assert.True(probe.IsValid, $"再生できないセグメントがあります: {probe}");
            Assert.True(probe.StartsOnASyncSample,
                $"セグメントの先頭がキーフレームではありません: {probe}");
        }

        // 尺は設定値の周辺（最後の 1 本は途中で終わるので除く）。
        foreach (var probe in segments.SkipLast(1).Select(Mp4File.Probe))
            Assert.InRange(probe.DurationSeconds ?? 0, SegmentSeconds * 0.5, SegmentSeconds * 2.5);

        var log = instance.ReadActivityLog();
        Assert.NotEmpty(ActivityLogFile.Events(log, "continuous.start"));
        Assert.NotEmpty(ActivityLogFile.Events(log, "continuous.finalize"));
        Assert.Empty(ActivityLogFile.Events(log, "continuous.error"));
        Assert.Empty(ActivityLogFile.Events(log, "continuous.leak"));
        Assert.Empty(ActivityLogFile.Events(log, "app.error"));
    }

    /// <summary>
    /// <b>イベント録画と共存する。</b> 同じ tee を共有しているので、
    /// 片方を動かすともう片方が止まる形の退行がいちばん起きやすい。
    /// </summary>
    [Fact]
    public void TheEventRecordingStillWorksWhileTheContinuousOneRuns()
    {
        var settings = new SettingsFile();
        settings.FragmentedOutput = false;
        settings.AddRecorder("R1").WithContinuous(SegmentSeconds);

        using var instance = AppInstance.Create(app, settings);

        var start = instance.Run("start-recording-all");
        Assert.Equal(0, start.ExitCode);
        Thread.Sleep(TimeSpan.FromSeconds(3));
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        // イベント録画のファイル（常時録画のセグメントとは名前で分かれている）
        string[] eventFiles = [.. instance.ListRecordings().Where(p => !Path.GetFileName(p).Contains("_c0"))];
        Assert.Single(eventFiles);
        RecordedMp4.AssertUsable(eventFiles[0], instance, output);

        // 常時録画は止まっていない
        Thread.Sleep(TimeSpan.FromSeconds(SegmentSeconds + 3));
        CloseGracefully(instance);

        var segments = Segments(instance, "R1");
        Assert.True(2 <= segments.Count,
            $"イベント録画を挟んだあと常時録画が続いていません（{segments.Count} 本）"
            + Environment.NewLine + instance.DiagnosticDump());

        var log = instance.ReadActivityLog();
        Assert.NotEmpty(ActivityLogFile.Events(log, "recording.stop"));
        Assert.Empty(ActivityLogFile.Events(log, "app.error"));
    }

    /// <summary>
    /// <b>隔離契約。</b> 常時録画側の設定が壊れていても、イベント録画は普通に録れる。
    /// 枝は同じ ParseLaunch に同居するので、2 段初期化が効いていないと
    /// レコーダーごと初期化に失敗する。
    /// </summary>
    [Fact]
    public void ABrokenContinuousSetting_DoesNotTakeDownTheEventRecording()
    {
        var settings = new SettingsFile();
        settings.FragmentedOutput = false;
        var recorder = settings.AddRecorder("R1").WithContinuous(SegmentSeconds);
        // 存在しない要素。枝つきの ParseLaunch だけが失敗する。
        recorder.ContinuousEncodingProperties = "no-such-encoder-for-e2e";

        using var instance = AppInstance.Create(app, settings);

        var status = instance.Run("status");
        output.WriteLine(status.ToString());

        // イベント録画は健全なまま（終了コード 15 にならない）
        Assert.Equal(0, status.ExitCode);
        string[] cells = status.StdOut.TrimEnd().Split(TabChar);
        Assert.Equal("True", cells[1]);          // 初期化済み
        Assert.Equal("error", cells[5]);         // 常時録画の列（6 列目）

        // そして実際に録れる
        Assert.Equal(0, instance.Run("start-recording", "R1").ExitCode);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Assert.Equal(0, instance.Run("stop-recording", "R1").ExitCode);
        RecordedMp4.AssertUsable(instance.ListRecordings().Single(), instance, output);

        var log = instance.ReadActivityLog();
        Assert.NotEmpty(ActivityLogFile.Events(log, "recorder.continuous-init fail"));
        // レコーダー自体の初期化は失敗していない
        Assert.Empty(ActivityLogFile.Events(log, "recorder.init fail"));
    }

    private const char TabChar = (char)9;

    /// <summary>
    /// <b>別のフレームレートで回せる。</b> 尺だけでは分からない（15fps でも 5fps でも
    /// 5 秒は 5 秒）ので、stsz のサンプル数から実効フレームレートを出して見る。
    ///
    /// <para>
    /// videorate は同梱ランタイムに入れてあるので、同梱構成でもこの経路は動く
    /// （同梱物に videorate が在ることは L1 の ContinuousRuntimeDependencyTests が固定する）。
    /// </para>
    /// </summary>
    [Fact]
    public void TheContinuousRecordingCanRunAtItsOwnFramerate()
    {
        var settings = new SettingsFile();
        settings.FragmentedOutput = false;
        var recorder = settings.AddRecorder("R1").WithContinuous(SegmentSeconds);
        recorder.ContinuousFramerate = "5/1";
        recorder.ContinuousResolution = "320x240";

        using var instance = AppInstance.Create(app, settings);

        Thread.Sleep(TimeSpan.FromSeconds(SegmentSeconds * 2 + 4));
        CloseGracefully(instance);

        var segments = Segments(instance, "R1");
        Assert.True(2 <= segments.Count,
            $"別 fps の常時録画が分割されていません（{segments.Count} 本）"
            + Environment.NewLine + instance.DiagnosticDump());

        var probe = Mp4File.Probe(segments[0]);
        output.WriteLine(probe.ToString());
        Assert.True(probe.IsValid, probe.ToString());

        double? fps = probe.EffectiveFramerate;
        Assert.NotNull(fps);
        // エンコーダーのプライムと丸めがあるので厳密には一致しない。
        // ソースの 15fps と取り違えないだけの幅で見る。
        Assert.InRange(fps!.Value, 3.5, 6.5);

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "continuous.error"));
    }

    /// <summary>
    /// <b>終了時に最後のセグメントが確定する。</b> ここを飛ばすと moov が書かれず、
    /// 書きかけの 1 本が丸ごと失われる。
    /// </summary>
    [Fact]
    public void TheLastSegmentIsFinalizedWhenTheAppExits()
    {
        var settings = new SettingsFile();
        settings.FragmentedOutput = false;
        settings.AddRecorder("R1").WithContinuous(SegmentSeconds);

        using var instance = AppInstance.Create(app, settings);

        // 切り替え直後ではなく、書きかけの状態で終える
        Thread.Sleep(TimeSpan.FromSeconds(SegmentSeconds + 2));
        CloseGracefully(instance);

        var segments = Segments(instance, "R1");
        Assert.NotEmpty(segments);

        string last = segments[^1];
        Assert.True(Mp4File.IsClosedByWriter(last), $"終了後もハンドルが残っています: {last}");
        var probe = Mp4File.Probe(last);
        output.WriteLine(probe.ToString());
        Assert.True(probe.IsValid, $"最後のセグメントが確定していません: {probe}");

        Assert.NotEmpty(ActivityLogFile.Events(instance.ReadActivityLog(), "continuous.stop"));
    }
}
