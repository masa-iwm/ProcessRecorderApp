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
    /// SetAction 内で録画開始が例外になったとき（テンプレートの書式誤り）、CLI が
    /// **終了コード 0 の偽成功を返さない**こと。
    ///
    /// <para>
    /// System.CommandLine の既定例外ハンドラが有効なままだと、SetAction 内の例外は
    /// ライブラリに握られて setOutcome 未呼び出しの Silent（終了コード 0）が返る ──
    /// <c>ActivationCommands.ParseCore</c> の <c>EnableDefaultExceptionHandler=false</c> の
    /// 1 行が退行するとここが赤くなる。
    /// </para>
    /// </summary>
    [Fact]
    public void StartFailureInsideTheCommand_DoesNotReportSuccess()
    {
        var settings = new SettingsFile();
        var recorder = settings.AddRecorder("R1");
        // "L" は DateTime の書式指定子として不正で、展開（FormatFilename）が
        // FormatException になる。設定だけで、コード変更なしに SetAction 内の例外を起こせる。
        recorder.FilenameTemplate = "{Now:L}.mp4";

        using var instance = AppInstance.Create(app, settings);

        var start = instance.Run("start-recording", "R1");
        output.WriteLine(start.ToString());

        Assert.NotEqual(0, start.ExitCode);

        var log = instance.ReadActivityLog();
        output.WriteLine(string.Join(Environment.NewLine, log));

        Assert.NotEmpty(ActivityLogFile.Events(log, "recording.start fail"));
        // cli の行は例外で抜ける経路でも必ず出る（そのときの終了コードは非 0）。
        Assert.Contains(ActivityLogFile.Events(log, "cli"),
            l => l.Contains("start-recording", StringComparison.Ordinal)
                 && !l.Contains("exitCode=0", StringComparison.Ordinal));
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

    /// <summary>
    /// <b>録画中にソースが死んで復帰したら、録画も戻ること。</b>
    ///
    /// <para>
    /// 作り直し（<c>Initialize()</c>）は先頭の <c>Close()</c> で進行中の録画を確定させる
    /// ── ファイルは壊れないが、そこで録画は終わる。**常時録画は作り直されるのに
    /// イベント録画だけ再開しない**という非対称が長らくあり、実機のカメラ抜き差しで
    /// 「復帰したのに録れていない」として実際に踏んだ。
    /// </para>
    /// <para>
    /// 見るのは <c>recording.start</c> が <b>2 本</b>出ること ── 利用者が始めた 1 本目と、
    /// 復帰が戻した 2 本目である。1 本目のファイルが確定していること
    /// （<c>recording.stop … result=ok</c>）も併せて見る。
    /// </para>
    /// </summary>
    [Fact]
    public void AfterRecovery_TheRecordingIsResumed()
    {
        var settings = new SettingsFile();
        var recorder = settings.AddRecorder("R1");

        // 15fps で約 6 秒後に本物の Error。EOS も伴うので作り直しへ進む。
        //
        // **`start-recording-all` の到着より先に発火させない。** 全件を並べて走らせた
        // 負荷の下では起動から最初のコマンドまでが数秒に伸びることがあり、短くすると
        // 1 本目が始まる前に壊れて `recording.stop … result=empty` で落ちる。
        recorder.SrcPipeline =
            "videotestsrc is-live=true do-timestamp=true ! identity error-after=90 ! videoconvert ! " +
            "video/x-raw,format=I420,width=320,height=240,framerate=15/1";

        using var instance = AppInstance.Create(app, settings);

        var start = instance.Run("start-recording-all");
        output.WriteLine(start.ToString());
        Assert.Equal(0, start.ExitCode);

        // 障害（約 6 秒）→ 最初の待ち 5 秒 → 作り直し → 録り直し、までを観測する。
        Thread.Sleep(TimeSpan.FromSeconds(18));

        var log = instance.ReadActivityLog();
        output.WriteLine(string.Join(Environment.NewLine, log));

        var restarts = ActivityLogFile.Events(log, "recorder.restart");
        Assert.Contains(restarts, l => l.Contains("will be resumed once the pipeline is rebuilt", StringComparison.Ordinal));
        // **末尾まで書くこと。** "resuming the recording" だけだと、取り消しの行
        // "not resuming the recording after the rebuild (...)" にも一致してしまい、
        // 「録り直さなかった」でこの表明が緑になる。
        Assert.Contains(restarts,
            l => l.Contains("resuming the recording that the rebuild finalized", StringComparison.Ordinal));

        var started = ActivityLogFile.Events(log, "recording.start");
        Assert.True(2 <= started.Count,
            $"復帰後に録画が戻っていません（recording.start が {started.Count} 件）:" + Environment.NewLine +
            string.Join(Environment.NewLine, started));

        // 畳まれた 1 本目は壊れていないこと。
        var stopped = ActivityLogFile.Events(log, "recording.stop");
        Assert.Contains(stopped, l => l.Contains("result=ok", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>つながっていないモニターをパスで指定したら、黙って別の画面を録らずに初期化が失敗すること。</b>
    ///
    /// <para>
    /// <c>monitor-device-path</c> はアプリの擬似プロパティで、パイプラインを組む直前に
    /// <c>monitor-handle</c> へ解決される。一致するモニターが無いときに <c>monitor-index</c> へ
    /// 縮退させると、<b>直そうとしている取り違えを自分で作る</b> ── だから失敗させる。
    /// 失敗の理由には<b>指定されたパスそのもの</b>が入る（入っていないと、利用者は
    /// どのモニターを探して見つからなかったのかを知りようがない）。
    /// </para>
    /// <para>
    /// <b>前提はモニターが 1 台でも列挙できること。</b> 列挙が空の機械では規則が変わり
    /// （縮退＋警告）、この失敗は起こらない ── 二股の表明にすると「ずっと縮退側だけを
    /// 見て緑」になりうるので、前提が無い環境では<b>飛ばす</b>。判定材料は
    /// <c>monitor.devices</c> の <c>count=</c>（0 台でも 1 行出る）。
    /// </para>
    /// </summary>
    [Fact]
    public void AMonitorDevicePathThatIsNotConnected_FailsInitializationAndNamesThePath()
    {
        // 実在しえない形（ZZZTEST）にしてある。書式は本物と同じにして、
        // 「読めなかった」ではなく「一致しなかった」を踏ませる。
        const string bogusPath = @"\\?\DISPLAY#ZZZTEST#5&0&UID0#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        var settings = new SettingsFile();
        var recorder = settings.AddRecorder("R1");

        // パイプライン文字列の中では '\' がエスケープされる（ダイアログの Assemble と同じ形）。
        recorder.SrcPipeline =
            $"d3d12screencapturesrc monitor-index=0 monitor-device-path=\"{bogusPath.Replace(@"\", @"\\")}\""
            + " ! video/x-raw(memory:D3D12Memory), framerate=15/1";

        using var instance = AppInstance.Create(app, settings);

        // **準備完了（ping）はレコーダーの初期化を待たない** ── 待つのは
        // WaitForControllerAsync を通る録画コマンドだけで、ping はそのまま 0 を返す。
        // ここで待たずに読むと、まだ 1 行も出ていない activity.log を見て
        // 下の SkipWhen が成立し、**モニターが在る機械でも恒久的に静かに飛ぶ**。
        instance.WaitForActivityLogEvent("monitor.devices", TimeSpan.FromSeconds(30));

        var log = instance.ReadActivityLog();
        output.WriteLine(string.Join(Environment.NewLine, log));

        var devices = ActivityLogFile.Events(log, "monitor.devices");
        Assert.SkipWhen(devices.Count == 0,
            "monitor.devices が出ていない（モニターの列挙まで到達していない）");
        Assert.SkipWhen(
            devices.All(l => ActivityLogFile.DetailOf(l).StartsWith("count=0 ", StringComparison.Ordinal)),
            "この機械では画面キャプチャのモニターが 1 台も列挙されないため、"
            + "「列挙できたのに一致しない」を踏めない");

        // 列挙の行と初期化失敗の行は別々に書かれるので、ここでも待ち直す
        // （前提が成立している以上、出ないことは無い）。
        instance.WaitForActivityLogEvent("recorder.init fail", TimeSpan.FromSeconds(30));
        log = instance.ReadActivityLog();
        output.WriteLine(string.Join(Environment.NewLine, log));

        var failures = ActivityLogFile.Events(log, "recorder.init fail");
        Assert.NotEmpty(failures);

        // **理由がアプリのものであること。** gst_parse_launch の `no property` で落ちていたら、
        // 擬似プロパティを消し忘れているということになる。
        Assert.Contains(failures, l => l.Contains("is not connected", StringComparison.Ordinal));
        Assert.Contains(failures, l => l.Contains(bogusPath, StringComparison.Ordinal));
        Assert.DoesNotContain(failures, l => l.Contains("no property", StringComparison.Ordinal));
    }
}
