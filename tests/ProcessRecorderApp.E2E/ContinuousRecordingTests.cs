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
        Assert.Equal("error", cells[4]);         // 常時録画の列

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
