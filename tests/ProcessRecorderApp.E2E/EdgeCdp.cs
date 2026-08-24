using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// ヘッドレスの Microsoft Edge を 1 つ起こし、DevTools プロトコル（CDP）で 1 ページを操る。
///
/// <para>
/// <b>依存は増やさない。</b> ブラウザ自動化のパッケージ（Playwright 等）は入れず、
/// BCL の <see cref="ClientWebSocket"/> と<b>システムに入っている</b> <c>msedge.exe</c> だけで動かす
/// ── L2 はもともと「発行物を外から叩く」層で、ここで足すのはその相手をブラウザにしただけである。
/// </para>
/// <para>
/// <b>待ち受けポートは 0（OS が選ぶ）。</b> 固定にすると、開発機で何かがそのポートを
/// 使っていた日に「製品の欠陥」に見える形で落ちる。実際の値は Edge が
/// <c>&lt;user-data-dir&gt;\DevToolsActivePort</c> へ書く（1 行目がポート、2 行目が
/// <c>/devtools/browser/&lt;uuid&gt;</c>）。
/// </para>
/// <para>
/// <b>プロファイルは 1 起動につき 1 つの一時ディレクトリ。</b> キャッシュも Cookie も
/// 持ち越さないので、「古い <c>app.js</c> が残っていただけ」という結末が起こりえない。
/// </para>
/// <para>
/// <b><c>Start-Process -Wait</c> 相当は使わない。</b> プロセスは
/// <see cref="Process.Start(ProcessStartInfo)"/> で直接持ち、破棄はツリーごと落とす。
/// </para>
/// </summary>
internal sealed class EdgeCdp : IAsyncDisposable
{
    /// <summary>システムに入っている Edge の場所（安定版はここ 1 つ）。</summary>
    private static readonly string ExecutablePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Microsoft", "Edge", "Application", "msedge.exe");

    /// <summary><c>DevToolsActivePort</c> が現れるまでの待ち。</summary>
    private static readonly TimeSpan LaunchBudget = TimeSpan.FromSeconds(60);

    /// <summary>1 コマンドの応答を待つ上限。</summary>
    private static readonly TimeSpan CommandBudget = TimeSpan.FromSeconds(60);

    private readonly Process _browser;
    private readonly ClientWebSocket _socket;
    private readonly string _userDataDir;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private string _targetId = string.Empty;
    private string _sessionId = string.Empty;
    private int _nextId;
    private bool _closed;

    private EdgeCdp(Process browser, ClientWebSocket socket, string userDataDir)
    {
        _browser = browser;
        _socket = socket;
        _userDataDir = userDataDir;
    }

    /// <summary>Edge が入っていない環境か（テストを Skip する判断に使う）。</summary>
    public static bool IsAvailable => File.Exists(ExecutablePath);

    /// <summary>Skip の理由（不在の事実とパスを書く ── 「なぜか飛んだ」を残さない）。</summary>
    public static string UnavailableReason =>
        $"ヘッドレス ブラウザのテストにはシステムの Edge が要ります（{ExecutablePath} がありません）。";

