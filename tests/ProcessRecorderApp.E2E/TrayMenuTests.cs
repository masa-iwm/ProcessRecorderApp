using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L3: タスクトレイアイコンの右クリックメニュー。
///
/// <para>
/// <b>これが唯一の防波堤になっているもの:</b>
/// <list type="bullet">
/// <item><b>メニューの文言がローカライズされていること。</b>
///   <c>Localization.GetString("Resources/Tray_Show")</c> をリテラルへ戻す退行は、
///   L4 の <c>UnreferencedKeys_AreReportedAsAWarning</c> が「未参照のキー」として
///   <b>警告</b>するだけでゲートにならない。<c>LanguageMatrixTests</c> が見ているのは
///   ウィンドウ<b>内</b>の文言で、トレイメニューはウィンドウの外にある。</item>
/// <item><b>「終了」の経路（<c>SingleInstanceManager.ExitApplication</c>）。</b>
///   <c>ShutdownTests</c> が通すのは Ctrl+閉じる（<c>OnAppWindowClosing</c>）で、
///   <c>ExitApplication</c> は<b>別のメソッド</b> ── キーの登録解除と
///   <c>_allowRealClose</c> を立ててから <c>Application.Exit()</c> する。ここでしか通らない。</item>
/// </list>
/// </para>
///
/// <para>
/// <b><c>Category=Fragile</c>：CI の必須ゲートには入れない。</b> 理由は製品ではなくシェル側にある
/// （オーバーフローの開閉・アイコンの識別・<b>物理的なマウスカーソルを動かすこと</b>）。
/// 詳細は <see cref="TrayUi"/> の説明を参照。ローカルで全件を回すとカーソルが取られるので、
/// 手元で他の作業をしながら回さないこと。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "Fragile")]
public sealed class TrayMenuTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>メニュー項目の並び（<c>OnTrayIconContextMenu</c> の追加順）。</summary>
    private const int ShowItemIndex = 0;
    private const int QuitItemIndex = 1;

    /// <summary>
    /// トレイメニューの文言が強制した表示言語の <c>.resw</c> の値になること。
    ///
    /// <para>
    /// <b>どの行が実際に効くかはホストの表示言語で入れ替わる</b>のは
    /// <see cref="LanguageMatrixTests"/> と同じ ── この開発機（表示言語 ja）で意味があるのは
    /// en-US / de-DE の行で、GitHub ランナー（en-US）では ja-JP の行だけが効く。
    /// <b>1言語に減らしてはいけない。</b>
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("en-US", "en-US")]
    // en-US でも ja-JP でもない表示言語。ニュートラル言語へのフォールバックを見るための行。
    [InlineData("de-DE", UiResources.NeutralLocale)]
    public void TheTrayMenuTextComesFromTheForcedLanguage(string forced, string expectedLocale)
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        // **activate しない。** Launch-to-Tray のまま、ウィンドウの外だけを相手にする
        // ── メインウィンドウを出してしまうと、メニューを「自分のプロセスが所有する
        // ポップアップ」として見分ける手がかりが弱くなる。
        using var instance = AppInstance.Create(app, settings, language: forced);
        using var tray = TrayUi.AttachTo(instance);

        var names = tray.OpenAndReadMenuItemNames();
        output.WriteLine($"[{forced}] トレイメニュー: [{string.Join(" | ", names)}]");

        string[] expected =
        [
            UiResources.Get(expectedLocale, "Tray_Show"),
            UiResources.Get(expectedLocale, "Tray_Quit"),
        ];

        Assert.True(
            names.SequenceEqual(expected, StringComparer.Ordinal),
            $"[{forced}] トレイメニューの文言が {expectedLocale} になっていません。" +
            $" 期待 [{string.Join(" | ", expected)}] / 実際 [{string.Join(" | ", names)}]");

        // 「表示」が実際にウィンドウを出すこと（文言だけ合っていて配線が切れている状態を弾く）。
        tray.InvokeMenuItem(ShowItemIndex);
        Assert.True(
            tray.WaitForMainWindow(TimeSpan.FromSeconds(20)),
            $"[{forced}] トレイメニューの「{expected[ShowItemIndex]}」でウィンドウが出ませんでした。" +
            Environment.NewLine + instance.DiagnosticDump());

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// トレイメニューの「終了」でプロセスが正常に終わること
    /// （<c>ExitApplication</c> → キーの登録解除 → <c>Application.Exit()</c> →
    /// <c>AppWindow.Destroying</c> → <c>Save()</c> → <c>engine.Dispose()</c>）。
    ///
    /// <para>
    /// <b><c>app.exit exitCode=0</c> だけを見てはいけない。</b> <c>App_UnhandledException</c> が
    /// <c>e.Handled = true</c> を立てるので、破棄の途中で例外が出てもプロセスは 0 で終わり
    /// <c>app.exit</c> も書かれる。<c>app.error</c> が無いことと、
    /// <b>保存された settings.json の中身</b>までを併せて見る（<c>ShutdownTests</c> と同じ理由）。
    /// </para>
    /// </summary>
    [Fact]
    public void TheTrayMenuQuitItemShutsTheProcessDownCleanly()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");
        settings.AddRecorder("R2");

        using var instance = AppInstance.Create(app, settings);
        using var tray = TrayUi.AttachTo(instance);

        var names = tray.OpenAndReadMenuItemNames();
        output.WriteLine($"トレイメニュー: [{string.Join(" | ", names)}]");
        Assert.Equal(2, names.Count);

        tray.InvokeMenuItem(QuitItemIndex);

        var deadline = System.Diagnostics.Stopwatch.StartNew();
        bool exited = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (!IsAlive(tray.ProcessId))
            {
                exited = true;
                break;
            }
            Thread.Sleep(250);
        }

        Assert.True(exited,
            $"トレイメニューの「{names[QuitItemIndex]}」で pid {tray.ProcessId} が 30 秒以内に終了しませんでした。" +
            Environment.NewLine + instance.DiagnosticDump());

        var log = instance.ReadActivityLog();
        output.WriteLine(string.Join(Environment.NewLine, log));

        Assert.NotEmpty(ActivityLogFile.Events(log, "app.exit"));
        Assert.Empty(ActivityLogFile.Events(log, "app.error"));

        // 終了経路の Save() が壊れたエンジンを相手に走ると、空の Recorders を
        // 書き出しうる ── これは終了コードにもログにも現れない。
        string saved = File.ReadAllText(instance.SettingsPath);
        Assert.Contains("\"R1\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"R2\"", saved, StringComparison.Ordinal);
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
