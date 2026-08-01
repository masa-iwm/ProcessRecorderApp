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
        Bindings.Update();

        // プレビュー用パイプライン（d3d12swapchainsink）を初期化し、生成されたコンポジション
        // スワップチェーンを NativeSwapChainPanel にバインドする。パネルへのサイズ追従・高DPI補正は
        // NativeSwapChainPanel（Controls プロジェクト）側が担当するため、ここではハンドルの
        // 取得・バインドと、パネルからのリサイズ要求を GStreamer 側へ伝えるだけでよい。
        // スワップチェーンは初期化直後にはまだ生成されていないことがあるため、
        // 生成され次第バインドできるよう、成功するまで毎フレーム再試行する。
        ViewModel.GstController.InitializePreview();
        swapChainPanel.SwapChainSizeRequested += SwapChainPanel_SwapChainSizeRequested;
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
        swapChainPanel.SwapChainSizeRequested -= SwapChainPanel_SwapChainSizeRequested;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingBindSwapChain;
        // パイプライン破棄前にパネルのスワップチェーン参照を外す
        swapChainPanel.SwapChainHandle = 0;

        // プロセス寿命の録画エンジンに張った「ページ寿命のデリゲート」を必ず外す。
        // 外さないと死んだページと XamlRoot が永久に参照され、後から CLI／トレイ経由で
        // 削除コマンドが走ったときに破棄済みビジュアルツリー上で ContentDialog を出そうとする。
        if (ViewModel is not null)
            ViewModel.GstController.ConfirmRecorderRemovalAsync = null;
        recorderPropertyGrid.ValueBuilder = null;

        ViewModel?.Dispose();
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
    private IReadOnlyList<Controls.PropertyGridChoice> ProvideChoices(string key, string currentValue)
    {
        // 具体型を明示する。CsWinRT の解析（CsWinRT1032）が、非可変インターフェイスを
        // 対象にしたコレクション式を「トリミング／AOT で安全でない」として弾く。
        if (!string.Equals(key, AppSettings.EncoderChoiceListKey, StringComparison.Ordinal))
            return System.Array.Empty<Controls.PropertyGridChoice>();

        return GStreamer.EncoderCatalog
            .ChoicesFor(currentValue, GStreamer.EncoderCatalog.LastProbe)
            .Select(c => new Controls.PropertyGridChoice { Value = c.Value, Display = DescribeChoice(c) })
            .ToArray();
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
}
