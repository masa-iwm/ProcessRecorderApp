using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// リモート操作の HTTP サーバー（波 3 の読み取り系と認証）。
///
/// <para>
/// <b>発行物を相手に本物の TCP で叩く。</b> ここで検証しているのは「認証が実際に断るか」で、
/// 単体では成立しない ── Kestrel のルーティング・Cookie の書式・ヘッダー・
/// AOT でのソース生成 JSON まで含めて初めて意味を持つ。
/// </para>
/// <para>
/// <b>ポートは 0（OS が選ぶ）で待ち受けさせ、<c>activity.log</c> の
/// <c>remote.start</c> から実際の値を読む。</b> 固定ポートにすると、開発機で何かが
/// そのポートを使っていた日に「製品の欠陥」に見える形で落ちる。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class RemoteControlTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>
    /// テスト用の固定トークン。<b>製品が生成する形（Base64Url・43 文字）に合わせてある</b>
    /// ── 長さや文字種で通り方が変わらないことは <c>RemoteApiRulesTests</c>（L1）の担当だが、
    /// ここで別の形を使うと「実際に配られる形」を一度も通さないことになる。
    /// </summary>
    private const string Token = "E2E-remote-control-token-0123456789-abcdefg";

    /// <summary>誤ったトークン（長さは同じ ── 長さ違いだけで弾いていないことを見るため）。</summary>
    private const string WrongToken = "E2E-remote-control-token-9876543210-gfedcba";

    /// <summary><c>remote.start</c> が出るまでの待ち。</summary>
    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(30);

    /// <summary>1 回の HTTP 要求の打ち切り。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// テストの取り消しを伝える token。xunit v3 は <c>CancellationToken</c> を取る
    /// 呼び出しにこれを渡すことを求める（xUnit1051。このリポジトリは
    /// <c>-warnaserror</c> なので警告のままにはできない）。
    /// </summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Regex BindPattern = new(@"\bbind=([0-9.]+):(\d+)\b", RegexOptions.Compiled);

    /// <summary>リモート操作を有効にした settings.json。</summary>
    private static SettingsFile RemoteSettings()
    {
        var settings = new SettingsFile
        {
            RemoteControlEnabled = true,
            // **127.0.0.1 に固定する。** 0.0.0.0（製品の既定）で待ち受けると、
            // 開発機や CI ランナーの LAN から到達できてしまう。
            RemoteControlBindAddress = "127.0.0.1",
            RemoteControlPort = 0,
            RemoteControlAccessToken = Token,
        };
        settings.AddRecorder("R1");
        settings.AddRecorder("R2");
        return settings;
    }

    /// <summary>
    /// <c>activity.log</c> の <c>remote.start</c> から実ポートを読む。
    /// 出ない場合は <c>remote.error</c> を添えて落とす（起動できなかった理由はそこにしか無い）。
    /// </summary>
    private int WaitForPort(AppInstance instance)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartBudget)
        {
            foreach (string line in ActivityLogFile.Events(instance.ReadActivityLog(), "remote.start"))
            {
                var match = BindPattern.Match(ActivityLogFile.DetailOf(line));
                if (match.Success)
                {
                    output.WriteLine(line);
                    return int.Parse(match.Groups[2].Value);
                }
            }
            Thread.Sleep(200);
        }

        var log = instance.ReadActivityLog();
        Assert.Fail(
            $"remote.start が {StartBudget.TotalSeconds:F0} 秒以内に現れませんでした。"
            + Environment.NewLine
            + string.Join(Environment.NewLine, ActivityLogFile.Events(log, "remote.error"))
            + Environment.NewLine + instance.DiagnosticDump());
        return 0;
    }

    /// <summary>
    /// <b>Cookie は手で運ぶ</b>（<c>UseCookies=false</c>）── 自動保管に任せると、
    /// 「Cookie が付かないと断られること」を確かめる要求にまで勝手に付く。
    /// リダイレクトも追わない（302 そのものが検証対象）。
    /// </summary>
    private static HttpClient CreateClient(int port) =>
        new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = RequestTimeout,
        };

    private static HttpRequestMessage Post(string path) => new(HttpMethod.Post, path);

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    // ---- 読み取り（認証不要） ----

    /// <summary>
    /// 読み取りの経路が、<c>status</c> コマンドと同じレコーダーを同じ順で返すこと。
    ///
    /// <para>
    /// <b>CLI と突き合わせるのが要点。</b> どちらも同じ
    /// <c>RecorderControlService.GetStatusAsync</c> を通っているはずで、
    /// ずれたら「HTTP 側だけ別の道を通り始めた」ことになる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReadEndpoints_ReportTheSameRecordersAsTheStatusCommand()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string[] fromCli = [.. instance.RunExpecting(0, "status").StdOut
            .Split('\n').Select(l => l.TrimEnd('\r')).Where(l => 0 < l.Length)
            .Select(l => l.Split('\t')[0])];
        Assert.Equal(["R1", "R2"], fromCli);

        using var response = await client.GetAsync("api/recorders", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = await ReadJsonAsync(response);
        var recorders = body.RootElement.GetProperty("recorders");
        Assert.Equal(fromCli, recorders.EnumerateArray().Select(r => r.GetProperty("name").GetString()!));

        // 8 項目が揃っていること（status の 8 列と 1:1）。1 つでも欠けると、
        // 「見えていないだけ」を「起きていない」と読む利用者が出る。
        foreach (string field in new[]
        {
            "name", "isInitialized", "isRecording", "isAwaitingRecoveryResume",
            "lastFilename", "continuousState", "continuousLastFilename", "lastError",
        })
        {
            Assert.True(recorders[0].TryGetProperty(field, out _), $"recorders[0] に '{field}' がありません。");
        }

        foreach (string flag in new[] { "canStartAll", "canStopAll", "isIdleAll" })
            Assert.True(body.RootElement.TryGetProperty(flag, out _), $"応答に '{flag}' がありません。");

        // 個別の取得。数値はインデックス、それ以外は名前（CLI の対象解決と同じ規則）。
        foreach (string id in new[] { "0", "R1" })
        {
            using var one = await client.GetAsync($"api/recorders/{id}", Ct);
            Assert.Equal(HttpStatusCode.OK, one.StatusCode);
            using var oneBody = await ReadJsonAsync(one);
            Assert.Equal("R1", oneBody.RootElement.GetProperty("name").GetString());
        }

        using var missing = await client.GetAsync("api/recorders/99", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var missingBody = await ReadJsonAsync(missing);
        // 13 = CLI の「対象のレコーダーが見つからない」。番号は CLI と共通。
        Assert.Equal(13, missingBody.RootElement.GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// 設定の読み取りが<b>許可リストの中だけ</b>を返すこと。
    ///
    /// <para>
    /// <b>アクセストークンが出ないことが最重要。</b> 読み取りは認証不要なので、
    /// ここに漏れると「読み取りは誰でも・書き込みはトークン」という分け方そのものが崩れる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSettingsEndpoint_HidesTheTokenAndThePaths()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using var response = await client.GetAsync("api/settings", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = await ReadJsonAsync(response);
        string[] keys = [.. body.RootElement.EnumerateObject().Select(p => p.Name)];
        output.WriteLine(string.Join(", ", keys));

        Assert.DoesNotContain("RemoteControlAccessToken", keys);
        Assert.DoesNotContain("OutputDirectory", keys);
        Assert.DoesNotContain("Recorders", keys);
        Assert.Contains("RecordingRetentionDays", keys);
    }

    // ---- 書き込み（トークンとクライアントヘッダーが要る） ----

    /// <summary>
    /// <c>POST /api/ping</c> の認証。<b>判定順は ① 資格（Bearer か Cookie）② クライアントヘッダー</b> ──
    /// 資格が無ければ 401、資格はあるのにヘッダーが無ければ 403。ヘッダーの検査が
    /// CSRF 対策の本体である（他所のページが仕込んだフォーム送信は Cookie を運べても
    /// カスタムヘッダーを付けられない）。
    ///
    /// <para>
    /// 併せて <c>remote.auth fail</c> の間引き（1 分に 1 行）も見る ── 失敗のたびに
    /// 書くと、総当たりが activity.log を数分で使い切って他の記録を押し流せてしまう。
    /// </para>
    /// </summary>
    [Fact]
    public async Task WriteRequests_NeedBothTheTokenAndTheClientHeader()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        // ① 何も付けない → 401（資格を先に見るので「認証情報が無い」で断る）
        using (var response = await client.SendAsync(Post("api/ping"), Ct))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // ①' ヘッダーはあるが認証情報が無い → 401（未認証）
        using (var request = Post("api/ping"))
        {
            request.Headers.Add("X-PRApp-Client", "1");
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // ② 正しい Bearer だがクライアントヘッダーが無い → 403（資格は正しいので 401 にはしない）
        using (var request = Post("api/ping"))
        {
            request.Headers.Add("Authorization", "Bearer " + Token);
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // ③ 誤った Bearer ＋ ヘッダーあり → 401
        using (var request = Post("api/ping"))
        {
            request.Headers.Add("Authorization", "Bearer " + WrongToken);
            request.Headers.Add("X-PRApp-Client", "1");
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // ④ 正しい Bearer ＋ ヘッダーあり → 200
        using (var request = Post("api/ping"))
        {
            request.Headers.Add("Authorization", "Bearer " + Token);
            request.Headers.Add("X-PRApp-Client", "1");
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await ReadJsonAsync(response);
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        }

        // 断られたのは 4 回、そのうち remote.auth fail を書く経路（401）は 3 回。
        // 記録は 1 行だけ ── 間引きが効いている。403 は資格が正しいので数えない
        //（数えると「資格を当てにきている」ものが薄まる）。
        var failures = ActivityLogFile.Events(instance.ReadActivityLog(), "remote.auth fail");
        Assert.True(failures.Count == 1,
            $"remote.auth fail が {failures.Count} 行あります（間引きが効いていれば 1 行）:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures));

        // トークンが記録に残っていないこと（activity.log は貼り付けて共有される）。
        Assert.DoesNotContain(Token, string.Join('\n', instance.ReadActivityLog()), StringComparison.Ordinal);
    }

    /// <summary>
    /// ブラウザ向けの入口 <c>GET /?token=</c>。正しければセッション Cookie を配って
    /// <c>302 /</c>（＝アドレスバーと履歴からトークンを落とす）、誤りなら 401。
    /// </summary>
    [Fact]
    public async Task TheBrowserEntryPoint_IssuesASessionCookieOnlyForTheRightToken()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var wrong = await client.GetAsync("?token=" + WrongToken, Ct))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        string cookie;
        using (var right = await client.GetAsync("?token=" + Token, Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
            Assert.Equal("/", right.Headers.Location?.ToString());

            string setCookie = Assert.Single(right.Headers.GetValues("Set-Cookie"));
            output.WriteLine(setCookie);
            Assert.Contains("prapp_session=", setCookie, StringComparison.Ordinal);
            Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);

            cookie = setCookie.Split(';')[0];
        }

        // その Cookie ＋ クライアントヘッダーで書き込みが通ること。
        using (var request = Post("api/ping"))
        {
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("X-PRApp-Client", "1");
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // トークンの付かないルートは仮の本文（波 5 で Web UI に置き換わる）。
        using (var root = await client.GetAsync("/", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            Assert.Contains("ProcessRecorderApp", await root.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // 未知の経路は 404（本文は HTTP 層の失敗と同じ形）。
        using (var unknown = await client.GetAsync("api/there-is-no-such-thing", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            using var body = await ReadJsonAsync(unknown);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
        }
    }

    // ---- SSE ----

    /// <summary>
    /// <c>GET /api/events</c> が、接続直後に 1 件と、以後の変化を push すること。
    ///
    /// <para>
    /// <b>変化は CLI で起こす。</b> 同じ状態を 2 つの経路（CLI が動かし、HTTP が見る）で
    /// 突き合わせられるのはここだけで、「購読が張られていない」という失敗モードは
    /// ポーリングでは決して見えない（読むたびに最新が返るため）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheEventStream_SendsTheStateAndPushesChanges()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using var response = await client.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);

        var first = await ReadEventAsync(reader, "state", TimeSpan.FromSeconds(5));
        output.WriteLine(first);
        Assert.Contains("\"recorders\"", first, StringComparison.Ordinal);

        instance.RunExpecting(0, "start-recording-all");
        string started = await ReadUntilAsync(reader, s => IsRecordingAny(s), TimeSpan.FromSeconds(10));
        output.WriteLine(started);

        instance.RunExpecting(0, "stop-recording-all");
        string stopped = await ReadUntilAsync(reader, s => !IsRecordingAny(s), TimeSpan.FromSeconds(10));
        output.WriteLine(stopped);
    }

    private static bool IsRecordingAny(string stateJson)
    {
        using var document = JsonDocument.Parse(stateJson);
        return document.RootElement.GetProperty("recorders").EnumerateArray()
            .Any(r => r.GetProperty("isRecording").GetBoolean());
    }

    /// <summary>次に届く <paramref name="eventName"/> のデータ部を返す。</summary>
    private static async Task<string> ReadEventAsync(StreamReader reader, string eventName, TimeSpan budget)
    {
        var deadline = Stopwatch.StartNew();
        string? current = null;

        while (deadline.Elapsed < budget)
        {
            var line = await reader.ReadLineAsync(Ct).AsTask().WaitAsync(budget - deadline.Elapsed, Ct);
            if (line is null)
                break;

            if (line.StartsWith("event: ", StringComparison.Ordinal))
                current = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && current == eventName)
                return line["data: ".Length..];
        }

        Assert.Fail($"SSE の '{eventName}' が {budget.TotalSeconds:F0} 秒以内に届きませんでした。");
        return "";
    }

    /// <summary><paramref name="accept"/> を満たす <c>state</c> が来るまで読み続ける。</summary>
    private static async Task<string> ReadUntilAsync(
        StreamReader reader, Func<string, bool> accept, TimeSpan budget)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < budget)
        {
            string data = await ReadEventAsync(reader, "state", budget - deadline.Elapsed);
            if (accept(data))
                return data;
        }

        Assert.Fail($"期待した state が {budget.TotalSeconds:F0} 秒以内に届きませんでした。");
        return "";
    }

    // ---- 正常終了 ----

    /// <summary>
    /// ウィンドウを閉じて正常終了したとき、サーバーが止まったことが記録に残ること。
    ///
    /// <para>
    /// <b>ここでしか確かめられない。</b> <c>Destroying</c> は Ctrl+閉じる かトレイの
    /// 「終了」でしか発火せず、CLI からは到達できない ── 停止を怠っても
    /// プロセスが消える以上ポートは解放されるので、<b>症状が出ない退行</b>になる。
    /// </para>
    /// </summary>
    [Fact]
    public void ClosingTheWindow_StopsTheServer()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        output.WriteLine($"port={port}");

        using var ui = AppUi.Activate(instance);
        ui.CloseWindow(holdControl: true);

        Assert.True(ui.WaitForProcessExit(TimeSpan.FromSeconds(420)),
            "Ctrl+閉じる でプロセスが終了しませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        var log = instance.ReadActivityLog();
        Assert.True(0 < ActivityLogFile.Events(log, "remote.stop").Count,
            "正常終了なのに remote.stop がありません（RemoteControlService.Dispose が呼ばれていない）。"
            + Environment.NewLine + instance.DiagnosticDump());
    }

    /// <summary>
    /// <b>SSE を開いたまま</b>ウィンドウを閉じても、プロセスが終わり <c>remote.stop</c> が残ること。
    ///
    /// <para>
    /// <b>開いた接続があって初めて停止が締切に掛かる。</b> Kestrel の停止は開いている
    /// 接続の排出を待つので、記録を排出の後ろに置くと「終了はしたのに記録だけ無い」に
    /// なりうる ── 接続が 1 本も無ければ停止は即座に終わり、この失敗モードは再現しない。
    /// 上の <see cref="ClosingTheWindow_StopsTheServer"/> では見えない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ClosingTheWindow_StopsTheServer_WithAnOpenEventStream()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        output.WriteLine($"port={port}");

        using var client = CreateClient(port);
        using var response = await client.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);
        // 1 件だけ読んで、本当に確立した接続であることを確かめる（応答ヘッダーが
        // 返っただけでは排出待ちに掛かる保証が無い）。以後は読まずに開けたままにする。
        output.WriteLine(await ReadEventAsync(reader, "state", TimeSpan.FromSeconds(5)));

        using var ui = AppUi.Activate(instance);
        ui.CloseWindow(holdControl: true);

        Assert.True(ui.WaitForProcessExit(TimeSpan.FromSeconds(420)),
            "SSE を開いたまま Ctrl+閉じる を行うとプロセスが終了しませんでした。"
            + Environment.NewLine + instance.DiagnosticDump());

        var log = instance.ReadActivityLog();
        Assert.True(0 < ActivityLogFile.Events(log, "remote.stop").Count,
            "SSE を開いたままだと remote.stop が残りません（記録が排出待ちの後ろにある）。"
            + Environment.NewLine + instance.DiagnosticDump());
    }
}
