using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using ProcessRecorderApp.Components;
using Windows.ApplicationModel.DataTransfer;

namespace ProcessRecorderApp.Controls;

/// <summary>
/// <see cref="LogBuffer"/> の内容を WebView2 の中の xterm.js へ流して表示する。
/// WebView2 が使えないときは <c>ListView</c> 用の行コレクション
/// （<see cref="FallbackItems"/>）へ切り替える。
///
/// <para>
/// <b>背圧が設計の中心。</b> <c>PostWebMessageAsString</c> はプロセスを跨ぐマーシャルで、
/// <c>term.write()</c> は JS 側の内部キューに積む。どちらも投げっぱなしにすると
/// ログ洪水でキューが片側だけ伸び続ける。そこで
/// <b>「1 tick に 1 通・前の書き込みの ack を受け取るまで次を送らない」</b>を守る。
/// カーソルは絶対位置なので、ack を待つあいだにバッファが溢れても特別扱いは要らない
/// ── 次の <see cref="LogBuffer.Read"/> が破棄行数を返し、印が正しく入る。
/// </para>
/// <para>
/// <b>ブリッジは <c>PostWebMessage*</c> と <c>WebMessageReceived</c> だけを使う。</b>
/// <c>AddHostObjectToScript</c> は IDispatch/リフレクション経由で Native AOT では成立しない
/// （<c>NativeSwapChainPanel</c> と同じ方針）。
/// </para>
/// </summary>
public sealed partial class LogTerminalView : UserControl
{
    /// <summary>解決できない予約 TLD。実在ホストへ出て行かないことを名前で保証する</summary>
    private const string VirtualHost = "log-terminal.invalid";

    /// <summary>メッセージの区切り。端末ストリームに現れない制御文字なのでエスケープが要らない</summary>
    private const char Separator = '\u001F';

    /// <summary>排出の周期。30Hz。人間には連続に見え、1 tick あたりの送信は 1 通に収まる</summary>
    private const int TickMilliseconds = 33;

    /// <summary>1 通に載せる最大文字数。大きすぎるとマーシャルで詰まる</summary>
    private const int MaxPostCharacters = 256 * 1024;

    /// <summary>
    /// ack を待つ上限。これを超えたら 1 通落ちたものとして進める。
    /// <b>短くしすぎない</b> ── 諦めた結果は「恒久的にリスト表示へ落ちる」であって
    /// 一時的な遅れではない。負荷の高い機械で偶発的に落ちる方が害が大きい
    /// </summary>
    private const int AckTimeoutMilliseconds = 5000;

    /// <summary>ack の取りこぼしが続いたら端末を諦める回数</summary>
    private const int MaxConsecutiveAckTimeouts = 3;

    /// <summary>ブラウザープロセスの再生成を試みる上限</summary>
    private const int MaxProcessFailures = 3;

    private enum State
    {
        /// <summary>未初期化、または Log 画面が非表示（タイマー停止）</summary>
        Stopped,
        /// <summary>初期化済み・遷移済み。JS の ready 待ち。まだ 1 バイトも送らない</summary>
        Starting,
        /// <summary>送信できる</summary>
        Idle,
        /// <summary>直前の書き込みの ack 待ち</summary>
        AwaitingAck,
        /// <summary>端末を諦めた（終端状態）</summary>
        Fallback,
    }

    private readonly LogListAdapter _adapter = new();
    private readonly DispatcherQueueTimer _timer;

    /// <summary>環境は参照等価で比較されるので、必ず持ち続ける</summary>
    private CoreWebView2Environment? _environment;

    private State _state = State.Stopped;
    private bool _initializeAttempted;
    private long _position;
    private long _line;
    private int _epoch;
    private long _lastPostId;
    private long _postSentAt;
    private int _consecutiveAckTimeouts;
    private int _processFailures;


    /// <summary>初期化がどこで失敗したか（記録に残す。画面の注記は理由を名指ししない）</summary>
    private string _initializeFailure = "init-failed";

    public LogTerminalView()
    {
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(TickMilliseconds);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
    }

    /// <summary>表示する内容。既定はプロセス全体で共有する実体</summary>
    public LogBuffer Buffer { get; set; } = LogBuffer.Shared;

