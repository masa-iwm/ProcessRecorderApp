using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using WinUIEx;

namespace ProcessRecorderApp.Views;

/// <summary>
/// 常駐ワーカーのメインウィンドウ。
/// タスクトレイ常駐・閉じる/最小化ボタンの挙動は <see cref="SingleInstance.SingleInstanceManager"/>
/// （<c>App.xaml.cs</c>から<c>AttachWindow</c>で紐付け）が担当する。
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // **ウィンドウのタイトルを明示的に設定する。**
        // XAML の <TitleBar Title="..."> は WinAppSDK の TitleBar **コントロール**で、
        // クライアント領域に文字を描くだけ ── ウィンドウのキャプションは設定しない
        // （microsoft-ui-xaml#10557）。<Window> 要素に Title= を書いていない場合、
        // Window.Title は WinUI 3 の既定値 "WinUI Desktop" のままになる
        // （WinUIEx の既知の問題）。影響はタスクバー / Alt+Tab / UIA のウィンドウ名に加えて、
        // **タスクトレイアイコンのツールチップ** ── WinUIEx は
        // WindowManager.AddToTray() で AppWindow.Title をツールチップとして渡すので、
        // 既定値がそのまま見える。
        //
        // 出所を1つに保つためコントロール側の値を使う。
        // **SingleInstanceManager.AttachWindow() より前**である必要がある
        // ── WinUIEx はトレイアイコン登録時の値を1回だけ読み、以後追従しない。
        //
        // **この行を消しても自動テストでは落ちない。** キャプションは XAML の TitleBar が
        // 読み込まれた時点で製品名に直る（この行が無くてもしばらくすると正しく見える）ため、
        // 外から観測できるのは「トレイアイコンのツールチップ」だけで、そこは
        // Category=Fragile（通知領域を UIA で辿る）でしか届かない。
        // 検証は実測で行った ── UIA で通知領域の項目名を読み、この行あり/なしで
        // 'Process Recorder App' / 'WinUI Desktop' になることを確認済み。
        // **消すときは同じ手順で確かめること。**
        Title = AppTitleBar.Title;

        Activated += MainWindow_Activated;
        SizeChanged += MainWindow_SizeChanged;
        // 全画面（プレビューの全画面表示）ではタイトルバーも畳む。
        // **MainPage からは触れない**（AppTitleBar はこのウィンドウの要素で、
        // ページに Window を参照させない方針）ので、ここで presenter の変化を拾う。
        AppWindow.Changed += AppWindow_Changed;

        // 相対パスは実行時の作業ディレクトリに依存するため、絶対パスで指定する。
        // （単一ファイル発行ではAppContext.BaseDirectoryが展開先フォルダを指す）
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new(Settings.AppSettings.Default.WindowWidth, Settings.AppSettings.Default.WindowHeight));

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // 初回起動時のみ実行
        Activated -= MainWindow_Activated;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
    }

    private void AppWindow_Changed(Microsoft.UI.Windowing.AppWindow sender,
                                   Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange)
            return;

        // 旗を実態に合わせるのはここ（PreviewFullScreen の doc を参照）。
        PreviewFullScreen.NotifyPresenterChanged(sender);

        AppTitleBar.Visibility =
            sender.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        var wm = WindowManager.Get(this);

        // **全画面の最中は保存しない。** 入った瞬間の SizeChanged は画面いっぱいの大きさを
        // 報告するので、そのまま書くと次回起動が全画面サイズで開く
        // （戻す手段は設定ファイルの手編集だけ）。
        // ここで AppWindow.Presenter.Kind を見ても駄目 ── サイズ変更の通知は
        // **プレゼンターの差し替えより先**に来るため、その時点ではまだ Overlapped と報告される
        // （実測。PreviewFullScreen の doc に記録）。WinUIEx の WindowState も Normal のまま。
        if (wm.WindowState == WindowState.Normal && !PreviewFullScreen.IsActive)
        {
            Settings.AppSettings.Default.WindowWidth = AppWindow.Size.Width;
            Settings.AppSettings.Default.WindowHeight = AppWindow.Size.Height;
        }
    }
}
