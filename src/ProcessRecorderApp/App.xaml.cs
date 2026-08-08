using Microsoft.UI.Xaml;
using ProcessRecorderApp.SingleInstance;
using ProcessRecorderApp.Views;
using WinUIEx;

namespace ProcessRecorderApp;

/// <summary>
/// 常駐ワーカー（実処理を行うインスタンス）のアプリケーションクラス。
///
/// 単一インスタンス制御・タスクトレイ常駐・起動引数のディスパッチは <see cref="SingleInstanceManager"/>
/// に委譲する。このクラスは、メインウィンドウの生成と、アプリ固有のコマンド解析処理
/// （<see cref="ActivationCommands.Parse"/>）を結び付ける役割のみを持つ。
/// </summary>
public partial class App : Application
{
    private readonly SingleInstanceManager _singleInstanceManager;
    private MainWindow? _mainWindow;

    public App()
    {
        UnhandledException += App_UnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        InitializeComponent();

        _singleInstanceManager = new SingleInstanceManager(Program.KeyPrefix, ActivationCommands.Parse);
    }

    static void LogException(string source, Exception ex)
    {
        // activity.log にも残す。DebugLogEx.Log は gst_debug_log 経由で GST_DEBUG が
        // 未設定だと何も出力しないため、これまで未処理例外は**どこにも記録されず**、
        // かつ App_UnhandledException が e.Handled = true にするので終了コードにも現れなかった。
        // 例: AppWindow.Destroying の Save() / engine.Dispose() で例外が出ても、
        // 「正常終了（exitCode=0）」と見分けがつかない。
        Components.ActivityLog.Error("app.error", $"source={source} {ex}");

        System.Diagnostics.StackTrace st = new(ex, true);
        var fr = st.GetFrame(0);
        var mi = fr is null ? null : System.Diagnostics.DiagnosticMethodInfo.Create(fr);
        var flag = fr?.GetFileLineNumber() > 0;
        GStreamer.DebugLogEx.Log(
            Gst.DebugLevel.Error,
            $"Unhandled exception ocured in {source}\n{ex}",
            null,
            flag ? $"{fr?.GetFileName()}" : "",
            flag ? fr?.GetFileLineNumber() ?? 0 : 0,
            flag ? $"{mi?.DeclaringTypeName}, {mi?.DeclaringAssemblyName}" : "");
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogException("App.UnhandledException", e.Exception);
        e.Handled = true;
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        LogException("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        LogException("AppDomain.UnhandledException", (Exception)e.ExceptionObject);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 録画エンジン（Controller + 全 EventRecorder + 常時稼働 sink パイプライン）をプロセス寿命で
        // 生成する。ウィンドウ生成より前に行うことで、MainPage が表示されなくても（Launch-to-Tray）
        // 常時バッファリングが走り、コマンドライン処理の唯一の入口である
        // GstControllerViewModel.Current が最初のコマンド処理より前に必ず用意される。
        //
        // 起動順序: Program.Main → StartResidentWorker → Controller.StaticInitialize()
        //   → Gst.Functions.Init → Application.Start → new App() → OnLaunched
        // であり、この時点で GStreamer は初期化済み。アクティベーション転送は TryEnqueue 経由なので
        // メッセージループが回るまで処理されない。
        var engine = ViewModels.GstControllerViewModel.Start(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        // 設定変更をデバウンス自動保存する。これが無いと保存契機は Destroying と
        // WindowHiddenToTray だけで、トレイ常駐中に --set して強制終了すると失われる。
        // エンジン生成の後に張るのは、起動時のレコーダー復元で無駄な保存を誘発しないため。
        Settings.AppSettings.Default.AttachAutoSave(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        // 保存先の古い mp4 を掃除する常駐タスク。起動直後に1回 → 以後は設定の間隔ごと。
        // 設定は毎回読み直すので、保存先や日数を変えれば次の周回から効く。
        // ここに置くのは AttachAutoSave と同じ理由 ── 「常駐ワーカーで一度だけ、
        // エンジンが出来てから」。触るのはファイルシステムだけで録画エンジンには依存しない。
        var cleanup = new Services.RecordingCleanupScheduler(() =>
        {
            var settings = Settings.AppSettings.Default;
            return (Components.AppDirectories.ResolveOrBase(settings.OutputDirectory),
                    settings.RecordingRetentionDays,
                    settings.RecordingCleanupIntervalHours);
        });
        cleanup.Start();

        // UIA トリガ監視。置き場所は cleanup と同じ理由（常駐ワーカーで一度だけ、
        // エンジンが出来てから・ウィンドウ生成より前）。トリガ未設定なら監視スレッドは
        // 作られないので、Launch-to-Tray でも E2E でも害が無い。
        var triggers = Services.UiaTriggerService.Start(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        _mainWindow = new MainWindow();
        // MainWindow の ctor が Window.Title を設定済み（詳細はそちらのコメント）。
        // ここで AppWindow へも入れるのは、WinUIEx がトレイアイコンのツールチップに
        // **AppWindow.Title** を使うため。
        _mainWindow.AppWindow.Title = _mainWindow.Title;
        // トレイ格納は Closing をキャンセルするだけなので、Destroying は本当の終了時のみ発火する。
        // 設定を保存してから録画エンジンを破棄する（2つのハンドラの発火順に依存させない）。
        // 各段は互いに独立なので、1段の失敗で残りを道連れにしない ── 特に engine.Dispose() は
        // 録画の確定（moov の書き出し）を担うため、必ず到達させる。ここで例外を素通しにすると、
        // ディスク満杯で Save() が投げた場合に、録画データが最も危険な状況でその確定処理が飛ぶ。
        _mainWindow.AppWindow.Destroying += (_, __) =>
        {
            try { Settings.AppSettings.Default.Save(); }
            catch (Exception ex) { LogException("AppWindow.Destroying(Save)", ex); }
            // トリガ監視をエンジンより先に止める ── 以後レコーダーへ新しい開始/停止を積ませない。
            try { triggers.Dispose(); }
            catch (Exception ex) { LogException("AppWindow.Destroying(Triggers)", ex); }
            // 掃除タスクを先に止める。順序に意味は無い（互いに触るものが無い）が、
            // 「掃除の途中でプロセスが消える」のを避けるため終了経路で待つ。
            try { cleanup.Dispose(); }
            catch (Exception ex) { LogException("AppWindow.Destroying(Cleanup)", ex); }
            try { engine.Dispose(); }
            catch (Exception ex) { LogException("AppWindow.Destroying(Engine)", ex); }
        };

        _singleInstanceManager.AttachWindow(_mainWindow);
        _singleInstanceManager.WindowHiddenToTray += (_, __) => Settings.AppSettings.Default.Save();

        // プレビューは SwapChainPanel 描画へ移行したため、ネイティブ子HWND 生成のための
        // 強制表示（旧: Restore + SetForegroundWindow）と初回未描画のリサイズ回避策は不要。
        // AttachWindow が設定した Launch-to-Tray（最小化・非表示）のまま常駐する。
        // ただしデバッグ実行時は、開発中の確認のため表示した状態で起動する。
        if (System.Diagnostics.Debugger.IsAttached)
        {
            _mainWindow.Restore();
            _mainWindow.SetForegroundWindow();
        }
    }
}
