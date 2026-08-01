using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>常駐と多重起動の契約。</summary>
[Collection(E2ECollection.Name)]
public sealed class ResidentWorkerTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>常駐ワーカーが既に居るときの2本目は即座に終了する。</summary>
    private const int ExitWorkerAlreadyRunning = 3;

    [Fact]
    public void SecondWorkerBootstrap_ExitsImmediatelyInsteadOfRunningTwoEngines()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        // 内部フラグを付けて手で2重起動した場合、ランチャーの Mutex を通らないので
        // StartResidentWorker 側のキー登録が唯一の防衛線になる。
        var second = instance.RunWorkerBootstrap(TimeSpan.FromSeconds(60));
        output.WriteLine(second.ToString());
        Assert.Equal(ExitWorkerAlreadyRunning, second.ExitCode);

        // 元のワーカーは生きていて、コマンドは引き続き通ること。
        Assert.Equal(0, instance.Run("ping").ExitCode);

        // 常駐ワーカーは1プロセスだけ（activity.log の app.start が増えていない）。
        var pids = ActivityLogFile.WorkerPids(instance.ReadActivityLog()).ToArray();
        Assert.Single(pids);
    }

    /// <summary>
    /// <b>ワーカーを起動した直後の「最初の」コマンドが 1 回で通ること。</b>
    ///
    /// <para>
    /// これは実バグの回帰テスト。ワーカーは
    /// <c>StartResidentWorker</c> の冒頭でインスタンスキーを登録するが、
    /// リダイレクトの受信ハンドラ（<c>Activated</c>）が張られるのは
    /// <c>Application.Start</c> → App ctor で、間に GStreamer の
    /// <c>StaticInitialize()</c> が挟まる。**この窓ではキーは登録済み＝ランチャーからは
    /// 「ワーカーが居る」と見えるのに購読者が居ない**ため、リダイレクトは
    /// <b>痕跡ゼロで捨てられ</b>、ランチャーは結果通知を待ち切って
    /// <c>ExitCode_WorkerResultTimeout</c> を返していた。
    /// 利用者から見れば「アプリ起動直後のコマンドが黙って失われる」不具合。
    /// </para>
    ///
    /// <para>
    /// <b>「ping が成功すること」を見るだけでは、この退行を検出できない。</b>
    /// <see cref="AppInstance.StartWorkerAndWaitUntilReady"/> は ping を
    /// <b>繰り返す</b>ので、1回目を取りこぼしてもスイートは緑のままで、
    /// **変わるのは所要時間だけ**になる（大幅に伸びる）。
    /// <b>そこで「何回目で通ったか」を直接表明する。</b>
    /// </para>
    ///
    /// <para>
    /// <b>偽陽性の見立て</b>: 分離は 2 桁ある。退行があると 4/4 が 60 秒で失敗（＝
    /// ハーネスの <see cref="AppInstance.ReadyPingTimeout"/> 15 秒に確実に掛かる）、
    /// 無ければ 4/4 が 0.3〜4 秒で成功。GStreamer レジストリは
    /// <see cref="PublishedApp"/> がアセンブリ毎に 1 回温めてあるので、
    /// ここで初回構築の 10 秒超を踏むことはない。
    /// </para>
    /// </summary>
    [Fact]
    public void TheFirstCommandAfterTheWorkerStarts_SucceedsOnTheFirstAttempt()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        // ワーカーはここでは起こさない ── Create の中のポーリングを通してしまうと
        // 「何回目で通ったか」が観測できなくなる。
        using var instance = AppInstance.Create(app, settings, startWorker: false);

        int attempts = instance.StartWorkerAndWaitUntilReady(AppInstance.DefaultReadyBudget);
        output.WriteLine($"ping attempts = {attempts}");

        Assert.True(attempts == 1,
            $"ワーカー起動直後の最初の ping が {attempts} 回目で通りました（1 回目で通るはずです）。" +
            "リダイレクト経路のレディ待ち（SingleInstanceManager.Launcher の " +
            "WaitUntilWorkerAcceptsCommands）が効いていない可能性があります ── " +
            "キー登録済みだが Activated 未購読の窓へリダイレクトすると、" +
            "コマンドは痕跡ゼロで捨てられます。" +
            Environment.NewLine + instance.DiagnosticDump());
    }

    /// <summary>
    /// 常駐したまま複数のコマンドを跨いでも、エンジンが作り直されないこと
    /// （docs/coverage-gaps.md「録画エンジンの寿命（App 所有）」の、L2 で見える範囲の確認）。
    /// ページ破棄を伴う経路は GUI 操作が要るので L3 の担当。
    /// </summary>
    [Fact]
    public void TheEngineOutlivesIndividualCommands()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
            Thread.Sleep(TimeSpan.FromSeconds(2));
            Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);
        }

        var files = instance.ListRecordings();
        Assert.Equal(3, files.Count);
        foreach (string file in files)
        {
            RecordedMp4.AssertUsable(file, instance, output);
        }

        // 常駐ワーカーは終始1プロセス（＝録画のたびに起動し直していない）。
        Assert.Single(ActivityLogFile.WorkerPids(instance.ReadActivityLog()));

        // レコーダーの初期化も1回だけ（＝エンジンが作り直されていない）。
        Assert.Single(ActivityLogFile.Events(instance.ReadActivityLog(), "recorder.init ok"));
    }
}
