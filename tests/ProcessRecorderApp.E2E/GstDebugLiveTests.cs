using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L2/L3: <c>GstDebug</c> の即時反映と、Log 画面の「グラフを保存」。
///
/// <para>
/// どちらも<b>起動順序に依存する</b>ので、この層でしか見られない。
/// <c>AppSettings</c> は <c>Gst.Functions.Init</c> より前に読み込まれ、その逆シリアル化が
/// <c>OnGstDebugChanged</c> を呼ぶ ── ネイティブへ触るガードを外すと、
/// <b>settings.json に <c>GstDebug</c> を書いた利用者だけがアプリを起動できなくなる。</b>
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class GstDebugLiveTests(PublishedApp app)
{
    /// <summary>デバウンス保存（約1秒）が書き切るまでの待ち。</summary>
    private static readonly TimeSpan DebounceMargin = TimeSpan.FromSeconds(3);

    /// <summary>
    /// <b>これが今回いちばん重要な回帰</b>: <c>GstDebug</c> が入った settings.json で
    /// <b>初回から</b>起動しきること。
    ///
    /// <para>
    /// 反映のフック（<c>AppSettings.OnGstDebugChanged</c>）は逆シリアル化の setter でも
    /// 発火する。そこはロケーターの選定と <c>Initialize(options)</c> より前なので、
    /// <c>DebugLogEx.IsGstInitialized</c> のガードが無いとローダーが勝手な根をピンし、
    /// 後続の初期化が InvalidOperationException でプロセスごと落ちる。
    /// <b>症状は「起動しない」であって、ログには何も残らない。</b>
    /// </para>
    /// </summary>
    [Fact]
    public void AWorkerStartsEvenWhenGstDebugIsAlreadySetInSettings()
    {
        var settings = new SettingsFile { GstDebug = "3" };
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        // ping が 0 を返す＝ワーカーが起動しきってコマンドを受けられている。
        instance.RunExpecting(0, "ping");

        var lines = instance.ReadActivityLog();
        Assert.NotEmpty(ActivityLogFile.Events(lines, "app.start"));
        Assert.Empty(ActivityLogFile.Events(lines, "app.error"));
    }

    /// <summary>
    /// 実行中の <c>GstDebug</c> の変更が<b>再起動なしで</b>適用されること。
    ///
    /// <para>
    /// <b>「API を呼んだ」で満足しない。</b> <c>activity.log</c> の <c>gst.debug</c> 行だけを
    /// 見ると、しきい値の適用が実際には効いていなくても緑になる。そこで
    /// <b>GStreamer のデバッグ出力そのものが出始めること</b>まで見る
    /// （<c>DebugLogFile</c> に落ちる。<c>0:00:01.234567890</c> 形式の行頭は GStreamer 固有で、
    ///  アプリ自身の出力＝<c>yyyy-MM-dd HH:mm:ss.fff</c> とは形が違うので取り違えない）。
    /// </para>
    /// <para>
    /// 経路は「外部で settings.json を書き換え → 再読み込み」。UI へ直接打ち込む経路より
    /// 安定していて、かつ <c>Reload()</c> でも反映されることまで同時に押さえられる
    /// （以前は <c>GstDebug</c> は再読み込みで<b>効かない</b>値だった）。
    /// </para>
    /// <para>
    /// しきい値を <c>basesrc:5</c> に絞っているのは、常時稼働の sink パイプラインの
    /// <c>videotestsrc</c> が絶えず出す一方で、<b>全カテゴリを 5 にしたときのような
    /// 洪水にはならない</b>ため。
    /// </para>
    /// </summary>
    [Fact]
    public void ChangingGstDebugAtRuntimeIsAppliedWithoutRestarting()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        string debugLog = Path.Combine(instance.DataDir, "debug.log");

        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);
        Assert.Equal("", ui.WaitForPropertyText("GstDebug", ""));

        // 起動直後の値（空欄）では適用も出力も起きない ── ここで既に出ていると、
        // 下の表明が「変更したから出た」ことを示さなくなる。
        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "gst.debug"));
        Assert.False(HasGStreamerDebugOutput(debugLog),
            "変更する前から GStreamer のデバッグ出力が出ています（この試験は「変わったこと」を見られません）。");

        // 起動・表示に伴うデバウンス保存を出し切らせてから外部の書き換えを行う。
        Thread.Sleep(DebounceMargin);

        const string edited = "basesrc:5";
        instance.Settings.GstDebug = edited;
        instance.WriteSettings();

        ui.Click("ReloadSettingsButton");
        ui.ConfirmDialog();

        Assert.Equal(edited, ui.WaitForPropertyText("GstDebug", edited));

        var applied = WaitForEvents(instance, "gst.debug");
        Assert.Contains(applied, line => ActivityLogFile.DetailOf(line).Contains(edited, StringComparison.Ordinal));

        Assert.True(WaitUntil(() => HasGStreamerDebugOutput(debugLog)),
            "しきい値は適用されたのに、GStreamer のデバッグ出力が出てきません。");

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// 保存先が空欄でも<b>何もしないで終わらせない</b>こと（データディレクトリへ出す）。
    ///
    /// <para>
    /// 空欄は製品の既定なので、ここが no-op だと<b>ほとんどの利用者にとってボタンが
    /// 無反応な部品</b>になる（しかも押した側には理由が見えない）。
    /// </para>
    /// </summary>
    [Fact]
    public void SavingGraphsFallsBackToTheDataDirectoryWhenNoDirectoryIsConfigured()
    {
        var settings = new SettingsFile();   // GstDebugDumpDotDir は書かない（＝空欄）
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Log);
        ui.Click("SaveDotFilesButton");

        WaitForEvents(instance, "gst.dot");
        Assert.NotEmpty(Directory.GetFiles(instance.DataDir, "*.dot"));
    }

    /// <summary>
    /// 「…」（ビルダー起動）ボタンが <c>GstDebugDumpDotDir</c> と <c>DebugLogFile</c> の
    /// 両方に出ていること。
    ///
    /// <para>
    /// <b>押さない。</b> 押すとネイティブのモーダルが開き、閉じるまで戻ってこない。
    /// しかも<b>そのダイアログは UIA のデスクトップ直下の子として現れない</b>ので、
    /// 「開いたこと」を要素の増加で確かめることもできない（実測）。
    /// ここで見たいのは「<c>[ValueBuilder]</c> の配線が届いて Builder 行になっていること」なので
    /// ボタンの存在で足り、ダイアログ自体は手動確認に回す
    /// （<c>docs/coverage-gaps.md</c>「デバッグ用パスのピッカー」）。
    /// </para>
    /// </summary>
    [Fact]
    public void TheDebugPathRowsOfferABuilderButton()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);
        Assert.NotNull(ui.TryFindElement("GstDebugDumpDotDir.Build", TimeSpan.FromSeconds(5)));
        Assert.NotNull(ui.TryFindElement("DebugLogFile.Build", TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// 起動時は<b>外部の <c>GST_DEBUG</c> が勝つ</b>こと（settings.json の値で上書きしない）。
    ///
    /// <para>
    /// <c>ApplyStartupEnvironmentVariables</c> の「既に設定済みなら上書きしない」は、
    /// シェルや <c>launchSettings.json</c> で指定して起動する使い方を支えている。
    /// <b><c>OnLoaded()</c> で設定値を押し込む実装にすると、ここが黙って壊れる</b>
    /// ── 空欄の設定が外部指定を既定へ戻してしまい、症状は「環境変数が効かない」になる。
    /// </para>
    /// </summary>
    [Fact]
    public void AnExternalGstDebugWinsAtStartup()
    {
        var settings = new SettingsFile();   // GstDebug は空欄（＝製品の既定）
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, startWorker: false);
        instance.ExtraEnvironment["GST_DEBUG"] = "basesrc:5";
        instance.StartWorkerAndWaitUntilReady(TimeSpan.FromMinutes(1));

        string debugLog = Path.Combine(instance.DataDir, "debug.log");
        Assert.True(WaitUntil(() => HasGStreamerDebugOutput(debugLog)),
            "外部の GST_DEBUG を指定して起動したのに、GStreamer のデバッグ出力が出ません。");

        // 設定側は空欄のままなので、実行中の適用は起きていない。
        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "gst.debug"));
    }

    /// <summary>
    /// 実行中の変更は<b>外部の <c>GST_DEBUG</c> より優先される</b>こと。
    ///
    /// <para>
    /// 起動時とは非対称にしてある（起動時は外部が勝つ）。<b>この非対称は意図</b>で、
    /// 「環境変数で起動した後でも、画面から出力を上げ下げできる」ことを支えている。
    /// </para>
    /// <para>
    /// 外部指定を <c>1</c>（ERROR のみ＝実質何も出ない）にしておき、
    /// 変更後に出力が<b>出はじめる</b>ことで見る ── 「出なくなること」を見ようとすると
    /// 待ち時間の勝負になって不安定になる。
    /// </para>
    /// </summary>
    [Fact]
    public void AChangeAtRuntimeOverridesTheExternalGstDebug()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, startWorker: false);
        instance.ExtraEnvironment["GST_DEBUG"] = "1";
        instance.StartWorkerAndWaitUntilReady(TimeSpan.FromMinutes(1));

        string debugLog = Path.Combine(instance.DataDir, "debug.log");

        using var ui = AppUi.Activate(instance);
        ui.SwitchTo(UiSection.Settings);
        Assert.False(HasGStreamerDebugOutput(debugLog),
            "外部指定は ERROR のみのはずなのに、デバッグ出力が出ています。");

        Thread.Sleep(DebounceMargin);

        const string edited = "basesrc:5";
        instance.Settings.GstDebug = edited;
        instance.WriteSettings();

        ui.Click("ReloadSettingsButton");
        ui.ConfirmDialog();
        Assert.Equal(edited, ui.WaitForPropertyText("GstDebug", edited));

        Assert.True(WaitUntil(() => HasGStreamerDebugOutput(debugLog)),
            "画面から変更したのに、外部の GST_DEBUG のままで出力が変わりません。");
    }

    /// <summary>GStreamer の既定ログ書式（<c>0:00:01.234567890</c> で始まる行）。</summary>
    private static readonly System.Text.RegularExpressions.Regex GStreamerLogLine =
        new(@"\d:\d{2}:\d{2}\.\d{9}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// <c>DebugLogFile</c> に GStreamer のデバッグ出力が入っているか。
    /// アプリが書き込み中でも読めるよう共有で開く。
    /// </summary>
    private static bool HasGStreamerDebugOutput(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return GStreamerLogLine.IsMatch(reader.ReadToEnd());
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Log 画面の「グラフを保存」が、<b>設定した保存先へ</b> <c>.dot</c> を書くこと。
    ///
    /// <para>
    /// 保存先は押すたびに <c>GstDebugDumpDotDir</c> から読む。GStreamer の
    /// <c>gst_debug_bin_to_dot_file</c> は <c>gst_init</c> 時に控えた出力先しか見ないので、
    /// <b>その API に戻すとこのテストは「1件も出ない」で落ちる</b>
    /// （既定の起動では <c>GST_DEBUG_DUMP_DOT_DIR</c> が未設定＝無言で何も書かない）。
    /// </para>
    /// </summary>
    [Fact]
    public void SavingGraphsWritesDotFilesIntoTheConfiguredDirectory()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        string dotDir = Path.Combine(instance.DataDir, "dots");

        using var ui = AppUi.Activate(instance);

        // 設定は画面から入れる（＝押す前に変更した値がその場で効くことも同時に見る）。
        ui.SwitchTo(UiSection.Settings);
        ui.SetPropertyText("GstDebugDumpDotDir", dotDir);
        Assert.Equal(dotDir, ui.WaitForPropertyText("GstDebugDumpDotDir", dotDir));

        ui.SwitchTo(UiSection.Log);
        ui.Click("SaveDotFilesButton");

        var saved = WaitForEvents(instance, "gst.dot");
        Assert.Contains(saved, line => ActivityLogFile.DetailOf(line).Contains(dotDir, StringComparison.Ordinal));

        // レコーダー1台なら sink（初期化済み）のグラフが出る。**件数は決め打ちしない**
        // ── src は録画していないと存在せず、プレビューは画面を開かないと作られない。
        string[] files = Directory.GetFiles(dotDir, "*.dot");
        Assert.NotEmpty(files);

        // **中身まで見る。** 「ファイルはあるが空」は、書き出しだけ残して
        // グラフの取得が壊れたときの見え方そのもので、存在確認では緑のまま通る。
        string first = File.ReadAllText(files[0]);
        Assert.Contains("digraph", first, StringComparison.Ordinal);
        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// 指定したイベントが activity.log に出るまで待って返す。
    /// <b>固定 sleep で読まない</b> ── 書き込みはワーカー側で非同期に起きる。
    /// </summary>
    private static IReadOnlyList<string> WaitForEvents(AppInstance instance, string eventName)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<string> found;
        do
        {
            found = ActivityLogFile.Events(instance.ReadActivityLog(), eventName);
            if (found.Count > 0)
                return found;
            Thread.Sleep(200);
        }
        while (deadline.Elapsed < TimeSpan.FromSeconds(15));

        Assert.Fail($"activity.log に '{eventName}' の行が出ませんでした。");
        return found;
    }

    /// <summary>条件が成立するまで有界で待つ。<b>固定 sleep で読まない</b>ため。</summary>
    private static bool WaitUntil(Func<bool> condition)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            if (condition())
                return true;
            Thread.Sleep(200);
        }
        while (deadline.Elapsed < TimeSpan.FromSeconds(15));
        return false;
    }
}
