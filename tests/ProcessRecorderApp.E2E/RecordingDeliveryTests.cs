using System.Diagnostics;
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
            // **このクラスの主題は認証ではない。** 波 3 で読み取りにも役割が要るように
            // なったので、ゲスト読み取りを明示して従来どおり未認証で読ませる
            // ── 認証そのものは RemoteControlTests が見る。
            RemoteControlAllowGuestRead = true,
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

        // 録画中でも本文が取れること。moov は未確定なので再生はできないが、
        // 「握られているから読めない」であってはならない。
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

        var (_, finished) = await ListAsync(client);
        var done = Assert.Single(finished);
        Assert.False(done.GetProperty("inProgress").GetBoolean());
        Assert.Equal(relativePath, done.GetProperty("path").GetString());

        string full = Assert.Single(instance.ListRecordings());
        Assert.Equal(new FileInfo(full).Length, done.GetProperty("length").GetInt64());

        // 更新時刻は ISO-8601 で載る（文字列ではなく時刻として突き合わせる）。
        Assert.Equal(
            File.GetLastWriteTimeUtc(full),
            done.GetProperty("lastWriteTimeUtc").GetDateTime(),
            TimeSpan.FromSeconds(2));
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
    /// <b>fragmented なら録画中でも中身が読める。</b> 既定（<c>faststart=true</c>）では
    /// 録画中のファイルは 0 バイトで、再生できる形が 1 バイトも無い
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

        var (_, done) = await ListAsync(client);
        Assert.False(Assert.Single(done).GetProperty("inProgress").GetBoolean());
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
        // **尺は 0 のまま**（moov を書き直さないので mvhd の duration が伸びない）。
        // だから `<video src>` 直結では 1 秒に見え、ブラウザ側は完成後も MSE 経路で読む。
        Assert.True(asPlainMp4.DurationSeconds is null or 0, "fragmented なのに mvhd の尺が入っている: " + asPlainMp4);
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
    /// 既定（<c>FragmentedOutput=false</c>）では書き込み中のセグメントに
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
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                Assert.Equal("true", Assert.Single(live.Headers.GetValues("X-In-Progress")));

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

    /// <summary>
    /// <b>強制終了しても、そこまでの fragment が読める。</b> 既定の <c>faststart</c> では
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
