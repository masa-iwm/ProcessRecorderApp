using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using ProcessRecorderApp.Components;
using Windows.UI.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace ProcessRecorderApp.SingleInstance;

/// <summary>
/// 単一インスタンス制御・タスクトレイ常駐・起動引数のディスパッチを担う常駐ワーカー側の実装。
///
/// ・コンストラクターでインスタンスキーを登録し、以後は他プロセス（ランチャー）からの
///   リダイレクトを AppInstance.Activated イベントで受信し続ける。
/// ・キー登録に成功したら、名前付きイベント（WorkerReadyEvent）をSetし、
///   自分を起動したランチャープロセスに「常駐ワーカーとして起動完了した」ことを知らせる
///   （ランチャー側は <see cref="StartResidentWorkerAndWaitForRegistration"/> でこれを待つ）。
/// ・<see cref="AttachWindow"/> でウィンドウを紐付けると、タスクトレイアイコン（WinUIExの
///   WindowManager.IsVisibleInTray）と、閉じる／最小化ボタンでのトレイ格納を自動的に有効化する。
/// ・受信した起動引数はトークン配列に分解したうえで、コンストラクターで渡された
///   コマンドハンドラー（アプリ固有のコマンド解析処理）へディスパッチする。
/// </summary>
public sealed partial class SingleInstanceManager
{
    /// <summary>常駐ワーカー内でコマンド処理中に予期しない例外が発生した場合の終了コード。</summary>
    public const int UnexpectedErrorExitCode = 99;

    /// <summary>
    /// リダイレクトを受け取ったものの、常駐ワーカーが既に終了処理に入っていて
    /// コマンドを実行できなかった場合の終了コード。
    ///
    /// <para>
    /// <b><see cref="ExitCode_WorkerResultTimeout"/>（2）と分けることに意味がある。</b>
    /// 2 は「委譲したが結果が返らない＝<b>成否不明</b>」だが、こちらは
    /// <b>一度も実行していないと分かっている</b>。呼び出し側（バッチ）は
    /// <b>安全に再試行できる</b>ので、区別できないと再試行の可否が判断できない。
    /// </para>
    /// </summary>
    public const int ExitCode_WorkerShuttingDown = 5;

    private readonly string _keyPrefix;
    private readonly Func<string[], Task<CommandOutcome>> _handleCommand;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly AppInstance _mainInstance;

    private Window? _window;
    private WindowManager? _windowManager;
    private bool _allowRealClose;

    /// <summary>
    /// 終了を決めた（トレイの「終了」または Ctrl+閉じる）。
    ///
    /// <para>
    /// <b><see cref="ExitCode_WorkerShuttingDown"/> を <c>TryEnqueue</c> の戻り値で
    /// 返す経路とは、守っている区間が違う。</b>
    /// あちらが <c>false</c> を返すのは <b><see cref="DispatcherQueue"/> が既に停止に
    /// 入った後</b>だが、終了を決めてから実際に停止するまでの間（設定の保存・エンジンの
    /// 破棄・排出）は<b>まだキューが受け付ける</b> ── そこへ積んだコマンドは実行されるとも
    /// 限らないのに、ランチャーには「委譲できた」ように見え、結果が来ないまま
    /// 上限（最大60秒）まで待って「成否不明」の 2 になる。
    /// 決めた時点から「一度も実行していない」と答えるための旗である。
    /// </para>
    /// <para>
    /// バックグラウンドスレッド（<see cref="OnActivationRedirected"/>）から読むので
    /// <c>volatile</c>。
    /// </para>
    /// </summary>
    private volatile bool _shuttingDown;

    /// <summary>トレイ格納（最小化または閉じるボタン）によりウィンドウが非表示になった直後に発火する。</summary>
    public event EventHandler? WindowHiddenToTray;

