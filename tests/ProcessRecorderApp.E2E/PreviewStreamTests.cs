using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// ライブプレビューの配信（<c>GET /api/recorders/{id}/preview.mp4</c>）。
///
/// <para>
/// <b>ここは発行物でしか確かめられない。</b> 本物の GStreamer が
/// <c>mp4mux fragment-mode=dash-or-mss</c> で何を吐くか、chunked の本文が
/// 実際に切れ目なく届くか、そして<b>配信しても録画が変わらないか</b>は、
/// どれも単体テストからは 1 バイトも観測できない。
/// </para>
/// <para>
/// <b>「fragment の先頭は必ず IDR」は成立しない。</b> 実測のとおり
/// <c>mp4mux</c> は時間だけで切るので、条件は「最初の 3 個のどれかが同期始まり」まで
/// 緩めてある（GOP 2 秒・fragment 1 秒が前提）。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class PreviewStreamTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>製品が生成する形（Base64Url・43 文字）に合わせた固定トークン。</summary>
    private const string Token = "E2E-preview-stream-token-0123456789-abcde";

    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(30);

    /// <summary>短い要求（JSON）の打ち切り。本文が有限のものにだけ使う。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>ストリームを読む時間。1 秒 fragment が 3 個以上出るのに足りる。</summary>
    private static readonly TimeSpan StreamWindow = TimeSpan.FromSeconds(6);

    /// <summary>録画を回す時間。<c>mp4mux</c> が意味のある長さを書くのに足りる最小限。</summary>
    private static readonly TimeSpan RecordingWindow = TimeSpan.FromSeconds(3);

    /// <summary>初期化・購読・停止の記録が現れるまでの待ち。</summary>
    private static readonly TimeSpan EventBudget = TimeSpan.FromSeconds(30);

    /// <summary><c>gst-launch-1.0</c> が保存物を読み切るまでの上限。</summary>
    private static readonly TimeSpan DemuxBudget = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Regex BindPattern = new(@"\bbind=([0-9.]+):(\d+)\b", RegexOptions.Compiled);

    /// <summary>
    /// リモート操作を有効にした settings.json。
    ///
    /// <para>
    /// <b><c>key-int-max=30</c> と 15fps のソースで GOP は 2 秒。</b> §7 の
    /// 「6 秒で <c>moof</c> 3 個以上・最初の 3 個のどれかが同期始まり」は
    /// この GOP を前提にしている ── 伸ばすとどちらの下限も割る。
    /// </para>
    /// </summary>
    private static SettingsFile PreviewSettings()
    {
        var settings = new SettingsFile
        {
            // 127.0.0.1 に固定する（0.0.0.0 だと開発機と CI の LAN から到達できる）。
            RemoteControlEnabled = true,
            RemoteControlBindAddress = "127.0.0.1",
            RemoteControlPort = 0,
            RemoteControlAccessToken = Token,
            // **このクラスの主題は認証ではない。** 読み取りにも役割が要るので、
            // ゲスト読み取りを明示して未認証で読ませる
            // ── 認証そのものは RemoteControlTests が見る。
            RemoteControlAllowGuestRead = true,
        };
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
    /// <b>初期化を待たずに購読すると 503</b>（配信の器はパイプラインが
    /// <c>PLAYING</c> に達してから作られる）。
    /// </summary>
    private (AppInstance Instance, int Port) StartReady()
    {
        var instance = AppInstance.Create(app, PreviewSettings());
        int port = WaitForPort(instance);

        Assert.True(instance.WaitForActivityLogEvent("recorder.init ok", EventBudget),
            "recorder.init ok が現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        return (instance, port);
    }

    /// <summary>
    /// <b><see cref="HttpClient.Timeout"/> は無限にする。</b> あちらは本文の読み取りにも
    /// 効くので、終端の無いプレビューでは必ず打ち切ってしまう ── 締め切りは
    /// 要求ごとの <see cref="CancellationTokenSource"/> で掛ける。
    /// </summary>
    private static HttpClient CreateClient(int port) =>
        new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };

    /// <summary>
    /// 応答ヘッダーまでを開く。<b>締め切りはここで掛ける</b> ──
    /// <see cref="HttpClient.Timeout"/> を無限にしてあるので、これが無いと
    /// 「応答しなくなった」という退行が<b>失敗ではなくハング</b>として現れる。
    /// </summary>
    private static async Task<HttpResponseMessage> OpenPreviewAsync(HttpClient client, string id)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            return await client.GetAsync(
                "api/recorders/" + Uri.EscapeDataString(id) + "/preview.mp4",
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            Assert.Fail(
                $"preview.mp4 ({id}) の応答ヘッダーが {RequestTimeout.TotalSeconds:F0} 秒以内に返りませんでした。"
                + "サーバーは購読を作ってからヘッダーを送るので、ここで止まるのは"
                + "購読の生成（UI スレッドへの乗り換え）が返っていないことを意味する。");
            throw;
        }
    }

    /// <summary>
    /// 本文の先頭が <c>ftyp</c>＋<c>moov</c> に達するまで読む（有界）。
    /// <b>固定の待ち時間で代用しない</b> ── 待ちたいのは経過時間ではなく
    /// 「init セグメントがこの接続へ届いたこと」である。
    /// </summary>
    private static async Task<byte[]> ReadUntilInitAsync(HttpResponseMessage response, TimeSpan budget)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(budget);

        var collected = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        try
        {
            using var body = await response.Content.ReadAsStreamAsync(Ct);
            while (true)
            {
                var probe = Fmp4File.Probe("(partial)", collected.ToArray());
                if (probe.StartsWithInitSegment)
                    break;

                int read = await body.ReadAsync(chunk, deadline.Token);
                if (read <= 0)
                    break;
                collected.Write(chunk, 0, read);
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            // 呼び出し側が「届かなかった」を表明する。
        }

        return collected.ToArray();
    }

    /// <summary>本文を <paramref name="window"/> のあいだ読んで返す（末尾は途中で切れる）。</summary>
    private static async Task<byte[]> ReadForAsync(HttpResponseMessage response, TimeSpan window)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(window);

        var collected = new MemoryStream();
        try
        {
            using var body = await response.Content.ReadAsStreamAsync(Ct);
            await body.CopyToAsync(collected, deadline.Token);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            // 期待どおりの打ち切り（配信に終端は無い）。
        }

        return collected.ToArray();
    }

    /// <summary>
    /// 打ち切られるまで本文を汲み続ける。<b>汲む長さを呼び出し側の出来事に連動させる</b>
    /// ために時間ではなくトークンで止める（固定秒だと、測りたい区間と汲む区間がずれる）。
    /// </summary>
    private static async Task<byte[]> ReadUntilCancelledAsync(HttpResponseMessage response, CancellationToken stop)
    {
        var collected = new MemoryStream();
        try
        {
            using var body = await response.Content.ReadAsStreamAsync(Ct);
            await body.CopyToAsync(collected, stop);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            // 期待どおりの打ち切り（配信に終端は無い）。
        }

        return collected.ToArray();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(RequestTimeout);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
    }

    /// <summary>
    /// 本文を読み、<b>箱の境界で切り落として</b>保存する。末尾が途中で終わった
    /// ファイルは <c>qtdemux</c> が読めないので、保存の時点で閉じておく。
    /// </summary>
    private async Task<Fmp4Probe> CaptureStreamAsync(
        AppInstance instance, HttpResponseMessage response, string name, TimeSpan window)
    {
        byte[] body = await ReadForAsync(response, window);
        var raw = Fmp4File.Probe(name, body);

        string path = Path.Combine(instance.DataDir, name);
        File.WriteAllBytes(path, body.AsSpan(0, raw.ParsedLength).ToArray());

        var probe = Fmp4File.Probe(path, File.ReadAllBytes(path));
        output.WriteLine($"{probe} (received {body.Length:N0} bytes)");
        return probe;
    }

    // ---- ストリームの形 ----

    /// <summary>
    /// 録画をしていなくても配信が始まり、本文が <c>ftyp / moov / (moof mdat)+</c> の
    /// fragmented MP4 になっていること。
    /// </summary>
    [Fact]
    public async Task TheLiveStream_IsFragmentedMp4()
    {
        var (instance, port) = StartReady();
        using (instance)
        {
            using var client = CreateClient(port);
            using var response = await OpenPreviewAsync(client, "0");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            // 長さの決まらない本文なので chunked でなければならない
            // （Content-Length が付いていたら、それは終端のある別物）。
            Assert.True(response.Headers.TransferEncodingChunked ?? false,
                "Transfer-Encoding: chunked ではありません: "
                + string.Join(",", response.Headers.TransferEncoding));
            Assert.Null(response.Content.Headers.ContentLength);

            var probe = await CaptureStreamAsync(instance, response, "preview-shape.mp4", StreamWindow);

            Assert.True(probe.StartsWithInitSegment,
                "本文が ftyp → moov で始まっていない: " + probe);
            Assert.True(probe.MediaSegmentsAlternate(),
                "moov の後ろが moof / mdat の対になっていない: " + probe);
            Assert.True(3 <= probe.MoofCount,
                $"{StreamWindow.TotalSeconds:F0} 秒で moof が {probe.MoofCount} 個しか出ていない: " + probe);
            Assert.True(probe.HasMvex, "moov に mvex が無い（fragmented ではない）: " + probe);
            Assert.True(probe.HasAvc1 && probe.HasAvcC, "moov に avc1/avcC が無い: " + probe);

            // **「各 moof が同期始まり」は成立しない**（実測）。MSE は最初の
            // RAP まで捨てるので、初画に必要なのは「最初の数個のどれかが同期始まり」。
            Assert.Contains(true, probe.MoofStartsWithSync.Take(3));
        }
    }

    /// <summary>
    /// 保存した本文が <c>qtdemux</c> で demux できること。
    ///
    /// <para>
    /// <b>自前のパーサだけを信じない。</b> こちらは箱の並びしか見ておらず、
    /// 中身（<c>tfdt</c> の刻み・<c>trun</c> のオフセット）が壊れていても気付けない
    /// ── <c>mfra</c> の無い切り落としファイルなので、要求するのは
    /// 「fragment 単位で demux できる」ことだけである。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheCapturedStream_DemuxesWithQtdemux()
    {
        string launcher = Path.Combine(
            RepositoryLayout.Root, "src", "GStreamer.GstSharpNet", "runtimes", "win-x64", "bin", "gst-launch-1.0.exe");
        Assert.SkipUnless(File.Exists(launcher),
            $"同梱ランタイムの gst-launch-1.0.exe がありません（{launcher}）。"
            + "tools/Fetch-GStreamerRuntime.ps1 を実行すると検証されます。");

        var (instance, port) = StartReady();
        using (instance)
        {
            using var client = CreateClient(port);
            using var response = await OpenPreviewAsync(client, "0");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var probe = await CaptureStreamAsync(instance, response, "preview-qtdemux.mp4", StreamWindow);
            Assert.True(3 <= probe.MoofCount, "demux させるだけの fragment が取れていない: " + probe);

            var start = new ProcessStartInfo(launcher)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(launcher)!,
            };
            start.ArgumentList.Add("filesrc");
            // **区切りは '/' にする。** gst-launch のパイプライン記述はプロパティ値の
            // '\' をエスケープとして食うので、Windows のパスをそのまま渡すと
            // 区切りの消えた別のパスになる（実測: "C:UsersmasanoriAppData..."）。
            start.ArgumentList.Add("location=" + probe.Path.Replace('\\', '/'));
            start.ArgumentList.Add("!");
            start.ArgumentList.Add("qtdemux");
            start.ArgumentList.Add("!");
            start.ArgumentList.Add("h264parse");
            start.ArgumentList.Add("!");
            start.ArgumentList.Add("fakesink");

            // 同梱の配置（bin の隣の lib\gstreamer-1.0）を名指しする。開発機には
            // フル構成の GStreamer もあるので、暗黙の探索に任せるとどちらを読んだか分からない。
            string pluginDir = Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(launcher)!, "..", "lib", "gstreamer-1.0"));
            // **1.x 系は接尾辞付きを先に見る。** 素の名前だけを設定すると、
            // 開発機に `GST_PLUGIN_SYSTEM_PATH_1_0` が在る場合にそちらが勝つ。
            start.Environment["GST_PLUGIN_PATH"] = pluginDir;
            start.Environment["GST_PLUGIN_SYSTEM_PATH"] = pluginDir;
            start.Environment["GST_PLUGIN_PATH_1_0"] = pluginDir;
            start.Environment["GST_PLUGIN_SYSTEM_PATH_1_0"] = pluginDir;
            // レジストリのキャッシュも隔離する（システム側のものを書き換えない）。
            start.Environment["GST_REGISTRY"] = Path.Combine(instance.DataDir, "gst-registry-e2e.bin");

            using var process = Process.Start(start)!;

            // 両方を同時に汲む（片方だけを待つと、もう片方のパイプが埋まって止まる）。
            var stdout = process.StandardOutput.ReadToEndAsync(Ct);
            var stderr = process.StandardError.ReadToEndAsync(Ct);

            using var kill = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            kill.CancelAfter(DemuxBudget);
            try
            {
                await process.WaitForExitAsync(kill.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail($"gst-launch-1.0 が {DemuxBudget.TotalSeconds:F0} 秒で終わりませんでした。");
            }

            output.WriteLine(await stdout);
            output.WriteLine(await stderr);

            Assert.Equal(0, process.ExitCode);
        }
    }

    // ---- 途中参加 ----

    /// <summary>
    /// 配信の途中で繋いだ 2 本目にも、先頭で init セグメントが渡ること。
    /// <b>これが無いと後から開いた画面は永久に何も映らない</b>
    /// （MSE は init より前の media を受け取れない）。
    /// </summary>
    [Fact]
    public async Task ASecondViewer_GetsTheInitSegmentFirst()
    {
        var (instance, port) = StartReady();
        using (instance)
        {
            using var client = CreateClient(port);

            using var first = await OpenPreviewAsync(client, "0");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            // **1 本目が init を受け取ってから 2 本目を開く。** 時間で待つと
            // 「まだ mux が起きていないので 2 本目も 1 番乗り」という、
            // 途中参加を検査していない緑がありうる。
            var early = Fmp4File.Probe("preview-first", await ReadUntilInitAsync(first, EventBudget));
            output.WriteLine("first: " + early);
            Assert.True(early.StartsWithInitSegment,
                "1 本目が init セグメントを受け取れていないので、途中参加を検査できない: " + early);

            using var second = await OpenPreviewAsync(client, "0");
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            var late = await CaptureStreamAsync(instance, second, "preview-late.mp4", TimeSpan.FromSeconds(4));

            Assert.True(late.StartsWithInitSegment,
                "途中参加した 2 本目が ftyp → moov で始まっていない: " + late);
            Assert.True(0 < late.MoofCount,
                "途中参加した 2 本目に media が 1 件も来ていない: " + late);
        }
    }

    // ---- 上限と対象の解決 ----

    /// <summary>
    /// 1 レコーダーあたりの同時購読の上限（4）を超えた要求が 503 で断られ、
    /// 存在しないレコーダーが 404 になること。
    /// </summary>
    [Fact]
    public async Task TheSubscriberLimitAndTheUnknownRecorderAreRejected()
    {
        var (instance, port) = StartReady();
        using (instance)
        {
            using var client = CreateClient(port);
            var open = new List<HttpResponseMessage>();
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    var response = await OpenPreviewAsync(client, "0");
                    open.Add(response);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }

                using var overflow = await OpenPreviewAsync(client, "0");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, overflow.StatusCode);

                using var body = await ReadJsonAsync(overflow);
                output.WriteLine(body.RootElement.ToString());
                Assert.Equal(12, body.RootElement.GetProperty("exitCode").GetInt32());
                Assert.Contains("subscribers", body.RootElement.GetProperty("error").GetString()!,
                    StringComparison.Ordinal);
            }
            finally
            {
                foreach (var response in open)
                    response.Dispose();
            }

            using var missing = await OpenPreviewAsync(client, "99");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            using var reason = await ReadJsonAsync(missing);
            Assert.Equal(13, reason.RootElement.GetProperty("exitCode").GetInt32());
        }
    }

    // ---- 録画への影響 ----

    /// <summary>
    /// <b>配信していても録画は変わらないこと。</b> プレビュー無しで録った 1 本目と、
    /// 購読を 1 本開いたまま録った 2 本目を、どちらも有効・尺 2 秒以上・
    /// <b>サンプル数が ±25% 以内</b>で突き合わせる。
    ///
    /// <para>
    /// <b>秒数の厳密一致もサイズ比較もしない。</b> 前者はタイマー精度に、後者は
    /// レート制御に依存し、どちらも配信の有無とは無関係に揺れる
    /// ── 「同じだけのフレームが記録されたか」がここで答えたい問いである。
    /// </para>
    /// <para>
    /// 併せて、購読の増減と mux の起き/落ちが
    /// <c>preview.subscribe</c> → <c>preview.stream-start</c> →
    /// <c>preview.unsubscribe</c> → <c>preview.stream-stop</c> の順に記録されること、
    /// 全員が去ったあとも録画が続けられることを見る。
    /// </para>
    /// </summary>
    [Fact]
    public async Task RecordingIsUnaffectedWhileThePreviewIsStreaming()
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
            Mp4Probe during;
            using (var response = await OpenPreviewAsync(client, "0"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                // **読み続けないと chunked が詰まる**（購読の待ち行列は 8 件）。
                // 汲む長さは録画の所要そのもの ── 固定秒にすると、録画が延びた日に
                // 「配信中」でない区間で録ったものを比べることになる。
                using var pumping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                var pump = ReadUntilCancelledAsync(response, pumping.Token);

                Assert.True(instance.WaitForActivityLogEvent("preview.stream-start", EventBudget),
                    "preview.stream-start が現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

                during = Mp4File.Probe(RecordOnce(instance, seen));
                output.WriteLine("during: " + during);

                await pumping.CancelAsync();
                output.WriteLine($"pumped {(await pump).Length:N0} bytes while recording");
            }

            Assert.True(during.IsValid, "配信中の録画が有効な MP4 になっていない: " + during);
            Assert.True(during.DurationSeconds >= 2, "配信中の録画が 2 秒未満: " + during);
            // **サンプル数の生比較はしない** ── stop-recording の CLI 往復が負荷で伸びると
            // 録画の尺そのものが揺れる（CI 実測: 基準 3.9 秒 / 配信中 5.3 秒）。「配信が録画を
            // 邪魔していない」の観測点は尺ではなくレート（サンプル数 ÷ 尺）である。
            var baselineFps = Assert.NotNull(baseline.EffectiveFramerate);
            var duringFps = Assert.NotNull(during.EffectiveFramerate);
            Assert.InRange(duringFps, baselineFps * 0.75, baselineFps * 1.25);

            // 購読が切れたら mux も落ちる（落とすのは録画側の次のサンプル）。
            Assert.True(instance.WaitForActivityLogEvent("preview.unsubscribe", EventBudget),
                "preview.unsubscribe が現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());
            Assert.True(instance.WaitForActivityLogEvent("preview.stream-stop", EventBudget),
                "preview.stream-stop が現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

            // 配信を畳んだ後も録画は続けられる。
            var after = Mp4File.Probe(RecordOnce(instance, seen));
            output.WriteLine("after: " + after);
            Assert.True(after.IsValid, "配信を閉じた後の録画が有効な MP4 になっていない: " + after);
            Assert.True(after.DurationSeconds >= 2, "配信を閉じた後の録画が 2 秒未満: " + after);

            // **記録は最後に読み直す。** 途中で読むと、この後に出た失敗を見逃す。
            var lines = instance.ReadActivityLog();
            int subscribe = FirstIndexOf(lines, "preview.subscribe");
            int started = FirstIndexOf(lines, "preview.stream-start");
            int unsubscribe = FirstIndexOf(lines, "preview.unsubscribe");
            int stopped = FirstIndexOf(lines, "preview.stream-stop");

            output.WriteLine($"subscribe={subscribe} start={started} unsubscribe={unsubscribe} stop={stopped}");
            Assert.True(0 <= subscribe && subscribe < started && started < unsubscribe && unsubscribe < stopped,
                "配信の記録が subscribe → stream-start → unsubscribe → stream-stop の順になっていない:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, lines.Where(l => ActivityLogFile.EventNameOf(l)?.StartsWith("preview.", StringComparison.Ordinal) == true)));

            // 配信の失敗は 1 件も出ていないこと。
            Assert.Empty(ActivityLogFile.Events(lines, "preview.stream-error"));
            Assert.Empty(ActivityLogFile.Events(lines, "preview.leak"));
        }
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

    private static int FirstIndexOf(IReadOnlyList<string> lines, string eventName)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (ActivityLogFile.EventNameOf(lines[i]) == eventName)
                return i;
        }
        return -1;
    }
}
