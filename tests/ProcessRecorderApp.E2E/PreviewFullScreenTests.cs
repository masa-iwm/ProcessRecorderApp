using FlaUI.Core.WindowsAPI;
using FlaUI.Core.Input;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L3: プレビューの全画面表示。
///
/// <para>
/// <b>ここでしか確かめられないのは「隠れていること」である。</b> 全画面は
/// <c>AppWindow.SetPresenter(FullScreen)</c> と、ナビ／プロパティペインを畳む
/// <c>x:Bind</c> 関数の組み合わせで成り立っており、<b>片方だけ効いても画面は
/// それらしく見える</b>（ウィンドウは画面いっぱいだが上のバーが残っている、など）。
/// UIA から実際に要素が消えることを表明して初めて、両方効いていると言える。
/// </para>
/// <para>
/// <b>左右キーと右クリックメニューはここでは見ない。</b> フライアウトは
/// <b>別のトップレベル UIA ウィンドウ</b>に出る（<c>AppUi.SearchTree</c> は
/// このウィンドウを根に降りる）ので辿れず、右クリック自体も物理カーソルを要求する
/// ＝ <c>Category=Fragile</c> 行き（CI の必須ゲート外）になる。
/// 手動確認の項目は <c>docs/coverage-gaps.md</c> にある。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class PreviewFullScreenTests(PublishedApp app)
{
    /// <summary>
    /// 自動保存のデバウンス（約1秒）が着地するまでの余白。
    /// <b>裸のリテラルで書かない</b> ── 間隔を延ばしたときにここだけ取り残されると、
    /// 保存が着地する前に読んで <c>before == after</c> が自明に成立し、
    /// 「全画面中は保存しない」という表明が偽緑で形骸化する
    /// （<c>PersistenceTests</c> / <c>GstDebugLiveTests</c> と同じ値・同じ名前）。
    /// </summary>
    private static readonly TimeSpan DebounceMargin = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 全画面に入るとナビゲーションとプロパティペインが UIA から消え、
    /// プレビュー面だけが残ること。戻すと元に戻ること。
    ///
    /// <para>
    /// <b>両方向を見る。</b> 片道だけだと「常に隠れている」実装でも通ってしまう。
    /// </para>
    /// </summary>
    [Fact]
    public void EnteringFullScreen_HidesTheChromeButKeepsThePreviewSurface()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SelectRecorder("R1");

        // ---- 前提: 通常表示ではどちらも見えている ----
        Assert.NotNull(ui.WaitForElement("ToggleFullScreenButton"));
        Assert.NotNull(ui.TryFindElement("StartAllButton"));
        Assert.NotNull(ui.TryFindElement("TogglePaneButton"));

        // ---- 全画面へ ----
        ui.Click("ToggleFullScreenButton");

        // ナビゲーションのペイン（既定は上部のバー）が消えること。
        // **ここが `NavigationView.IsPaneVisible` が Top モードでも効くことの実測でもある。**
        Assert.True(ui.WaitUntilGone("StartAllButton"),
            "全画面にしたのにナビゲーションのペイン（StartAllButton）が残っています。"
            + Environment.NewLine + instance.DiagnosticDump());

        // プロパティペインも畳まれること（ヘッダーのボタン列ごと）。
        Assert.True(ui.WaitUntilGone("TogglePaneButton"),
            "全画面にしたのにプロパティペイン（TogglePaneButton）が残っています。"
            + Environment.NewLine + instance.DiagnosticDump());

        // **プレビュー面は残る。** AccessibilityView="Control" を付けてあるので
        // 全画面中も UIA から見える（見えなくなったら隠し過ぎ）。
        Assert.NotNull(ui.TryFindElement("PreviewSurface"));

        // ---- Esc で戻す ----
        // FlaUI の Keyboard は物理カーソルを動かさないので Category=Fragile にしなくてよい。
        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.NotNull(ui.WaitForElement("StartAllButton"));
        Assert.NotNull(ui.WaitForElement("TogglePaneButton"));
        Assert.NotNull(ui.TryFindElement("PreviewSurface"));

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// <b>全画面のあいだにウィンドウの大きさを設定へ焼き込まないこと。</b>
    ///
    /// <para>
    /// <c>MainWindow_SizeChanged</c> は WinUIEx の <c>WindowState</c> しか見ていなかった。
    /// あれは <c>Normal / Minimized / Maximized</c> の3値で、<c>FullScreenPresenter</c> でも
    /// <c>Normal</c> と報告されうる ── 見落とすと、全画面へ入った瞬間の <c>SizeChanged</c> が
    /// 画面いっぱいの大きさを <c>WindowWidth</c>/<c>WindowHeight</c> へ書き、
    /// <b>次回起動が全画面サイズで開く</b>（戻す手段は設定ファイルの手編集だけ）。
    /// </para>
    /// </summary>
    [Fact]
    public void FullScreen_DoesNotOverwriteTheSavedWindowSize()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SelectRecorder("R1");

        // **起動直後に読んではいけない。** AppWindow.Resize（起動時の復元）由来の
        // SizeChanged がデバウンス保存（約1秒）で遅れて着地するので、
        // 落ち着くまで待たないと「全画面のせいで変わった」と誤認する。
        var before = WaitUntilWindowSizeSettles(instance);

        ui.Click("ToggleFullScreenButton");
        Assert.True(ui.WaitUntilGone("StartAllButton"));

        // デバウンス保存より十分に長く待ってから読む
        // ── 早く読むと「まだ書かれていないだけ」を「書かれていない」と誤認する。
        Thread.Sleep(DebounceMargin);
        var after = TryReadWindowSize(instance);

        Assert.True(before == after,
            $"全画面のあいだにウィンドウサイズが保存されました（{before} -> {after}）。"
            + " 次回起動が全画面サイズで開きます。"
            + Environment.NewLine + instance.DiagnosticDump());

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Assert.NotNull(ui.WaitForElement("StartAllButton"));

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// 保存済みのウィンドウサイズが落ち着く（連続する2回の読みが一致する）まで待つ。
    /// 起動時の <c>AppWindow.Resize</c> 由来の保存が遅れて着地するため。
    /// </summary>
    private static (int Width, int Height)? WaitUntilWindowSizeSettles(AppInstance instance)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        var previous = TryReadWindowSize(instance);
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            Thread.Sleep(1500);
            var current = TryReadWindowSize(instance);
            if (previous == current)
                return current;
            previous = current;
        }
        return previous;
    }

    /// <summary>
    /// 保存済みのウィンドウサイズ。<b>まだ書かれていなければ null</b>
    /// （ハーネスは書かないし、アプリも大きさが変わらなければ書かない）。
    /// </summary>
    private static (int Width, int Height)? TryReadWindowSize(AppInstance instance)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(instance.SettingsPath));
            var root = document.RootElement;
            return root.TryGetProperty("WindowWidth", out var w)
                   && root.TryGetProperty("WindowHeight", out var h)
                ? (w.GetInt32(), h.GetInt32())
                : null;
        }
        catch (IOException)
        {
            return null; // 保存中に読んだ
        }
    }
}
