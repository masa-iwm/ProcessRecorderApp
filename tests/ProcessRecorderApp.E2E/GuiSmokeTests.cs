using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L3（GUI・UIA）ハーネス自体が正しいことの確認。ここが緑にならないうちは、
/// 以降の GUI テストの赤は「製品の不具合」ではなく「テスト基盤の不具合」でありうる。
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class GuiSmokeTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>
    /// ウィンドウのタイトル。<b>製品名なので en/ja で同じ</b>ため resw には入れていない
    /// （<c>LocalizationTests.Values_AreActuallyTranslated</c> が ja==en を未翻訳として落とす）。
    /// 出所は <c>MainWindow.xaml</c> の <c>&lt;TitleBar Title="..."&gt;</c>。
    /// </summary>
    public const string WindowTitle = "Process Recorder App";

    [Fact]
    public void TheWindowOpens_AndEverySectionSwitches()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        output.WriteLine(ui.DescribeVisibleElements());

        // README の画面構成契約（4画面）。切替そのものを表明しているので、
        // どれか1つでもパネルが出なくなれば落ちる。
        foreach (var section in new[] { UiSection.Log, UiSection.Variables, UiSection.Settings, UiSection.Preview })
        {
            ui.SwitchTo(section);
            output.WriteLine($"--- {section} ---");
            output.WriteLine(ui.DescribeVisibleElements());
        }

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// Log 画面が既定でターミナル（WebView2 + xterm.js）を出していること。
    ///
    /// <para>
    /// <b>ここで表明できるのは「要素が在る・有効である」までで、中身は読めない。</b>
    /// WebView2 はブラウザープロセス側に別の UIA ツリーを持ち、WebGL レンダラーでは
    /// 文字が GPU テクスチャになるのでアクセシブルテキストが 1 つも出ない。
    /// 表示内容の確認は目視（docs/coverage-gaps.md「Log 画面への表示経路」）。
    /// </para>
    /// <para>
    /// それでもこの表明には意味がある ── <b>フォールバックの注記が出ていない</b>ことが、
    /// WebView2 の初期化が実際に成功したことの唯一の自動的な証拠である。
    /// アンパッケージ発行での WinRT アクティベーションが壊れると、ここが最初に落ちる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheLogScreen_ShowsTheTerminal_NotTheFallbackList()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Log);
        output.WriteLine(ui.DescribeVisibleElements());

        var terminal = ui.WaitForElement("logTerminalView");
        Assert.True(terminal.Properties.IsEnabled.ValueOrDefault);
        _ = ui.WaitForElement("CopyAllLogButton");

        // 注記が出ているなら WebView2 を諦めてリスト表示に落ちている
        Assert.Null(ui.TryFindElement("LogTerminalUnavailableText", TimeSpan.FromSeconds(2)));

        // 画面からはどちらのレンダラーか分からないので、記録側で確かめる。
        // ここが「WebView2 が実際に起きて JS が走った」ことの唯一の自動的な証拠
        var terminalEvents = ActivityLogFile.Events(instance.ReadActivityLog(), "log.terminal");
        output.WriteLine(string.Join(Environment.NewLine, terminalEvents));
        Assert.Contains(terminalEvents, e => e.Contains("ready renderer=", StringComparison.Ordinal));
        Assert.DoesNotContain(terminalEvents, e => e.Contains("fallback=list", StringComparison.Ordinal));
    }

    /// <summary>
    /// アプリのトップレベルウィンドウの一覧を診断として残す。
    ///
    /// <para>
    /// <b>これは退行検出器ではない。</b> トレイアイコンのツールチップが
    /// WinUI 3 の既定値 <c>"WinUI Desktop"</c> に化ける退行
    /// （<c>MainWindow</c> の ctor で <c>Title</c> を設定することで防いでいる）は
    /// <b>キャプション経由では原理的に検出できない</b>ので、
    /// 観測できる事実だけを記録する形にしてある。
    /// </para>
    /// <para>
    /// <b>なぜできないか。</b> WinUIEx は <c>WindowManager.AddToTray()</c> で
    /// <c>AppWindow.Title</c> をツールチップに渡し、<b>それは登録時の1回きり</b>で
    /// 以後追従しない。登録は Launch-to-Tray の起動直後に起こる。一方
    /// <b>キャプションは XAML の <c>TitleBar</c> が読み込まれた時点で製品名に直る</b>
    /// ── <c>Title</c> の設定を外す退行注入を入れても、少し後に外から見た
    /// キャプションは正しい値になっている（実測。注入下でも
    /// <c>class='WinUIDesktopWin32WindowClass' caption='Process Recorder App'</c> が現れた）。
    /// つまり「症状が出るのは登録の一瞬だけ」で、キャプションを見る表明は
    /// <b>いつ見ても緑</b>になる。
    /// </para>
    /// <para>
    /// <b>実際に効く観測は通知領域のツールチップだけ</b>で、そこは
    /// <c>Category=Fragile</c>（<see cref="TrayUi"/>）の領域。
    /// この防御は UIA で通知領域の項目名を読んで両方向に確認済み
    /// （<c>Title</c> あり → <c>'Process Recorder App'</c> /
    /// なし → <c>'WinUI Desktop'</c>）。詳細は docs/coverage-gaps.md の
    /// 「トレイアイコンのツールチップ」の節。
    /// </para>
    /// </summary>
    [Fact]
    public void TheTopLevelWindows_AreRecordedForDiagnosis()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        // ウィンドウは開かない（AppUi.Activate を呼ばない）。
        var windows = instance.ListWorkerWindows();
        output.WriteLine(string.Join(Environment.NewLine,
            windows.Select(w => $"class='{w.ClassName}' caption='{w.Caption}'")));

        // ここで表明できるのは「アプリのウィンドウが在ること」まで。
        Assert.Contains(windows, w => w.ClassName == AppInstance.MainWindowClassName);
    }
}
