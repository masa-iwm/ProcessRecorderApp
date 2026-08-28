using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// 録画ファイルの配信（<c>GET /api/recordings</c>・<c>GET /api/recordings/{*path}</c>）と、
/// 埋め込みの Web UI。
///
/// <para>
/// <b>ここは発行物でしか確かめられない。</b> Range・条件付き要求・
/// <c>Content-Disposition</c> の符号化はどれも ASP.NET Core が書くもので、
/// 単体テストからは 1 バイトも観測できない ── 埋め込み資産に至っては、
/// 発行物へ入っているかどうかそのものが検証対象である。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class RecordingDeliveryTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>製品が生成する形（Base64Url・43 文字）に合わせた固定トークン。</summary>
    private const string Token = "E2E-recording-delivery-token-0123456789-abc";

    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>録画を回す時間。<c>mp4mux</c> が意味のある長さを書くのに足りる最小限。</summary>
    private static readonly TimeSpan RecordingWindow = TimeSpan.FromSeconds(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Regex BindPattern = new(@"\bbind=([0-9.]+):(\d+)\b", RegexOptions.Compiled);

    /// <summary>
    /// リモート操作を有効にし、<b>配信 root を隔離ディレクトリへ向けた</b> settings.json。
    /// <c>OutputDirectory</c> を書かないと相対の基準が発行ディレクトリになり、
    /// 一覧が開発機の実ファイルを映す。
    /// </summary>
    private static SettingsFile RemoteSettings()
    {
        var settings = new SettingsFile
        {
            RemoteControlEnabled = true,
            // 127.0.0.1 に固定する（0.0.0.0 だと開発機と CI の LAN から到達できる）。
            RemoteControlBindAddress = "127.0.0.1",
            RemoteControlPort = 0,
            RemoteControlAccessToken = Token,
            // **このクラスの主題は認証ではない。** 読み取りにも役割が要るので、
            // ゲスト読み取りを明示して未認証で読ませる
            // ── 認証そのものは RemoteControlTests が見る。
            RemoteControlAllowGuestRead = true,
            // **非 fragmented を明示する（製品の既定は true）。** このクラスは
            // 「fragmented だとどう変わるか」を対で見るので、土台の側も明示しておく
            // ── fMP4 側は FragmentedSettings() / FragmentedContinuousSettings()。
            FragmentedOutput = false,
        };
        settings.AddRecorder("R1");
        return settings;
    }

    private static void UseIsolatedRoot(AppInstance instance)
        => instance.Settings.OutputDirectory = instance.RecordingsDir;

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

        Assert.Fail(
            $"remote.start が {StartBudget.TotalSeconds:F0} 秒以内に現れませんでした。"
            + Environment.NewLine
            + string.Join(Environment.NewLine, ActivityLogFile.Events(instance.ReadActivityLog(), "remote.error"))
            + Environment.NewLine + instance.DiagnosticDump());
        return 0;
    }

    private static HttpClient CreateClient(int port) =>
        new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = RequestTimeout,
        };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    /// <summary>一覧を読み、<c>files</c> の要素を素の JSON のまま返す。</summary>
    private static async Task<(string Root, JsonElement[] Files)> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync("api/recordings", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        using var body = await ReadJsonAsync(response);
        return (body.RootElement.GetProperty("root").GetString()!,
                [.. body.RootElement.GetProperty("files").EnumerateArray().Select(e => e.Clone())]);
    }

    /// <summary>索引が新しい状態へ追いつくまでの上限。</summary>
    private static readonly TimeSpan IndexBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// <paramref name="accept"/> を満たす一覧が返るまで引き直す。
    ///
    /// <para>
    /// <b>一覧はメモリの索引（<c>RecordingIndex</c>）から返る</b>ので、要求のたびの走査では
    /// なくなった ── 停止の応答が返った直後はまだ「録画中」のままでありうる
    /// （watcher の通知 → 500ms デバウンス → 作り直し）。ここで待たずに断じると、
    /// 機械の速さで通ったり落ちたりする検査になる。
    /// </para>
    /// </summary>
    private static async Task<JsonElement[]> WaitForListingAsync(
        HttpClient client, Func<JsonElement[], bool> accept, TimeSpan budget)
    {
        var deadline = Stopwatch.StartNew();
        JsonElement[] files = [];

        while (deadline.Elapsed < budget)
        {
            (_, files) = await ListAsync(client);
            if (accept(files))
                return files;

            Thread.Sleep(250);
        }

        Assert.Fail(
            $"一覧が {budget.TotalSeconds:F0} 秒以内に期待した状態になりませんでした: "
            + string.Join(", ", files.Select(f => f.ToString())));
        return files;
    }

    /// <summary>録画が 1 本だけ在って、それが確定していること。</summary>
    private static bool IsSingleFinished(JsonElement[] files)
        => files.Length == 1 && !files[0].GetProperty("inProgress").GetBoolean();

    /// <summary>録画を 1 本作って、その相対パスを返す。</summary>
    private string RecordOnce(AppInstance instance)
    {
        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        Thread.Sleep(RecordingWindow);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        string file = Assert.Single(instance.ListRecordings());
        output.WriteLine(file);
        return Path.GetFileName(file);
    }

    // ---- 一覧 ----

    /// <summary>
    /// 録画中のファイルが <c>inProgress</c> で出て、停止後は確定した長さで出ること。
    /// 併せて、<b>録画中でも取得できる</b>こと（<c>filesink</c> が握ったままでも読める）。
    /// </summary>
    [Fact]
    public async Task TheListing_MarksTheFileInProgressAndServesItAnyway()
    {
        using var instance = AppInstance.Create(app, RemoteSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        var (root, empty) = await ListAsync(client);
        Assert.Equal(instance.RecordingsDir, root, ignoreCase: true);
        Assert.Empty(empty);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        Thread.Sleep(RecordingWindow);

        var (_, recording) = await ListAsync(client);
        var live = Assert.Single(recording);
        Assert.True(live.GetProperty("inProgress").GetBoolean(),
            "録画中のファイルが inProgress で出ていない: " + live);

        string relativePath = live.GetProperty("path").GetString()!;
        Assert.DoesNotContain('\\', relativePath);

        // 録画中でも本文が取れること。ここは非 fragmented（RemoteSettings）なので
        // moov は未確定で再生はできないが、「握られているから読めない」であってはならない。
        //
        // **長さは 0 でありうる。** `mp4mux faststart=true` は EOS まで自前の一時ファイルへ
        // 溜めるので、`filesink` の出力先は録画が終わるまで 0 バイトのままになる（実測）。
        // ここで確かめられるのは「共有読み取りで開けて 200 が返る」ことだけである。
        using (var live200 = await client.GetAsync("api/recordings/" + Uri.EscapeDataString(relativePath), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, live200.StatusCode);
            Assert.NotNull(live200.Content.Headers.ContentLength);
            output.WriteLine($"in-progress Content-Length: {live200.Content.Headers.ContentLength}");
        }

        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        var finished = await WaitForListingAsync(client, IsSingleFinished, IndexBudget);
        var done = Assert.Single(finished);
        Assert.Equal(relativePath, done.GetProperty("path").GetString());

        string full = Assert.Single(instance.ListRecordings());
        Assert.Equal(new FileInfo(full).Length, done.GetProperty("length").GetInt64());

        // 更新時刻は ISO-8601 で載る（文字列ではなく時刻として突き合わせる）。
        Assert.Equal(
            File.GetLastWriteTimeUtc(full),
            done.GetProperty("lastWriteTimeUtc").GetDateTime(),
            TimeSpan.FromSeconds(2));
    }

    // ---- sidecar・絞り込み・ページング・日集計 ----

    /// <summary>
    /// sidecar が書かれて一覧に載るまでの上限。排出のあとにスレッドプールで書かれ、
    /// 索引はさらに 500ms のデバウンス越しに作り直される。
    /// </summary>
    private static readonly TimeSpan SidecarBudget = TimeSpan.FromSeconds(20);

    /// <summary>200 と <c>no-store</c> を確かめて本文の JSON を返す（破棄は呼び出し側）。</summary>
    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return await ReadJsonAsync(response);
    }

    private static async Task AssertBadRequestAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// 録画が終わると <c>&lt;name&gt;.mp4.json</c> が書かれ、一覧の項目に
    /// <c>startTimeUtc</c> / <c>recorder</c> / <c>durationMs</c> が載ること。
    /// 併せて <c>from</c> / <c>to</c> / <c>recorder</c> / <c>limit</c> / <c>offset</c> と
    /// <c>/api/recording-days</c>、断り方（400）を固定する。
    ///
    /// <para>
    /// <b>ここは発行物でしか見られない。</b> sidecar を書くのは録画エンジン（排出の完了時）、
    /// 読むのはリモート操作側の索引で、間に <see cref="System.IO.FileSystemWatcher"/> が
    /// 挟まる ── 3 つが揃って初めて「一覧に尺が出る」になる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheListing_CarriesTheSidecarFactsAndPages()
    {
        using var instance = AppInstance.Create(app, RemoteSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        // 2 本録る（ページングは 1 本では「次が在る」を見られない）。
        for (int take = 0; take < 2; take++)
        {
            Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
            Thread.Sleep(RecordingWindow);
            Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);
        }

        // **sidecar が載るまで待つ。** 停止の応答が返った時点ではまだ書かれていない
        // （best-effort・スレッドプール）ので、直後を見て断じるとまぐれで通る／落ちる。
        JsonElement[] files = [];
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < SidecarBudget)
        {
            (_, files) = await ListAsync(client);
            if (files.Length == 2
                && files.All(f => f.GetProperty("durationMs").ValueKind != JsonValueKind.Null))
            {
                break;
            }

            Thread.Sleep(500);
        }

        Assert.Equal(2, files.Length);
        foreach (var file in files)
        {
            output.WriteLine(file.ToString());
            Assert.False(file.GetProperty("inProgress").GetBoolean());
            Assert.Equal("R1", file.GetProperty("recorder").GetString());
            Assert.True(0 < file.GetProperty("durationMs").GetInt64(),
                "sidecar の尺が一覧に載っていない: " + file);
            Assert.False(file.GetProperty("hasThumbnail").GetBoolean());
        }

        // ディスクにも並んでいること（サムネイル `<録画ファイル名>.png` はこの隣に置かれる）。
        Assert.Equal(2, Directory.GetFiles(instance.RecordingsDir, "*.mp4.json").Length);

        // 並びは開始時刻の降順。
        DateTime newest = files[0].GetProperty("startTimeUtc").GetDateTime();
        DateTime oldest = files[1].GetProperty("startTimeUtc").GetDateTime();
        Assert.True(oldest < newest, $"開始時刻の降順で並んでいない: {oldest:O} / {newest:O}");

        // ページング。total は絞り込みの後・ページングの前の件数である。
        using (var page = await GetJsonAsync(client, "api/recordings?limit=1"))
        {
            Assert.Equal(2, page.RootElement.GetProperty("total").GetInt32());
            Assert.True(page.RootElement.GetProperty("hasMore").GetBoolean());
            var only = Assert.Single(page.RootElement.GetProperty("files").EnumerateArray());
            Assert.Equal(newest, only.GetProperty("startTimeUtc").GetDateTime());
        }

        using (var tail = await GetJsonAsync(client, "api/recordings?limit=1&offset=1"))
        {
            Assert.Equal(2, tail.RootElement.GetProperty("total").GetInt32());
            Assert.False(tail.RootElement.GetProperty("hasMore").GetBoolean());
            var only = Assert.Single(tail.RootElement.GetProperty("files").EnumerateArray());
            Assert.Equal(oldest, only.GetProperty("startTimeUtc").GetDateTime());
        }

        // 絞り込み。**`from` は含み、`to` は含まない** ── 同じ値で両端を突いて確かめる。
        string boundary = Uri.EscapeDataString(oldest.ToString("O", CultureInfo.InvariantCulture));
        using (var inclusive = await GetJsonAsync(client, "api/recordings?from=" + boundary))
            Assert.Equal(2, inclusive.RootElement.GetProperty("total").GetInt32());

        using (var exclusive = await GetJsonAsync(client, "api/recordings?to=" + boundary))
            Assert.Equal(0, exclusive.RootElement.GetProperty("total").GetInt32());

        using (var mine = await GetJsonAsync(client, "api/recordings?recorder=R1"))
            Assert.Equal(2, mine.RootElement.GetProperty("total").GetInt32());

        using (var none = await GetJsonAsync(client, "api/recordings?recorder=R9"))
            Assert.Equal(0, none.RootElement.GetProperty("total").GetInt32());

        // 日ごとの件数。**日付は返ってきた開始時刻から導く**（`UtcNow` から作ると
        // 日付をまたいだ瞬間に落ちる）。
        string today = newest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using (var days = await GetJsonAsync(client, "api/recording-days"))
        {
            var rows = days.RootElement.GetProperty("days").EnumerateArray().ToArray();
            output.WriteLine(days.RootElement.ToString());
            Assert.Equal(2, rows.Sum(d => d.GetProperty("count").GetInt32()));
            var row = Assert.Single(rows, d => d.GetProperty("date").GetString() == today);
            Assert.True(0 < row.GetProperty("count").GetInt32());
        }

        // tz は固定オフセットと Windows のタイムゾーン ID を受ける（IANA は受けない
        // ── InvariantGlobalization=true）。数え上げる総数は変わらない。
        foreach (string tz in new[] { "+09:00", "Tokyo Standard Time" })
        {
            using var shifted = await GetJsonAsync(
                client, "api/recording-days?tz=" + Uri.EscapeDataString(tz));
            Assert.Equal(
                2, shifted.RootElement.GetProperty("days").EnumerateArray().Sum(d => d.GetProperty("count").GetInt32()));
        }

        // 受け付けない問い合わせは丸めずに断る（返る件数を応答を数えるまで知れない、を作らない）。
        await AssertBadRequestAsync(client, "api/recordings?limit=0");
        await AssertBadRequestAsync(client, "api/recordings?limit=1001");
        await AssertBadRequestAsync(client, "api/recordings?offset=-1");
        await AssertBadRequestAsync(client, "api/recordings?from=yesterday");

        // **時刻だけ・ロケール依存の日付は ISO-8601 ではない。** 緩く読むと `10:00` が
        // 「今日の 10 時」に、`08/28/2026` が月日の順序込みで通ってしまう。
        await AssertBadRequestAsync(client, "api/recordings?from=" + Uri.EscapeDataString("10:00"));
        await AssertBadRequestAsync(client, "api/recording-days?tz=" + Uri.EscapeDataString("Not/AZone"));
        await AssertBadRequestAsync(client, "api/recording-days?tz=" + Uri.EscapeDataString("+99:00"));
    }

    // ---- fragmented 出力（録画中の追いかけ再生） ----

    /// <summary>fragment が書かれるのを待つ上限。<c>fragment-duration</c> は 1 秒。</summary>
    private static readonly TimeSpan FragmentBudget = TimeSpan.FromSeconds(20);

    /// <summary><see cref="RemoteSettings"/> を fragmented 出力（アプリ全体）にしたもの。</summary>
    private static SettingsFile FragmentedSettings()
    {
        var settings = RemoteSettings();
        settings.FragmentedOutput = true;
        return settings;
    }

    /// <summary>本文の先頭の箱の名前（<c>size(4) + type(4)</c>）。</summary>
    private static string FirstBoxOf(byte[] body)
        => 8 <= body.Length ? Encoding.ASCII.GetString(body, 4, 4) : "(too short)";

    /// <summary>
    /// <b>fragmented なら録画中でも中身が読める。</b> <c>FragmentedOutput</c> を切った形
    /// （<c>faststart=true</c>）では録画中のファイルは 0 バイトで、再生できる形が 1 バイトも無い
    /// ── その違いがブラウザの追いかけ再生の土台である。
    ///
    /// <para>
    /// 併せて、追いかけ再生が使うヘッダー（<c>X-In-Progress</c> / <c>X-Codecs</c>）と、
    /// 伸びを追う仕掛け（末尾からの <c>Range</c> が 416、数秒後には total が伸びる）を固定する。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFragmentedRecording_IsReadableWhileItIsBeingWritten()
    {
        using var instance = AppInstance.Create(app, FragmentedSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);

        // fragment が 1 つ以上書かれるまで待つ（1 秒ごとに 1 つ出る）。
        string relativePath = "";
        byte[] body = [];
        long snapshot = 0;
        var deadline = Stopwatch.StartNew();
        Fmp4Probe? probe = null;

        while (deadline.Elapsed < FragmentBudget)
        {
            var (_, files) = await ListAsync(client);
            if (files.Length == 1 && files[0].GetProperty("fragmented").GetBoolean())
            {
                Assert.True(files[0].GetProperty("inProgress").GetBoolean(),
                    "録画中のファイルが inProgress で出ていない: " + files[0]);

                relativePath = files[0].GetProperty("path").GetString()!;
                using var live = await client.GetAsync("api/recordings/" + Uri.EscapeDataString(relativePath), Ct);
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);

                Assert.Equal("true", Assert.Single(live.Headers.GetValues("X-In-Progress")));
                string codecs = Assert.Single(live.Headers.GetValues("X-Codecs"));
                Assert.StartsWith("avc1.", codecs, StringComparison.Ordinal);

                snapshot = Assert.IsType<long>(live.Content.Headers.ContentLength);
                body = await live.Content.ReadAsByteArrayAsync(Ct);
                Assert.Equal(snapshot, body.Length);

                probe = Fmp4File.Probe(relativePath, body);
                if (0 < probe.MoofCount)
                    break;
            }

            Thread.Sleep(500);
        }

        Assert.False(relativePath.Length == 0, "録画中のファイルが fragmented として一覧に出ませんでした。");
        Assert.NotNull(probe);
        output.WriteLine(probe.ToString());

        // 先頭は ftyp、その次が mvex 入りの moov、以後は moof+mdat の対
        // ── つまり「MSE へそのまま渡せる形」である。
        Assert.Equal("ftyp", FirstBoxOf(body));
        Assert.True(probe.StartsWithInitSegment, "先頭が ftyp+moov ではない: " + probe);
        Assert.True(probe.HasMvex, "moov に mvex が無い（fragmented ではない）: " + probe);
        Assert.True(probe.HasAvcC, "moov に avcC が無い: " + probe);
        Assert.True(0 < probe.MoofCount, "moof が 1 つも無い: " + probe);

        // 末尾からの Range は「まだ伸びていない」＝ 416。取得と要求の間に
        // fragment が 1 つ増えることはあるので、その分だけ追いかけて確かめる。
        HttpStatusCode beyond = HttpStatusCode.OK;
        for (int attempt = 0; attempt < 5 && beyond != HttpStatusCode.RequestedRangeNotSatisfiable; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "api/recordings/" + Uri.EscapeDataString(relativePath));
            request.Headers.Range = new RangeHeaderValue(snapshot, null);
            using var response = await client.SendAsync(request, Ct);

            beyond = response.StatusCode;
            if (beyond == HttpStatusCode.PartialContent)
                snapshot = response.Content.Headers.ContentRange?.Length ?? snapshot;
        }
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, beyond);

        // 数秒後には伸びている（＝続きが Range で取れる）。
        Thread.Sleep(TimeSpan.FromSeconds(3));

        long grown;
        using (var request = new HttpRequestMessage(
                   HttpMethod.Get, "api/recordings/" + Uri.EscapeDataString(relativePath)))
        {
            request.Headers.Range = new RangeHeaderValue(snapshot, null);
            using var more = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.PartialContent, more.StatusCode);
            Assert.Equal("true", Assert.Single(more.Headers.GetValues("X-In-Progress")));

            grown = Assert.IsType<long>(more.Content.Headers.ContentRange?.Length);
            output.WriteLine($"total {snapshot} -> {grown}");
            Assert.True(snapshot < grown, $"Content-Range の total が伸びていない（{snapshot} -> {grown}）。");

            byte[] tail = await more.Content.ReadAsByteArrayAsync(Ct);
            Assert.Equal(grown - snapshot, tail.Length);
        }

        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        // 停止後は X-In-Progress が false（ブラウザはこれを見て endOfStream する）。
        using (var finished = await client.GetAsync(
                   "api/recordings/" + Uri.EscapeDataString(relativePath), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
            Assert.Equal("false", Assert.Single(finished.Headers.GetValues("X-In-Progress")));
            Assert.True(grown <= finished.Content.Headers.ContentLength);
        }

        var done = await WaitForListingAsync(client, IsSingleFinished, IndexBudget);
        Assert.True(done[0].GetProperty("fragmented").GetBoolean());

        // 完成したファイルも fragmented のまま（EOS で足すのは末尾の mfra だけで、
        // moov は書き直されない ＝ 尺は 0 のまま）。
        string full = Assert.Single(instance.ListRecordings());
        var complete = Fmp4File.Probe(full);
        output.WriteLine(complete.ToString());
        Assert.True(complete.StartsWithInitSegment, complete.ToString());
        Assert.True(complete.HasMvex, complete.ToString());
        Assert.True(0 < complete.MoofCount, complete.ToString());

        var asPlainMp4 = Mp4File.Probe(full);
        output.WriteLine(asPlainMp4.ToString());
        Assert.True(asPlainMp4.HasFtyp && asPlainMp4.HasMoov && asPlainMp4.HasMdat && asPlainMp4.HasAvcC,
            "完成したファイルが MP4 として最低限の箱を持っていない: " + asPlainMp4);
        // **mvhd の尺は 0 のまま**（moov を書き直さないので duration が伸びない）。
        // だから `<video src>` 直結では 1 秒に見え、ブラウザ側は完成後も MSE 経路で読む。
        Assert.True(asPlainMp4.MvhdDurationSeconds is null or 0,
            "fragmented なのに mvhd の尺が入っている: " + asPlainMp4);
        // **それでも「録画物として使えるか」は答えられる。** Mp4Probe は fragmented を
        // 見分けて moof の trun から尺とサンプル数を出す（＝録画系 E2E が既定構成で
        // 走れる根拠）。ここで 0 を返すと、この形の録画物は全部無検査になる。
        Assert.True(asPlainMp4.IsFragmented, "fragmented と判定されていない: " + asPlainMp4);
        Assert.True(asPlainMp4.DurationSeconds is > 0, "fragment から尺が出ていない: " + asPlainMp4);
        Assert.True(0 < asPlainMp4.SampleCount, "fragment からサンプル数が出ていない: " + asPlainMp4);
    }

    // ---- fragment の索引（GET /api/recording-fragments/{*path}） ----

    /// <summary>索引を引いて、応答の本体（<c>ETag</c> 込み）を返す。</summary>
    private static async Task<(JsonElement Body, string? ETag)> IndexAsync(
        HttpClient client, string relativePath, long from = 0)
    {
        using var response = await client.GetAsync(
            "api/recording-fragments/" + Uri.EscapeDataString(relativePath)
            + "?from=" + from.ToString(CultureInfo.InvariantCulture), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        using var body = await ReadJsonAsync(response);
        return (body.RootElement.Clone(), response.Headers.ETag?.Tag);
    }

    private static int FragmentCount(JsonElement index) => index.GetProperty("fragments").GetArrayLength();

    private static long NextOffsetOf(JsonElement index) => index.GetProperty("nextOffset").GetInt64();

    /// <summary>
    /// <b>録画中でも索引が引けて、伸びること。</b> ブラウザはこれで「その秒はどのバイトか」を
    /// 引く ── 索引が止まると、任意の位置へのシークがその時点までに縮む。
    ///
    /// <para>
    /// 併せて、<c>from</c> が差分だけを返すこと（毎秒引き直すので全件を返すと二乗の転送になる）、
    /// 停止後は <c>inProgress=false</c> で尺がファイルの実尺と一致すること、
    /// <c>ETag</c> が本体の配信のものと別値であることを固定する。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheFragmentIndexGrowsWhileRecordingAndSettlesWhenItStops()
    {
        using var instance = AppInstance.Create(app, FragmentedSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);

        // fragment が 1 つ以上書かれ、索引に載るまで待つ（1 秒ごとに 1 つ出る）。
        string relativePath = "";
        JsonElement first = default;
        string? indexETag = null;
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < FragmentBudget)
        {
            var (_, files) = await ListAsync(client);
            if (files.Length == 1 && files[0].GetProperty("fragmented").GetBoolean())
            {
                relativePath = files[0].GetProperty("path").GetString()!;
                (first, indexETag) = await IndexAsync(client, relativePath);
                if (0 < FragmentCount(first))
                    break;
            }

            Thread.Sleep(500);
        }

        Assert.False(relativePath.Length == 0, "録画中のファイルが fragmented として一覧に出ませんでした。");
        Assert.True(0 < FragmentCount(first), "録画中のファイルの索引が空のままでした: " + first);
        output.WriteLine($"first: {FragmentCount(first)} fragments, next={NextOffsetOf(first)}");

        Assert.True(first.GetProperty("inProgress").GetBoolean(), "録画中なのに inProgress が false: " + first);
        Assert.True(0 < first.GetProperty("timescale").GetUInt32(), "timescale が 0: " + first);
        Assert.StartsWith("avc1.", first.GetProperty("codecs").GetString(), StringComparison.Ordinal);

        // init セグメント（ftyp + moov）は最初の moof の手前にある。
        long initSize = first.GetProperty("initSize").GetInt64();
        Assert.True(0 < initSize, "initSize が 0: " + first);
        Assert.Equal(initSize, first.GetProperty("fragments")[0].GetProperty("offset").GetInt64());

        // **同期でないフラグメントが在るのが録画の形である**（fragment 1 秒・GOP 2 秒）。
        // 先頭だけは必ず同期でなければ、そもそも再生が始まらない。
        Assert.True(first.GetProperty("fragments")[0].GetProperty("sync").GetBoolean(),
            "先頭のフラグメントが同期でない: " + first);

        // 本体の配信の ETag と衝突していないこと（同じ経路の別の表現である）。
        using (var body = await client.GetAsync("api/recordings/" + Uri.EscapeDataString(relativePath), Ct))
        {
            Assert.NotNull(indexETag);
            Assert.NotEqual(body.Headers.ETag?.Tag, indexETag);
        }

        Thread.Sleep(TimeSpan.FromSeconds(3));

        var (grown, _) = await IndexAsync(client, relativePath);
        output.WriteLine($"grown: {FragmentCount(grown)} fragments, next={NextOffsetOf(grown)}");

        Assert.True(FragmentCount(first) < FragmentCount(grown),
            $"索引が伸びていません（{FragmentCount(first)} -> {FragmentCount(grown)}）。");
        Assert.True(NextOffsetOf(first) < NextOffsetOf(grown),
            $"nextOffset が進んでいません（{NextOffsetOf(first)} -> {NextOffsetOf(grown)}）。");

        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        var (done, _) = await IndexAsync(client, relativePath);
        Assert.False(done.GetProperty("inProgress").GetBoolean(), "停止後も inProgress が true: " + done);

        // **差分の件数は止めてから見る。** 録画中は 2 回の取得の間にも伸びるので、
        // 別々の応答から引き算した数は一致しない ── 確定したファイルなら決まる。
        long secondOffset = done.GetProperty("fragments")[1].GetProperty("offset").GetInt64();
        var (delta, _) = await IndexAsync(client, relativePath, secondOffset);

        Assert.Equal(FragmentCount(done) - 1, FragmentCount(delta));
        Assert.Equal(secondOffset, delta.GetProperty("fragments")[0].GetProperty("offset").GetInt64());
        foreach (var fragment in delta.GetProperty("fragments").EnumerateArray())
            Assert.True(secondOffset <= fragment.GetProperty("offset").GetInt64(), "from より手前が返りました。");

        // 全体の尺と init の大きさは from で切っても変わらない（切るのは配列だけ）。
        Assert.Equal(done.GetProperty("totalDuration").GetUInt64(), delta.GetProperty("totalDuration").GetUInt64());
        Assert.Equal(done.GetProperty("initSize").GetInt64(), delta.GetProperty("initSize").GetInt64());

        double indexed = done.GetProperty("totalDuration").GetUInt64() / (double)done.GetProperty("timescale").GetUInt32();
        var probe = Mp4File.Probe(Assert.Single(instance.ListRecordings()));
        output.WriteLine($"index {indexed:F3}s / file {probe}");

        Assert.True(probe.DurationSeconds is { } length && Math.Abs(length - indexed) <= 1,
            $"索引の尺 {indexed:F3}s がファイルの尺と 1 秒以上ずれています: {probe}");

        // **件数は独立した数え方と突き合わせる。** 索引が途中で止まっても
        // 「伸びた・尺が近い」までは通ってしまう ── moof の総数は
        // <see cref="Mp4File.Probe"/> がこの実装とは別に数えている。
        Assert.Equal(probe.FragmentCount, FragmentCount(done));

        // **同期の別が本当に読めている証人。** 先頭 1 件が true なのは
        // 「フラグを 1 つも読めず既定の 0（＝同期）で答えた」ときも同じなので、
        // それだけでは何も言えない ── フラグメント 1 秒・GOP 2 秒の録画には
        // 同期でないフラグメントが必ず在り、それが在ることまで見て初めて塞がる。
        bool[] syncs = [.. done.GetProperty("fragments").EnumerateArray()
            .Select(fragment => fragment.GetProperty("sync").GetBoolean())];
        output.WriteLine("sync: " + string.Concat(syncs.Select(s => s ? "K" : ".")));

        Assert.True(syncs[0], "先頭のフラグメントが同期でない: " + done);
        Assert.Contains(false, syncs);
    }

    /// <summary>
    /// <b>fragmented でないファイルには索引が無い。</b> <c>moof</c> が 1 つも無いので
    /// 「その秒はどのバイトか」に答えようがなく、<b>400 ではなく 404</b> で断る
    /// ── 要求の書き方ではなく、その資源が在るかどうかの答えである。
    /// </summary>
    [Fact]
    public async Task ANonFragmentedRecording_HasNoFragmentIndex()
    {
        using var instance = AppInstance.Create(app, RemoteSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string relativePath = RecordOnce(instance);

        using var response = await client.GetAsync(
            "api/recording-fragments/" + Uri.EscapeDataString(relativePath), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("not fragmented", body.RootElement.GetProperty("error").GetString());

        // 無いファイルは、本体の配信と同じ断り方（404 / not found）になる。
        using var missing = await client.GetAsync("api/recording-fragments/no-such-file.mp4", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // 規則で断るもの（拡張子違い）は 400 のまま。
        using var rejected = await client.GetAsync("api/recording-fragments/notes.txt", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    /// <summary>常時録画のセグメント長(秒)。製品の下限。</summary>
    private const int ContinuousSegmentSeconds = 5;

    /// <summary>セグメントが現れ、伸び、切り替わるまでの待ち上限（それぞれ別に測る）。</summary>
    private static readonly TimeSpan SegmentBudget = TimeSpan.FromSeconds(60);

    /// <summary>fragmented 出力（アプリ全体）＋常時録画（最短セグメント）の構成。</summary>
    private static SettingsFile FragmentedContinuousSettings()
    {
        var settings = RemoteSettings();
        settings.FragmentedOutput = true;
        settings.Recorders[0].WithContinuous(ContinuousSegmentSeconds);
        return settings;
    }

    /// <summary>
    /// <b>常時録画のセグメントも fMP4 で書かれ、追いかけ再生の対象になる。</b>
    ///
    /// <para>
    /// 常時録画は<b>イベント録画の開始を待たない</b>（枝は sink パイプラインの一部）ので、
    /// 起動しただけで書き込み中のセグメントが一覧に出る。
    /// そのセグメントが <c>inProgress=true, fragmented=true</c> で現れ、
    /// <c>Range: bytes=&lt;next&gt;-</c> で伸びを追え、切り替わったあとの前セグメントは
    /// <c>inProgress=false, fragmented=true</c> のまま単体で読めること。
    /// </para>
    /// <para>
    /// <c>FragmentedOutput=false</c> では書き込み中のセグメントに
    /// <c>moof</c> は 1 つも無く、ここは成立しない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFragmentedContinuousSegment_IsFollowableAndSurvivesRotation()
    {
        using var instance = AppInstance.Create(app, FragmentedContinuousSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        // 書き込み中のセグメントが fragment を持って一覧に出るまで待つ。
        string relativePath = "";
        long snapshot = 0;
        Fmp4Probe? probe = null;
        var appearing = Stopwatch.StartNew();

        while (appearing.Elapsed < SegmentBudget && probe is null)
        {
            var (_, files) = await ListAsync(client);
            foreach (var file in files)
            {
                if (!file.GetProperty("inProgress").GetBoolean() || !file.GetProperty("fragmented").GetBoolean())
                    continue;

                string path = file.GetProperty("path").GetString()!;
                using var live = await client.GetAsync("api/recordings/" + Uri.EscapeDataString(path), Ct);

                // **一覧と GET のあいだでセグメントが切り替わりうる。** 常時録画は
                // 数秒ごとに次のファイルへ移るので、一覧では「書き込み中」だったものが
                // GET の時点では確定済み（404 や X-In-Progress 無し）になっていることがある。
                // それはこのテストが見ている性質の反証ではないので、次の周回へ進める
                // ── ここを Assert にすると、切り替わりの瞬間に当たっただけで赤くなる。
                if (live.StatusCode != HttpStatusCode.OK)
                    continue;
                if (!live.Headers.TryGetValues("X-In-Progress", out var inProgressHeader)
                    || !string.Equals(inProgressHeader.FirstOrDefault(), "true", StringComparison.Ordinal))
                {
                    continue;
                }

                long length = Assert.IsType<long>(live.Content.Headers.ContentLength);
                byte[] body = await live.Content.ReadAsByteArrayAsync(Ct);
                var candidate = Fmp4File.Probe(path, body);
                if (candidate.MoofCount == 0)
                    continue;

                relativePath = path;
                snapshot = length;
                probe = candidate;
                break;
            }

            if (probe is null)
                Thread.Sleep(500);
        }

        Assert.NotNull(probe);
        output.WriteLine(relativePath + ": " + probe);

        // イベント録画は一度も開始していないので、出てくるのは常時録画のセグメントだけ。
        Assert.Contains("_c", relativePath, StringComparison.Ordinal);
        Assert.True(probe.StartsWithInitSegment, "書き込み中のセグメントの先頭が ftyp+moov ではない: " + probe);
        Assert.True(probe.HasMvex, "moov に mvex が無い（fragmented ではない）: " + probe);

        // 伸びが Range で追えること（追いかけ再生の土台）。
        long grown = 0;
        var growing = Stopwatch.StartNew();
        while (growing.Elapsed < SegmentBudget && grown == 0)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "api/recordings/" + Uri.EscapeDataString(relativePath));
            request.Headers.Range = new RangeHeaderValue(snapshot, null);
            using var more = await client.SendAsync(request, Ct);

            if (more.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // まだ 1 バイトも増えていない。次の fragment を待つ。
                Thread.Sleep(250);
                continue;
            }

            Assert.Equal(HttpStatusCode.PartialContent, more.StatusCode);
            grown = Assert.IsType<long>(more.Content.Headers.ContentRange?.Length);
            byte[] tail = await more.Content.ReadAsByteArrayAsync(Ct);
            Assert.Equal(grown - snapshot, tail.Length);
        }

        output.WriteLine($"total {snapshot} -> {grown}");
        Assert.True(snapshot < grown, $"書き込み中のセグメントが伸びていない（{snapshot} -> {grown}）。");

        // セグメントが切り替わると、前のセグメントは確定して inProgress が下りる。
        JsonElement finished = default;
        var rotating = Stopwatch.StartNew();
        while (rotating.Elapsed < SegmentBudget && finished.ValueKind != JsonValueKind.Object)
        {
            var (_, files) = await ListAsync(client);
            foreach (var file in files)
            {
                if (!string.Equals(file.GetProperty("path").GetString(), relativePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (file.GetProperty("inProgress").GetBoolean())
                    continue;

                finished = file;
                break;
            }

            if (finished.ValueKind != JsonValueKind.Object)
                Thread.Sleep(500);
        }

        Assert.Equal(JsonValueKind.Object, finished.ValueKind);
        output.WriteLine(finished.ToString());
        Assert.True(finished.GetProperty("fragmented").GetBoolean(),
            "確定したセグメントが fragmented として出ていない: " + finished);

        var (_, after) = await ListAsync(client);
        Assert.True(1 < after.Length, $"セグメントが切り替わっていない（{after.Length} 本）。");

        // 確定したセグメントは単体で読める（moov(mvex) ＋ moof/mdat の対）。
        var complete = Fmp4File.Probe(Path.Combine(instance.RecordingsDir, relativePath));
        output.WriteLine(complete.ToString());
        Assert.True(complete.StartsWithInitSegment, complete.ToString());
        Assert.True(complete.HasMvex, complete.ToString());
        Assert.True(0 < complete.MoofCount, complete.ToString());
        Assert.True(complete.MediaSegmentsAlternate(), "moof/mdat の対になっていない: " + complete);
    }

    // ---- 書き込み中のファイルが「他プロセスから見て」伸びる刻み ----

    /// <summary>伸びを測るあいだセグメントを切り替えさせない長さ(秒)。</summary>
    private const int LongSegmentSeconds = 60;

    /// <summary>伸びを測る時間。fragment は 1 秒ごとなので、この窓で 20 前後の標本が取れる。</summary>
    private static readonly TimeSpan GrowthWindow = TimeSpan.FromSeconds(25);

    /// <summary>伸びを見に行く間隔。fragment 長（1 秒）より十分に細かい。</summary>
    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 許す伸びの間隔の中央値(秒)。<c>fragment-duration</c> は 1 秒で、
    /// 余裕を見てもこれを超えてはいけない。
    /// </summary>
    private const double MaxMedianGrowthGapSeconds = 1.5;

    /// <summary>中央値に意味を持たせるために要る標本の数。</summary>
    private const int MinGrowthSamples = 10;

    /// <summary><c>filesink</c> が既定で溜める量(bytes)。</summary>
    private const int FilesinkBufferSize = 65536;

    /// <summary>常時録画の枝のフレームレート。</summary>
    private const int SlowContinuousFps = 4;

    /// <summary>
    /// 常時録画の枝のエンコーダー起動文字列。<b>ビットレートを明示するのが要点である。</b>
    ///
    /// <para>
    /// 見えるファイル長が遅れるかどうかは「fragment 1 つが <c>filesink</c> の
    /// <c>buffer-size</c>（65536）を埋めるか」で決まる。自動選択に任せると
    /// 2000kbit/s が入り、毎秒あふれるので<b>遅れが起きていても観測できない</b>
    /// ── 判別力が「静止した画がどこまで縮むか」という測っていない性質に乗ってしまう。
    /// 150kbit/s なら 1 秒ぶんは約 18KiB で、何を撮っていても埋まらない。
    /// </para>
    /// <para>
    /// <b>GOP は固定する</b>（<c>key-int-max</c> ＝ フレームレート × 2 秒）── 手書きすると
    /// エンコーダー既定の長い GOP になり、分割がセグメント長どおりに起きない。
    /// 値は自動選択が入れるもの（<c>EncoderCatalog</c>）と同じで、変えたのは
    /// <c>bitrate</c> だけである。
    /// </para>
    /// </summary>
    private const string SlowContinuousEncoder =
        "x264enc tune=zerolatency bitrate=150 speed-preset=ultrafast key-int-max=8";

    /// <summary>低ビットレートで回す常時録画（伸びの刻みを測るための構成）。</summary>
    private static SettingsFile SlowFragmentedContinuousSettings()
    {
        var settings = RemoteSettings();
        settings.FragmentedOutput = true;
        var recorder = settings.Recorders[0];
        recorder.WithContinuous(LongSegmentSeconds);
        recorder.ContinuousFramerate = SlowContinuousFps.ToString(CultureInfo.InvariantCulture) + "/1";
        recorder.ContinuousResolution = "320x240";
        recorder.ContinuousEncodingProperties = SlowContinuousEncoder;
        return settings;
    }

    /// <summary>伸びの観測結果。</summary>
    /// <param name="Gaps">伸びた時刻の間隔(秒)。</param>
    /// <param name="Grown">窓のあいだに増えた総バイト数。</param>
    /// <param name="Seconds">実際に測っていた時間(秒)。</param>
    private readonly record struct GrowthObservation(
        IReadOnlyList<double> Gaps, long Grown, double Seconds);

    /// <summary>
    /// 書き込み中のセグメントを共有読み取りで開き、<b>ハンドルから見た長さ</b>が
    /// 変わった時刻を <paramref name="window"/> のあいだ記録する。
    ///
    /// <para>
    /// <b><c>FileInfo.Length</c> では測れない</b> ── ディレクトリのメタデータは
    /// 書き込みに遅れて更新されるので、測っているものが <c>filesink</c> の蓄積なのか
    /// NTFS の遅延なのか区別できなくなる。
    /// </para>
    /// </summary>
    private static GrowthObservation MeasureGrowth(string path, TimeSpan window)
    {
        var gaps = new List<double>();
        long first = -1;
        long last = -1;
        double lastChange = 0;
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < window)
        {
            long length;
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                length = stream.Length;
            }
            catch (IOException)
            {
                Thread.Sleep(GrowthPollInterval);
                continue;
            }

            if (last < 0)
            {
                first = length;
                last = length;
                lastChange = clock.Elapsed.TotalSeconds;
            }
            else if (last < length)
            {
                double now = clock.Elapsed.TotalSeconds;
                gaps.Add(now - lastChange);
                lastChange = now;
                last = length;
            }

            Thread.Sleep(GrowthPollInterval);
        }

        return new GrowthObservation(
            gaps, first < 0 ? 0 : last - first, clock.Elapsed.TotalSeconds);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        double[] sorted = [.. values.Order()];
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// <b>書き込み中の fMP4 は、他プロセスから見ても fragment ごとに伸びる。</b>
    ///
    /// <para>
    /// 既定の <c>filesink</c> は受け取ったバッファを自分の中に溜め、<c>buffer-size</c>
    /// （既定 65536）に届いてから 1 度に書くので、mux が 1 秒ごとに fragment を出していても
    /// <b>他のプロセスから見えるファイル長は 64 KiB 溜まるまで伸びない</b> ──
    /// 低ビットレートでは数秒に 1 度しか伸びず、ブラウザの追いかけ再生はデータ切れで
    /// カタつく。強制終了では末尾の 64 KiB が失われる。
    /// </para>
    /// <para>
    /// ここが見ているのは<b>伸びの間隔の中央値</b>である。総量や平均では検出できない
    /// ── まとめて書かれても総量は同じで、平均も窓の長さで割れば同じになる。
    /// </para>
    /// <para>
    /// <b>成立条件は「実効ビットレートが低いこと」</b>で、そこは
    /// <see cref="SlowContinuousEncoder"/> が明示している ── それでも撮っている画に
    /// よらず成り立つとは限らないので、<b>測った実効速度そのものを前提として表明する</b>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFragmentedFileGrows_OnEveryFragment_NotEvery64KiB()
    {
        using var instance = AppInstance.Create(app, SlowFragmentedContinuousSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        // 書き込み中のセグメントが fragment を持って一覧に出るまで待つ
        // （常時録画はイベント録画の開始を待たない）。
        string relativePath = "";
        var appearing = Stopwatch.StartNew();
        while (appearing.Elapsed < SegmentBudget && relativePath.Length == 0)
        {
            var (_, files) = await ListAsync(client);
            foreach (var file in files)
            {
                if (file.GetProperty("inProgress").GetBoolean()
                    && file.GetProperty("fragmented").GetBoolean())
                {
                    relativePath = file.GetProperty("path").GetString()!;
                    break;
                }
            }

            if (relativePath.Length == 0)
                Thread.Sleep(500);
        }

        Assert.False(relativePath.Length == 0,
            "書き込み中のセグメントが fragmented として一覧に出ませんでした。"
            + Environment.NewLine + instance.DiagnosticDump());

        string full = Path.Combine(instance.RecordingsDir, relativePath);
        var growth = MeasureGrowth(full, GrowthWindow);
        var gaps = growth.Gaps;

        double bytesPerSecond = growth.Grown / growth.Seconds;
        output.WriteLine(
            $"{relativePath}: {gaps.Count} growths in {growth.Seconds:F1}s, "
            + $"{growth.Grown} bytes ({bytesPerSecond:F0} bytes/s)");
        output.WriteLine("gaps: " + string.Join(", ", gaps.Select(g => g.ToString("F2"))));

        // **前提の表明。** 溜める実装と溜めない実装を区別できるのは、fragment 1 つが
        // buffer-size を埋めないときだけである。実効速度がこれを超えていたら、
        // 溜める実装でも毎秒あふれて間隔が縮み、この検査は何も言っていない。
        double maxBytesPerSecond = FilesinkBufferSize / MaxMedianGrowthGapSeconds;
        Assert.True(bytesPerSecond < maxBytesPerSecond,
            $"実効ビットレートが高すぎます（{bytesPerSecond:F0} bytes/s ≧ {maxBytesPerSecond:F0} bytes/s）"
            + " ── この検査は成立しません。");

        Assert.True(MinGrowthSamples <= gaps.Count,
            $"伸びが {gaps.Count} 回しか観測できませんでした（{growth.Seconds:F1} 秒）。"
            + Environment.NewLine + instance.DiagnosticDump());

        double median = Median(gaps);
        output.WriteLine($"median growth gap: {median:F2}s");
        Assert.True(median <= MaxMedianGrowthGapSeconds,
            $"見えるファイル長の伸びが fragment ごとになっていません（中央値 {median:F2} 秒）。"
            + " filesink が溜めていると、64 KiB 溜まるまで伸びません。");

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "continuous.error"));
    }

    /// <summary>
    /// <b>強制終了しても、そこまでの fragment が読める。</b> <c>faststart</c> 側では
    /// kill されたファイルは 0 バイト（＝録画が丸ごと失われる）。
    /// </summary>
    [Fact]
    public void AKilledFragmentedRecording_KeepsWhatItHadWritten()
    {
        using var instance = AppInstance.Create(app, FragmentedSettings(), configure: UseIsolatedRoot);
        WaitForPort(instance);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        Thread.Sleep(TimeSpan.FromSeconds(5));

        instance.KillWorkers();

        string full = Assert.Single(instance.ListRecordings());
        var probe = Fmp4File.Probe(full);
        output.WriteLine(probe.ToString());

        Assert.True(probe.StartsWithInitSegment, "kill されたファイルの先頭が ftyp+moov ではない: " + probe);
        Assert.True(probe.HasMvex, probe.ToString());
        Assert.True(0 < probe.MoofCount, "kill されたファイルに moof が 1 つも無い: " + probe);
        Assert.True(probe.MediaSegmentsAlternate(), "moof/mdat の対になっていない: " + probe);
    }

    // ---- Range と条件付き要求 ----

    /// <summary>
    /// 完結したファイルが、全体・末尾の部分・範囲外・再検証のそれぞれに正しく答えること。
    /// <b>シークはこれが動いていないと成立しない</b>（ブラウザは末尾の <c>moov</c> を
    /// 部分要求で読む）。
    /// </summary>
    [Fact]
    public async Task AFinishedRecording_ServesRangesAndRevalidates()
    {
        using var instance = AppInstance.Create(app, RemoteSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string name = RecordOnce(instance);
        byte[] expected = File.ReadAllBytes(Path.Combine(instance.RecordingsDir, name));
        string url = "api/recordings/" + Uri.EscapeDataString(name);

        string etag;
        using (var whole = await client.GetAsync(url, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
            Assert.Equal("video/mp4", whole.Content.Headers.ContentType?.MediaType);
            Assert.Contains("bytes", whole.Headers.AcceptRanges);
            Assert.Equal("no-cache", whole.Headers.CacheControl?.ToString());

            Assert.NotNull(whole.Headers.ETag);
            etag = whole.Headers.ETag.ToString();
            output.WriteLine("ETag: " + etag);
            Assert.Equal(expected, await whole.Content.ReadAsByteArrayAsync(Ct));
        }

        const int TailLength = 1024;
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.Range = new RangeHeaderValue(null, TailLength);
            using var tail = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.PartialContent, tail.StatusCode);
            Assert.Equal(
                $"bytes {expected.Length - TailLength}-{expected.Length - 1}/{expected.Length}",
                tail.Content.Headers.ContentRange?.ToString());
            Assert.Equal(expected[^TailLength..], await tail.Content.ReadAsByteArrayAsync(Ct));
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.Range = new RangeHeaderValue(expected.Length + 10, null);
            using var beyond = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.StatusCode);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var revalidated = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        }
    }

    // ---- パス ----

    /// <summary>
    /// 配信 root の外・録画以外の拡張子・存在しないファイルの断り方。
    ///
    /// <para>
    /// <b>「無い」だけが 404 で、規則で断ったものは 400。</b> 逆にすると、
    /// 断り方の違いだけで root の外のファイルの存在を当てられる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task PathsOutsideTheRootAreRejected()
    {
        using var instance = AppInstance.Create(app, RemoteSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var missing = await client.GetAsync("api/recordings/nope.mp4", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var body = await ReadJsonAsync(missing);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        using (var wrongExtension = await client.GetAsync("api/recordings/a.txt", Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongExtension.StatusCode);
            using var body = await ReadJsonAsync(wrongExtension);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        using (var absolute = await client.GetAsync("api/recordings/C:%5Cx.mp4", Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, absolute.StatusCode);
            using var body = await ReadJsonAsync(absolute);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        // 親への遡り、その 1: 区切りも符号化したもの（%2e%2e%2f）は**ハンドラーに届く前に畳まれる**。
        // `Uri` は %2e だけを '.' へ戻し %2f は符号化のまま残すので、線上は
        // /api/recordings/..%2fx.mp4 の 1 セグメント。これを復号したサーバー側の
        // 正規化がドットセグメントを取り除き、ハンドラーが受け取るのは x.mp4 になる
        // ── だから 404 の理由は「規則で断った（path rejected）」ではなく
        // 「無い（not found）」である（実測）。経路が MapFallback（`{*path:nonfile}`）へ
        // 落ちているのではない ── 落ちていれば理由は unknown endpoint になる。
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(client.BaseAddress!, "api/recordings/%2e%2e%2fx.mp4")))
        using (var encoded = await client.SendAsync(request, Ct))
        {
            string body = await encoded.Content.ReadAsStringAsync(Ct);
            output.WriteLine($"%2e%2e%2f sent as {request.RequestUri!.PathAndQuery} -> {(int)encoded.StatusCode} {body}");

            Assert.Equal("/api/recordings/..%2fx.mp4", request.RequestUri.PathAndQuery);
            Assert.Equal(HttpStatusCode.NotFound, encoded.StatusCode);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(4, json.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("not found", json.RootElement.GetProperty("error").GetString());
        }

        // 親への遡り、その 2: 区切りが '\'（%5C）なら 1 セグメントのまま**ハンドラーへ届く**
        // ── ここで初めて ".." を断る規則（TryResolveUnderRoot）が働く。
        // root の外に本物のファイルを置いてあるので、断りが破れれば中身が返ってしまう。
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(instance.RecordingsDir)!, "outside.mp4"), new byte[16]);

        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(client.BaseAddress!, "api/recordings/..%5Coutside.mp4")))
        using (var traversal = await client.SendAsync(request, Ct))
        {
            string body = await traversal.Content.ReadAsStringAsync(Ct);
            output.WriteLine($"..%5C -> {(int)traversal.StatusCode} {body}");

            Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
            Assert.Equal("application/json", traversal.Content.Headers.ContentType?.MediaType);

            using var json = JsonDocument.Parse(body);
            Assert.Equal(4, json.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("path rejected", json.RootElement.GetProperty("error").GetString());
        }
    }

    // ---- ダウンロード ----

    /// <summary>
    /// <c>?download=1</c> が <c>Content-Disposition: attachment</c> を付け、
    /// <b>非 ASCII のファイル名が <c>filename*=UTF-8''</c> で運ばれる</b>こと。
    /// 自前で組み立てると必ず間違える部分なので、フレームワークに書かせている。
    /// </summary>
    [Fact]
    public async Task TheDownloadQuery_AttachesTheEncodedFilename()
    {
        var settings = RemoteSettings();

        using var instance = AppInstance.Create(app, settings, configure: i =>
        {
            UseIsolatedRoot(i);
            // 日本語を含むファイル名。データディレクトリは ASCII のままにしてある。
            i.Settings.Recorders[0].FilenameTemplate =
                Path.Combine(i.RecordingsDir, "録画_{Name}_{Now:HHmmssfff}.mp4");
        });

        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        string name = RecordOnce(instance);
        Assert.StartsWith("録画_", name, StringComparison.Ordinal);

        string url = "api/recordings/" + Uri.EscapeDataString(name);

        using (var attachment = await client.GetAsync(url + "?download=1", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, attachment.StatusCode);

            string disposition = Assert.Single(attachment.Content.Headers.GetValues("Content-Disposition"));
            output.WriteLine(disposition);

            Assert.StartsWith("attachment", disposition, StringComparison.Ordinal);
            Assert.Contains("filename*=UTF-8''", disposition, StringComparison.Ordinal);
            // 「録」の UTF-8 パーセント符号化。ここが素通しだとヘッダーが壊れる。
            Assert.Contains("%E9%8C%B2", disposition, StringComparison.OrdinalIgnoreCase);
        }

        using (var inline = await client.GetAsync(url, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, inline.StatusCode);
            Assert.False(
                inline.Content.Headers.TryGetValues("Content-Disposition", out var values)
                    && values.Any(v => v.StartsWith("attachment", StringComparison.OrdinalIgnoreCase)),
                "download を付けていないのに attachment が返った。");
        }
    }

    // ---- Web UI ----

    /// <summary>
    /// 埋め込みの Web UI が発行物から配られること。
    /// <b>これは発行の検査でもある</b> ── 資産が入っていなければ、
    /// サーバーは待ち受ける前に落ちる。
    /// </summary>
    [Fact]
    public async Task TheEmbeddedWebUiIsServed()
    {
        using var instance = AppInstance.Create(app, RemoteSettings(), configure: UseIsolatedRoot);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var index = await client.GetAsync("/", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Equal("text/html", index.Content.Headers.ContentType?.MediaType);

            string html = await index.Content.ReadAsStringAsync(Ct);
            Assert.Contains("<title>ProcessRecorderApp</title>", html, StringComparison.Ordinal);
            Assert.Contains("app.js", html, StringComparison.Ordinal);
        }

        string etag;
        using (var script = await client.GetAsync("app.js", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);

            Assert.NotNull(script.Headers.ETag);
            etag = script.Headers.ETag.ToString();
            Assert.Equal("no-cache", script.Headers.CacheControl?.ToString());
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, "app.js"))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var revalidated = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        }

        using (var style = await client.GetAsync("app.css", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, style.StatusCode);
            Assert.Equal("text/css", style.Content.Headers.ContentType?.MediaType);
        }

        // 台帳に無い 1 セグメントは 404（本文は HTTP 層の失敗と同じ形）。
        using (var unknown = await client.GetAsync("nope.js", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            using var body = await ReadJsonAsync(unknown);
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
        }
    }

    /// <summary>
    /// <c>PROCESSRECORDERAPP_WEBROOT</c> がディスクの資産で上書きすること、
    /// <b>置かれていない名前は埋め込みへ戻る</b>こと（上書きは一部だけでも成立する）。
    /// </summary>
    [Fact]
    public async Task TheWebRootVariableOverridesOnlyTheFilesItHas()
    {
        const string Marker = "<!doctype html><title>overridden</title>";

        using var instance = AppInstance.Create(app, RemoteSettings(), configure: i =>
        {
            UseIsolatedRoot(i);
            string webRoot = Path.Combine(i.DataDir, "webroot");
            Directory.CreateDirectory(webRoot);
            File.WriteAllText(Path.Combine(webRoot, "index.html"), Marker, new UTF8Encoding(false));
            i.ExtraEnvironment["PROCESSRECORDERAPP_WEBROOT"] = webRoot;
        });

        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        using (var index = await client.GetAsync("/", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Equal(Marker, await index.Content.ReadAsStringAsync(Ct));
        }

        using (var script = await client.GetAsync("app.js", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            // 上書きが無い名前は埋め込みのまま（SSE の購読はこのファイルにしかない）。
            Assert.Contains("EventSource", await script.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }
}