    /// <summary>
    /// Edge を起こし、ページを 1 つ作って接続した状態で返す。
    ///
    /// <para>
    /// <c>--autoplay-policy=no-user-gesture-required</c> を付けている。追いかけ再生の
    /// <c>video.play()</c> は最初の <c>appendBuffer</c> の後（Promise の中）で呼ばれるので、
    /// 合成したクリックの操作連鎖はそこまで届かない ── 既定の方針のままでは
    /// <b>製品が正しくても</b>再生が始まらない。
    /// </para>
    /// </summary>
    public static async Task<EdgeCdp> LaunchAsync(CancellationToken ct)
    {
        string userDataDir = Path.Combine(Path.GetTempPath(), "prapp-edge-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(userDataDir);

        var start = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false };
        foreach (string argument in new[]
        {
            "--headless=new",
            "--disable-gpu",
            "--remote-debugging-port=0",
            "--user-data-dir=" + userDataDir,
            "--no-first-run",
            "--no-default-browser-check",
            "--autoplay-policy=no-user-gesture-required",
            "about:blank",
        })
        {
            start.ArgumentList.Add(argument);
        }

        Process browser = Process.Start(start)
            ?? throw new InvalidOperationException("msedge.exe を起動できませんでした。");

        try
        {
            string endpoint = await ReadBrowserEndpointAsync(browser, userDataDir, ct);

            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(endpoint), ct);

            var cdp = new EdgeCdp(browser, socket, userDataDir);
            await cdp.AttachAsync(ct);
            return cdp;
        }
        catch
        {
            KillTree(browser);
            TryDelete(userDataDir);
            throw;
        }
    }

    /// <summary>
    /// <c>DevToolsActivePort</c> をポーリングして <c>ws://127.0.0.1:&lt;port&gt;&lt;path&gt;</c> を組む。
    /// <b>2 行揃うまで待つ</b> ── 書きかけを読むと path が空になり、接続だけが失敗する。
    /// </summary>
    private static async Task<string> ReadBrowserEndpointAsync(Process browser, string userDataDir, CancellationToken ct)
    {
        string path = Path.Combine(userDataDir, "DevToolsActivePort");
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < LaunchBudget)
        {
            if (browser.HasExited)
                throw new InvalidOperationException($"msedge.exe が待ち受けを書く前に終了しました（exit={browser.ExitCode}）。");

            if (File.Exists(path))
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(path);
                }
                catch (IOException)
                {
                    lines = [];  // 書いている最中。次の周回で読み直す。
                }

                if (2 <= lines.Length && 0 < lines[0].Length && lines[1].StartsWith('/'))
                    return $"ws://127.0.0.1:{lines[0]}{lines[1]}";
            }

            await Task.Delay(100, ct);
        }

        throw new InvalidOperationException(
            $"{LaunchBudget.TotalSeconds:F0} 秒以内に DevToolsActivePort が現れませんでした（{path}）。");
    }

    /// <summary>
    /// ページを 1 つ作り、<b>flatten モード</b>で attach する。以後のコマンドは
    /// <c>sessionId</c> 付きで同じ 1 本の WebSocket を通る（セッションごとの接続を張らない）。
    /// </summary>
    private async Task AttachAsync(CancellationToken ct)
    {
        using (JsonDocument created = await CommandAsync(
            "Target.createTarget", w => w.WriteString("url", "about:blank"), sessionId: null, ct))
        {
            _targetId = created.RootElement.GetProperty("targetId").GetString()!;
        }

        using (JsonDocument attached = await CommandAsync("Target.attachToTarget", w =>
        {
            w.WriteString("targetId", _targetId);
            w.WriteBoolean("flatten", true);
        }, sessionId: null, ct))
        {
            _sessionId = attached.RootElement.GetProperty("sessionId").GetString()!;
        }

        using (await CommandAsync("Page.enable", parameters: null, _sessionId, ct)) { }
        using (await CommandAsync("Runtime.enable", parameters: null, _sessionId, ct)) { }
    }

    /// <summary>
    /// 指定の URL を開き、<c>document.readyState === 'complete'</c> になるまで待つ。
    ///
    /// <para>
    /// <b>読み込み完了はイベントではなくポーリングで見る。</b> 受信ループはコマンドの
    /// 応答だけを拾って通知を捨てる形なので、<c>Page.loadEventFired</c> を待つと
    /// 取りこぼしたときに永久に待つことになる。
    /// </para>
    /// </summary>
    public async Task NavigateAsync(string url, TimeSpan budget, CancellationToken ct)
    {
        using (await CommandAsync("Page.navigate", w => w.WriteString("url", url), _sessionId, ct)) { }

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < budget)
        {
            try
            {
                if (await EvaluateStringAsync("document.readyState", ct) == "complete")
                    return;
            }
            catch (InvalidOperationException)
            {
                // 遷移の最中は実行コンテキストが入れ替わる。次の周回で聞き直す。
            }

            await Task.Delay(100, ct);
        }

        throw new InvalidOperationException($"{url} が {budget.TotalSeconds:F0} 秒以内に読み込み終わりませんでした。");
    }

    /// <summary>
    /// 式を評価して生の値を返す（<c>returnByValue</c> ＋ <c>awaitPromise</c>）。
    /// 例外が投げられた式・値を返さなかった式は <see cref="InvalidOperationException"/> にする
    /// ── 黙って既定値になると、判定が「常に偽」へ倒れて緑になる。
    ///
    /// <para>
    /// <c>TimeRanges</c> のように JSON にできないものは、式の側で配列や数値へ写して返すこと。
    /// </para>
    /// </summary>
    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct)
    {
        using JsonDocument reply = await CommandAsync("Runtime.evaluate", w =>
        {
            w.WriteString("expression", expression);
            w.WriteBoolean("returnByValue", true);
            w.WriteBoolean("awaitPromise", true);
        }, _sessionId, ct);

        var root = reply.RootElement;
        if (root.TryGetProperty("exceptionDetails", out var details))
            throw new InvalidOperationException($"式が例外を投げました: {expression}{Environment.NewLine}{details}");

        var result = root.GetProperty("result");
        if (!result.TryGetProperty("value", out var value))
            throw new InvalidOperationException($"式が値を返しませんでした: {expression}{Environment.NewLine}{result}");

        return value.Clone();
    }

    /// <summary>真偽値を返す式を評価する。</summary>
    public async Task<bool> EvaluateBoolAsync(string expression, CancellationToken ct)
    {
        var value = await EvaluateAsync(expression, ct);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException($"真偽値ではありません: {expression} -> {value}"),
        };
    }

    /// <summary>数値を返す式を評価する。</summary>
    public async Task<double> EvaluateNumberAsync(string expression, CancellationToken ct)
    {
        var value = await EvaluateAsync(expression, ct);
        return value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : throw new InvalidOperationException($"数値ではありません: {expression} -> {value}");
    }

    /// <summary>文字列を返す式を評価する。</summary>
    public async Task<string> EvaluateStringAsync(string expression, CancellationToken ct)
    {
        var value = await EvaluateAsync(expression, ct);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidOperationException($"文字列ではありません: {expression} -> {value}");
    }

    /// <summary>述語が真になるまで待つ（真偽値を返す JavaScript 式）。</summary>
    public async Task<bool> WaitUntilAsync(string expression, TimeSpan budget, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < budget)
        {
            if (await EvaluateBoolAsync(expression, ct))
                return true;

            await Task.Delay(200, ct);
        }

        return false;
    }

    /// <summary>コマンドを 1 つ送り、同じ <c>id</c> の応答の <c>result</c> を返す。</summary>
    private async Task<JsonDocument> CommandAsync(
        string method, Action<Utf8JsonWriter>? parameters, string? sessionId, CancellationToken ct)
    {
        await _turn.WaitAsync(ct);
        try
        {
            int id = ++_nextId;
            byte[] request = Compose(id, method, parameters, sessionId);
            await _socket.SendAsync(request, WebSocketMessageType.Text, endOfMessage: true, ct);

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < CommandBudget)
            {
                // **受信にも予算を効かせる。** while の条件だけでは受信待ちを縛れない
                // ── CDP が黙ると `ReceiveAsync` から戻らず、条件は二度と評価されない。
                using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                receiveCts.CancelAfter(CommandBudget - deadline.Elapsed);

                JsonDocument message;
                try
                {
                    message = await ReceiveAsync(receiveCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 予算切れ。呼び出し側の取り消しと取り違えないよう、下の throw で終わらせる。
                    break;
                }

                bool mine = message.RootElement.TryGetProperty("id", out var replyId) && replyId.GetInt32() == id;
                if (!mine)
                {
                    // CDP の通知（Runtime.consoleAPICalled 等）。待っているのは応答だけ。
                    message.Dispose();
                    continue;
                }

                if (message.RootElement.TryGetProperty("error", out var error))
                {
                    string text = error.ToString();
                    message.Dispose();
                    throw new InvalidOperationException($"{method} が失敗しました: {text}");
                }

                var result = message.RootElement.GetProperty("result");
                var detached = JsonDocument.Parse(result.GetRawText());
                message.Dispose();
                return detached;
            }

            throw new InvalidOperationException(
                $"{method} の応答が {CommandBudget.TotalSeconds:F0} 秒以内に返りませんでした。");
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// 1 メッセージを読み切る。<b><c>EndOfMessage</c> まで繋ぐ</b> ──
    /// CDP の応答は 1 フレームに収まるとは限らない（評価の結果は特に長い）。
    /// </summary>
    private async Task<JsonDocument> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>();
        byte[] chunk = new byte[64 * 1024];

        while (true)
        {
            ValueWebSocketReceiveResult received = await _socket.ReceiveAsync(chunk.AsMemory(), ct);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("DevTools の接続が閉じられました。");

            buffer.Write(chunk.AsSpan(0, received.Count));
            if (received.EndOfMessage)
                return JsonDocument.Parse(buffer.WrittenMemory);
        }
    }

    /// <summary>
    /// 要求 1 件を組み立てる。<see cref="Utf8JsonWriter"/> で書くのは、
    /// リフレクションを使う直列化を持ち込まないため。
    /// </summary>
    private static byte[] Compose(int id, string method, Action<Utf8JsonWriter>? parameters, string? sessionId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (sessionId is { Length: > 0 })
                writer.WriteString("sessionId", sessionId);

            if (parameters is not null)
            {
                writer.WritePropertyName("params");
                writer.WriteStartObject();
                parameters(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// ページを閉じ、WebSocket を閉じ、プロセスをツリーごと落としてから一時ディレクトリを消す。
    /// <b>順番が要る</b> ── 生きている Edge の子プロセスがプロファイルを掴んだままだと消せない。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_closed)
            return;
        _closed = true;

        try
        {
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (_socket.State == WebSocketState.Open && 0 < _targetId.Length)
            {
                using (await CommandAsync(
                    "Target.closeTarget", w => w.WriteString("targetId", _targetId), sessionId: null, closing.Token)) { }
            }

            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closing.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or WebSocketException or OperationCanceledException)
        {
            // 相手が先に消えていても、後始末そのものは続ける。
        }

        _socket.Dispose();
        _turn.Dispose();
        KillTree(_browser);
        TryDelete(_userDataDir);
    }

    private static void KillTree(Process browser)
    {
        try
        {
            if (!browser.HasExited)
                browser.Kill(entireProcessTree: true);

            browser.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // すでに終わっている。
        }

        browser.Dispose();
    }

    /// <summary>
    /// プロファイルを消す。<b>失敗しても投げない</b> ── 掴んでいるのはブラウザの側で、
    /// 消せなかったことをテストの失敗として報告しても直せる人は居ない
    /// （残るのは %TEMP% の 1 ディレクトリ）。
    /// </summary>
    private static void TryDelete(string directory)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
