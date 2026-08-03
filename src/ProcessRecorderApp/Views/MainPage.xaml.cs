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

        // Log 画面のターミナル。初期化も排出も IsActive（＝Log 画面の表示）が駆動するので、
        // ここでは「使えなかったとき」の受け口だけ張る
        logTerminal.FallbackActivated += LogTerminal_FallbackActivated;
        _swapChainBindAttempts = 0;
        if (!TryBindSwapChain())
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRenderingBindSwapChain;
    }

    private void SwapChainPanel_SwapChainSizeRequested(object? sender, Controls.SwapChainSizeRequestedEventArgs e)
        => ViewModel?.GstController.ResizeSwapChain(e.Width, e.Height);

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
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingBindSwapChain;
        // パイプライン破棄前にパネルのスワップチェーン参照を外す
        swapChainPanel.SwapChainHandle = 0;

        // プロセス寿命の録画エンジンに張った「ページ寿命のデリゲート」を必ず外す。
        // 外さないと死んだページと XamlRoot が永久に参照され、後から CLI／トレイ経由で
        // 削除コマンドが走ったときに破棄済みビジュアルツリー上で ContentDialog を出そうとする。
        if (ViewModel is not null)
        {
            ViewModel.GstController.ConfirmRecorderRemovalAsync = null;
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

    internal static Orientation PaneHeaderOrientation(bool collapsed) => collapsed ? Orientation.Vertical : Orientation.Horizontal;

    internal static string PaneToggleGlyph(bool collapsed) => collapsed ? "\uE76C" : "\uE76B"; // ChevronRight / ChevronLeft

    internal static Visibility PaneContentVisibility(bool collapsed) => collapsed ? Visibility.Collapsed : Visibility.Visible;

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
    internal static GridLength PaneColumnWidth(bool collapsed)
    {
        if (collapsed)
            return GridLength.Auto;
        double width = AppSettings.Default.SettingsWidth;
        return new GridLength(width >= 150 ? width : 300);
    }

    internal static double PaneColumnMinWidth(bool collapsed) => collapsed ? 0 : 150;

    private void RecorderPropertyGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // ペイン展開中のみ現在幅を設定へ保存する
        // （折りたたみ中の列幅を SettingsWidth に保存しないため、TwoWay バインドではなく手動で永続化する）
        if (ViewModel?.IsPropertyPaneCollapsed == false && settingsColumn.ActualWidth > 0)
            AppSettings.Default.SettingsWidth = settingsColumn.ActualWidth;
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
