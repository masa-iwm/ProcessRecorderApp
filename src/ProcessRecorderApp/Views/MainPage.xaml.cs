using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.Settings;
using ProcessRecorderApp.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

// Native AOT 発行時に正常に LayoutTransform が効かない workaround
[assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(Microsoft.UI.Xaml.Media.RotateTransform))]

namespace ProcessRecorderApp.Views;

/// <summary>
/// The main content page displayed inside the application window.
/// NavigationView のメニュー生成/同期/選択連携は RecorderNavViewBehavior、
/// パネル切替やペイン折りたたみ状態は ViewModel へのバインドに委譲しており、
/// ここには View 固有の処理（状態→レイアウト変換の x:Bind 関数、幅の永続化、削除確認ダイアログ）のみを置く。
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPageViewModel? ViewModel { get; private set; }

    public MainPage()
    {
        InitializeComponent();

        this.Loaded += MainPage_Loaded;
        this.Unloaded += MainPage_Unloaded;
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 録画エンジンはプロセス寿命（App 所有）。ページはそれを受け取ってバインドするだけで、
        // 破棄はしない。Start() は生成済みなら既存インスタンスを返すため、画面の再生成でも
        // 録画とバッファリングは途切れない。
        ViewModel = new(DispatcherQueue, GstControllerViewModel.Start(DispatcherQueue));
        // 削除確認ダイアログ（View の責務）を VM のコマンドから呼び出せるように登録する
        ViewModel.GstController.ConfirmRecorderRemovalAsync = ConfirmRecorderRemovalAsync;
        // 追加時に「空から新規／既存をコピー」を尋ねるダイアログ（View の責務）
        ViewModel.GstController.ChooseRecorderTemplateAsync = ChooseRecorderTemplateAsync;
        // SrcPipeline の編集支援ダイアログ（View の責務）を PropertyGridView の「…」ボタンへ接続する
        recorderPropertyGrid.ValueBuilder = BuildValueAsync;
        // 設定画面の選択肢（実行時の状況で決まるもの）を供給する。
        // 項目の組み立てはここより先（SelectedObject の x:Bind は OneTime で
        // InitializeComponent の時点で評価される）に終わっているが、
        // PropertyGridView 側が代入時に作り直すので順序に依存しない。
        settingsPanel.ChoiceProvider = ProvideChoices;
        // トリガ一覧エディタ（View の責務）を設定画面の「…」ボタンへ接続する
        settingsPanel.ValueBuilder = BuildSettingsValueAsync;
        // 設定の再読み込み: 確認ダイアログ（View の責務）と、読み直した後の作り直し。
        // 選択肢はレコーダー名を項目生成時に焼き込むので、作り直さないと古い名前が残る
        // （トリガ一覧の編集後に同じことをしている。BuildSettingsValueAsync 参照）。
        ViewModel.ConfirmSettingsReloadAsync = ConfirmSettingsReloadAsync;
        ViewModel.SettingsReloaded = () => settingsPanel.ChoiceProvider = ProvideChoices;
        Bindings.Update();

        // プレビュー用パイプライン（d3d12swapchainsink）を初期化し、生成されたコンポジション
        // スワップチェーンを NativeSwapChainPanel にバインドする。パネルへのサイズ追従・高DPI補正は
        // NativeSwapChainPanel（Controls プロジェクト）側が担当するため、ここではハンドルの
        // 取得・バインドと、パネルからのリサイズ要求を GStreamer 側へ伝えるだけでよい。
        // スワップチェーンは初期化直後にはまだ生成されていないことがあるため、
        // 生成され次第バインドできるよう、成功するまで毎フレーム再試行する。
        ViewModel.GstController.InitializePreview();
        swapChainPanel.SwapChainSizeRequested += SwapChainPanel_SwapChainSizeRequested;

        // 構図補助線。映像の実サイズはプレビュー用スレッドから飛んでくるので UI へ移す。
        // 設定の変更にも追随させる（PropertyGrid で切り替えたその場で反映される）。
        ViewModel.GstController.PreviewVideoSizeChanged += Preview_VideoSizeChanged;
        AppSettings.Default.PropertyChanged += FramingGrid_SettingsPropertyChanged;
        UpdateFramingGrid();

        // 全画面の正本は AppWindow.Presenter。ここは VM へ写すだけ
        // （トレイ格納など View を通らない解除にも追随させるため）。
        if (TryGetAppWindow() is { } appWindow)
            appWindow.Changed += AppWindow_Changed;
        ViewModel.PropertyChanged += FullScreen_SectionChanged;

        // Log 画面のターミナル。初期化も排出も IsActive（＝Log 画面の表示）が駆動するので、
        // ここでは「使えなかったとき」の受け口だけ張る
        logTerminal.FallbackActivated += LogTerminal_FallbackActivated;
        _swapChainBindAttempts = 0;
        if (!TryBindSwapChain())
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRenderingBindSwapChain;
    }

    private void SwapChainPanel_SwapChainSizeRequested(object? sender, Controls.SwapChainSizeRequestedEventArgs e)
        => ViewModel?.GstController.ResizeSwapChain(e.Width, e.Height);

    // ---- 構図補助線（フレーミンググリッド） ----

    /// <summary>
    /// 補助線の色。<b>プレビューの内容は選べない</b>（暗い画面にも明るい画面にも重なる）ので、
    /// テーマ色ではなく半透明の白で固定する。太さは 1（DIP）。
    /// </summary>
    private static readonly Microsoft.UI.Xaml.Media.Brush FramingGridBrush =
        new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    /// <summary>
    /// プレビューへ流れている映像の表示サイズ。0x0 は「未知」で、そのときは線を出さない
    /// （<c>FramingGridGeometry.Fit</c> が空の矩形を返す）。
    /// </summary>
    private int _previewVideoWidth;
    private int _previewVideoHeight;

    /// <summary>
    /// 映像サイズの変化。<b>プレビュー用スレッドから飛んでくる</b>ので UI スレッドへ移す
    /// （<c>GstEventRecorderViewModel</c> が <c>LastError</c> でしていることと同じ）。
    /// </summary>
    private void Preview_VideoSizeChanged(object? sender, GStreamer.PreviewVideoSizeEventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            _previewVideoWidth = e.Width;
            _previewVideoHeight = e.Height;
            UpdateFramingGrid();
        });

    private void FramingGrid_SettingsPropertyChanged(
        object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.FramingGrid))
            DispatcherQueue.TryEnqueue(UpdateFramingGrid);
    }

    private void SwapChainPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateFramingGrid();

    /// <summary>
    /// 補助線を引き直す。契機は 3 つ（パネルのサイズ変更・映像サイズの変化・設定の変更）。
    ///
    /// <para>
    /// <b>渡すのは <c>ActualWidth</c>/<c>ActualHeight</c>（DIP）であって物理ピクセルではない。</b>
    /// <c>NativeSwapChainPanel</c> が高DPI対策の逆スケール行列を掛けているのは
    /// スワップチェーンだけで、その上に載るこの <c>Canvas</c> は論理座標のままである
    /// ── <c>SwapChainSizeRequested</c> が渡す値（＝物理ピクセル）を使うと高DPI環境でずれる。
    /// </para>
    /// <para>
    /// 線は毎回作り直す。数は最大 4 本なので、差分更新に見合う量ではない。
    /// </para>
    /// </summary>
    private void UpdateFramingGrid()
    {
        framingGrid.Children.Clear();

        var rect = FramingGridGeometry.Fit(
            swapChainPanel.ActualWidth, swapChainPanel.ActualHeight,
            _previewVideoWidth, _previewVideoHeight);

        foreach (var line in FramingGridGeometry.Lines(AppSettings.Default.FramingGrid, rect))
        {
            framingGrid.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = line.X1,
                Y1 = line.Y1,
                X2 = line.X2,
                Y2 = line.Y2,
                Stroke = FramingGridBrush,
                StrokeThickness = 1,
            });
        }
    }

    // ---- プレビューの全画面表示 ----

    /// <summary>
    /// このページが載っている <c>AppWindow</c>。
    /// <b>ページに <c>Window</c> を参照させない</b> ── プロセス寿命のウィンドウと
    /// ページの寿命が絡む（<see cref="PickDirectoryAsync"/> と同じ判断）。
    /// まだ表示されていなければ null。
    /// </summary>
    private Microsoft.UI.Windowing.AppWindow? TryGetAppWindow()
        => XamlRoot?.ContentIslandEnvironment is { } island
            ? Microsoft.UI.Windowing.AppWindow.GetFromWindowId(island.AppWindowId)
            : null;

    /// <summary>
    /// プレゼンターの変化を <see cref="MainPageViewModel.IsPreviewFullScreen"/> へ写す。
    /// <b>正本はプレゼンターの側</b>なので、View を経由しない解除
    /// （トレイ格納で <c>App</c> が戻す）でも表示が追随する。
    /// </summary>
    private void AppWindow_Changed(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange || ViewModel is null)
            return;

        ViewModel.IsPreviewFullScreen =
            sender.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
    }

    /// <summary>
    /// Preview 以外のセクションへ移ったら全画面を解除する。
    /// 全画面のまま Log 画面などへ切り替えられると、タイトルバーもナビも無い状態で
    /// プレビュー以外が全画面に出ることになり、戻す手段が分かりにくい。
    /// </summary>
    private void FullScreen_SectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainPageViewModel.SelectedSection) || ViewModel is null)
            return;
        if (ViewModel.CanEnterPreviewFullScreen)
            return;
        if (TryGetAppWindow() is { } appWindow
            && appWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
        {
            ExitPreviewFullScreen(appWindow);
        }
    }

    /// <summary>全画面の出入りを切り替える（F11・ダブルタップ・ツールバーのボタン）。</summary>
    private void TogglePreviewFullScreen()
    {
        if (ViewModel is null || TryGetAppWindow() is not { } appWindow)
            return;

        if (appWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
        {
            ExitPreviewFullScreen(appWindow);
            return;
        }

        // Preview 画面のときだけ。他のセクションで入ると、映像が出ていないのに
        // タイトルバーごと消えて戻し方が分からなくなる。
        if (!ViewModel.CanEnterPreviewFullScreen)
            return;

        PreviewFullScreen.Enter(appWindow);
    }

    private static void ExitPreviewFullScreen(Microsoft.UI.Windowing.AppWindow appWindow)
        => PreviewFullScreen.Exit(appWindow);

    private void FullScreenAccelerator_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        TogglePreviewFullScreen();
    }

    private void ExitFullScreenAccelerator_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        // アクセラレータ自体が IsEnabled で全画面中に限定されているので、ここへ来た時点で全画面。
        // **Handled を立てないと ContentDialog の既定の閉じる操作まで奪う**（そちらは
        // 全画面中には出ないが、状態の食い違いで両方生きる瞬間を作らないため常に立てる）。
        args.Handled = true;
        if (TryGetAppWindow() is { } appWindow)
            ExitPreviewFullScreen(appWindow);
    }

    private void PreviousRecorderAccelerator_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel?.SelectAdjacentRecorder(-1);
    }

    private void NextRecorderAccelerator_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel?.SelectAdjacentRecorder(1);
    }

    private void PreviousFramingGridAccelerator_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel?.CycleFramingGrid(-1);
    }

    private void NextFramingGridAccelerator_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel?.CycleFramingGrid(1);
    }

    private void SwapChainPanel_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        TogglePreviewFullScreen();
    }

    private void ToggleFullScreenButton_Click(object sender, RoutedEventArgs e)
        => TogglePreviewFullScreen();

    /// <summary>
    /// プレビュー面の右クリックメニューを開くたびに組み直す。
    ///
    /// <para>
    /// <b>項目を保持して購読を張らない。</b> レコーダーは追加・削除・改名されるので、
    /// 作り置きすると解除対象を取り違える（<c>RecorderNavViewBehavior</c> が実際に踏んだ形）。
    /// 開くのは人の操作なので、毎回作り直す費用は問題にならない。
    /// </para>
    /// </summary>
    private void PreviewFlyout_Opening(object? sender, object e)
    {
        if (ViewModel is null)
            return;

        // sender をキャストしない ── WinRT のランタイムクラスへのキャストは
        // トリミング安全でない（CsWinRT1034）。x:Name で持っている実体を直接使う。
        // 開いているあいだは全画面のキー操作を止める（アクセラレータがメニューのキー操作を先に取る）。
        ViewModel.IsPreviewMenuOpen = true;

        var flyout = previewFlyout;
        flyout.Items.Clear();

        // 「プレビュー」＝ 表示するレコーダーの選択。
        var recorders = new MenuFlyoutSubItem { Text = Localization.GetString("Resources/Menu_Preview") };
        foreach (var recorder in ViewModel.GstController.Recorders)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = recorder.Name,
                IsChecked = ReferenceEquals(recorder, ViewModel.GstController.SelectedRecorder),
            };
            var captured = recorder;
            item.Click += (_, _) => ViewModel.GstController.SelectedRecorder = captured;
            recorders.Items.Add(item);
        }
        // 空のサブメニューは開いても何も無い袋小路になるので、そのときは押させない。
        recorders.IsEnabled = 0 < recorders.Items.Count;
        flyout.Items.Add(recorders);

        // 「フレーミンググリッド」＝ 構図の補助線。設定画面の項目と同じ一覧・同じ並びを使う
        // （正本は Components.FramingGridChoices）。
        var grid = new MenuFlyoutSubItem { Text = Localization.GetString("Resources/Menu_FramingGrid") };
        var currentGrid = AppSettings.Default.FramingGrid;
        foreach (var choice in Components.FramingGridChoices.All)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = Localization.GetString(choice.ResourceKey),
                IsChecked = choice.Kind == currentGrid,
            };
            var captured = choice.Kind;
            // 設定へ書くだけでよい ── 描き直しは FramingGrid_SettingsPropertyChanged が拾う。
            item.Click += (_, _) => AppSettings.Default.FramingGrid = captured;
            grid.Items.Add(item);
        }
        flyout.Items.Add(grid);

        // **全画面の出入りを切り替える 1 項目にする（サブメニューは持たない）。**
        // このフライアウトは通常表示でも開けるので、「全画面を終了」を無条件に置くと
        // 押しても何も起きない死に項目になる（Exit は Kind が FullScreen でなければ
        // 早期 return する）。しかもフライアウトは別のトップレベル UIA ウィンドウなので、
        // E2E では死んでいることを検出できない。
        // 全画面でないときは「全画面表示」にして、どちらの状態でも意味のある項目にする。
        bool fullScreen = ViewModel.IsPreviewFullScreen;
        var toggle = new MenuFlyoutItem
        {
            Text = Localization.GetString(
                fullScreen ? "Resources/Menu_ExitFullScreen" : "Resources/Menu_EnterFullScreen"),
            // 入る方は Preview 画面かつ初期化済みのときだけ（F11 と同じ条件）。
            IsEnabled = fullScreen || ViewModel.CanEnterPreviewFullScreen,
        };
        toggle.Click += (_, _) => TogglePreviewFullScreen();
        flyout.Items.Add(toggle);
    }

    /// <summary>
    /// 右クリックメニューが閉じたら全画面のキー操作を戻す。
    ///
    /// <para>
    /// <c>Closed</c> は<b>どの閉じ方でも</b>上がる（項目の選択・領域外の押下・<c>Esc</c>）ので、
    /// ここだけで戻せる。上げ忘れると全画面中のキーが二度と効かなくなる。
    /// </para>
    /// </summary>
    private void PreviewFlyout_Closed(object? sender, object e)
    {
        if (ViewModel is not null)
            ViewModel.IsPreviewMenuOpen = false;
    }

    /// <summary>
    /// スワップチェーン生成待ちの再試行回数。
    /// <c>InitializePreview()</c> は失敗を握りつぶす（<c>Controller.InitializePreview</c> の設計）ため、プレビューが
    /// 成立しない環境（GPU 無し・WARP など）では <see cref="GetSwapChainHandle"/> が
    /// 恒久的に 0 を返す。上限を設けないと、このハンドラがページの寿命いっぱい
    /// **毎フレーム**走り続ける（毎回 <c>_previewGate</c> を取る）。
    /// </summary>
    private int _swapChainBindAttempts;

    /// <summary>約5秒（60fps 換算）で再試行を打ち切る。</summary>
    private const int MaxSwapChainBindAttempts = 300;

    private void OnRenderingBindSwapChain(object? sender, object e)
    {
        if (TryBindSwapChain())
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingBindSwapChain;
            return;
        }

        if (++_swapChainBindAttempts <= MaxSwapChainBindAttempts)
            return;

        // 打ち切り。プレビューは出ないが録画（アプリの中核機能）には影響しないため、
        // ログだけ残して続行する。
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingBindSwapChain;
        GStreamer.DebugLogEx.Log(
            Gst.DebugLevel.Warning,
            $"Swap chain was not created after {MaxSwapChainBindAttempts} frames; preview stays blank (recording is unaffected).");
    }

    /// <summary>
    /// d3d12swapchainsink が生成したスワップチェーンを NativeSwapChainPanel にバインドする。
    /// 未生成なら false を返す（呼び出し側でリトライする）。
    /// </summary>
    private bool TryBindSwapChain()
    {
        if (swapChainPanel.SwapChainHandle != 0)
            return true;
        var handle = ViewModel?.GstController.GetSwapChainHandle() ?? 0;
        if (handle == 0)
            return false;
        swapChainPanel.SwapChainHandle = handle;
        return true;
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // ブラウザープロセスをページと一緒に畳む（開き直しても積み上がらないこと）。
        // プロセス寿命の LogBuffer にページ寿命のデリゲートを残さないのは、
        // プレビュー面の SwapChainHandle = 0 と同じ理由
        logTerminal.FallbackActivated -= LogTerminal_FallbackActivated;
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= LogFollow_ViewModelPropertyChanged;
        if (_autoScrollCallbackToken != -1)
        {
            logListView.UnregisterPropertyChangedCallback(
                Behaviors.AutoScrollToBottomBehavior.IsAutoScrollEnabledProperty, _autoScrollCallbackToken);
            _autoScrollCallbackToken = -1;
        }
        logTerminal.Stop();

        swapChainPanel.SwapChainSizeRequested -= SwapChainPanel_SwapChainSizeRequested;
        // プロセス寿命の Previewer / AppSettings にページ寿命のデリゲートを残さない
        // （SwapChainHandle = 0 と同じ理由）。
        if (ViewModel is not null)
            ViewModel.GstController.PreviewVideoSizeChanged -= Preview_VideoSizeChanged;
        AppSettings.Default.PropertyChanged -= FramingGrid_SettingsPropertyChanged;
        if (TryGetAppWindow() is { } appWindow)
            appWindow.Changed -= AppWindow_Changed;
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= FullScreen_SectionChanged;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingBindSwapChain;
        // パイプライン破棄前にパネルのスワップチェーン参照を外す
        swapChainPanel.SwapChainHandle = 0;

        // プロセス寿命の録画エンジンに張った「ページ寿命のデリゲート」を必ず外す。
        // 外さないと死んだページと XamlRoot が永久に参照され、後から CLI／トレイ経由で
        // 削除コマンドが走ったときに破棄済みビジュアルツリー上で ContentDialog を出そうとする。
        if (ViewModel is not null)
        {
            ViewModel.GstController.ConfirmRecorderRemovalAsync = null;
            ViewModel.GstController.ChooseRecorderTemplateAsync = null;
            ViewModel.ConfirmSettingsReloadAsync = null;
            ViewModel.SettingsReloaded = null;
        }
        recorderPropertyGrid.ValueBuilder = null;
        settingsPanel.ValueBuilder = null;

        ViewModel?.Dispose();
    }

    // ---- Log 画面（ターミナル / フォールバックのリスト表示） ----

    /// <summary>ターミナルを出すのはフォールバックしていないときだけ。フォールバック側の裏</summary>
    internal static Visibility LogTerminalVisibility(bool fallbackActive)
        => fallbackActive ? Visibility.Collapsed : Visibility.Visible;

    private void CopyAllLog_Click(object sender, RoutedEventArgs e) => logTerminal.CopyAll();

    /// <summary><see cref="Behaviors.AutoScrollToBottomBehavior.IsAutoScrollEnabledProperty"/> の購読トークン</summary>
    private long _autoScrollCallbackToken = -1;

    /// <summary>
    /// WebView2 を諦めたので、リスト表示へ切り替える。
    ///
    /// <para>
    /// 自動スクロールの状態を <c>ListView</c> の添付プロパティへ<b>ここで</b>結び直す。
    /// XAML で束ねないのは、<c>AutoScrollToBottomBehavior</c> 自身がスクロール判定で
    /// 同じ添付プロパティを書くため ── バインドしていると黙って上書きされ、
    /// 添付プロパティを対象にした <c>x:Bind Mode=TwoWay</c> は効かないことがある。
    /// フォールバックは稀な経路なので、こちら側で1回だけ配線する。
    /// </para>
    /// </summary>
    private void LogTerminal_FallbackActivated(object? sender, EventArgs e)
    {
        if (ViewModel is null)
            return;

        ViewModel.IsLogFallbackActive = true;
        Behaviors.AutoScrollToBottomBehavior.SetIsAutoScrollEnabled(logListView, ViewModel.IsLogFollowEnabled);
        _autoScrollCallbackToken = logListView.RegisterPropertyChangedCallback(
            Behaviors.AutoScrollToBottomBehavior.IsAutoScrollEnabledProperty, LogFollow_ListChanged);
        ViewModel.PropertyChanged += LogFollow_ViewModelPropertyChanged;
    }

    private void LogFollow_ListChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (ViewModel is not null)
            ViewModel.IsLogFollowEnabled = Behaviors.AutoScrollToBottomBehavior.GetIsAutoScrollEnabled(logListView);
    }

    private void LogFollow_ViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainPageViewModel.IsLogFollowEnabled) && ViewModel is not null)
            Behaviors.AutoScrollToBottomBehavior.SetIsAutoScrollEnabled(logListView, ViewModel.IsLogFollowEnabled);
    }

    // ---- ペイン折りたたみ状態 (ViewModel.IsPropertyPaneCollapsed) をレイアウトへ変換する x:Bind 関数群 ----

    // 全画面（IsPreviewFullScreen）は「折りたたみ」の上に重ねる**表示の上書き**であって、
    // 折りたたみ状態そのものではない ── IsPropertyPaneCollapsed を書き換えて隠すと
    // AppSettings へ永続化され（MainPageViewModel の OnIsPropertyPaneCollapsedChanged）、
    // 全画面を抜けても畳んだままになり settings.json にも残る。
    // したがって各関数は 2 つ目の引数として全画面状態を受け取る。

    internal static Orientation PaneHeaderOrientation(bool collapsed, bool fullScreen)
        => collapsed || fullScreen ? Orientation.Vertical : Orientation.Horizontal;

    /// <summary>全画面中はプロパティペインそのもの（ヘッダーのボタン列を含む）を畳む。</summary>
    internal static Visibility PaneVisibility(bool fullScreen)
        => fullScreen ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>全画面中はナビゲーションのペイン（既定では上部のバー）を隠す。</summary>
    internal static bool NavPaneVisible(bool fullScreen) => !fullScreen;

    internal static string PaneToggleGlyph(bool collapsed) => collapsed ? "\uE76C" : "\uE76B"; // ChevronRight / ChevronLeft

    /// <summary>全画面のトグルボタンのアイコン（全画面中は「ウィンドウへ戻る」）。</summary>
    // **生の私用領域文字を埋めない。** diff でもレビュー画面でも両分岐が同じ空白に見えて
    // 判別できなくなる（PaneToggleGlyph と同じくエスケープで書く）。
    internal static string FullScreenGlyph(bool fullScreen) => fullScreen ? "" : ""; // BackToWindow / FullScreen

    internal static Visibility PaneContentVisibility(bool collapsed, bool fullScreen)
        => collapsed || fullScreen ? Visibility.Collapsed : Visibility.Visible;

    // ---- PropertyGridView へ供給する選択肢（実行時の状況で決まるもの） ----

    /// <summary>
    /// <c>ChoiceListAttribute</c> のキーに応じて選択肢を返す。
    ///
    /// <para>
    /// <b>「どれを出すか」の判断は <c>EncoderCatalog.ChoicesFor</c>（純関数・L1 で検証）にあり、
    /// ここは表示文言を付けるだけ。</b> ローカライズはアプリ層の責務なので、
    /// 文言の組み立てだけがここに残っている。
    /// </para>
    /// <para>
    /// 未知のキーには空を返す ── <c>PropertyGridView</c> は空なら
    /// <b>通常のテキスト編集に倒す</b>ので、綴りを間違えても値が編集不能になることはない。
    /// </para>
    /// </summary>
    private IReadOnlyList<Controls.PropertyGridChoice> ProvideChoices(string key, string currentValue, IPropertyAccess? owner)
    {
        // 具体型を明示する。CsWinRT の解析（CsWinRT1032）が、非可変インターフェイスを
        // 対象にしたコレクション式を「トリミング／AOT で安全でない」として弾く。
        if (string.Equals(key, AppSettings.EncoderChoiceListKey, StringComparison.Ordinal))
        {
            return GStreamer.EncoderCatalog
                .ChoicesFor(currentValue, GStreamer.EncoderCatalog.LastProbe)
                .Select(c => new Controls.PropertyGridChoice { Value = c.Value, Display = DescribeChoice(c) })
                .ToArray();
        }

        if (string.Equals(key, AppSettings.FramingGridChoiceListKey, StringComparison.Ordinal))
        {
            // 保存値は列挙型の名前のまま（settings.json の形を変えないため）。訳すのは表示だけ。
            return Components.FramingGridChoices.All
                .Select(c => new Controls.PropertyGridChoice
                {
                    Value = c.Kind.ToString(),
                    Display = Localization.GetString(c.ResourceKey),
                })
                .ToArray();
        }

        if (string.Equals(key, UiaTriggerAssignment.ActionChoiceListKey, StringComparison.Ordinal))
            return TriggerActionChoices(currentValue, owner as UiaTriggerAssignment);

        if (string.Equals(key, UiaTriggerAssignment.TargetRecorderChoiceListKey, StringComparison.Ordinal))
            return TriggerTargetChoices(currentValue);

        return System.Array.Empty<Controls.PropertyGridChoice>();
    }

    /// <summary>
    /// トリガ割り当ての Action の選択肢。保存値は <see cref="UiaTriggerAssignment"/> の定数のまま、
    /// 表示だけをローカライズする（Value/Display の分離。enum にしない理由は ChoiceListAttribute 参照）。
    ///
    /// <para>
    /// <b>「条件成立中のみ録画」は、そのトリガが不成立化を通知できて初めて完結する。</b>
    /// できない設定のまま選ばせると「開始したのに止まらない」になるので、
    /// <paramref name="assignment"/> の指すトリガを見て括弧書きで注記する
    /// （<c>PreferredH264Encoder</c> が実在しないエンコーダー名に注記するのと同じ形）。
    /// 選択肢は項目の生成時に 1 回だけ確定するので、トリガを編集したら作り直す
    /// （<see cref="BuildSettingsValueAsync"/> が <c>ChoiceProvider</c> を再代入する）。
    /// </para>
    /// </summary>
    private static IReadOnlyList<Controls.PropertyGridChoice> TriggerActionChoices(
        string currentValue, UiaTriggerAssignment? assignment)
    {
        string whileDisplay = Localization.GetString("Resources/TriggerAction_While");
        if (assignment is not null
            && !Services.UiaTriggerService.CanCompleteWhileRecording(assignment.TriggerId))
        {
            whileDisplay = string.Format(
                CultureInfo.CurrentCulture,
                Localization.GetString("Resources/TriggerAction_WhileCannotStop"),
                whileDisplay);
        }

        var choices = new List<Controls.PropertyGridChoice>(4)
        {
            new() { Value = UiaTriggerAssignment.ActionNone, Display = Localization.GetString("Resources/TriggerAction_None") },
            new() { Value = UiaTriggerAssignment.ActionStart, Display = Localization.GetString("Resources/TriggerAction_Start") },
            new() { Value = UiaTriggerAssignment.ActionStop, Display = Localization.GetString("Resources/TriggerAction_Stop") },
            new() { Value = UiaTriggerAssignment.ActionWhile, Display = whileDisplay },
        };
        AppendRawValueIfMissing(choices, currentValue);
        return choices;
    }

    /// <summary>トリガ割り当ての対象レコーダーの選択肢。空値 =（全レコーダー）＋現在のレコーダー名。</summary>
    private static IReadOnlyList<Controls.PropertyGridChoice> TriggerTargetChoices(string currentValue)
    {
        var choices = new List<Controls.PropertyGridChoice>
        {
            new() { Value = "", Display = Localization.GetString("Resources/TriggerTarget_AllRecorders") },
        };
        foreach (var recorder in AppSettings.Default.Recorders)
            choices.Add(new() { Value = recorder.Name, Display = recorder.Name });
        AppendRawValueIfMissing(choices, currentValue);
        return choices;
    }

    /// <summary>
    /// 一覧に無い現在値（改名済みのレコーダー・手で編集された設定）を素の選択肢として末尾へ差し込む。
    /// 入れないとコンボボックスが空を表示し、他の行に触れた瞬間に値が消える
    /// （ChoiceListAttribute の設計。EncoderChoice の Unknown と同じ配慮）。
    /// </summary>
    private static void AppendRawValueIfMissing(List<Controls.PropertyGridChoice> choices, string currentValue)
    {
        if (!choices.Any(c => string.Equals(c.Value, currentValue, StringComparison.Ordinal)))
            choices.Add(new() { Value = currentValue, Display = currentValue });
    }

    /// <summary>選択肢1件の表示文言。<b>印は「分かっていること」だけを言う。</b></summary>
    private static string DescribeChoice(GStreamer.EncoderCatalog.EncoderChoice choice)
        => choice.Kind switch
        {
            GStreamer.EncoderCatalog.EncoderChoiceKind.Automatic
                => Localization.GetString("Resources/EncoderChoice_Automatic"),

            // カタログに無い現在値。実在するかは調べていないので「無い」とは言わない。
            GStreamer.EncoderCatalog.EncoderChoiceKind.Unknown
                => string.Format(
                    CultureInfo.CurrentCulture,
                    Localization.GetString("Resources/EncoderChoice_NotInCatalog"),
                    choice.Value),

            _ when !choice.Available
                => string.Format(
                    CultureInfo.CurrentCulture,
                    Localization.GetString("Resources/EncoderChoice_NotFound"),
                    choice.Value),

            _ => choice.Value,
        };

    // ---- プレビュー面と「表示するものが無い」表示の出し分け ----

    /// <summary>
    /// プレビュー面（スワップチェーン）を出すのは、選択中レコーダーが初期化済みのときだけ。
    ///
    /// <para>
    /// <b>スワップチェーンはプロセス内で1面を共有している。</b> フレームを push するのは
    /// 選択中のレコーダーだけ（<c>Controller.GstEventRecorder_Preview</c> が
    /// <c>SelectedRecorder</c> で絞る）なので、未初期化のレコーダーへ切り替えると
    /// <b>誰も push しなくなり、前のレコーダーの最後の画がそのまま残り続ける</b>
    /// ── 利用者からは「別のレコーダーの映像が映っている」ようにしか見えない。
    /// </para>
    /// <para>
    /// <b><see cref="NoPreviewVisibility"/> と必ず逆になること。</b>
    /// 同時に両方出る／両方消えるのは、どちらも利用者に嘘をつく。
    /// </para>
    /// <para>
    /// <c>x:Bind</c> の経路が <c>null</c>（レコーダー未選択）のときは <c>bool</c> の既定値
    /// <c>false</c> が渡るので、そのまま「表示するものが無い」側に倒れる。
    /// </para>
    /// </summary>
    internal static Visibility PreviewSurfaceVisibility(bool selectedRecorderIsInitialized)
        => selectedRecorderIsInitialized ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「表示するものが無い」の表示。<see cref="PreviewSurfaceVisibility"/> の裏。</summary>
    internal static Visibility NoPreviewVisibility(bool selectedRecorderIsInitialized)
        => selectedRecorderIsInitialized ? Visibility.Collapsed : Visibility.Visible;

    // ---- フッター（全Recorder録画）の表示を状態から変換する x:Bind 関数群 ----

    /// <summary>PaneDisplayMode に応じたフッターの並び。Top のみ横並び、それ以外(Left/LeftCompact/LeftMinimal/Auto)は縦並び。</summary>
    internal static Orientation PaneFooterOrientation(NavigationViewPaneDisplayMode mode)
        => mode == NavigationViewPaneDisplayMode.Top ? Orientation.Horizontal : Orientation.Vertical;

    /// <summary>全録画開始ボタンのグリフ。録画中=Record(U+E7C8) / 停止中=Record2(U+EA3F)。</summary>
    internal static string RecordAllGlyph(bool recording) => recording ? "\uE7C8" : "\uEA3F";

    /// <summary>全録画開始ボタンのアイコン色。録画中=赤 / 停止中=既定前景色(テーマ自動追従)。</summary>
    [WinRT.DynamicWindowsRuntimeCast(typeof(Microsoft.UI.Xaml.Media.Brush))]
    internal static Microsoft.UI.Xaml.Media.Brush RecordAllBrush(bool recording)
        => recording
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red)
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    /// <summary>展開時は SettingsWidth（SizeChanged で常時永続化済み）、折りたたみ時は縦ヘッダー幅にフィットさせる。</summary>
    internal static GridLength PaneColumnWidth(bool collapsed, bool fullScreen)
    {
        if (collapsed || fullScreen)
            return GridLength.Auto;
        double width = AppSettings.Default.SettingsWidth;
        return new GridLength(width >= 150 ? width : 300);
    }

    internal static double PaneColumnMinWidth(bool collapsed, bool fullScreen) => collapsed || fullScreen ? 0 : 150;

    private void RecorderPropertyGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // ペイン展開中のみ現在幅を設定へ保存する
        // （折りたたみ中の列幅を SettingsWidth に保存しないため、TwoWay バインドではなく手動で永続化する）
        if (ViewModel?.IsPropertyPaneCollapsed == false && settingsColumn.ActualWidth > 0)
            AppSettings.Default.SettingsWidth = settingsColumn.ActualWidth;
    }

    /// <summary>
    /// カメラ設定ダイアログ（<c>[ValueBuilder("GstCameraControls")]</c> の「…」から呼ばれる）。
    ///
    /// <para>
    /// デバイスは <c>SrcPipeline</c> の <c>device-path</c> で開く
    /// ── <c>device-name</c> / <c>device-index</c> からの逆引きには頼らない
    /// （<c>mfdeviceprovider</c> の並びと MF の列挙順が一致する保証は無い）。
    /// 取れなくてもダイアログは開く（理由を出す ── 空のダイアログにしない）。
    /// </para>
    /// <para>
    /// <b><c>SrcPipeline</c> と違って初期化はやり直さない。</b> あちらは文字列が
    /// パイプラインそのものなので組み直しが要るが、カメラ設定は<b>ダイアログが操作のたびに
    /// 直接カメラへ当てている</b>ので、確定した時点で既に効いている。
    /// ここで <c>OnInitialize()</c> を呼ぶと <c>Initialize()</c> → <c>Close()</c> の順に
    /// sink パイプラインが作り直され、<b>録画中なら録画と事前バッファが飛ぶ</b>
    /// ── <c>ApplyCameraControls</c> に書いた「カメラ制御の失敗で録画を止めない」という
    /// 規則を、UI の側から破ることになる。
    /// 保存した文字列は次の初期化（起動・レコーダー追加・自動復帰）で当て直される。
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task<string?> BuildCameraControlsAsync(string current)
    {
        // **エディタは非モーダルの別ウィンドウなので、2 枚目を開かせないガードが要る**
        // （トリガ一覧エディタと同じ理由）。
        if (_cameraEditorOpen)
            return null;
        _cameraEditorOpen = true;
        try
        {
            var recorder = ViewModel?.GstController.SelectedRecorder;

            // **デバイスの解決は渡さずに任せる。** 適用側とまったく同じ規則
            // （`CameraControl.ResolveDevicePath`）を専用スレッドの上で通す ──
            // 別々に書くと、編集画面で触るカメラと初期化時に設定が当たるカメラがずれる。
            // 入力も適用側と揃える（実際に動いている構成を優先）。
            // 開くのも解決も await の向こう側なので、カメラが占有されていても画面は固まらない。
            // 戻り値が null（取り消し・1 行も出せなかった）なら PropertyGrid は何も書かない。
            return await CameraControlWindow.EditAsync(
                recorder?.ActualSrcPipeline ?? recorder?.SrcPipeline, current);
        }
        finally
        {
            _cameraEditorOpen = false;
        }
    }

    /// <summary>
    /// 「レコーダーを追加」で、空から新規にするか既存の設定をコピーするかを尋ねる
    /// （<c>AddRecorderCommand</c> から呼ばれる）。取り消しなら <c>null</c> を返して追加そのものを止める。
    ///
    /// <para>
    /// 表示文言はローカライズされるので、E2E から押せるよう<b>要素には AutomationId を付ける</b>
    /// （<c>AppUi.AddRecorder</c> / <c>AppUi.AddRecorderCopyingFrom</c> がこれを使う）。
    /// </para>
    /// </summary>
    private async Task<AddRecorderChoice?> ChooseRecorderTemplateAsync(IReadOnlyList<string> existingNames)
    {
        var blank = new RadioButton
        {
            Content = Localization.GetString("Resources/Dialog_AddRecorder_Blank"),
            IsChecked = true,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(blank, "AddRecorderBlankOption");

        var copy = new RadioButton
        {
            Content = Localization.GetString("Resources/Dialog_AddRecorder_CopyFrom"),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(copy, "AddRecorderCopyOption");

        var sources = new ComboBox
        {
            ItemsSource = existingNames,
            SelectedIndex = 0,
            Margin = new Thickness(28, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(sources, "AddRecorderSourceCombo");
        // コピー元を選んだら自動的に「コピー」へ倒す（先に一覧へ触る操作の方が自然なため）。
        sources.SelectionChanged += (_, _) => copy.IsChecked = true;

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(blank);
        panel.Children.Add(copy);
        panel.Children.Add(sources);

        ContentDialog dialog = new()
        {
            XamlRoot = this.XamlRoot,
            Title = Localization.GetString("Resources/Dialog_AddRecorder_Title"),
            Content = panel,
            PrimaryButtonText = Localization.GetString("Resources/Common_Add"),
            CloseButtonText = Localization.GetString("Resources/Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "AddRecorderDialog");

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;   // 取り消し ── 1台も増やさない

        return copy.IsChecked == true && sources.SelectedItem is string name
            ? new AddRecorderChoice(name)
            : new AddRecorderChoice(null);
    }

    /// <summary>Recorder 削除前の確認ダイアログを表示する（RemoveSelectedRecorderCommand から呼ばれる）。</summary>
    private async Task<bool> ConfirmRecorderRemovalAsync(GstEventRecorderViewModel recorder)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = this.XamlRoot,
            Title = Localization.GetString("Resources/Dialog_RemoveRecorder_Title"),
            Content = Localization.GetString("Resources/Dialog_RemoveRecorder_Content", recorder.Name),
            PrimaryButtonText = Localization.GetString("Resources/Common_Remove"),
            CloseButtonText = Localization.GetString("Resources/Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// settings.json 再読み込み前の確認ダイアログ（ReloadSettingsCommand から呼ばれる）。
    /// <b>「戻せない」ことを伝えるためのダイアログ</b> ── 読み直しは、まだ書き出されていない
    /// 変更と、<c>--persist</c> していないセッション限りのテンプレート変数を捨てる。
    /// </summary>
    private async Task<bool> ConfirmSettingsReloadAsync()
    {
        ContentDialog dialog = new()
        {
            XamlRoot = this.XamlRoot,
            Title = Localization.GetString("Resources/Dialog_ReloadSettings_Title"),
            Content = Localization.GetString("Resources/Dialog_ReloadSettings_Content"),
            PrimaryButtonText = Localization.GetString("Resources/Common_Reload"),
            CloseButtonText = Localization.GetString("Resources/Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// PropertyGridView の値ビルダー(「…」ボタン)から呼ばれる。
    /// key="GstSrcPipeline" のとき SrcPipeline 編集支援ダイアログを表示し、確定文字列(またはキャンセル時 null)を返す。
    /// </summary>
    private async System.Threading.Tasks.Task<string?> BuildValueAsync(string key, string current)
    {
        if (key == "GstCameraControls")
            return await BuildCameraControlsAsync(current);

        if (key != "GstSrcPipeline")
            return null;

        PipelineBuilderDialog dialog = new(current)
        {
            XamlRoot = this.XamlRoot,
        };
        var result = await dialog.ShowAsync();

        if (result != ContentDialogResult.Primary)
            return null;

        // 生成した SrcPipeline を選択中 Recorder へ反映し、続けて自動で再初期化する。
        // 先に SrcPipeline を反映(コミット)してから初期化することで、新しいパイプラインで初期化される。
        var recorder = ViewModel?.GstController.SelectedRecorder;
        if (recorder is not null)
        {
            recorder.SrcPipeline = dialog.ResultPipeline;
            try
            {
                recorder.OnInitialize();
            }
            catch
            {
                // 初期化失敗時は Recorder 側で Close 済み(IsInitialized=false)。ここでは無視する。
            }
        }
        return dialog.ResultPipeline;
    }

    /// <summary>
    /// トリガ一覧エディタ（<c>TriggerListEditorWindow</c>）が開いているあいだ true。
    /// WinUI のエディタは<b>非モーダル</b>（窓単位のモーダルが無い）なので、
    /// 開いたまま「…」をもう一度押せてしまう ── 2 枚目を開かせないためのガード。
    /// </summary>
    private bool _uiaTriggerEditorOpen;

    /// <summary>
    /// リモート利用者の編集ダイアログが開いているあいだ true。
    /// <c>_uiaTriggerEditorOpen</c> と同じ理由の 2 枚目防止ガード。
    /// </summary>
    private bool _remoteUserEditorOpen;

    /// <summary>
    /// カメラ設定の編集ウィンドウが開いているあいだ true。
    /// <b>非モーダル</b>（プレビューを見ながら合わせるため）なので、
    /// 開いたまま「…」をもう一度押せてしまう ── 2 枚目を開かせないためのガード。
    /// </summary>
    private bool _cameraEditorOpen;

    /// <summary>
    /// 設定画面の値ビルダー(「…」ボタン)から呼ばれる。key=UiaTriggerBuilderKey のとき
    /// トリガ一覧エディタを開き、確定なら正本（<see cref="AppSettings.UiaTriggers"/>）を
    /// 丸ごと差し替えて割り当て行を追随させる。
    ///
    /// 戻り値は常に null ── UiaTriggerList プロパティの文字列値に意味は無く、
    /// 何もコミットしない。差し替えの PropertyChanged が自動保存と
    /// <c>UiaTriggerService</c> の監視再起動を連鎖させる（ここから直接は呼ばない）。
    /// </summary>
    private async System.Threading.Tasks.Task<string?> BuildSettingsValueAsync(string key, string current)
    {
        if (string.Equals(key, AppSettings.OutputDirectoryBuilderKey, StringComparison.Ordinal) ||
            string.Equals(key, AppSettings.GstDebugDumpDotDirBuilderKey, StringComparison.Ordinal))
            return await PickDirectoryAsync(current);

        if (string.Equals(key, AppSettings.DebugLogFileBuilderKey, StringComparison.Ordinal))
            return await PickLogFileAsync(current);

        // 「…」＝トークンの作り直し。**確認ダイアログは出さない** ── 押した瞬間に
        // 新しい値が欄へ入るだけで、確定するのは通常の設定変更と同じ経路だからである。
        if (string.Equals(key, AppSettings.RemoteControlAccessTokenBuilderKey, StringComparison.Ordinal))
            return Components.RemoteApiRules.GenerateAccessToken();

        // 「…」＝リモート利用者の一覧を編集する。確定なら正本（AppSettings.RemoteUsers）を
        // 丸ごと差し替える。戻り値は常に null ── RemoteUserList の文字列値に意味は無い。
        if (string.Equals(key, AppSettings.RemoteUserBuilderKey, StringComparison.Ordinal))
        {
            if (_remoteUserEditorOpen)
                return null;
            _remoteUserEditorOpen = true;
            try
            {
                // ダイアログは渡したリストに触れず写しを返す。
                IReadOnlyList<Components.RemoteUserDefinition>? edited =
                    // `?? []` は手で書かれた `"RemoteUsers": null` への備え
                    // ── 渡せないとダイアログが二度と開けなくなる。
                    await RemoteUserEditorDialog.EditAsync(this.XamlRoot, AppSettings.Default.RemoteUsers ?? []);
                if (edited is null)
                    return null; // 取り消し。正本は変わっていない

                AppSettings.Default.RemoteUsers = [.. edited];
                return null;
            }
            finally
            {
                _remoteUserEditorOpen = false;
            }
        }

        if (!string.Equals(key, AppSettings.UiaTriggerBuilderKey, StringComparison.Ordinal))
            return null;
        if (_uiaTriggerEditorOpen)
            return null;
        _uiaTriggerEditorOpen = true;
        try
        {
            // エディタは渡したリストに触れず写しを返す。ピッカー（要素の採取）も
            // エディタが内包しているので、ここでは 1 窓だけ扱えばよい。
            IReadOnlyList<UiaTrigger.Models.TriggerDefinition>? edited =
                await UiaTrigger.Picker.WinUI.TriggerListEditorWindow.EditAsync(AppSettings.Default.UiaTriggers);
            if (edited is null)
                return null; // 取り消し。正本は変わっていない

            AppSettings.Default.UiaTriggers = [.. edited];
            TriggerAssignmentReconciler.Reconcile(
                AppSettings.Default.UiaTriggerAssignments,
                edited.Select(d => d.Id).ToArray());

            // 選択肢は項目の生成時に 1 回だけ確定するので、作り直さないと
            // 「条件成立中のみ録画」の注記が編集前のままになる（PropertyGridView は
            // ChoiceProvider の代入で項目を組み立て直す）。
            settingsPanel.ChoiceProvider = ProvideChoices;
            return null;
        }
        finally
        {
            _uiaTriggerEditorOpen = false;
        }
    }

    /// <summary>
    /// ディレクトリをフォルダー選択ダイアログで選ぶ。選ばれた絶対パスを返し、取り消しなら null。
    /// <c>OutputDirectory</c> と <c>GstDebugDumpDotDir</c> の両方が使う
    /// （どちらも「空欄なら実行ファイルのあるディレクトリ基準」という同じ規則）。
    ///
    /// <remarks>
    /// <para>
    /// <b>UWP の <c>Windows.Storage.Pickers</c> ではなく Windows App SDK 側の
    /// <c>Microsoft.Windows.Storage.Pickers</c> を使う。</b> あちらはアンパッケージ配布だと
    /// <c>InitializeWithWindow</c> でウィンドウハンドルを注入しないと実行時に落ちるが、
    /// こちらは <see cref="Microsoft.UI.WindowId"/> をコンストラクターで受け取るので、
    /// HWND を持ち回らずに済む。
    /// </para>
    /// <para>
    /// ウィンドウは <c>XamlRoot</c> から辿る ── ページは <c>Window</c> を参照していないし、
    /// 参照させると（プロセス寿命の）ウィンドウとページの寿命が絡む。
    /// </para>
    /// </remarks>
    /// </summary>
    private async System.Threading.Tasks.Task<string?> PickDirectoryAsync(string current)
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } island)
            return null;   // まだ表示されていない（この経路は「…」からしか来ないので通常あり得ない）

        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(island.AppWindowId);
        // 現在の設定が指している場所から開く。空欄・相対パスも実行ファイルのあるディレクトリを
        // 基準に解決する（保存先の解決規則そのものは Components.AppDirectories が持つ）。
        string start = AppDirectories.ResolveOrBase(current);
        if (Directory.Exists(start))
            picker.SuggestedStartFolder = start;

        var result = await picker.PickSingleFolderAsync();
        return result?.Path;   // null = 取り消し。値は返さず、現在の設定を保つ
    }

    /// <summary>
    /// デバッグログの保存先をファイル保存ダイアログで選ぶ。選ばれた絶対パスを返し、取り消しなら null。
    ///
    /// <remarks>
    /// ピッカーの種類が <see cref="PickDirectoryAsync"/> と違うだけで、<c>Microsoft.Windows.Storage.Pickers</c>
    /// を使う理由（アンパッケージ配布で <c>InitializeWithWindow</c> が要らない）は同じ。
    /// <b>選ぶと必ず絶対パスになる</b>が、空欄（＝保存しない）と相対パスはダイアログでは
    /// 表せないので、直接入力の道は残してある（<c>[ReadOnly]</c> を付けていない）。
    /// </remarks>
    /// </summary>
    private async System.Threading.Tasks.Task<string?> PickLogFileAsync(string current)
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } island)
            return null;

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(island.AppWindowId);
        picker.FileTypeChoices.Add("Log", [".txt", ".log"]);

        string trimmed = current?.Trim() ?? string.Empty;
        if (trimmed.Length > 0)
        {
            // 空欄のときは既定名だけ埋めて、開く場所はピッカーに任せる
            // （空欄は「保存しない」であって「どこか」を指していないため）。
            string full = AppDirectories.ResolveOrBase(trimmed);
            string? parent = Path.GetDirectoryName(full);
            if (parent is not null && Directory.Exists(parent))
                picker.SuggestedStartFolder = parent;
            picker.SuggestedFileName = Path.GetFileName(full);
        }
        else
        {
            picker.SuggestedFileName = "debug.txt";
        }

        var result = await picker.PickSaveFileAsync();
        return result?.Path;   // null = 取り消し
    }
}