    /// <summary>フォールバック時に <c>ListView</c> へバインドする行</summary>
    public ObservableCollection<string> FallbackItems => _adapter.Items;

    /// <summary>端末を諦めてリスト表示に落ちた</summary>
    public bool IsFallbackActive => _state == State.Fallback;

    /// <summary>端末を諦めた（フォールバックへ切り替える合図）</summary>
    public event EventHandler? FallbackActivated;

    /// <summary>利用者のスクロールで最下部追従の状態が変わった</summary>
    public event EventHandler<bool>? FollowStateChanged;

    // ---- 依存関係プロパティ ----

    /// <summary>
    /// Log 画面が表示されているあいだだけ true にする。
    /// <b>初期化も排出もここで駆動する</b> ── 見ていない画面のために
    /// ブラウザープロセスを起こし続けたり、録画中にメッセージをマーシャルし続けたりしない。
    /// 非表示のあいだに溜まった分は、絶対位置のカーソルが残っているので再表示時に続きから流れる
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(LogTerminalView),
            new PropertyMetadata(false, OnIsActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>最下部への追従。ToggleButton と双方向で結ぶ</summary>
    public static readonly DependencyProperty IsFollowEnabledProperty =
        DependencyProperty.Register(nameof(IsFollowEnabled), typeof(bool), typeof(LogTerminalView),
            new PropertyMetadata(true, OnIsFollowEnabledChanged));

    public bool IsFollowEnabled
    {
        get => (bool)GetValue(IsFollowEnabledProperty);
        set => SetValue(IsFollowEnabledProperty, value);
    }

    /// <summary>端末が保持する行数。<see cref="LogBuffer.MaxLines"/> と必ず同値にする</summary>
    public static readonly DependencyProperty ScrollbackLinesProperty =
        DependencyProperty.Register(nameof(ScrollbackLines), typeof(int), typeof(LogTerminalView),
            new PropertyMetadata(LogBuffer.DefaultMaxLines, OnScrollbackLinesChanged));

    public int ScrollbackLines
    {
        get => (int)GetValue(ScrollbackLinesProperty);
        set => SetValue(ScrollbackLinesProperty, value);
    }

    [WinRT.DynamicWindowsRuntimeCast(typeof(LogTerminalView))]
    private static void OnIsActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((LogTerminalView)sender).ApplyActiveState((bool)e.NewValue);

    [WinRT.DynamicWindowsRuntimeCast(typeof(LogTerminalView))]
    private static void OnIsFollowEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((LogTerminalView)sender).SendFollow((bool)e.NewValue);

    [WinRT.DynamicWindowsRuntimeCast(typeof(LogTerminalView))]
    private static void OnScrollbackLinesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((LogTerminalView)sender).SendScrollback((int)e.NewValue);

    // ---- 開始・停止 ----

    private async void ApplyActiveState(bool active)
    {
        if (!active)
        {
            _timer.Stop();
            if (_state is State.Idle or State.AwaitingAck)
            {
                _state = State.Stopped;
            }
            return;
        }

        if (_state == State.Fallback)
        {
            _timer.Start();
            return;
        }

        if (!_initializeAttempted)
        {
            // 初期化は一度きり。失敗経路では WinUI 内部の生成中フラグが戻らず、
            // 2 回目の EnsureCoreWebView2Async が永久に返らない
            _initializeAttempted = true;
            if (!await TryInitializeAsync())
            {
                FallBackToListView(_initializeFailure);
                _timer.Start();
                return;
            }
            _state = State.Starting;
        }
        else if (_state == State.Stopped)
        {
            // **ここで流し直してはいけない。** ページは生きたままなので、端末には
            // 既に描いたものが残っている。カーソルは絶対位置で残っているので、
            // 続きから送れば非表示のあいだの分に追いつく。先頭へ戻すと
            // 保持中の全量がもう一度上書きされずに追記され、**行が二重・三重に増える**
            // （どのテスト層も端末の中身を読めないので、これは緑のまま起こる）。
            // 非表示のあいだに押し出しが起きていれば、次の Read が破棄行数を返して印が入る。
            _state = State.Idle;
        }

        _timer.Start();
    }

    /// <summary>
    /// カーソルを保持窓の先頭へ戻す。
    /// <b>端末が空になったときだけ呼ぶこと</b>（初回の ready と、ブラウザー再生成後の再遷移）。
    /// 中身が残っている端末に対して呼ぶと、保持中の全量が追記されて行が二重になる
    /// </summary>
    public void RequestReplay()
    {
        var head = Buffer.Head;
        (_position, _line, _epoch) = (head.Position, head.Line, head.Epoch);
    }

    /// <summary>ページから外れるときに呼ぶ。タイマーを止め、ブラウザーを畳む</summary>
    public void Stop()
    {
        _timer.Stop();
        _state = State.Stopped;
        try { webView.Close(); } catch (Exception) { /* 既に閉じている */ }
    }

    private async Task<bool> TryInitializeAsync()
    {
        try
        {
            // ランタイムが無ければブラウザーを 1 本も起こさずに諦める
            if (string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()))
            {
                _initializeFailure = "runtime-missing";
                return false;
            }
        }
        catch (Exception)
        {
            _initializeFailure = "runtime-missing";
            return false;
        }

        // Ensure より「前」に入れること。コントローラー生成時に一度だけ流し込まれるので、
        // 後から入れても最初の 1 フレームが既定の白のままになる
        webView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C);

