using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
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

    /// <summary>役割ごとの利用者名とパスワード（ハッシュは <see cref="RemoteUserSpec"/> の定数）。</summary>
    private const string ViewerUser = "viewer";

    /// <inheritdoc cref="ViewerUser"/>
    private const string ViewerPassword = "pw-viewer";

    /// <inheritdoc cref="ViewerUser"/>
    private const string OperatorUser = "operator";

    /// <inheritdoc cref="ViewerUser"/>
    private const string OperatorPassword = "pw-operator";

    /// <inheritdoc cref="ViewerUser"/>
    private const string AdminUser = "admin";

    /// <inheritdoc cref="ViewerUser"/>
    private const string AdminPassword = "pw-admin";

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

    /// <summary>リモート操作を有効にした settings.json（レコーダーは呼び出し側が足す）。</summary>
    private static SettingsFile RemoteBase()
    {
        return new SettingsFile
        {
            RemoteControlEnabled = true,
            // **127.0.0.1 に固定する。** 0.0.0.0（製品の既定）で待ち受けると、
            // 開発機や CI ランナーの LAN から到達できてしまう。
            RemoteControlBindAddress = "127.0.0.1",
            RemoteControlPort = 0,
            RemoteControlAccessToken = Token,
            // **既存のケースの前提を保つ。** 波 3 で読み取りにも役割が要るように
            // なったので、ここでゲスト読み取りを明示して「未認証の GET は 200」を
            // 従来どおりにする ── 認証そのものを見るケースは RemoteUserSettings()
            // で自分の settings を組み立てる。
            RemoteControlAllowGuestRead = true,
        };
    }

    /// <summary>ログインを見るケースが使う構成（利用者 3 人・レコーダー 2 台）。</summary>
    /// <param name="allowGuestRead">名乗っていない相手に読み取りを許すか。</param>
    private static SettingsFile RemoteUserSettings(bool allowGuestRead)
    {
        var settings = RemoteBase();
        settings.RemoteControlAllowGuestRead = allowGuestRead;
        settings.RemoteUsers.Add(new RemoteUserSpec(ViewerUser, RemoteUserSpec.ViewerPasswordHash, "Viewer"));
        settings.RemoteUsers.Add(new RemoteUserSpec(OperatorUser, RemoteUserSpec.OperatorPasswordHash, "Operator"));
        settings.RemoteUsers.Add(new RemoteUserSpec(AdminUser, RemoteUserSpec.AdminPasswordHash, "Admin"));
        settings.AddRecorder("R1");
        settings.AddRecorder("R2");
        return settings;
    }

    /// <summary>読み取り系が使う既定の構成（レコーダー 2 台）。</summary>
    private static SettingsFile RemoteSettings()
    {
        var settings = RemoteBase();
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
        // **利用者が実際に居る構成で見る。** 空の一覧では「落としている」のか
        // 「そもそも何も無い」のかが区別できない。
        using var instance = AppInstance.Create(app, RemoteUserSettings(allowGuestRead: true));
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using var response = await client.GetAsync("api/settings", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string text = await response.Content.ReadAsStringAsync(Ct);
        using var body = JsonDocument.Parse(text);
        string[] keys = [.. body.RootElement.EnumerateObject().Select(p => p.Name)];
        output.WriteLine(string.Join(", ", keys));

        Assert.DoesNotContain("RemoteControlAccessToken", keys);
        Assert.DoesNotContain("OutputDirectory", keys);
        Assert.DoesNotContain("Recorders", keys);
        // パスワードのハッシュは Viewer から見えてはいけない（総当たりの材料になる）。
        Assert.DoesNotContain("RemoteUsers", keys);
        Assert.DoesNotContain("RemoteUserList", keys);
        Assert.DoesNotContain(RemoteUserSpec.AdminPasswordHash, text, StringComparison.Ordinal);
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

        // トークンの付かないルートは Web UI（index.html）。
        // 資産そのものの検証は RecordingDeliveryTests の担当。
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

    /// <summary>
    /// <b>設定画面の「…」でトークンを作り直すと、その場で効くこと（L3）。</b>
    /// <c>RemoteControlAccessToken</c> は <c>[ReadOnly(true)]</c> ＋ <c>[ValueBuilder]</c> ＝
    /// 「直接は編集できないが、ビルダーでなら変更できる」行なので、
    /// <b>これが唯一のトークン変更手段</b>である。
    ///
    /// <para>
    /// <b>欄の表示が変わったことでは足りない。</b> ビルダーの結果を対象へ書かずに
    /// 表示値だけ更新しても画面上は同じに見え、<c>settings.json</c> と待ち受け側は
    /// 古いトークンのまま残る ── 古い資格が<b>通らなくなること</b>まで見る。
    /// </para>
    /// </summary>
    [Fact]
    public async Task RegeneratingTheTokenFromTheSettingsScreen_InvalidatesTheOldToken()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var response = await SendAsync(client, HttpMethod.Post, "api/ping"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        }

        using var ui = AppUi.Activate(instance);
        ui.SwitchTo(UiSection.Settings);
        ui.WaitForPropertyRow("RemoteControlAccessToken");
        Assert.Equal(Token, ui.GetPropertyText("RemoteControlAccessToken"));

        ui.Click("RemoteControlAccessToken.Build");

        string shown = ui.GetPropertyText("RemoteControlAccessToken");
        var deadline = Stopwatch.StartNew();
        while (shown == Token && deadline.Elapsed < DebounceMargin)
        {
            await Task.Delay(200, Ct);
            shown = ui.GetPropertyText("RemoteControlAccessToken");
        }
        Assert.NotEqual(Token, shown);

        // トークンの変更は待ち受けの作り直しを起こす（`RemoteControlService` が購読している）。
        // `RemoteControlPort=0` なのでポートも変わる ── 2 本目の `remote.start` を待つ。
        int newPort = WaitForRestartedPort(instance, port);
        using var restarted = CreateClient(newPort);

        // 古いトークンはもう通らない（＝表示だけでなく AppSettings が変わっている）。
        using (var response = await SendAsync(restarted, HttpMethod.Post, "api/ping"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // 新しいトークンで通ること（＝差し替わっただけで、締め出しではない）。
        using (var request = Post("api/ping"))
        {
            request.Headers.Add("Authorization", "Bearer " + shown);
            request.Headers.Add("X-PRApp-Client", "1");
            using var response = await restarted.SendAsync(request, Ct);
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        }
    }

    /// <summary>作り直された待ち受けのポートを <c>activity.log</c> の 2 本目以降から読む。</summary>
    private int WaitForRestartedPort(AppInstance instance, int previousPort)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartBudget)
        {
            foreach (string line in ActivityLogFile.Events(instance.ReadActivityLog(), "remote.start"))
            {
                var match = BindPattern.Match(ActivityLogFile.DetailOf(line));
                if (match.Success && int.Parse(match.Groups[2].Value) != previousPort)
                {
                    output.WriteLine(line);
                    return int.Parse(match.Groups[2].Value);
                }
            }
            Thread.Sleep(200);
        }

        Assert.Fail(
            $"待ち受けの作り直し（2 本目の remote.start）が {StartBudget.TotalSeconds:F0} 秒以内に"
            + "現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());
        return 0;
    }

    // ---- 操作・変数・設定の書き込み ----

    /// <summary>デバウンス保存（1 秒）が確実に走り終わるまでの待ち。</summary>
    private static readonly TimeSpan DebounceMargin = TimeSpan.FromSeconds(3);

    /// <summary>録画が実際に流れるだけの長さ。</summary>
    private static readonly TimeSpan RecordingWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 書き込み要求。<b>トークンとクライアントヘッダーの両方</b>を付ける
    /// （片方だけで通らないことは <c>WriteRequests_NeedBothTheTokenAndTheClientHeader</c> の担当）。
    /// </summary>
    private static HttpRequestMessage Authorized(HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Authorization", "Bearer " + Token);
        request.Headers.Add("X-PRApp-Client", "1");
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? json = null)
        => client.SendAsync(Authorized(method, path, json), Ct);

    private static readonly HttpMethod Patch = HttpMethod.Patch;

    /// <summary>応答を本文つきで読む（失敗したときに何が返ったのかが分かるように）。</summary>
    private async Task<JsonDocument> ExpectAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        string text = await response.Content.ReadAsStringAsync(Ct);
        output.WriteLine($"{(int)response.StatusCode} {text}");
        Assert.Equal(expected, response.StatusCode);
        return JsonDocument.Parse(text);
    }

    private static string[] StringsOf(JsonElement array)
        => [.. array.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];

    /// <summary>
    /// <b>HTTP から開始して停止すると、使える MP4 が残ること。</b>
    ///
    /// <para>
    /// <b>ファイル名は応答が返した値をそのまま使う。</b> ディレクトリを走査して 1 件を拾うと、
    /// 「応答が返したパスが本当にその録画か」を一度も確かめないまま緑になる
    /// ── 呼び出し側（バッチ・Web UI）が使うのは応答の値だけである。
    /// </para>
    /// </summary>
    [Fact]
    public async Task StartAndStop_OverHttp_LeaveAUsableRecording()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string filename;
        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/0/start"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal("R1", body.RootElement.GetProperty("name").GetString());
            filename = body.RootElement.GetProperty("filename").GetString() ?? string.Empty;
            Assert.False(string.IsNullOrEmpty(filename), "開始の応答にファイル名がありません。");
        }

        Thread.Sleep(RecordingWindow);

        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/0/stop"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            // 開始と停止が同じファイルを指していること（違えば、どちらかが前回の録画を指している）。
            Assert.Equal(filename, body.RootElement.GetProperty("filename").GetString());
        }

        RecordedMp4.AssertUsable(filename, instance, output);
    }

    /// <summary>
    /// 同じ往復を <c>D3d12</c> 経路で。<b>コンバーターを挟む構成でも壊れないこと</b>
    /// ── 開始・停止の配線は同じでも、パイプラインの組み立てが別の枝を通る。
    /// </summary>
    [Fact]
    public async Task StartAndStop_OverHttp_WorkOnTheD3d12Path()
    {
        var settings = RemoteBase();
        var recorder = settings.AddRecorder("R1");
        recorder.Type = EventRecordingType.D3d12;
        recorder.SrcPipeline =
            "d3d12testsrc is-live=true do-timestamp=true ! "
            + "video/x-raw(memory:D3D12Memory), format=NV12, width=640, height=480, framerate=15/1";

        using var instance = AppInstance.Create(app, settings);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string filename;
        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/R1/start"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            filename = body.RootElement.GetProperty("filename").GetString() ?? string.Empty;
        }

        Thread.Sleep(RecordingWindow);

        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/R1/stop"))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        RecordedMp4.AssertUsable(filename, instance, output);
    }

    /// <summary>
    /// <b>断り方が CLI と同じ番号になること。</b> 対象が無ければ 13（404）、
    /// その状態では行えなければ 14（409）── HTTP のステータスだけでは
    /// 「名前が違う」と「今はできない」を区別できない。
    /// </summary>
    [Fact]
    public async Task RejectedOperations_CarryTheSameExitCodesAsTheCli()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/99/start"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.NotFound);
            Assert.Equal(13, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        // 録画していないものは止められない。
        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/0/stop"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.Conflict);
            Assert.Equal(14, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/stop-all"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.Conflict);
            Assert.Equal(14, body.RootElement.GetProperty("exitCode").GetInt32());
        }
    }

    /// <summary>
    /// <b>使えない成果物は 422 で返し、ファイルのパスを添えること。</b>
    ///
    /// <para>
    /// 症状は設定だけで作れる（<c>num-buffers</c> でソースを終わらせる ──
    /// <c>StopOutcomeTests</c> と同じ手）。停止処理そのものは綺麗に終わるので、
    /// この信号が無いと <b>200 ＋ 空のファイル</b>になる。
    /// </para>
    /// <para>
    /// <b>17（未確定）は再現していない。</b> 排出の打ち切りを決定論的に踏ませる手段が無く、
    /// <c>StopOutcomeTests</c> でも E2E には置いていない（判定規則は L1、CLI への配線は
    /// あちらの <c>-all</c> のテストが見ている）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARecordingThatWroteNoFrames_IsReportedAsUnprocessableWithItsFilename()
    {
        var settings = RemoteBase();
        settings.AddRecorder("R1").AsSourceThatEnds();

        using var instance = AppInstance.Create(app, settings);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        // 前提: 初期化は成功していること（ここが fail だと別のテストになっている）。
        Assert.NotEmpty(ActivityLogFile.Events(instance.ReadActivityLog(), "recorder.init ok"));

        Assert.True(instance.WaitForActivityLogEvent("recorder.eos", TimeSpan.FromSeconds(60)),
            "ソースが EOS に達しませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/0/start"))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        using var stop = await SendAsync(client, HttpMethod.Post, "api/recorders/0/stop");
        using var stopBody = await ExpectAsync(stop, HttpStatusCode.UnprocessableEntity);

        Assert.Equal(16, stopBody.RootElement.GetProperty("exitCode").GetInt32());

        // **パスは失敗の応答にも載る。** 呼び出し側はそれで後始末や救済ができる。
        string? filename = stopBody.RootElement.GetProperty("filename").GetString();
        Assert.False(string.IsNullOrEmpty(filename), "422 の本文にファイル名がありません。");
    }

    /// <summary>
    /// <b>認証を通らなかった開始要求が、実際には何も始めていないこと。</b>
    /// 401 を返しながら副作用だけ起きていれば、認証は何も守っていない。
    /// </summary>
    [Fact]
    public async Task AnUnauthenticatedStart_DoesNotStartAnything()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var response = await client.SendAsync(Post("api/recorders/0/start"), Ct))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var recorders = await client.GetAsync("api/recorders", Ct);
        using var body = await ExpectAsync(recorders, HttpStatusCode.OK);
        foreach (var recorder in body.RootElement.GetProperty("recorders").EnumerateArray())
            Assert.False(recorder.GetProperty("isRecording").GetBoolean());
    }

    /// <summary>
    /// テンプレート変数の読み書き。<b>値の設定と「保存するか」は別の指定</b>
    /// （CLI の <c>--set</c> と <c>--persist</c> と同じ）で、保存を指定したものだけが
    /// settings.json に落ちる。
    /// </summary>
    [Fact]
    public async Task Variables_CanBeSetAndPersistedOverHttp()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var response = await SendAsync(client, HttpMethod.Put, "api/variables/Site", "{\"value\":\"tokyo\"}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal("Site", body.RootElement.GetProperty("key").GetString());
            Assert.Equal("tokyo", body.RootElement.GetProperty("value").GetString());
            Assert.False(body.RootElement.GetProperty("persistent").GetBoolean());
        }

        using (var response = await client.GetAsync("api/variables", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            var variable = body.RootElement.GetProperty("variables").EnumerateArray()
                .Single(v => v.GetProperty("key").GetString() == "Site");
            Assert.Equal("tokyo", variable.GetProperty("value").GetString());
            Assert.False(variable.GetProperty("persistent").GetBoolean());
        }

        using (var response = await SendAsync(client, HttpMethod.Put, "api/variables/Site", "{\"persist\":true}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            // 値は保ったまま、保存だけが変わること。
            Assert.Equal("tokyo", body.RootElement.GetProperty("value").GetString());
            Assert.True(body.RootElement.GetProperty("persistent").GetBoolean());
        }

        // 未定義のキーに保存だけを指定 → 11（404）。CLI の --persist と同じ番号。
        using (var response = await SendAsync(client, HttpMethod.Put, "api/variables/NoSuch", "{\"persist\":true}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.NotFound);
            Assert.Equal(11, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        // 何も指定していない要求は断る（「何もしない成功」を返さない）。
        using (var response = await SendAsync(client, HttpMethod.Put, "api/variables/Site", "{}"))
            (await ExpectAsync(response, HttpStatusCode.BadRequest)).Dispose();

        Thread.Sleep(DebounceMargin);
        instance.KillWorkers();

        string saved = File.ReadAllText(instance.SettingsPath);
        output.WriteLine(saved);
        using var document = JsonDocument.Parse(saved);
        var variables = document.RootElement.GetProperty("TemplateVariables");
        Assert.Equal("tokyo", variables.GetProperty("Site").GetString());
    }

    /// <summary>
    /// レコーダー設定の読み取り。<b>値だけでなく項目の説明も返す</b> ──
    /// 画面（波 6）は型・選択肢・範囲を知らないと編集欄を組めない。
    /// </summary>
    [Fact]
    public async Task RecorderSettings_ExposeTheValuesAndHowToEditThem()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        // **GET は認証不要**（読み取りの規則はここでも同じ）。
        using var response = await client.GetAsync("api/recorders/0/settings", Ct);
        using var body = await ExpectAsync(response, HttpStatusCode.OK);

        // 値のキーは settings.json と同じ PascalCase。
        Assert.Equal(3000, body.RootElement.GetProperty("values").GetProperty("BufferDuration").GetInt32());

        var properties = body.RootElement.GetProperty("properties").EnumerateArray().ToArray();
        Assert.Contains(properties, p => p.GetProperty("name").GetString() == "Name");

        var type = properties.Single(p => p.GetProperty("name").GetString() == "Type");
        Assert.Equal("enum", type.GetProperty("type").GetString());
        Assert.Contains("D3d12", StringsOf(type.GetProperty("choices")));

        var buffer = properties.Single(p => p.GetProperty("name").GetString() == "BufferDuration");
        Assert.Equal("int", buffer.GetProperty("type").GetString());
        Assert.Equal(0, buffer.GetProperty("min").GetInt64());
        Assert.Equal(600_000, buffer.GetProperty("max").GetInt64());
    }

    /// <summary>
    /// レコーダー設定の部分更新。<b>断り方・丸め・「再初期化が要る」の 3 つ</b>を見る。
    ///
    /// <para>
    /// <b>丸めは 200 のまま起きる。</b> 黙って丸めると、呼び出し側は
    /// 「指定が効かなかった」ことに気付けない ── だから応答に載せる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task PatchingRecorderSettings_ReportsRejectionsClampingAndReinitialization()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var response = await SendAsync(client, Patch, "api/recorders/0/settings", "{\"Nope\":1}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
            // どのキーが駄目だったのかが分かること。
            Assert.Contains("Nope", body.RootElement.GetProperty("error").GetString() ?? "");
        }

        using (var response = await SendAsync(
            client, Patch, "api/recorders/0/settings", "{\"BufferDuration\":\"abc\"}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("BufferDuration", body.RootElement.GetProperty("error").GetString() ?? "");
        }

        using (var response = await SendAsync(
            client, Patch, "api/recorders/0/settings", "{\"BufferDuration\":999999999}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Contains("BufferDuration", StringsOf(body.RootElement.GetProperty("applied")));
            Assert.Contains("BufferDuration", StringsOf(body.RootElement.GetProperty("clamped")));
        }

        using (var response = await client.GetAsync("api/recorders/0/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal(600_000,
                body.RootElement.GetProperty("values").GetProperty("BufferDuration").GetInt32());
        }

        using (var response = await SendAsync(client, Patch, "api/recorders/0/settings", "{\"Type\":\"System\"}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Contains("Type", StringsOf(body.RootElement.GetProperty("requiresReinitialize")));
        }
    }

    /// <summary>
    /// <b>レコーダー設定にも拒否キーがあること。</b> <c>SrcPipeline</c> は実行内容そのもので、
    /// ファイル名テンプレート 2 つは絶対パスを許すので <c>OutputDirectory</c> の外へ書ける
    /// （<c>RemoteApiRules.RemoteDeniedRecorderSettings</c>）。
    ///
    /// <para>
    /// 断り方は<b>アプリ設定の拒否と同じ形</b>でなければならない ── 終了コードも本文も
    /// 呼び出し側が同じ経路で読むものであり、面ごとに違うと「どちらの言い回しも扱う」
    /// 分岐が呼び出し側に増える。
    /// </para>
    /// </summary>
    [Fact]
    public async Task PatchingDeniedRecorderSettings_IsRejectedLikeTheAppSettings()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string pipelineBefore;
        string? encodingBefore;
        using (var response = await client.GetAsync("api/recorders/0/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            pipelineBefore = body.RootElement.GetProperty("values").GetProperty("SrcPipeline").GetString() ?? "";
            var encoding = body.RootElement.GetProperty("values").GetProperty("EncodingProperties");
            encodingBefore = encoding.ValueKind == JsonValueKind.Null ? null : encoding.GetString();
        }

        // アプリ設定側の拒否（既に固定されている形）を取って、文言の型を比べる。
        string appDenied;
        using (var response = await SendAsync(client, Patch, "api/settings", "{\"OutputDirectory\":\"C:\\\\x\"}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
            appDenied = body.RootElement.GetProperty("error").GetString() ?? "";
        }

        using (var response = await SendAsync(
            client, Patch, "api/recorders/0/settings", "{\"SrcPipeline\":\"videotestsrc\"}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal(
                appDenied.Replace("OutputDirectory", "SrcPipeline", StringComparison.Ordinal),
                body.RootElement.GetProperty("error").GetString());
        }

        // エンコーダーの指定も同じく拒否される ── 値は検証されずパイプライン記述へ
        // 生で入るので、`SrcPipeline` と同じだけの実行能力がある。
        using (var response = await SendAsync(
            client, Patch, "api/recorders/0/settings",
            "{\"EncodingProperties\":\"x264enc ! tee name=k ! queue ! fakesink k. ! queue\"}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal(
                appDenied.Replace("OutputDirectory", "EncodingProperties", StringComparison.Ordinal),
                body.RootElement.GetProperty("error").GetString());
        }

        // 断った要求は書いていないこと。
        using (var response = await client.GetAsync("api/recorders/0/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal(pipelineBefore,
                body.RootElement.GetProperty("values").GetProperty("SrcPipeline").GetString());
            Assert.Equal(encodingBefore,
                body.RootElement.GetProperty("values").GetProperty("EncodingProperties").GetString());
        }
    }

    /// <summary>
    /// <b>録画中の PATCH がパイプラインを落とさないこと。</b>
    ///
    /// <para>
    /// 設定オブジェクトを差し替えると（<c>Recorders[i]</c> の入れ替え）録画エンジンが
    /// 作り直され、走っている録画が壊れる ── ここで見ているのは「壊れていない」ことで、
    /// 判定は<b>成果物が使えるかどうか</b>で行う。
    /// </para>
    /// </summary>
    [Fact]
    public async Task PatchingWhileRecording_DoesNotBreakTheRunningRecording()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string filename;
        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/0/start"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            filename = body.RootElement.GetProperty("filename").GetString() ?? string.Empty;
        }

        using (var response = await SendAsync(client, Patch, "api/recorders/0/settings", "{\"BufferDuration\":4000}"))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        Thread.Sleep(RecordingWindow);

        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/0/stop"))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        RecordedMp4.AssertUsable(filename, instance, output);
    }

    /// <summary>
    /// アプリ設定の部分更新。<b>許可していないキーは断る</b> ──
    /// 拒否リストにあるものは「読めない」だけでなく「書けない」ことも確かめる
    /// （<c>OutputDirectory</c> は配信 root そのもので、書ければ任意のディレクトリを晒せる）。
    /// </summary>
    [Fact]
    public async Task PatchingAppSettings_OnlyAcceptsTheEditableKeys()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var response = await SendAsync(client, Patch, "api/settings", "{\"OutputDirectory\":\"C:\\\\x\"}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("OutputDirectory", body.RootElement.GetProperty("error").GetString() ?? "");
        }

        using (var response = await SendAsync(client, Patch, "api/settings", "{\"RecordingRetentionDays\":7}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Contains("RecordingRetentionDays", StringsOf(body.RootElement.GetProperty("applied")));
            // アプリ設定に「初期化をやり直すまで効かない」項目は無い。
            Assert.Empty(body.RootElement.GetProperty("requiresReinitialize").EnumerateArray());
        }

        using (var response = await client.GetAsync("api/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal(7, body.RootElement.GetProperty("RecordingRetentionDays").GetInt32());
        }

        // **項目に付いた変換器も効くこと。** 列挙は settings.json と同じ文字列で書く。
        // 型ではなくプロパティに変換器が付いている項目なので、項目ごとの型情報を
        // そのまま引くだけでは読めない（数値だけを受ける形になる）。
        using (var response = await SendAsync(client, Patch, "api/settings", "{\"FramingGrid\":\"Thirds\"}"))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        using (var response = await client.GetAsync("api/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal("Thirds", body.RootElement.GetProperty("FramingGrid").GetString());
        }
    }

    /// <summary>
    /// <b>断ったアプリ設定の PATCH が、1 つも書いていないこと。</b>
    ///
    /// <para>
    /// 許可外のキーは、同じ要求に許可されたキーが並んでいても<b>全体を断る</b> ──
    /// 途中まで書いてから断ると、呼び出し側は何が反映されたのか分からない。
    /// アクセストークンは特に危ない（書ければ、以後の認証を要求側が決められる）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task RejectedAppSettingsPatch_WritesNothing()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        int retentionBefore;
        using (var response = await client.GetAsync("api/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            retentionBefore = body.RootElement.GetProperty("RecordingRetentionDays").GetInt32();
        }

        using (var response = await SendAsync(
            client, Patch, "api/settings", "{\"RemoteControlAccessToken\":\"x\"}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        // 元のトークンが今も通ること（＝断った要求はトークンを書き換えていない）。
        using (var response = await SendAsync(client, HttpMethod.Post, "api/ping"))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        using (var response = await SendAsync(
            client, Patch, "api/settings", "{\"RecordingRetentionDays\":9,\"OutputDirectory\":\"C:\\\\x\"}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        // 同じ要求に並んでいた許可されたキーも書かれていないこと。
        using (var response = await client.GetAsync("api/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal(retentionBefore, body.RootElement.GetProperty("RecordingRetentionDays").GetInt32());
        }
    }

    /// <summary>
    /// <b>受け付けられない本文を断ること。</b> オブジェクトでない JSON（空・文字列・配列）と、
    /// 非 null の項目への <c>null</c>。
    ///
    /// <para>
    /// <c>null</c> は「その項目を空へ戻す」意味で、<b>null 許容の項目にだけ</b>通る。
    /// 非 null の項目へ通すと settings.json に null が落ち、
    /// 以後の読み込みと録画の開始が壊れる ── 200 を返した後に効いてくるので、
    /// 応答だけを見ていては気付けない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task MalformedPatchBodies_AreRejected()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        foreach (string json in new[] { "", "abc", "[1]" })
        {
            using var response = await SendAsync(client, Patch, "api/recorders/0/settings", json);
            using var body = await ExpectAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        using (var response = await SendAsync(
            client, Patch, "api/recorders/0/settings", "{\"Name\":null}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        // 断った要求が値を消していないこと。
        using (var response = await client.GetAsync("api/recorders/0/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.False(string.IsNullOrEmpty(
                body.RootElement.GetProperty("values").GetProperty("Name").GetString()));
        }
    }

    /// <summary>
    /// <b><c>-all</c> の操作が 1 台ずつの結果を返すこと。</b>
    /// 名前とファイル名（開始）・終了コード（停止）が台数ぶん返らないと、
    /// 呼び出し側は「どれが録れたのか」を知る手が無い。
    /// </summary>
    [Fact]
    public async Task StartAllAndStopAll_ReportEveryRecorder()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string[] filenames;
        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/start-all"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Empty(body.RootElement.GetProperty("failed").EnumerateArray());

            var started = body.RootElement.GetProperty("started").EnumerateArray().ToArray();
            Assert.Equal(new[] { "R1", "R2" }, started.Select(s => s.GetProperty("name").GetString() ?? string.Empty));

            filenames = [.. started.Select(s => s.GetProperty("filename").GetString() ?? string.Empty)];
            Assert.All(filenames, f => Assert.False(string.IsNullOrEmpty(f), "開始の応答にファイル名がありません。"));
        }

        Thread.Sleep(RecordingWindow);

        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/stop-all"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            var stopped = body.RootElement.GetProperty("stopped").EnumerateArray().ToArray();

            // 停止は 1 台ずつ番号を返す（0 / 16 / 17）。ここは全部成功している。
            Assert.Equal(new[] { 0, 0 }, stopped.Select(s => s.GetProperty("exitCode").GetInt32()));
            // 開始が返したファイルと同じものを止めていること。
            Assert.Equal(filenames, stopped.Select(s => s.GetProperty("filename").GetString() ?? string.Empty));
        }

        foreach (string filename in filenames)
            RecordedMp4.AssertUsable(filename, instance, output);
    }

    // ---- 役割ベースの認証（ログイン・ゲスト） ----

    /// <summary>Cookie で名乗る要求（書き込みならクライアントヘッダーも要る）。</summary>
    private static HttpRequestMessage AsSession(
        HttpMethod method, string path, string cookie, string? json = null, bool clientHeader = true)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        if (clientHeader)
            request.Headers.Add("X-PRApp-Client", "1");
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>ログインの要求本文。<b>組み立ては 1 か所</b>にして綴りの取り違えを防ぐ。</summary>
    private static HttpRequestMessage LoginRequest(string user, string password, bool clientHeader = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/login");
        if (clientHeader)
            request.Headers.Add("X-PRApp-Client", "1");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["user"] = user, ["password"] = password }),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    /// <summary>名乗って Cookie を持ち帰る（名前と役割が応答に出ることも確かめる）。</summary>
    private async Task<string> LoginAsync(HttpClient client, string user, string password, string expectedRole)
    {
        using var request = LoginRequest(user, password);
        using var response = await client.SendAsync(request, Ct);
        using var body = await ExpectAsync(response, HttpStatusCode.OK);

        Assert.Equal(user, body.RootElement.GetProperty("name").GetString());
        Assert.Equal(expectedRole, body.RootElement.GetProperty("role").GetString());

        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        output.WriteLine(setCookie);
        // 属性は `?token=` の入口と同一（RemoteAuth.SessionCookieOptions が唯一の出所）。
        Assert.Contains("prapp_session=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);

        return setCookie.Split(';')[0];
    }

    /// <summary>
    /// <b>ゲスト読み取りを許していなければ、読み取りにも名乗りが要る。</b>
    ///
    /// <para>
    /// <b>静的資産だけは無認証のまま</b>でなければならない ── ログインの画面そのものが
    /// そこから配られるので、ここを閉じると誰も名乗れない鍵の掛かった箱になる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task WithoutGuestRead_TheApiNeedsASession_ButThePageIsStillServed()
    {
        using var instance = AppInstance.Create(app, RemoteUserSettings(allowGuestRead: false));
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        foreach (string path in new[] { "api/recorders", "api/settings", "api/variables", "api/recordings" })
        {
            using var response = await client.GetAsync(path, Ct);
            using var body = await ExpectAsync(response, HttpStatusCode.Unauthorized);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("authentication required", body.RootElement.GetProperty("error").GetString());
        }

        using (var index = await client.GetAsync("/", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Contains("ProcessRecorderApp", await index.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using (var script = await client.GetAsync("app.js", Ct))
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
    }

    /// <summary>
    /// <b>ゲスト読み取りは読み取りだけ。</b> 「読ませるだけ」のつもりの設定で
    /// 録画を止められては、この設定に意味が無い。
    /// </summary>
    [Fact]
    public async Task WithGuestRead_ReadsPass_ButWritesAreStillRefused()
    {
        using var instance = AppInstance.Create(app, RemoteUserSettings(allowGuestRead: true));
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var response = await client.GetAsync("api/recorders", Ct))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        // 名乗っていない相手は「ゲスト」として自分を読める。
        using (var response = await client.GetAsync("api/me", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal("", body.RootElement.GetProperty("name").GetString());
            Assert.Equal("Viewer", body.RootElement.GetProperty("role").GetString());
            Assert.True(body.RootElement.GetProperty("guest").GetBoolean());
        }

        // 書き込みは、クライアントヘッダーが付いていても 401（資格の判定が先）。
        using (var request = Post("api/recorders/R1/start"))
        {
            request.Headers.Add("X-PRApp-Client", "1");
            using var response = await client.SendAsync(request, Ct);
            using var body = await ExpectAsync(response, HttpStatusCode.Unauthorized);
            Assert.Equal("authentication required", body.RootElement.GetProperty("error").GetString());
        }
    }

    /// <summary>
    /// <b>Viewer は読めるだけ。</b> 開始（Operator）も設定の変更（Admin）も
    /// <c>403 insufficient role</c> で断られる ── 401 ではないのが要点で、
    /// 「名乗りは通っているが権限が足りない」と呼び出し側に伝わる。
    /// </summary>
    [Fact]
    public async Task AViewer_CanRead_ButCannotOperateOrConfigure()
    {
        using var instance = AppInstance.Create(app, RemoteUserSettings(allowGuestRead: false));
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string cookie = await LoginAsync(client, ViewerUser, ViewerPassword, "Viewer");

        // 読み取りは通る（クライアントヘッダーは要らない ── <video> も EventSource も付けられない）。
        using (var request = AsSession(HttpMethod.Get, "api/recorders", cookie, clientHeader: false))
        using (var response = await client.SendAsync(request, Ct))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        using (var request = AsSession(HttpMethod.Get, "api/me", cookie, clientHeader: false))
        using (var response = await client.SendAsync(request, Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal(ViewerUser, body.RootElement.GetProperty("name").GetString());
            Assert.Equal("Viewer", body.RootElement.GetProperty("role").GetString());
            Assert.False(body.RootElement.GetProperty("guest").GetBoolean());
        }

        using (var request = AsSession(HttpMethod.Post, "api/recorders/R1/start", cookie))
        using (var response = await client.SendAsync(request, Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.Forbidden);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("insufficient role", body.RootElement.GetProperty("error").GetString());
        }

        using (var request = AsSession(Patch, "api/settings", cookie, "{\"RecordingRetentionDays\":7}"))
        using (var response = await client.SendAsync(request, Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.Forbidden);
            Assert.Equal("insufficient role", body.RootElement.GetProperty("error").GetString());
        }

        // 断られた 2 回は remote.auth fail に出ない（資格は正しいので、
        // 「当てにきている」ものを見る行が薄まってはいけない）。
        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "remote.auth fail"));
    }

    /// <summary>
    /// <b>Operator は動かせるが、設定は変えられない。</b>
    /// 開始と停止が実際に成果物を残すところまで見る（403 と 200 の差が本物であること）。
    /// </summary>
    [Fact]
    public async Task AnOperator_CanStartAndStop_ButCannotConfigure()
    {
        using var instance = AppInstance.Create(app, RemoteUserSettings(allowGuestRead: false));
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string cookie = await LoginAsync(client, OperatorUser, OperatorPassword, "Operator");

        string filename;
        using (var request = AsSession(HttpMethod.Post, "api/recorders/0/start", cookie))
        using (var response = await client.SendAsync(request, Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            filename = body.RootElement.GetProperty("filename").GetString() ?? "";
            Assert.NotEmpty(filename);
        }

        Thread.Sleep(RecordingWindow);

        using (var request = AsSession(HttpMethod.Post, "api/recorders/0/stop", cookie))
        using (var response = await client.SendAsync(request, Ct))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        RecordedMp4.AssertUsable(filename, instance, output);

        // 変数の書き換えも Operator の範囲。
        using (var request = AsSession(HttpMethod.Put, "api/variables/Site", cookie, "{\"value\":\"lab\"}"))
        using (var response = await client.SendAsync(request, Ct))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        // 設定は Admin の範囲。
        using (var request = AsSession(Patch, "api/settings", cookie, "{\"RecordingRetentionDays\":7}"))
        using (var response = await client.SendAsync(request, Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.Forbidden);
            Assert.Equal("insufficient role", body.RootElement.GetProperty("error").GetString());
        }
    }

    /// <summary>
    /// <b>Admin は設定まで変えられる。</b> 併せて成功のログ
    /// （<c>remote.auth login user=admin role=Admin</c>）が残ることを見る。
    /// </summary>
    [Fact]
    public async Task AnAdmin_CanPatchTheAppSettings()
    {
        using var instance = AppInstance.Create(app, RemoteUserSettings(allowGuestRead: false));
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string cookie = await LoginAsync(client, AdminUser, AdminPassword, "Admin");

        using (var request = AsSession(Patch, "api/settings", cookie, "{\"RecordingRetentionDays\":7}"))
        using (var response = await client.SendAsync(request, Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Contains("RecordingRetentionDays", StringsOf(body.RootElement.GetProperty("applied")));
        }

        using (var request = AsSession(Patch, "api/recorders/0/settings", cookie, "{\"BufferDuration\":1500}"))
        using (var response = await client.SendAsync(request, Ct))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        string login = Assert.Single(ActivityLogFile.Events(instance.ReadActivityLog(), "remote.auth login"));
        output.WriteLine(login);
        Assert.Contains($"user={AdminUser}", login, StringComparison.Ordinal);
        Assert.Contains("role=Admin", login, StringComparison.Ordinal);
        // パスワードは記録に残さない（activity.log は貼り付けて共有される）。
        Assert.DoesNotContain(AdminPassword, string.Join('\n', instance.ReadActivityLog()), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>合わない資格は 401 <c>invalid credentials</c>。</b> 記録は既存の
    /// <c>remote.auth fail</c> が受け持ち、<b>1 分に 1 行へ間引かれる</b>
    /// ── 総当たりで activity.log を使い切らせないため。
    /// </summary>
    [Fact]
    public async Task AFailedLogin_IsRefusedAndRecordedOnce()
    {
        using var instance = AppInstance.Create(app, RemoteUserSettings(allowGuestRead: false));
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        foreach ((string user, string password) in new[]
        {
            (AdminUser, "wrong-password"),
            ("nobody", AdminPassword),
            (AdminUser, ""),
        })
        {
            using var request = LoginRequest(user, password);
            using var response = await client.SendAsync(request, Ct);
            using var body = await ExpectAsync(response, HttpStatusCode.Unauthorized);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("invalid credentials", body.RootElement.GetProperty("error").GetString());
            // 断った要求は Cookie を配らない。
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        var failures = ActivityLogFile.Events(instance.ReadActivityLog(), "remote.auth fail");
        Assert.True(failures.Count == 1,
            $"remote.auth fail が {failures.Count} 行あります（間引きが効いていれば 1 行）:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures));

        // 利用者名もパスワードも記録には出さない。
        string log = string.Join('\n', instance.ReadActivityLog());
        Assert.DoesNotContain(AdminPassword, log, StringComparison.Ordinal);

        // 正しい資格なら通る（上の 3 回が「全部落ちる」実装で緑にならないように）。
        _ = await LoginAsync(client, AdminUser, AdminPassword, "Admin");
    }

    /// <summary>
    /// <b>ログアウトした Cookie は二度と通らない。</b> ブラウザ側で消えるだけでは
    /// 「その値を控えていた相手」に対して何も変わらない ── サーバー側で失効させる。
    /// </summary>
    [Fact]
    public async Task LoggingOut_InvalidatesTheSameCookie()
    {
        using var instance = AppInstance.Create(app, RemoteUserSettings(allowGuestRead: false));
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string cookie = await LoginAsync(client, ViewerUser, ViewerPassword, "Viewer");

        using (var request = AsSession(HttpMethod.Get, "api/recorders", cookie, clientHeader: false))
        using (var response = await client.SendAsync(request, Ct))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        using (var request = AsSession(HttpMethod.Post, "api/logout", cookie))
        using (var response = await client.SendAsync(request, Ct))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        using (var request = AsSession(HttpMethod.Get, "api/recorders", cookie, clientHeader: false))
        using (var response = await client.SendAsync(request, Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.Unauthorized);
            Assert.Equal("authentication required", body.RootElement.GetProperty("error").GetString());
        }

        string logout = Assert.Single(ActivityLogFile.Events(instance.ReadActivityLog(), "remote.auth logout"));
        output.WriteLine(logout);
        Assert.Contains($"user={ViewerUser}", logout, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>ログインにもクライアントヘッダーが要る。</b> CSRF 対策は「Cookie を持っている
    /// 相手」ではなく「他所のページからの送信」を止めるもので、セッションを<b>作る</b>
    /// 要求もその対象に入る。断りは 403 で、<b>資格の照合まで到達しない</b>。
    /// </summary>
    [Fact]
    public async Task LoggingInWithoutTheClientHeader_IsRefusedBeforeTheCredentials()
    {
        using var instance = AppInstance.Create(app, RemoteUserSettings(allowGuestRead: false));
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var request = LoginRequest(AdminUser, AdminPassword, clientHeader: false))
        using (var response = await client.SendAsync(request, Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.Forbidden);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("client header required", body.RootElement.GetProperty("error").GetString());
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        // 記録しない ── ここへ来るのは資格を当てにきているものではない。
        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "remote.auth fail"));
        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "remote.auth login"));
    }

    /// <summary>
    /// <b>既存のアクセストークンは Admin のまま。</b> 役割を足したことで、
    /// これまでトークンで動いていた呼び出し側（バッチ・curl）の意味を変えない。
    /// <c>?token=</c> で配られる Cookie も同じく Admin。
    /// </summary>
    [Fact]
    public async Task TheAccessToken_StillActsAsAnAdmin()
    {
        using var instance = AppInstance.Create(app, RemoteUserSettings(allowGuestRead: false));
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        // Bearer は読み取りにも書き込みにも効く。
        using (var request = Authorized(HttpMethod.Get, "api/recorders"))
        using (var response = await client.SendAsync(request, Ct))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        using (var response = await SendAsync(client, Patch, "api/settings", "{\"RecordingRetentionDays\":7}"))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        using (var request = Authorized(HttpMethod.Get, "api/me"))
        using (var response = await client.SendAsync(request, Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal("token", body.RootElement.GetProperty("name").GetString());
            Assert.Equal("Admin", body.RootElement.GetProperty("role").GetString());
            Assert.False(body.RootElement.GetProperty("guest").GetBoolean());
        }

        string cookie;
        using (var landing = await client.GetAsync("?token=" + Token, Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, landing.StatusCode);
            cookie = Assert.Single(landing.Headers.GetValues("Set-Cookie")).Split(';')[0];
        }

        using (var request = AsSession(Patch, "api/settings", cookie, "{\"RecordingRetentionDays\":9}"))
        using (var response = await client.SendAsync(request, Ct))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();
    }

    // ---- ソース候補とテンプレートからの適用 ----

    /// <summary>
    /// <c>GET /api/sources</c> が<b>カタログと録画種別を配る</b>こと。
    ///
    /// <para>
    /// 種別はメモリ機能から導く（<c>SourcePresetRules.RecordingTypeFor</c>）── 画面は
    /// 「この要素を選ぶと <c>Type</c> がこうなる」を、適用する前に知れなければならない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSourceCatalog_ReportsTheElementsAndTheirRecordingType()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using var response = await client.GetAsync("api/sources", Ct);
        using var body = await ExpectAsync(response, HttpStatusCode.OK);

        var sources = body.RootElement.GetProperty("sources").EnumerateArray().ToArray();
        Assert.True(3 < sources.Length, $"候補が {sources.Length} 件しかありません。");

        var system = sources.Single(s => s.GetProperty("element").GetString() == "videotestsrc");
        Assert.Equal("System", system.GetProperty("recordingType").GetString());
        Assert.NotEmpty(system.GetProperty("displayName").GetString() ?? "");

        var d3d12 = sources.Single(s => s.GetProperty("element").GetString() == "d3d12testsrc");
        Assert.Equal("D3d12", d3d12.GetProperty("recordingType").GetString());
        Assert.Equal("memory:D3D12Memory", d3d12.GetProperty("memoryFeature").GetString());

        // 画面が編集欄を組むために要る 4 つ。種別の綴りは L1（SourcePresetRulesTests）が
        // SrcPropertyKind の名前と一致させている。
        var pattern = system.GetProperty("properties").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "pattern");
        Assert.Equal("Enum", pattern.GetProperty("kind").GetString());
        Assert.Contains("smpte", StringsOf(pattern.GetProperty("choices")));
        Assert.Equal("smpte", pattern.GetProperty("defaultValue").GetString());
        Assert.False(pattern.GetProperty("conditionallyAvailable").GetBoolean());

        var resolution = system.GetProperty("capsFields").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "resolution");
        Assert.True(resolution.GetProperty("isResolution").GetBoolean());
    }

    /// <summary>
    /// <b>テンプレートからの適用が <c>SrcPipeline</c> と <c>Type</c> を同時に書くこと。</b>
    ///
    /// <para>
    /// 文字列そのものの <c>PATCH</c> は拒否のままで（<c>PatchingDeniedRecorderSettings_*</c>）、
    /// ここが唯一の書き口である。種別まで書くのが要点 ── <c>d3d12testsrc</c> を
    /// <c>Type=System</c> のまま適用すると、次の初期化でパイプラインが繋がらない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ApplyingASourceTemplate_WritesThePipelineAndTheRecordingType()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var response = await SendAsync(
            client, HttpMethod.Put, "api/recorders/0/source",
            "{\"element\":\"videotestsrc\",\"properties\":{\"pattern\":\"smpte\"}}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            // 応答が組み立て結果を返すので、呼び出し側は逆パースをしなくてよい。
            Assert.StartsWith(
                "videotestsrc pattern=smpte",
                body.RootElement.GetProperty("srcPipeline").GetString(),
                StringComparison.Ordinal);
            Assert.Contains("SrcPipeline", StringsOf(body.RootElement.GetProperty("applied")));
            Assert.Contains("Type", StringsOf(body.RootElement.GetProperty("applied")));
            // どちらも「初期化をやり直すまで効かない」項目である。
            Assert.Contains("SrcPipeline", StringsOf(body.RootElement.GetProperty("requiresReinitialize")));
            Assert.Contains("Type", StringsOf(body.RootElement.GetProperty("requiresReinitialize")));
        }

        using (var response = await client.GetAsync("api/recorders/0/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            var values = body.RootElement.GetProperty("values");
            Assert.StartsWith(
                "videotestsrc pattern=smpte", values.GetProperty("SrcPipeline").GetString(),
                StringComparison.Ordinal);
            Assert.Equal("System", values.GetProperty("Type").GetString());
        }

        // メモリ機能を持つ要素は D3d12 側へ落ちる（別のレコーダーで見る ── 上の 1 台の
        // 結果を上書きすると、どちらの適用を見ているのか分からなくなる）。
        using (var response = await SendAsync(
            client, HttpMethod.Put, "api/recorders/R2/source",
            "{\"element\":\"d3d12testsrc\",\"caps\":{\"resolution\":\"640x480\"}}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Contains(
                "width=640, height=480", body.RootElement.GetProperty("srcPipeline").GetString() ?? "",
                StringComparison.Ordinal);
        }

        using (var response = await client.GetAsync("api/recorders/R2/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            var values = body.RootElement.GetProperty("values");
            Assert.StartsWith(
                "d3d12testsrc", values.GetProperty("SrcPipeline").GetString(), StringComparison.Ordinal);
            Assert.Equal("D3d12", values.GetProperty("Type").GetString());
        }
    }

    /// <summary>
    /// <b>カタログの外は 1 つも通らないこと。</b> ここが緩むと、リモートから任意の
    /// GStreamer パイプラインを実行できる ── <c>pattern</c> に <c>"x ! filesink"</c> を
    /// 通せば、それは <c>SrcPipeline</c> を直に書くのと同じことである。
    /// </summary>
    [Fact]
    public async Task ASourceTemplateOutsideTheCatalog_IsRefused()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string before;
        using (var response = await client.GetAsync("api/recorders/0/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            before = body.RootElement.GetProperty("values").GetProperty("SrcPipeline").GetString() ?? "";
        }

        (string Body, string Expected)[] refused =
        [
            ("{\"element\":\"filesrc\"}", "unknown source element"),
            ("{\"element\":\"videotestsrc\",\"properties\":{\"location\":\"C:\\\\x.mp4\"}}", "location"),
            ("{\"element\":\"videotestsrc\",\"properties\":{\"pattern\":\"x ! filesink\"}}", "pattern"),
            ("{\"element\":\"videotestsrc\",\"caps\":{\"resolution\":\"huge\"}}", "resolution"),
        ];

        foreach (var (json, expected) in refused)
        {
            using var response = await SendAsync(client, HttpMethod.Put, "api/recorders/0/source", json);
            using var body = await ExpectAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains(
                expected, body.RootElement.GetProperty("error").GetString() ?? "", StringComparison.Ordinal);
        }

        // 断った要求は 1 文字も書いていないこと。
        using (var response = await client.GetAsync("api/recorders/0/settings", Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal(before, body.RootElement.GetProperty("values").GetProperty("SrcPipeline").GetString());
        }
    }

    /// <summary>
    /// <b>適用は設定の変更なので <c>Admin</c> だけ。</b> <c>Operator</c> は 403
    /// （<c>PATCH …/settings</c> と同じ切り分け）。
    /// </summary>
    [Fact]
    public async Task ApplyingASourceTemplate_NeedsAnAdmin()
    {
        using var instance = AppInstance.Create(app, RemoteUserSettings(allowGuestRead: false));
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string cookie = await LoginAsync(client, OperatorUser, OperatorPassword, "Operator");

        using (var request = AsSession(
            HttpMethod.Put, "api/recorders/0/source", cookie, "{\"element\":\"videotestsrc\"}"))
        using (var response = await client.SendAsync(request, Ct))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.Forbidden);
            Assert.Equal("insufficient role", body.RootElement.GetProperty("error").GetString());
        }

        // 一覧は読み取りなので Operator でも通る（Viewer 以上）。
        using (var request = AsSession(HttpMethod.Get, "api/sources", cookie, clientHeader: false))
        using (var response = await client.SendAsync(request, Ct))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();
    }

    /// <summary>
    /// <b>録画中は断る（409）。</b> <c>SrcPipeline</c> も <c>Type</c> も初期化をやり直させる
    /// 項目なので、書けば走っている録画が黙って落ちる。
    /// </summary>
    [Fact]
    public async Task ApplyingASourceTemplateWhileRecording_IsRefused()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string filename;
        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/0/start"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK);
            filename = body.RootElement.GetProperty("filename").GetString() ?? string.Empty;
        }

        using (var response = await SendAsync(
            client, HttpMethod.Put, "api/recorders/0/source", "{\"element\":\"videotestsrc\"}"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.Conflict);
            Assert.Equal(14, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        Thread.Sleep(RecordingWindow);

        using (var response = await SendAsync(client, HttpMethod.Post, "api/recorders/0/stop"))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        // 断ったのだから、走っていた録画は無事でなければならない。
        RecordedMp4.AssertUsable(filename, instance, output);
    }

    /// <summary>
    /// <b>拒否キーはサーバーが「編集できない」と名乗ること。</b> 画面が名前を写すと、
    /// 拒否リストを増やした日に画面だけが古いまま編集欄を出し続ける。
    /// </summary>
    [Fact]
    public async Task RecorderSettings_SayWhichKeysTheRemoteApiRefuses()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using var response = await client.GetAsync("api/recorders/0/settings", Ct);
        using var body = await ExpectAsync(response, HttpStatusCode.OK);

        var properties = body.RootElement.GetProperty("properties").EnumerateArray().ToArray();
        Assert.NotEmpty(properties);

        foreach (string denied in new[]
        {
            "SrcPipeline", "EncodingProperties", "ContinuousEncodingProperties",
            "FilenameTemplate", "ContinuousFilenameTemplate",
        })
        {
            var property = properties.Single(p => p.GetProperty("name").GetString() == denied);
            Assert.False(property.GetProperty("remoteEditable").GetBoolean(),
                $"{denied} が編集可能として出ています。");
        }

        // 残りは編集できる ── 全部 false になっていても上の検査は通ってしまう。
        var buffer = properties.Single(p => p.GetProperty("name").GetString() == "BufferDuration");
        Assert.True(buffer.GetProperty("remoteEditable").GetBoolean());
    }
}