    /// <param name="keyPrefix">単一インスタンス判定キーや各種名前付きカーネルオブジェクトの共通プレフィックス。</param>
    /// <param name="handleCommand">
    /// トークン化された起動引数を受け取り、実行結果（<see cref="CommandOutcome"/>）を非同期に返すコールバック。
    /// 実際のコマンド体系（サブコマンドの定義等）はアプリ側が実装する。
    /// 常駐ワーカー起動直後などアプリの準備が整うまで待ってから処理する必要がある場合に備え、
    /// UI スレッドをブロックせず待機できるよう非同期（<see cref="Task{TResult}"/>）としている。
    /// </param>
    public SingleInstanceManager(string keyPrefix, Func<string[], Task<CommandOutcome>> handleCommand)
    {
        _keyPrefix = keyPrefix;
        _handleCommand = handleCommand;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // 常駐インスタンスとしてキーを登録し、以後の他プロセスからの
        // アクティブ化リダイレクトを受け取れるようにする。
        // （ランチャー側で一度 UnregisterKey された後、このプロセスが改めて
        //   登録することで「実処理を行うインスタンス」になる）
        _mainInstance = AppInstance.FindOrRegisterForKey(Names.InstanceKey(keyPrefix));
        _mainInstance.Activated += OnActivationRedirected;

        // **ここが「リダイレクトを受け取れるようになった」瞬間。**
        // キー登録（StartResidentWorker の冒頭）からこの行までの間、
        // ランチャーからは「ワーカーが居る」と見えるのに購読者が居ないため、
        // 届いたリダイレクトは痕跡ゼロで捨てられる（実測で確認した実バグ。
        // 詳細は Launcher 側の WaitUntilWorkerAcceptsCommands のコメント）。
        // 直接起動されたワーカーに対してランチャーはこの旗を待つ。
        SignalWorkerAcceptsCommands();

        if (_mainInstance.IsCurrent)
        {
            // 自分がキー登録に成功した＝正式に常駐ワーカーになれたことを、
            // 自分を起動したランチャープロセスへ通知する。
            using var workerReadyEvent = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, Names.WorkerReadyEvent(keyPrefix));
            workerReadyEvent.Set();
        }
    }

    /// <summary>
    /// ウィンドウを紐付け、タスクトレイ常駐（アイコン表示・右クリックメニュー・
    /// 閉じる/最小化でのトレイ格納・Launch-to-Trayパターンでの起動）を有効化する。
    /// </summary>
    public void AttachWindow(Window window)
    {
        _window = window;

        _windowManager = WindowManager.Get(window);
        _windowManager.IsVisibleInTray = true;
        _windowManager.TrayIconContextMenu += OnTrayIconContextMenu;
        _windowManager.WindowStateChanged += OnWindowStateChanged;

        window.AppWindow.Closing += OnAppWindowClosing;

        // 起動直後は Window.Activate() を一切呼ばない（＝Launch-to-Trayパターン）。
        // WindowState を Minimized に設定するだけで、画面には一度も表示されないまま
        // タスクトレイにのみ常駐する状態になる。
        _windowManager.WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// タスクバー/Alt+Tab切替への表示制御と、最小化ボタン押下時のトレイ格納。
    /// </summary>
    private void OnWindowStateChanged(object? sender, WindowState state)
    {
        if (_windowManager is null)
        {
            return;
        }

        _windowManager.AppWindow.IsShownInSwitchers = state != WindowState.Minimized;

        if (state == WindowState.Minimized)
        {
            // 最小化ボタン押下時も、タスクバーに残さずトレイへ完全に格納する。
            _windowManager.AppWindow.Hide();
            WindowHiddenToTray?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 閉じるボタン(X)を押されたときに、トレイへ格納するのではなくアプリを終了すべきか。
    /// 判定規則そのものをテストするために純粋関数として分離してある。
    ///
    /// <para>
    /// <b>必ず <see cref="CoreVirtualKeyStates.Down"/> フラグで判定する。</b>
    /// <see cref="CoreVirtualKeyStates"/> は <c>[Flags]</c>（<c>None</c>=0 / <c>Down</c>=1 /
    /// <c>Locked</c>=2）であり、<c>!= None</c> と書くと <b><c>Locked</c> にも一致してしまう</b>。
    /// <c>Locked</c> は CapsLock 等のトグル状態を表すもので Ctrl には意味が無いが、
    /// <b>実際に Ctrl が <c>Locked</c> と報告される環境がある</b>（切断中の RDP セッションで確認）。
    /// そこでは Ctrl に触れていなくても終了側へ分岐し、X ボタンでトレイに格納されず
    /// <b>常駐バッファリングごとプロセスが落ちていた</b> ── アプリの中核価値を静かに壊す不具合。
    /// </para>
    /// </summary>
    internal static bool ShouldExitOnClose(CoreVirtualKeyStates controlKeyState)
        => controlKeyState.HasFlag(CoreVirtualKeyStates.Down);

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowRealClose)
        {
            // アプリ終了時（タスクトレイの「終了」経由）は、そのまま閉じさせる。
            return;
        }

        // 閉じるボタン(X)が押された場合は、アプリを終了せずトレイへ格納する。
        // ただし、Controlキーを押している場合は、アプリを終了する。
        if (ShouldExitOnClose(InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)))
        {
            BeginShutdown();
        }
        else
        {
            args.Cancel = true;
            _windowManager?.WindowState = WindowState.Minimized;
            sender.Hide();
        }
    }

    private void OnTrayIconContextMenu(object? sender, TrayIconEventArgs e)
    {
        var flyout = new MenuFlyout();

        var showItem = new MenuFlyoutItem { Text = Localization.GetString("Resources/Tray_Show") };
        showItem.Click += (_, _) => ShowMainWindow();
        flyout.Items.Add(showItem);

        var exitItem = new MenuFlyoutItem { Text = Localization.GetString("Resources/Tray_Quit") };
        exitItem.Click += (_, _) => ExitApplication();
        flyout.Items.Add(exitItem);

        e.Flyout = flyout;
    }

    /// <summary>
    /// 他プロセス（ランチャー）から RedirectActivationToAsync で送られてきた
    /// アクティブ化を受信するハンドラー。
    /// </summary>
    private void OnActivationRedirected(object? sender, AppActivationArguments e)
    {
        // **相関 ID はここで確定させる。** ここはリダイレクトが届いた瞬間で、
        // 「このコマンドを待っているランチャーが誰か」が一意に決まる唯一の場所である
        // ── 処理が終わる頃には、そのランチャーは諦めて次のランチャーが同じ名前の
        // チャネルを張り直しているかもしれない。
        Guid requestId = CommandResultChannel.ClaimRequestId(_keyPrefix);

        // このイベントは呼び出し元スレッド（別プロセスからの呼び出しを処理する
        // バックグラウンドスレッド）で発火するため、UIスレッドへディスパッチする。
        //
        // **戻り値を捨ててはいけない。** false になるのは DispatcherQueue が
        // シャットダウン中＝終了経路のときで、黙って捨てると誰も結果を書かないため
        // ランチャーは結果通知を上限いっぱい（最大60秒）待ってから
        // ExitCode_WorkerResultTimeout（2）を返す ── 利用者から見れば
        // 「アプリ終了と同時に叩いたコマンドが60秒固まったうえ、成否不明で終わる」。
        // 実際には**一度も実行していない**と分かっているので、その場でそう答える。
        //
        // **_shuttingDown はその手前の区間を受け持つ**（キューはまだ受け付けるが、
        // 積んでも実行されるとは限らない区間。フィールドの doc を参照）。
        if (!_shuttingDown && _dispatcherQueue is not null
            && _dispatcherQueue.TryEnqueue(() => HandleActivation(e, requestId)))
        {
            return;
        }

        ReportCommandResult(requestId, ExitCode_WorkerShuttingDown, null, ShutdownMessageOrNull());
    }

    /// <summary>
    /// 終了処理中に返すメッセージ。<b>リソース解決に失敗しても投げないこと</b>
    /// ── ここは既にシャットダウン中で、投げると
    /// <see cref="ReportCommandResult"/> に到達せず<b>元の「60秒待って exit 2」に戻る</b>
    /// （この修正が消える）。<c>ReportCommandResult</c> の try/catch は
    /// 引数の評価までは守ってくれない。
    /// </summary>
    private static string? ShutdownMessageOrNull()
    {
        try
        {
            // 末尾の改行は他の CLI メッセージと揃える
            // （ランチャーは Console.Error.Write で、改行を足さずにそのまま書く）。
            return Localization.GetString("Resources/Cli_WorkerShuttingDown") + Environment.NewLine;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // アプリ側のコマンドハンドラが非同期のため async void とする。
    // （UI スレッドの DispatcherQueue 上で発火されるイベントハンドラー相当の呼び出しであり、
    //   例外は下の try/catch で捕捉して終了コードに反映するため async void でも取りこぼさない）
    private async void HandleActivation(AppActivationArguments e, Guid requestId)
    {
        int exitCode;
        string? consoleOutput = null;
        string? consoleError = null;
        try
        {
            string commandLine = ActivationTokenizer.ExtractCommandLine(e);
            string[] args = ActivationTokenizer.Tokenize(commandLine);
            args = ActivationTokenizer.StripExecutablePath(args);
            var outcome = await _handleCommand(args);

            if (outcome.ShowWindow)
            {
                ShowMainWindow();
            }

            if (outcome.ShowsToast)
            {
                TryShowToast(outcome.ToastTitle!, outcome.ToastMessage!);
            }

            exitCode = outcome.ExitCode;
            consoleOutput = outcome.ConsoleOutput;
            consoleError = outcome.ConsoleError;
        }
        catch
        {
            // 常駐ワーカーはここで例外を握りつぶし、プロセスを終了させない。
            // 呼び出し元のランチャーには専用の終了コードで異常を伝える。
            exitCode = UnexpectedErrorExitCode;
        }

        ReportCommandResult(requestId, exitCode, consoleOutput, consoleError);
    }

    /// <summary>
    /// トースト通知はあくまで補助的な結果通知であり、コマンド本体（<see cref="_handleCommand"/>）の
    /// 処理結果そのものではない。そのため通知の失敗はコマンドの終了コードに影響させず、
    /// ベストエフォートで無視する。環境（Windows App Runtimeのバージョン・配布形態等）によっては
    /// <c>AppNotificationManager.Register()</c> が失敗することがあるため
    /// （例: https://github.com/microsoft/WindowsAppSDK/issues/6071）。
    /// </summary>
    private static void TryShowToast(string title, string message)
    {
        try
        {
            Notifications.ShowToast(title, message);
        }
        catch
        {
        }
    }

    /// <summary>
    /// コマンド処理の結果（終了コード・コンソール出力文字列）を、リダイレクト元のランチャープロセスへ通知する。
    /// ランチャー側は <see cref="RedirectActivationAndGetExitCode"/> でこれを受け取り、
    /// 終了コードを自身のプロセス終了コードにそのまま反映し、出力文字列を呼び出し元コンソールへ出力する。
    ///
    /// <para>
    /// <b><paramref name="requestId"/> は「どのコマンドの結果か」を表す相関 ID</b>で、
    /// <see cref="OnActivationRedirected"/> がリダイレクトの到着時に claim したもの。
    /// これを刻まないと、遅れた通知が次のコマンドの答えとして読まれる
    /// （<see cref="CommandResultChannel"/> の doc を参照）。
    /// </para>
    /// </summary>
    private void ReportCommandResult(Guid requestId, int exitCode, string? consoleOutput, string? consoleError)
        => CommandResultChannel.Publish(_keyPrefix, requestId, exitCode, consoleOutput, consoleError);

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Restore();
        // 別プロセスからの要求で前面化する際は、フォアグラウンドロックの都合上
        // SetForegroundWindow を明示的に呼ぶ必要がある。
        _window.SetForegroundWindow();
    }

    private void ExitApplication()
    {
        BeginShutdown();

        // Closing をフックしてトレイ常駐化しているため、
        // 本当にアプリを終了させる際は事前にその挙動を無効化しておく。
        _allowRealClose = true;

        Application.Current?.Exit();
    }

    /// <summary>
    /// 終了経路の入口（トレイの「終了」と Ctrl+閉じる の<b>両方</b>から通る）。
    ///
    /// <para>
    /// <b>この2行の順序と、ここに<i>無い</i>1行がこのメソッドの本体である。</b>
    /// </para>
    /// <para>
    /// ① 先に「終了を決めた」ことを立てる ── 以後に届いたリダイレクトは
    ///    <see cref="ExitCode_WorkerShuttingDown"/>（5）で即答できる。
    /// </para>
    /// <para>
    /// ② 次にキーを解除する ── これで新しく起動したランチャーは「ワーカーが居ない」と見て
    ///    <b>コールドスタートへ倒れる</b>（＝正しい答えを得る）。
    /// </para>
    /// <para>
    /// ③ <b><c>Activated</c> は外さない。</b> 外すと、解除の直前にキーの持ち主を
    ///    見終わっていたランチャーのリダイレクトが<b>購読者不在で痕跡ゼロに消え</b>、
    ///    そのランチャーは結果通知を上限（最大60秒）まで待って「成否不明」の 2 を返す。
    ///    ハンドラを残しておけば、同じリダイレクトに<b>「一度も実行していない」5</b> を返せる
    ///    ── <c>TryEnqueue</c> の戻り値で塞げるのは「発火した後に
    ///    <c>DispatcherQueue</c> が止まった場合」だけで、
    ///    購読解除の後に届く分はここでしか塞げない。
    /// </para>
    /// <para>
    /// <b>この順序は自動テストでは観測できない</b> ── 購読解除とキー解除の間に
    /// リダイレクトを差し込むのはプロセス間のレースそのもので、狙って起こせない。
    /// ソース上の順序は L1 の <c>ShutdownRedirectHandlingTests</c> が固定している。
    /// </para>
    /// </summary>
    private void BeginShutdown()
    {
        _shuttingDown = true;

        // **ここに `Activated -= OnActivationRedirected;` を足さないこと**（上の ③）。
        // 外すと、キー解除の直前にキーの持ち主を見終わっていたランチャーのリダイレクトが
        // 購読者不在で痕跡ゼロに消え、そのランチャーは 60 秒待って終了コード 2 を返す。
        _mainInstance.UnregisterKey();
    }
}
