using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// DASH プレビューの配信（<c>GET /api/recorders/{id}/dash/{file}</c>）。
///
/// <para>
/// <b>ここが第 2 パイプラインを初めて実際に走らせる層である。</b> L1 が縛るのは
/// パイプライン文字列と純関数だけで、<b>本物の GStreamer が実 caps から何を吐くか</b>
/// ── ネゴシエートが通るか、<c>mp4mux</c> の fragment が IDR で切れるか、
/// <c>tfdt</c> の刻みが MPD の <c>SegmentTimeline</c> と一致するか ── は、
/// 発行物を起こさなければ 1 バイトも観測できない。
/// </para>
/// <para>
/// <b>「見ている」の表明は manifest を引き続けることだけである。</b> 供給側は
/// 最後に引かれた時刻から <c>DashPreviewLimits.LeaseMs</c>（10 秒）で mux を畳むので、
/// 待ちを入れるテストは<b>そのあいだも引き続けるか、畳まれることを見るか</b>の
/// どちらかしかない ── 引くのを止めたまま別のことを待つと、次に引いたときには
/// 別の連続体になっている。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class DashPreviewTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>製品が生成する形（Base64Url・43 文字）に合わせた固定トークン。</summary>
    private const string Token = "E2E-dash-preview-stream-token-0123456789-ab";

    /// <summary>
    /// 役割の差を見るケースの利用者。<b>パスワードは <see cref="RemoteUserSpec"/> の
    /// 固定ハッシュに対応するもの</b>で、名前もハッシュも L2 の認証テストと同じ。
    /// </summary>
    private const string ViewerUser = "viewer";

    /// <inheritdoc cref="ViewerUser"/>
    private const string ViewerPassword = "pw-viewer";

    /// <inheritdoc cref="ViewerUser"/>
    private const string OperatorUser = "operator";

    /// <inheritdoc cref="ViewerUser"/>
    private const string OperatorPassword = "pw-operator";

    private const string ManifestPath = "api/recorders/R1/dash/manifest.mpd";
    private const string InitPath = "api/recorders/R1/dash/init.mp4";

    private const string QualitiesPath = "api/recorders/R1/preview/qualities";
    private const string QualityPath = "api/recorders/R1/preview/quality";

    /// <summary>いま配信している画質の id（3 応答すべてに載る）。</summary>
    private const string QualityHeader = "X-Dash-Quality";

    /// <summary>MPD の名前空間（<c>DashManifest.Build</c> が書くもの）。</summary>
    private static readonly XNamespace Mpd = "urn:mpeg:dash:schema:mpd:2011";

    /// <summary>「まだ始まっていない」の正本（<c>Components.DashPreviewReasons.Starting</c>）。</summary>
    private const string StartingReason = "dash preview is starting";

    /// <summary>
    /// 「補助エンコーダー枠が空いていない」の正本
    /// （<c>Components.TranscodeReasons.Busy</c> ＝ <c>DashPreviewReasons.Busy</c>）。
    /// </summary>
    private const string BusyReason = "auxiliary encoder busy";

    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(30);

    /// <summary>短い要求（JSON・MPD・セグメント）の打ち切り。本文はすべて有限。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>初期化の記録が現れるまでの待ち。</summary>
    private static readonly TimeSpan EventBudget = TimeSpan.FromSeconds(30);

    /// <summary>manifest のポーリング間隔（クライアント側の既定と同じ 1 秒）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>リングの上限と単調性を見る窓（1 秒セグメントで 8 本ぶん）。</summary>
    private static readonly TimeSpan RingWindow = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 貸出（10 秒）が切れて mux が畳まれるのを待つ時間。
    /// <b>畳むのは次のサンプルの到着時</b>なので、10 秒ちょうどでは足りない。
    /// </summary>
    private static readonly TimeSpan LeaseExpiryWait = TimeSpan.FromSeconds(15);

    /// <summary>設定を変えてから新しい連続体が出るまでの待ち。</summary>
    private static readonly TimeSpan SettingsBudget = TimeSpan.FromSeconds(10);

    /// <summary>録画を回す時間（<c>PreviewStreamTests</c> と同じ）。</summary>
    private static readonly TimeSpan RecordingWindow = TimeSpan.FromSeconds(3);

    /// <summary>保持するセグメントの上限（<c>DashPreviewLimits.RingDepth</c>）。</summary>
    private const int RingDepth = 6;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Regex BindPattern = new(@"\bbind=([0-9.]+):(\d+)\b", RegexOptions.Compiled);

    // ---- 起動 ----

    /// <summary>
    /// リモート操作を有効にした settings.json（<c>PreviewStreamTests</c> と同じ形）。
    /// <b>ゲスト読み取りを明示する</b> ── このクラスの主題は認証ではない。
    /// </summary>
    /// <param name="allowGuestRead">名乗っていない相手に読み取りを許すか。</param>
    /// <param name="withRoles">
    /// <c>Viewer</c> と <c>Operator</c> の利用者を書くか。
    /// <b>ゲストでは役割の差を見られない</b> ── 名乗っていない相手の書き込みは
    /// 役割の手前で 401 になるので、403 を要求するケースには実在の利用者が要る。
    /// </param>
    private static SettingsFile PreviewSettings(bool allowGuestRead = true, bool withRoles = false)
    {
        var settings = new SettingsFile
        {
            // FragmentedOutput は書かない（＝製品の既定の true で走る）。
            // 127.0.0.1 に固定する（0.0.0.0 だと開発機と CI の LAN から到達できる）。
            RemoteControlEnabled = true,
            RemoteControlBindAddress = "127.0.0.1",
            RemoteControlPort = 0,
            RemoteControlAccessToken = Token,
            RemoteControlAllowGuestRead = allowGuestRead,
        };
        if (withRoles)
        {
            settings.RemoteUsers.Add(new RemoteUserSpec(ViewerUser, RemoteUserSpec.ViewerPasswordHash, "Viewer"));
            settings.RemoteUsers.Add(
                new RemoteUserSpec(OperatorUser, RemoteUserSpec.OperatorPasswordHash, "Operator"));
        }

        settings.AddRecorder("R1").EncodingProperties = SettingsFile.LargeEncodingProperties;
        return settings;
    }

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

    /// <summary>
    /// サーバーが起きていて、レコーダーの初期化まで終わっている実体を作る。
    /// <b>初期化を待たずに引くと 503</b>（第 2 パイプラインは枝A のサンプルで起きる）。
    /// </summary>
    /// <param name="allowGuestRead"><inheritdoc cref="PreviewSettings" path="/param[@name='allowGuestRead']"/></param>
    /// <param name="withRoles"><inheritdoc cref="PreviewSettings" path="/param[@name='withRoles']"/></param>
    private (AppInstance Instance, int Port) StartReady(bool allowGuestRead = true, bool withRoles = false)
    {
        var instance = AppInstance.Create(app, PreviewSettings(allowGuestRead, withRoles));
        int port = WaitForPort(instance);

        Assert.True(instance.WaitForActivityLogEvent("recorder.init ok", EventBudget),
            "recorder.init ok が現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        return (instance, port);
    }

    private static HttpClient CreateClient(int port) =>
        new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = RequestTimeout,
        };

    // ---- MPD の読み取り ----

    private static XElement PeriodOf(XDocument mpd) => mpd.Descendants(Mpd + "Period").Single();

    private static XElement TemplateOf(XDocument mpd) => mpd.Descendants(Mpd + "SegmentTemplate").Single();

    private static XElement RepresentationOf(XDocument mpd) => mpd.Descendants(Mpd + "Representation").Single();

    private static int GenerationOf(XDocument mpd)
        => int.Parse(PeriodOf(mpd).Attribute("id")!.Value, CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>SegmentTimeline</c> の <c>t</c>（文字列のまま ── URL へ入る値そのもので、
    /// 64bit の刻みは JavaScript でも .NET の <c>double</c> でも正確とは限らない）。
    /// </summary>
    private static string[] TimesOf(XDocument mpd)
        => [.. mpd.Descendants(Mpd + "S").Select(s => s.Attribute("t")!.Value)];

    /// <summary>
    /// 200 が返るまで manifest を引く。<b>503 の間はその本文が
    /// <see cref="StartingReason"/> であることを毎回確かめる</b> ── 別の理由
    /// （エンコーダーが尽きた等）で待ち続けてしまうと、失敗が「時間切れ」としてしか現れない。
    /// </summary>
    private async Task<XDocument> WaitForManifestAsync(HttpClient client, TimeSpan budget, string label)
    {
        var watch = Stopwatch.StartNew();
        int polls = 0;
        string last = "(1 度も応答を読めていない)";

        while (watch.Elapsed < budget)
        {
            polls++;
            using var response = await client.GetAsync(ManifestPath, Ct);
            string body = await response.Content.ReadAsStringAsync(Ct);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal("application/dash+xml", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                Assert.True(response.Headers.Contains("X-Dash-Generation"),
                    "200 の応答に X-Dash-Generation がありません。");
                Assert.False(string.IsNullOrEmpty(QualityOf(response)),
                    $"200 の応答に {QualityHeader} がありません。");

                var mpd = XDocument.Parse(body);
                Assert.Equal(
                    GenerationOf(mpd).ToString(CultureInfo.InvariantCulture),
                    response.Headers.GetValues("X-Dash-Generation").Single());

                output.WriteLine(
                    $"{label}: 200 after {watch.Elapsed.TotalSeconds:F1}s ({polls} polls), "
                    + $"generation={GenerationOf(mpd)} S={TimesOf(mpd).Length}");
                return mpd;
            }

            // 「まだ始まっていない」だけが待ってよい失敗で、Retry-After が付く。
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);

            using var reason = JsonDocument.Parse(body);
            Assert.Equal(12, reason.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal(StartingReason, reason.RootElement.GetProperty("error").GetString());

            last = body;
            await Task.Delay(PollInterval, Ct);
        }

        Assert.Fail(
            $"{label}: manifest が {budget.TotalSeconds:F0} 秒以内に 200 になりませんでした"
            + $"（{polls} 回引いた）。最後の応答: {last}");
        return null!;
    }

    /// <summary>
    /// 本文を取る（<c>manifest.mpd</c> 以外の 2 つ）。
    ///
    /// <para>
    /// <b><c>X-Dash-Generation</c> は 3 応答すべてに要る。</b> どの連続体の一部かが
    /// 分からないと、受け手は「init と噛み合わないセグメント」を掴んだことに
    /// 気付けない ── 絵が出ないだけで、HTTP はどれも 200 のままになる。
    /// <paramref name="generation"/> を渡したときは値まで照合する
    /// （渡さない呼び出しは、その時点の generation を当てにできないもの）。
    /// </para>
    /// </summary>
    private async Task<byte[]> GetBytesAsync(
        HttpClient client, string path, string expectedContentType, int? generation = null)
    {
        using var response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        Assert.True(response.Headers.Contains("X-Dash-Generation"),
            path + " の応答に X-Dash-Generation がありません。");
        Assert.False(string.IsNullOrEmpty(QualityOf(response)),
            path + $" の応答に {QualityHeader} がありません。");
        string served = response.Headers.GetValues("X-Dash-Generation").Single();
        Assert.True(
            int.TryParse(served, NumberStyles.None, CultureInfo.InvariantCulture, out int servedGeneration),
            path + " の X-Dash-Generation が 10 進数ではありません: '" + served + "'");
        if (generation is { } expected)
        {
            Assert.True(expected == servedGeneration,
                $"{path} の X-Dash-Generation が {servedGeneration} で、manifest の {expected} と違います。");
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        // Content-Length は明示している（chunked にすると、途中で切れた本文と
        // 「短いセグメント」を受け手が区別できない）。
        Assert.Equal(bytes.LongLength, response.Content.Headers.ContentLength ?? -1);
        return bytes;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        byte[] joined = new byte[first.Length + second.Length];
        first.CopyTo(joined, 0);
        second.CopyTo(joined, first.Length);
        return joined;
    }

    // ---- (1) 配られるもの ----

    /// <summary>
    /// <b>MPD・Init・そこに並んだ全セグメントが実際に配られること。</b>
    ///
    /// <para>
    /// セグメントは<b>先頭サンプルが同期サンプル</b>でなければならない ── DASH の
    /// クライアントはセグメントの先頭から復号を始めるので、そうでないものを配ると
    /// 「つながっているのに絵が出ない」という無音の失敗になる。判定は init と
    /// 連結してから <see cref="Fmp4File"/> に通す（<c>trex</c> の既定値まで見るため）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheManifestInitAndEverySegmentAreServed()
    {
        var (instance, port) = StartReady();
        using (instance)
        {
            using var client = CreateClient(port);

            var mpd = await WaitForManifestAsync(client, StartBudget, "first");

            Assert.Equal("dynamic", mpd.Root!.Attribute("type")!.Value);

            uint timescale = uint.Parse(TemplateOf(mpd).Attribute("timescale")!.Value, CultureInfo.InvariantCulture);
            Assert.True(0 < timescale, "timescale が 0 です。");

            string[] times = TimesOf(mpd);
            Assert.True(1 <= times.Length, "SegmentTimeline に S が 1 つもありません。");

            string codecs = mpd.Descendants(Mpd + "AdaptationSet").Single().Attribute("codecs")!.Value;
            Assert.StartsWith("avc1.", codecs, StringComparison.Ordinal);
            output.WriteLine($"codecs={codecs} timescale={timescale} times=[{string.Join(",", times)}]");

            // この 1 件は「その manifest が指した連続体」を丸ごと引く ── 途中で
            // generation が変われば、どのみち下のセグメントが 404 になる。
            int generation = GenerationOf(mpd);

            byte[] init = await GetBytesAsync(client, InitPath, "video/mp4", generation);
            var initProbe = Fmp4File.Probe("init.mp4", init);
            output.WriteLine("init: " + initProbe);
            Assert.True(initProbe.StartsWithInitSegment, "init.mp4 が ftyp → moov で始まっていない: " + initProbe);
            Assert.True(initProbe.HasMvex, "init.mp4 の moov に mvex がない（fragmented ではない）: " + initProbe);
            Assert.Equal(init.Length, initProbe.ParsedLength);

            foreach (string time in times)
            {
                byte[] segment = await GetBytesAsync(
                    client, "api/recorders/R1/dash/seg-" + time + ".m4s", "video/iso.segment", generation);

                var probe = Fmp4File.Probe("seg-" + time, Concat(init, segment));
                output.WriteLine($"seg-{time}: {segment.Length:N0} bytes {probe}");

                Assert.Equal(init.Length + segment.Length, probe.ParsedLength);
                Assert.Equal("moof", probe.Boxes[2]);
                Assert.True(0 < probe.MoofCount, "セグメントに moof がありません: " + probe);
                Assert.True(probe.MoofStartsWithSync[0],
                    $"seg-{time} の先頭サンプルが同期サンプルではありません（そこから参加した"
                    + "クライアントは復号を始められない）: " + probe);
            }
        }
    }

    // ---- (2) リングと貸出 ----

    /// <summary>
    /// <b>保持は有界で、読まれなくなれば畳まれること。</b>
    ///
    /// <para>
    /// リングは <c>RingDepth = 6</c> 本で、<c>SegmentTimeline</c> の <c>t</c> は
    /// 単調に増える。引くのを止めれば <c>lease expired</c> で mux が消え、
    /// 引き直すと<b>新しい連続体</b>（Period の id が増えたもの）が始まる ──
    /// 前の init では新しいセグメントを復号できないので、この差は
    /// クライアントが観測できなければならない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRingIsBoundedAndTheLeaseExpires()
    {
        var (instance, port) = StartReady();
        using (instance)
        {
            using var client = CreateClient(port);

            var first = await WaitForManifestAsync(client, StartBudget, "first");
            int firstGeneration = GenerationOf(first);

            ulong highest = 0;
            int polls = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < RingWindow)
            {
                var mpd = await WaitForManifestAsync(client, StartBudget, $"ring[{polls}]");
                polls++;

                string[] times = TimesOf(mpd);
                Assert.True(times.Length <= RingDepth,
                    $"S が {times.Length} 件あります（リングの上限は {RingDepth}）: [{string.Join(",", times)}]");
                Assert.Equal(firstGeneration, GenerationOf(mpd));

                ulong previous = 0;
                for (int i = 0; i < times.Length; i++)
                {
                    ulong value = ulong.Parse(times[i], CultureInfo.InvariantCulture);
                    if (0 < i)
                    {
                        Assert.True(previous < value,
                            $"S@t が単調に増えていません: [{string.Join(",", times)}]");
                    }
                    previous = value;
                }

                Assert.True(highest <= previous,
                    $"取り直した MPD の末尾が前より小さい（{previous} < {highest}）。");
                highest = previous;

                await Task.Delay(PollInterval, Ct);
            }

            output.WriteLine($"ring: {polls} polls, highest t={highest}");

            // 引くのを止める。貸出が切れると mux は次のサンプルで畳まれる。
            await Task.Delay(LeaseExpiryWait, Ct);

            var stops = ActivityLogFile.Events(instance.ReadActivityLog(), "dash.stream-stop");
            output.WriteLine(string.Join(Environment.NewLine, stops));
            Assert.Contains(stops, line =>
                ActivityLogFile.DetailOf(line).Contains("reason=lease expired", StringComparison.Ordinal));

            // 引き直すと新しい連続体が始まる（まずは starting に戻っている）。
            using (var restarted = await client.GetAsync(ManifestPath, Ct))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, restarted.StatusCode);
                using var reason = JsonDocument.Parse(await restarted.Content.ReadAsStringAsync(Ct));
                Assert.Equal(StartingReason, reason.RootElement.GetProperty("error").GetString());
            }

            var second = await WaitForManifestAsync(client, StartBudget, "after-lease");
            output.WriteLine($"generation {firstGeneration} -> {GenerationOf(second)}");
            Assert.True(firstGeneration < GenerationOf(second),
                $"貸出が切れて組み直したのに generation が増えていません（{firstGeneration}）。");
        }
    }

    // ---- (3) 設定の反映 ----

    /// <summary>
    /// <b>プレビューの 4 設定が配信物に効くこと。</b> 幅と高さを PATCH すると、
    /// 次のサンプルで <c>settings changed</c> として畳まれ、新しい連続体が
    /// 新しい解像度で始まる ── <c>Representation@width</c> だけでなく
    /// <b><c>init.mp4</c> のバイト列そのものが変わる</b>（<c>avcC</c> と
    /// <c>tkhd</c> が別物になる）。
    /// </summary>
    [Fact]
    public async Task ChangingThePreviewSettingsMakesANewGeneration()
    {
        var (instance, port) = StartReady();
        using (instance)
        {
            using var client = CreateClient(port);

            var before = await WaitForManifestAsync(client, StartBudget, "before");
            int beforeGeneration = GenerationOf(before);
            string beforeWidth = RepresentationOf(before).Attribute("width")!.Value;
            byte[] beforeInit = await GetBytesAsync(client, InitPath, "video/mp4");

            Assert.Equal("1280", beforeWidth);

            using (var request = new HttpRequestMessage(HttpMethod.Patch, "api/recorders/R1/settings"))
            {
                request.Headers.Add("Authorization", "Bearer " + Token);
                request.Headers.Add("X-PRApp-Client", "1");
                request.Content = new StringContent(
                    "{\"PreviewWidth\":640,\"PreviewHeight\":360}", Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request, Ct);
                output.WriteLine($"PATCH {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            // **引き続けるのが要点。** 引くのを止めると settings changed ではなく
            // lease expired で畳まれ、何を観測したのか分からなくなる。
            var watch = Stopwatch.StartNew();
            XDocument? after = null;
            while (watch.Elapsed < SettingsBudget)
            {
                using var response = await client.GetAsync(ManifestPath, Ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var mpd = XDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                    if (beforeGeneration < GenerationOf(mpd))
                    {
                        after = mpd;
                        break;
                    }
                }

                await Task.Delay(PollInterval, Ct);
            }

            Assert.True(after is not null,
                $"設定を変えてから {SettingsBudget.TotalSeconds:F0} 秒以内に generation が増えませんでした"
                + $"（{beforeGeneration} のまま）。"
                + Environment.NewLine
                + string.Join(Environment.NewLine,
                    ActivityLogFile.Events(instance.ReadActivityLog(), "dash.stream-stop"))
                + Environment.NewLine
                + string.Join(Environment.NewLine,
                    ActivityLogFile.Events(instance.ReadActivityLog(), "dash.stream-error")));

            output.WriteLine($"generation {beforeGeneration} -> {GenerationOf(after!)} "
                + $"after {watch.Elapsed.TotalSeconds:F1}s");

            Assert.Equal("640", RepresentationOf(after!).Attribute("width")!.Value);
            Assert.Equal("360", RepresentationOf(after!).Attribute("height")!.Value);

            byte[] afterInit = await GetBytesAsync(client, InitPath, "video/mp4");
            Assert.False(beforeInit.Length == afterInit.Length && beforeInit.SequenceEqual(afterInit),
                "解像度を変えたのに init.mp4 のバイト列が同じです。");
        }
    }

    // ---- (4) 録画への影響 ----

    /// <summary>
    /// <b>DASH を配信していても録画は変わらないこと</b>
    /// （<c>PreviewStreamTests.RecordingIsUnaffectedWhileThePreviewIsStreaming</c> の複製）。
    ///
    /// <para>
    /// こちらは<b>再エンコードする第 2 パイプライン</b>が同じ機械で回るので、
    /// chunked のときより負荷が高い ── 見るのは尺ではなくレート（サンプル数 ÷ 尺）で、
    /// 基準に対して ±25% に収まることを要求する。
    /// </para>
    /// </summary>
    [Fact]
    public async Task RecordingIsUnaffectedWhileDashIsStreaming()
    {
        var (instance, port) = StartReady();
        using (instance)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var baseline = Mp4File.Probe(RecordOnce(instance, seen));
            output.WriteLine("baseline: " + baseline);
            Assert.True(baseline.IsValid, "基準の録画が有効な MP4 になっていない: " + baseline);
            Assert.True(baseline.DurationSeconds >= 2, "基準の録画が 2 秒未満: " + baseline);
            // **0 と 0 は ±25% を満たす。** 下の比較が空振りしないことを先に固定する。
            Assert.True(0 < baseline.SampleCount, "基準の録画にサンプルが 1 枚も無い: " + baseline);

            using var client = CreateClient(port);
            await WaitForManifestAsync(client, StartBudget, "before-recording");

            // 録画のあいだ引き続ける（引くのを止めると mux が畳まれ、
            // 「配信中の録画」ではなくなる）。
            using var polling = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var poller = PollWhileAsync(client, polling.Token);

            var during = Mp4File.Probe(RecordOnce(instance, seen));
            output.WriteLine("during: " + during);

            await polling.CancelAsync();
            output.WriteLine($"polled the manifest {await poller} times while recording");

            Assert.True(during.IsValid, "配信中の録画が有効な MP4 になっていない: " + during);
            Assert.True(during.DurationSeconds >= 2, "配信中の録画が 2 秒未満: " + during);

            var baselineFps = Assert.NotNull(baseline.EffectiveFramerate);
            var duringFps = Assert.NotNull(during.EffectiveFramerate);
            Assert.InRange(duringFps, baselineFps * 0.75, baselineFps * 1.25);

            // 配信側の観測点（録画は止めないので、ここでしか見られない）。
            //
            // **「dash.stream-error が 1 件も無い」では縛れない。** エンコーダーの
            // 候補を先頭から試して落ちたものを不採用にしていくのは設計どおりの機械で、
            // どの候補が通るかは機械ごとに違う ── 落ちた候補は必ず 1 行残す。
            // 縛れるのは「いま動いている連続体が壊れていないこと」＝ 最後の
            // `dash.stream-start` より後に失敗が無いこと。
            var lines = instance.ReadActivityLog();
            int started = LastIndexOf(lines, "dash.stream-start");
            Assert.True(0 <= started,
                "dash.stream-start が 1 行も出ていません（第 2 パイプラインが一度も動いていない）。"
                + Environment.NewLine
                + string.Join(Environment.NewLine,
                    ActivityLogFile.Events(lines, "dash.stream-error")));

            Assert.Empty(ActivityLogFile.Events(lines.Skip(started + 1), "dash.stream-error"));
            Assert.Empty(ActivityLogFile.Events(lines, "dash.leak"));
        }
    }

    /// <summary>
    /// 指定したイベントの<b>最後</b>の行の位置（無ければ -1）。
    /// </summary>
    private static int LastIndexOf(IReadOnlyList<string> lines, string eventName)
    {
        for (int i = lines.Count - 1; 0 <= i; i--)
        {
            if (ActivityLogFile.EventNameOf(lines[i]) == eventName)
                return i;
        }

        return -1;
    }

    /// <summary>打ち切られるまで manifest を引き続ける（引いた回数を返す）。</summary>
    private async Task<int> PollWhileAsync(HttpClient client, CancellationToken stop)
    {
        int polls = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var response = await client.GetAsync(ManifestPath, stop);
                polls++;
                await Task.Delay(PollInterval, stop);
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            // 期待どおりの打ち切り。
        }

        return polls;
    }

    /// <summary>録画を 1 本作って、そのフルパスを返す。</summary>
    private string RecordOnce(AppInstance instance, HashSet<string> seen)
    {
        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        Thread.Sleep(RecordingWindow);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        string file = Assert.Single(instance.ListRecordings(), f => !seen.Contains(f));
        seen.Add(file);
        output.WriteLine(file);
        return file;
    }

    // ---- (5) 断る側 ----

    /// <summary>
    /// <b>知らない対象・知らない名前・持っていないセグメントはすべて 404 で、
    /// ゲスト読み取りが無ければ 401。</b>
    ///
    /// <para>
    /// <b>順序に意味がある。</b> 「持っていないセグメント」は、配信が始まる前に
    /// 引くと 503（まだ何も無い）になる ── 404 を見たいなら、先に manifest が
    /// 200 になっているところまで進めなければならない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task UnknownTargetsAndBadNamesAre404AndGuestsAre401()
    {
        var (instance, port) = StartReady();
        using (instance)
        {
            using var client = CreateClient(port);

            // 対象が無い ── 名前の解釈は通るので、答えるのは供給側（終了コード 13）。
            using (var response = await client.GetAsync("api/recorders/nope/dash/manifest.mpd", Ct))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                Assert.Equal(13, body.RootElement.GetProperty("exitCode").GetInt32());
            }

            // 名前が経路として成立しない ── 供給側へは行かない（HTTP 層の 4）。
            foreach (string bad in new[] { "seg-abc.m4s", "other.txt", "SEG-1.m4s", "seg-.m4s" })
            {
                using var response = await client.GetAsync("api/recorders/R1/dash/" + bad, Ct);
                string text = await response.Content.ReadAsStringAsync(Ct);
                output.WriteLine($"{bad}: {(int)response.StatusCode} {text}");
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

                using var body = JsonDocument.Parse(text);
                Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
                Assert.Equal("not found", body.RootElement.GetProperty("error").GetString());
            }

            // 配信が始まってからでないと、持っていないセグメントは 503 になる。
            await WaitForManifestAsync(client, StartBudget, "before-missing-segment");

            using (var response = await client.GetAsync(
                "api/recorders/R1/dash/seg-18446744073709551615.m4s", Ct))
            {
                string text = await response.Content.ReadAsStringAsync(Ct);
                output.WriteLine($"missing segment: {(int)response.StatusCode} {text}");
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

                using var body = JsonDocument.Parse(text);
                Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
                Assert.Equal("segment not available", body.RootElement.GetProperty("error").GetString());
            }
        }

        // ゲスト読み取りが無ければ、名乗らない要求は経路の手前で断られる。
        var (locked, lockedPort) = StartReady(allowGuestRead: false);
        using (locked)
        {
            using var client = CreateClient(lockedPort);
            using var response = await client.GetAsync(ManifestPath, Ct);
            output.WriteLine($"guest read off: {(int)response.StatusCode}");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // ---- (6') 補助エンコーダー枠 ----

    /// <summary>
    /// レコーダー 2 台・枠 1 つの構成。<b>両方とも初期化される</b>ので、どちらの DASH も
    /// 要求できる状態から始められる。
    /// </summary>
    private static SettingsFile TwoRecorderSettings(int limit)
    {
        var settings = new SettingsFile
        {
            RemoteControlEnabled = true,
            RemoteControlBindAddress = "127.0.0.1",
            RemoteControlPort = 0,
            RemoteControlAccessToken = Token,
            RemoteControlAllowGuestRead = true,
            RemoteAuxiliaryEncoderLimit = limit,
        };
        settings.AddRecorder("R1").EncodingProperties = SettingsFile.LargeEncodingProperties;
        settings.AddRecorder("R2").EncodingProperties = SettingsFile.LargeEncodingProperties;
        return settings;
    }

    /// <summary>
    /// <paramref name="expected"/> が返るまで引き続ける。
    ///
    /// <para>
    /// <b>待ってよい応答は 2 つだけ</b>: 503（まだ始まっていない）と 409（枠が空いていない）。
    /// 他が来たら即座に落とす ── 別の理由で待ち続けると、失敗が「時間切れ」としてしか現れない。
    /// </para>
    /// <para>
    /// <paramref name="keepAlive"/> を渡すと、毎回そちらも引く。<b>「見ている」の表明は
    /// 引き続けることだけ</b>なので、待っているあいだに別のレコーダーの mux が畳まれて
    /// 枠を返してしまうと、見たかった条件そのものが消える。
    /// </para>
    /// </summary>
    private async Task<string> PollUntilAsync(
        HttpClient client, string path, HttpStatusCode expected, TimeSpan budget, string label,
        string? keepAlive = null, Action<HttpResponseMessage>? inspect = null)
    {
        var watch = Stopwatch.StartNew();
        int polls = 0;
        string last = "(1 度も応答を読めていない)";

        while (watch.Elapsed < budget)
        {
            if (keepAlive is { } alive)
                (await client.GetAsync(alive, Ct)).Dispose();

            polls++;
            using var response = await client.GetAsync(path, Ct);
            string body = await response.Content.ReadAsStringAsync(Ct);

            if (response.StatusCode == expected)
            {
                output.WriteLine(
                    $"{label}: {(int)expected} after {watch.Elapsed.TotalSeconds:F1}s ({polls} polls)");
                inspect?.Invoke(response);
                return body;
            }

            Assert.True(
                response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.Conflict,
                $"{label}: 待てない応答が返りました: {(int)response.StatusCode} {body}");

            using (var reason = JsonDocument.Parse(body))
            {
                string? error = reason.RootElement.GetProperty("error").GetString();
                Assert.True(
                    error == StartingReason || error == BusyReason,
                    $"{label}: 待てない理由が返りました: {(int)response.StatusCode} {body}");
            }

            last = body;
            await Task.Delay(PollInterval, Ct);
        }

        Assert.Fail(
            $"{label}: {(int)expected} が {budget.TotalSeconds:F0} 秒以内に返りませんでした"
            + $"（{polls} 回引いた）。最後の応答: {last}");
        return "";
    }

    /// <summary>次に届く <c>state</c> の <c>auxiliaryEncodersFree</c>。</summary>
    private async Task<string> ReadStateAsync(StreamReader reader, TimeSpan budget)
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
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && current == "state")
                return line["data: ".Length..];
        }

        Assert.Fail($"SSE の 'state' が {budget.TotalSeconds:F0} 秒以内に届きませんでした。");
        return "";
    }

    /// <summary>
    /// <b>ライブ DASH はレコーダー 1 台につき補助エンコーダー枠を 1 つ占める。</b>
    /// 枠が 1 つしか無ければ、2 台目は空くまで 409 <c>auxiliary encoder busy</c> で断られ、
    /// 1 台目の貸出が切れて mux が畳まれると通るようになる。
    ///
    /// <para>
    /// <b>503 を経由するのは正常である。</b> 枠を試すのは最初のサンプルが届いたときなので、
    /// 要求の直後は「まだ始まっていない」で、そのあと 409 に変わる ── どちらも
    /// クライアントにとっては「待て」で、区別は表示だけである。
    /// </para>
    /// <para>
    /// <b>2 台目を待つあいだ 1 台目を引き続ける。</b> 引くのをやめたレコーダーは
    /// <c>LeaseMs</c>（10 秒）で mux を畳んで枠を返すので、待っているだけでは
    /// 見たかった状態（枠が埋まっている）が途中で消える。
    /// </para>
    /// <para>
    /// <b>409 に <c>Retry-After</c> は付けない。</b> 空くのは時間ではなく他人が止めたときで、
    /// 秒数を示せばそれは嘘になる（503 の「まだ始まっていない」とはそこが違う）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheLiveDashHoldsOneAuxiliaryEncoderPerRecorder()
    {
        using var instance = AppInstance.Create(app, TwoRecorderSettings(limit: 1));
        int port = WaitForPort(instance);

        // 2 台とも初期化されるまで待つ（1 台目だけでは 2 台目の要求が別の理由で 503 になる）。
        var initialized = Stopwatch.StartNew();
        while (initialized.Elapsed < EventBudget
            && ActivityLogFile.Events(instance.ReadActivityLog(), "recorder.init ok").Count < 2)
        {
            Thread.Sleep(200);
        }

        Assert.True(
            2 <= ActivityLogFile.Events(instance.ReadActivityLog(), "recorder.init ok").Count,
            "recorder.init ok が 2 件現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        using var client = CreateClient(port);
        const string SecondManifest = "api/recorders/R2/dash/manifest.mpd";

        // 1 台目が枠を取る。
        await WaitForManifestAsync(client, StartBudget, "R1");

        // 空きが 0 になったことは SSE にだけ出る。**開いて最初の state を読むだけ**
        // ── ここで長く待つと、その間 R1 を引けずに畳まれてしまう。
        using (var events = await client.GetAsync(
            "api/events", HttpCompletionOption.ResponseHeadersRead, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            using var stream = await events.Content.ReadAsStreamAsync(Ct);
            using var reader = new StreamReader(stream);

            string state = await ReadStateAsync(reader, TimeSpan.FromSeconds(5));
            output.WriteLine(state);
            using var snapshot = JsonDocument.Parse(state);
            Assert.Equal(1, snapshot.RootElement.GetProperty("auxiliaryEncoderLimit").GetInt32());
            Assert.Equal(0, snapshot.RootElement.GetProperty("auxiliaryEncodersFree").GetInt32());
        }

        // 2 台目は断られる（1 台目を引き続けながら）。
        string busy = await PollUntilAsync(
            client, SecondManifest, HttpStatusCode.Conflict, StartBudget, "R2-busy",
            keepAlive: ManifestPath,
            inspect: static response => Assert.Null(response.Headers.RetryAfter));

        using (var body = JsonDocument.Parse(busy))
        {
            Assert.Equal(BusyReason, body.RootElement.GetProperty("error").GetString());
            Assert.Equal(4, body.RootElement.GetProperty("exitCode").GetInt32());
        }

        // どちらも引くのをやめる。1 台目の貸出が切れれば mux が畳まれ、枠が戻る。
        await Task.Delay(LeaseExpiryWait, Ct);

        var stops = ActivityLogFile.Events(instance.ReadActivityLog(), "dash.stream-stop");
        output.WriteLine(string.Join(Environment.NewLine, stops));
        Assert.Contains(stops, line =>
            ActivityLogFile.DetailOf(line).Contains("reason=lease expired", StringComparison.Ordinal));

        // 空いた枠で 2 台目が通る（503 を経由するのは同じ）。
        await PollUntilAsync(client, SecondManifest, HttpStatusCode.OK, StartBudget, "R2-after-lease");
    }

    // ---- (6) ライブ画質プリセット ----

    /// <summary><see cref="QualityHeader"/> の値（付いていなければ null）。</summary>
    private static string? QualityOf(HttpResponseMessage response)
        => response.Headers.TryGetValues(QualityHeader, out var values) ? values.Single() : null;

    /// <summary>名乗ってセッション Cookie を持ち帰る（役割が応答に出ることも確かめる）。</summary>
    private async Task<string> LoginAsync(HttpClient client, string user, string password, string expectedRole)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/login");
        request.Headers.Add("X-PRApp-Client", "1");
        request.Content = new StringContent(
            $"{{\"user\":\"{user}\",\"password\":\"{password}\"}}", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(expectedRole, body.RootElement.GetProperty("role").GetString());

        return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
    }

    /// <summary>
    /// セッションを載せた要求。<b>書き込みには <c>X-PRApp-Client</c> が要る</b>
    /// ── 付け忘れると 403 が「役割が足りない」ではなく
    /// 「クライアントヘッダーが要る」になり、見たいものと別の失敗が緑に見える。
    /// </summary>
    private static HttpRequestMessage AsSession(
        HttpMethod method, string path, string cookie, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-PRApp-Client", "1");
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>画質の姿を 1 つ読む（200 と <c>no-store</c> まで確かめる）。</summary>
    private async Task<JsonElement> QualityStateAsync(HttpClient client, string cookie, string label)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, QualitiesPath);
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request, Ct);
        string text = await response.Content.ReadAsStringAsync(Ct);
        output.WriteLine($"{label}: {(int)response.StatusCode} {text}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        using var body = JsonDocument.Parse(text);
        return body.RootElement.Clone();
    }

    /// <summary>
    /// 画質を切り替える（<paramref name="cookie"/> の役割で）。応答の本体は GET と同じ形。
    /// </summary>
    private async Task SetQualityAsync(HttpClient client, string cookie, string id)
    {
        using var request = AsSession(HttpMethod.Post, QualityPath, cookie, $"{{\"id\":\"{id}\"}}");
        using var response = await client.SendAsync(request, Ct);
        string text = await response.Content.ReadAsStringAsync(Ct);
        output.WriteLine($"set {id}: {(int)response.StatusCode} {text}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        using var body = JsonDocument.Parse(text);
        Assert.Equal(id, body.RootElement.GetProperty("current").GetString());
    }

    /// <summary>
    /// <see cref="QualityHeader"/> が <paramref name="expected"/> になるまで manifest を引く。
    ///
    /// <para>
    /// <b>引き続けるのが要点。</b> 切り替えは次のサンプルで mux を組み直すことで効くので、
    /// 待っているあいだに引くのを止めると <c>lease expired</c> で畳まれ、
    /// 何を観測したのか分からなくなる（<see cref="ChangingThePreviewSettingsMakesANewGeneration"/> と同じ）。
    /// </para>
    /// </summary>
    private async Task<XDocument> WaitForServedQualityAsync(HttpClient client, string expected, string label)
    {
        var watch = Stopwatch.StartNew();
        string last = "(1 度も 200 を読めていない)";

        while (watch.Elapsed < SettingsBudget)
        {
            using var response = await client.GetAsync(ManifestPath, Ct);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                last = QualityOf(response) ?? "(ヘッダー無し)";
                var mpd = XDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                if (last == expected)
                {
                    var representation = RepresentationOf(mpd);
                    output.WriteLine(
                        $"{label}: {expected} after {watch.Elapsed.TotalSeconds:F1}s "
                        + $"({representation.Attribute("width")!.Value}x{representation.Attribute("height")!.Value}, "
                        + $"generation={GenerationOf(mpd)})");
                    return mpd;
                }
            }

            await Task.Delay(PollInterval, Ct);
        }

        Assert.Fail(
            $"{label}: {SettingsBudget.TotalSeconds:F0} 秒以内に {QualityHeader} が '{expected}' に"
            + $"なりませんでした（最後に見た値は '{last}'）。");
        return null!;
    }

    /// <summary>
    /// <b>プリセットの一覧が読め、切り替えると配信物の Representation がそれに従う。</b>
    ///
    /// <para>
    /// <b>期待値はサーバーが答えた値そのものを使う</b> ── プリセットの意味は
    /// 「ソースに対する相対」で、この機械のソース解像度は決め打ちにできない。
    /// 幅も高さもリテラルで書くと、別の画面の機械で落ちるか、
    /// あるいは<b>解決の算術を 2 か所に持つ</b>ことになる。
    /// </para>
    /// <para>
    /// <b>読みは <c>Viewer</c>、書きは <c>Operator</c>。</b> 切り替えは
    /// そのレコーダーの全視聴者に効くので、読み取りだけの役割には許さない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheQualityEndpointsListPresetsAndSwitchTheRepresentation()
    {
        var (instance, port) = StartReady(withRoles: true);
        using (instance)
        {
            using var client = CreateClient(port);

            // **先に配信を起こす。** ソースの caps を読めていないと選択肢はソース非依存の
            // 値になり、切り替えた後の Representation と突き合わせる意味が薄くなる。
            await WaitForManifestAsync(client, StartBudget, "before-quality");

            string viewer = await LoginAsync(client, ViewerUser, ViewerPassword, "Viewer");
            string operatorSession = await LoginAsync(client, OperatorUser, OperatorPassword, "Operator");

            try
            {
                var state = await QualityStateAsync(client, viewer, "viewer-get");
                Assert.Equal("custom", state.GetProperty("current").GetString());

                var qualities = state.GetProperty("qualities").EnumerateArray().ToArray();
                Assert.True(2 <= qualities.Length,
                    $"選択肢が {qualities.Length} 件しかありません（プリセット 1 つ ＋ custom が最小）。");

                // 末尾は必ず custom（「設定どおりに配る」へ戻る道）。
                Assert.Equal("custom", qualities[^1].GetProperty("id").GetString());
                Assert.Equal("Custom", qualities[^1].GetProperty("label").GetString());

                // 値の健全性は並びとは独立に見る。
                foreach (var option in qualities[..^1])
                {
                    string id = option.GetProperty("id").GetString()!;
                    Assert.Equal(id, option.GetProperty("label").GetString());
                    foreach (string field in new[] { "width", "height", "fps", "bitrateKbps" })
                    {
                        Assert.True(0 < option.GetProperty(field).GetInt32(),
                            $"{id} の {field} が 0 以下です: {option}");
                    }
                }

                // プリセットは表の順で、ソースより高いものは出ない。
                (string Id, int Height)[] order =
                    [("1080p", 1080), ("720p", 720), ("480p", 480), ("360p", 360)];
                string[] ids = qualities.Select(q => q.GetProperty("id").GetString()!).ToArray();

                // source は常に在り、読めていないときだけ JSON の null。
                var source = state.GetProperty("source");
                if (source.ValueKind == JsonValueKind.Object)
                {
                    // **ソースが読めているなら期待列は一意に導ける。** 部分列の検査では
                    // 「ソースより高いものは出ない」側を 1 つも縛れない。
                    int height = source.GetProperty("height").GetInt32();
                    int cap = height - (height % 2);

                    var expected = order.Where(entry => entry.Height <= cap).Select(entry => entry.Id).ToList();
                    if (expected.Count == 0) { expected.Add("360p"); }
                    expected.Add("custom");

                    Assert.Equal(expected, ids);
                }
                else
                {
                    // ソースが読めていないときは 4 つ全部が出る決まりだが、そう縛ると
                    // 「読めていない」の中身に依存する ── 並びが表の順であることだけ見る。
                    int next = 0;
                    foreach (string id in ids[..^1])
                    {
                        int at = Array.FindIndex(order, entry => entry.Id == id);
                        Assert.True(0 <= at && next <= at,
                            "プリセットの並びが表の順ではありません: " + string.Join(", ", ids));
                        next = at + 1;
                    }
                }

                // 最小のプリセットは必ず残る（1 つも収まらなければ最小だけを出す規則）。
                var preset = Assert.Single(qualities, q => q.GetProperty("id").GetString() == "360p");
                int presetWidth = preset.GetProperty("width").GetInt32();
                int presetHeight = preset.GetProperty("height").GetInt32();

                // 読める役割でも書けない ── 403 は「役割が足りない」側であること。
                using (var request = AsSession(HttpMethod.Post, QualityPath, viewer, "{\"id\":\"360p\"}"))
                using (var response = await client.SendAsync(request, Ct))
                {
                    string text = await response.Content.ReadAsStringAsync(Ct);
                    output.WriteLine($"viewer-post: {(int)response.StatusCode} {text}");
                    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

                    using var body = JsonDocument.Parse(text);
                    Assert.Equal("insufficient role", body.RootElement.GetProperty("error").GetString());
                }

                // 知らない id と読めない本文は経路が断る（供給側へ渡さない）。
                foreach ((string sent, string expected) in new[]
                {
                    ("{\"id\":\"bogus\"}", "unknown preview quality"),
                    ("{}", "unknown preview quality"),
                    ("not json", "invalid json body"),
                })
                {
                    using var request = AsSession(HttpMethod.Post, QualityPath, operatorSession, sent);
                    using var response = await client.SendAsync(request, Ct);
                    string text = await response.Content.ReadAsStringAsync(Ct);
                    output.WriteLine($"bad body {sent}: {(int)response.StatusCode} {text}");

                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    using var body = JsonDocument.Parse(text);
                    Assert.Equal(expected, body.RootElement.GetProperty("error").GetString());
                }

                // 対象が無ければ 404（供給側が答える ── 終了コード 13）。
                using (var request = AsSession(
                    HttpMethod.Post, "api/recorders/nope/preview/quality", operatorSession, "{\"id\":\"custom\"}"))
                using (var response = await client.SendAsync(request, Ct))
                {
                    string text = await response.Content.ReadAsStringAsync(Ct);
                    output.WriteLine($"unknown recorder: {(int)response.StatusCode} {text}");
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

                    using var body = JsonDocument.Parse(text);
                    Assert.Equal(13, body.RootElement.GetProperty("exitCode").GetInt32());
                }

                // --- 切り替えが配信物に出る ---
                await SetQualityAsync(client, operatorSession, "360p");

                var served = await WaitForServedQualityAsync(client, "360p", "after-360p");
                Assert.Equal(
                    presetWidth.ToString(CultureInfo.InvariantCulture),
                    RepresentationOf(served).Attribute("width")!.Value);
                Assert.Equal(
                    presetHeight.ToString(CultureInfo.InvariantCulture),
                    RepresentationOf(served).Attribute("height")!.Value);

                // --- custom へ戻すと設定の 4 値そのまま（クランプしない） ---
                await SetQualityAsync(client, operatorSession, "custom");

                var restored = await WaitForServedQualityAsync(client, "custom", "after-custom");
                Assert.Equal("1280", RepresentationOf(restored).Attribute("width")!.Value);
                Assert.Equal("720", RepresentationOf(restored).Attribute("height")!.Value);
            }
            finally
            {
                // **override はプロセスに残る**（settings.json には写らない代わりに、
                // 消えるのはアプリの終了だけ）。途中で落ちても custom へ戻しておく。
                //
                // **戻しの失敗で元の失敗を上書きしない。** finally から例外を投げると
                // 本文が置き換わり、何が落ちたのか読めなくなる。
                try
                {
                    using var request = AsSession(
                        HttpMethod.Post, QualityPath, operatorSession, "{\"id\":\"custom\"}");
                    using var response = await client.SendAsync(request, Ct);
                    output.WriteLine($"restore custom: {(int)response.StatusCode}");
                }
                catch (Exception error)
                {
                    output.WriteLine($"restore custom failed: {error}");
                }
            }
        }
    }
}