        try
        {
            // 既定のユーザーデータフォルダーは実行ファイルの隣。zip 配布なので
            // Program Files 配下に置かれた瞬間に書き込めず初期化ごと失敗する
            var userDataFolder = AppEnvironment.GetDataFilePath("WebView2");
            Directory.CreateDirectory(userDataFolder);

            _environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: string.Empty,
                userDataFolder: userDataFolder,
                options: new CoreWebView2EnvironmentOptions
                {
                    // ランチャーと常駐ワーカーが同時に生きうるので排他にはしない
                    ExclusiveUserDataFolderAccess = false,
                    AreBrowserExtensionsEnabled = false,
                });
            await webView.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception ex)
        {
            _initializeFailure = $"init-failed {ex.GetType().Name}";
            return false;
        }

        // ランタイム不在の主経路はここ。例外にならず、正常完了して CoreWebView2 が null のまま
        // （WinUI 側が「WebView2 が見つからない」旨の表示を自分の子として描く）
        if (webView.CoreWebView2 is null)
        {
            // ランタイム不在の主経路。例外にならず正常完了して null のまま返る
            _initializeFailure = "runtime-missing";
            return false;
        }

        HardenAndNavigate(webView.CoreWebView2);
        return true;
    }

    private void HardenAndNavigate(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsScriptEnabled = true;          // 必須
        settings.IsWebMessageEnabled = true;      // 必須（ブリッジ）
        settings.AreDevToolsEnabled = false;
        // 既定メニュー（再読み込み・ページの保存など）は要らないが、**無効にはしない**
        // ── 無効にすると ContextMenuRequested が来なくなり、独自の「コピー」も出せない。
        // 中身は下の ContextMenuRequested で丸ごと差し替える
        settings.AreDefaultContextMenusEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false; // F5/Ctrl+R 等を潰す（Ctrl+C は残る）
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreHostObjectsAllowed = false;   // AddHostObjectToScript を明示的に閉じる
        settings.IsReputationCheckingRequired = false;

        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.ContextMenuRequested += OnContextMenuRequested;
        core.NavigationStarting += (_, e) =>
            e.Cancel = !e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase);
        core.WebMessageReceived += OnWebMessageReceived;
        webView.CoreProcessFailed += OnCoreProcessFailed;

        // file:// ではモジュール/CORS の扱いが変わるので仮想ホストに写す。
        // 実ディレクトリしか受け取らないため、アセットは単一ファイル発行でも
        // バンドルへ埋めずルーズに置いている（csproj の ExcludeFromSingleFile）
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal"),
            CoreWebView2HostResourceAccessKind.Deny);

        webView.Visibility = Visibility.Visible;
        core.Navigate($"https://{VirtualHost}/index.html");
    }

    // ---- 排出 ----

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_state == State.Fallback)
        {
            _adapter.Pump(Buffer, MaxPostCharacters);
            return;
        }

        if (_state == State.AwaitingAck)
        {
            if (Environment.TickCount64 - _postSentAt < AckTimeoutMilliseconds)
            {
                return;
            }
            // ack を 1 回落としただけでログが永久に止まらないよう、待たずに進める
            if (++_consecutiveAckTimeouts >= MaxConsecutiveAckTimeouts)
            {
                FallBackToListView($"ack-timeout x{_consecutiveAckTimeouts}");
                return;
            }
            _state = State.Idle;
        }

        if (_state != State.Idle || webView.CoreWebView2 is not { } core)
        {
            return;
        }

        var read = Buffer.Read(_position, _line, _epoch, MaxPostCharacters);
        var flags = read.Cleared ? 1 : 0;
        if (read.Text.Length == 0 && flags == 0)
        {
            return;
        }

        var payload = read.Text;
        if (read.DroppedLines > 0)
        {
            // ESC[0m の前置は必須。捨てた領域で立っていた装飾が印と以後へ滲む
            payload = $"\u001b[0m{LogBuffer.DropMarkerFormatter(read.DroppedLines)}\r\n{payload}";
        }

        var id = ++_lastPostId;
        var message = new StringBuilder(payload.Length + 32)
            .Append('w').Append(Separator)
            .Append(id).Append(Separator)
            .Append(flags).Append(Separator)
            .Append(payload)
            .ToString();

        try
        {
            core.PostWebMessageAsString(message);
        }
        catch (Exception)
        {
            FallBackToListView("post-failed");
            return;
        }

        _state = State.AwaitingAck;
        _postSentAt = Environment.TickCount64;
        // 投稿の完了は待たずにカーソルを進める。ack は「次を送ってよいか」だけを意味し、
        // 再送はしない（再送すると同じ行が二重に出る）
        (_position, _line, _epoch) = (read.NextPos, read.NextLine, read.Epoch);
    }

    // ---- JS からの受信 ----

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string message;
        try
        {
            message = args.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return; // 文字列以外は送っていない
        }
        if (message.Length == 0)
        {
            return;
        }

        var body = message.Length > 2 ? message[2..] : string.Empty;
        switch (message[0])
        {
            case 'h': // hello。スクリプトが走って受信を張り終えた
                // ここで初めて送れる。これより前の post は受け手が居ないので消える
                SendInitialize();
                break;

            case 'y': // ready。端末が出来た。ここまで本文は 1 バイトも送っていない
                _state = State.Idle;
                _consecutiveAckTimeouts = 0;
                // どちらのレンダラーで描いているかは画面から見分けが付かない。
                // 「遅い」という申告を受けたときに最初に知りたい値なので記録に残す
                ActivityLog.Info("log.terminal", $"ready renderer={body}");
                RequestReplay();
                break;

            case 'a': // ack
                // 古い ack（Clear をまたいだもの）で旗を降ろさない
                if (long.TryParse(body, out var id) && id == _lastPostId && _state == State.AwaitingAck)
                {
                    _state = State.Idle;
                    _consecutiveAckTimeouts = 0;
                }
                break;

            case 'c': // 選択範囲のコピー
                SetClipboard(body);
                break;

            case 's': // 利用者のスクロールで追従状態が変わった
                var follow = body == "1";
                if (follow != IsFollowEnabled)
                {
                    IsFollowEnabled = follow;
                    FollowStateChanged?.Invoke(this, follow);
                }
                break;

            case 'e': // JS 側の致命的エラー
                FallBackToListView($"js-error {body}");
                break;
        }
    }

    // ---- JS への送信 ----

    private void SendInitialize()
    {
        var builder = new StringBuilder(256)
            .Append('i').Append(Separator)
            .Append(ScrollbackLines).Append(Separator)
            .Append(IsFollowEnabled ? '1' : '0').Append(Separator)
            .Append(CampbellPalette.DefaultForeground).Append(Separator)
            .Append(CampbellPalette.DefaultBackground);
        foreach (var color in CampbellPalette.Colors)
        {
            builder.Append(Separator).Append(color);
        }
        Send(builder.ToString());
    }

    private void SendFollow(bool follow) => Send($"f{Separator}{(follow ? '1' : '0')}");

    /// <summary>
    /// 右クリックメニューを「コピー」1 項目だけに差し替える
    /// （リスト表示のときの <c>ListViewCopyBehavior</c> と同じ文言・同じ挙動。
    /// 文言のリソースキーも同じものを引いている）。
    ///
    /// <para>
    /// <b>項目は常に有効にする。</b> メニューは同期に組み立てなければならず、
    /// 選択の有無をその場で JS へ問い合わせる余地が無い。状態を先回りで持つ形にすると
    /// 「選択しているのに押せない」という壊れ方をしうるので、押してから
    /// 選択が無ければ何もしない側に倒す ── <c>ListViewCopyBehavior.CopySelectedItems</c> も
    /// 同じ倒し方をしている。コピー自体は Ctrl+C とまったく同じ経路
    /// （JS へ要求 → <c>'c'</c> で受け取る）。
    /// </para>
    /// </summary>
    private void OnContextMenuRequested(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        args.MenuItems.Clear();
        if (_environment is null)
        {
            return;
        }

        var copy = _environment.CreateContextMenuItem(
            Localization.GetString("Controls/ControlsResources/Common_Copy"),
            iconStream: null,
            CoreWebView2ContextMenuItemKind.Command);
        copy.CustomItemSelected += (_, _) => Send($"x{Separator}");
        args.MenuItems.Add(copy);
    }

    private void SendScrollback(int lines)
    {
        Buffer.MaxLines = lines;
        Send($"n{Separator}{Buffer.MaxLines}");
    }

    private void Send(string message)
    {
        if (_state is State.Stopped or State.Fallback || webView.CoreWebView2 is not { } core)
        {
            return;
        }
        try
        {
            core.PostWebMessageAsString(message);
        }
        catch (Exception)
        {
            FallBackToListView("post-failed");
        }
    }

    // ---- コピー ----

    /// <summary>
    /// バッファの全量をクリップボードへ入れる。
    /// <b>正本は <see cref="LogBuffer"/> であって端末のバッファではない</b>
    /// ── 後者はそのときのウィンドウ幅で折り返された描画結果なので、
    /// 貼り付け内容がウィンドウサイズで変わってしまい、フォールバック経路では実装もできない
    /// </summary>
    public void CopyAll()
        => SetClipboard(LogText.FlattenCarriageReturns(AnsiEscape.Strip(Buffer.Snapshot())).TrimEnd());

    private static void SetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception)
        {
            // クリップボードは他プロセスに握られていることがある。失敗しても表示は続ける
        }
    }

    // ---- 障害とフォールバック ----

    private void OnCoreProcessFailed(WebView2 sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (++_processFailures >= MaxProcessFailures)
        {
            FallBackToListView($"process-failed kind={e.ProcessFailedKind} count={_processFailures}");
            return;
        }

        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            _state = State.Starting;
            try
            {
                webView.CoreWebView2!.Navigate($"https://{VirtualHost}/index.html");
            }
            catch (Exception)
            {
                FallBackToListView("renavigate-failed");
            }
            return;
        }

        // ブラウザープロセスごと落ちた場合、2 回目の Ensure は危険なので作り直さない
        FallBackToListView($"process-failed kind={e.ProcessFailedKind}");
    }

    /// <summary>
    /// 端末を諦めてリスト表示へ落ちる。冪等。
    /// <paramref name="reason"/> は必ず渡すこと ── <b>画面の注記は理由を名指ししない</b>
    /// （経路が 5 つあり、ランタイム不在はそのうちの 1 つでしかない）ので、
    /// どれで落ちたかを知る手段はこの記録だけになる
    /// </summary>
    private void FallBackToListView(string reason)
    {
        if (_state == State.Fallback)
        {
            return;
        }
        _state = State.Fallback;

        // 「なぜかリスト表示になっている」を後から診断できるようにする。
        // 画面の注記は今そこに居る人にしか届かない
        ActivityLog.Warn("log.terminal", $"fallback=list reason={reason}");

        // ランタイム不在では WinUI が自前の警告表示を描くので、必ず畳む
        webView.Visibility = Visibility.Collapsed;
        try { webView.Close(); } catch (Exception) { /* 初期化前・二重呼び出し */ }

        _adapter.Reset(Buffer);
        FallbackActivated?.Invoke(this, EventArgs.Empty);
    }
}
