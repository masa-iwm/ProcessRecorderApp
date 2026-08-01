using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// 障害が<b>ユーザーに届くこと</b>の検証（実際にあった不具合の回帰テスト）。
/// bus の Error をパースもログもせず捨てると、壊れた MP4 が黙って残り
/// CLI は 0 を返す。
///
/// <para>
/// 障害はどちらも<b>設定だけで</b>起こす ── コードに注入を入れると
/// 「注入したコードをコミットしない」運用に依存することになり、CI で回せない。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class ErrorReportingTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>
    /// 書き込めない出力先を指定して録画を始めると、src 側パイプライン（filesink）が
    /// エラーを出す。これが <c>recorder.error</c> と <c>recording.aborted</c> として
    /// 記録され、録画が止まること。
    /// </summary>
    [Fact]
    public void UnwritableDestination_IsLoggedAndAbortsTheRecording()
    {
        var settings = new SettingsFile();
        var recorder = settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, startWorker: false, configure: i =>
        {
            // 出力先と同名のディレクトリを作っておく。filesink はこのパスを開けない。
            // 「権限の無いパス」で書くと、管理者として実行されている環境で成立しなくなる。
            string blocked = Path.Combine(i.RecordingsDir, "blocked.mp4");
            Directory.CreateDirectory(blocked);
            recorder.FilenameTemplate = blocked;
        });
        instance.StartWorkerAndWaitUntilReady(AppInstance.DefaultReadyBudget);

        var start = instance.Run("start-recording-all");
        output.WriteLine(start.ToString());

        // 開始そのものが弾かれる場合もあれば、開始後に src バスのエラーで中止される場合もある。
        // どちらでも「黙って壊れたファイルが残る」ことにはならない、というのが契約。
        Thread.Sleep(TimeSpan.FromSeconds(5));

        var log = instance.ReadActivityLog();
        output.WriteLine(string.Join(Environment.NewLine, log));

        Assert.NotEmpty(ActivityLogFile.Events(log, "recorder.error"));
        Assert.NotEmpty(ActivityLogFile.Events(log, "recording.aborted"));

        // 中止後は録画中でないこと（＝停止コマンドが「実行できない」で弾かれる）。
        var stop = instance.Run("stop-recording-all");
        Assert.NotEqual(0, stop.ExitCode);
    }

    /// <summary>
    /// 録画元が途中で死んだとき（監視対象モニタのケーブルが抜けた等の模擬）、
    /// エラーが記録され、<b>復帰が多重化せずに</b>試行されること。
    /// </summary>
    [Fact]
    public void SourceFailure_IsLoggedAndRecoveryIsScheduledOnlyOnce()
    {
        var settings = new SettingsFile();
        var recorder = settings.AddRecorder("R1");

        // identity error-after=N は N バッファ後に本物の GStreamer Error を出す。
        // 15fps なので約 2 秒後。SrcPipeline はユーザーが編集できる項目なので、
        // 設定に書くだけでコード変更なしに障害を起こせる。
        recorder.SrcPipeline =
            "videotestsrc is-live=true do-timestamp=true ! identity error-after=30 ! videoconvert ! " +
            "video/x-raw,format=I420,width=320,height=240,framerate=15/1";

        using var instance = AppInstance.Create(app, settings);

        // 最初の復帰予約（5 秒後）とその結果までを観測する。
        Thread.Sleep(TimeSpan.FromSeconds(12));

        var log = instance.ReadActivityLog();
        output.WriteLine(string.Join(Environment.NewLine, log));

        var errors = ActivityLogFile.Events(log, "recorder.error");
        Assert.NotEmpty(errors);

        var restarts = ActivityLogFile.Events(log, "recorder.restart");

        // 復帰が試みられること。
        var scheduled = restarts.Where(l => l.Contains(" scheduled in ")).ToArray();
        Assert.NotEmpty(scheduled);

        // **予約が積まれないこと。** 1つの障害に対して GStreamer は複数の
        // エラーを出す（identity の失敗が basesrc の "Internal data stream error" を誘発する）。
        // 拒否しないと、その件数ぶん（実測 41 件）の復帰試行が並走する。
        // 「積まなかった」ことは拒否ログとして残るので、それが1件でも出ていることを見る
        // ── 件数の上限だけを見ると、予約が1回も起きていない場合にも通ってしまう。
        var rejected = restarts.Where(l => l.Contains("not stacking another")).ToArray();
        Assert.NotEmpty(rejected);
        Assert.True(scheduled.Length <= 8,
            $"復帰予約が多すぎます（{scheduled.Length} 件）:" + Environment.NewLine +
            string.Join(Environment.NewLine, scheduled));

        // ログが洪水になっていないこと（BusMessageThrottle が効いていること）。
        Assert.True(errors.Count <= 10,
            $"recorder.error が畳まれていません（{errors.Count} 件）");
    }
}
