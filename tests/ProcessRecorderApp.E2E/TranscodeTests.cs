using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// 録画トランスコードの<b>成立する側</b>（変換が実際に走る経路）。
///
/// <para>
/// <b>ソフトウェアの H.264 デコーダーを持つランタイムで走らせる</b>
/// （<see cref="SoftwareDecoderRuntime"/> ── 開発機は公式フルインストール、CI は MSYS2）。
/// 製品の候補表はハードウェアのデコーダーだけなので、これが無いと変換の経路は
/// どのテスト層でも 1 行も実行されない。<b>同梱ランタイムでは依然として走らない</b>
/// ── 同梱物としての true 経路は <c>tools/Verify-Transcode.ps1</c>（GPU 実機）の担当のまま。
/// </para>
/// <para>
/// <b>能力は断定する。</b> 各ケースの冒頭で <c>GET /api/capabilities</c> が
/// <c>transcode:true</c> かつ名指ししたデコーダーを返すことを表明し、返さなければ落とす
/// ── 黙って skip すると、能力検出が壊れたときに緑のままになる。
/// </para>
/// <para>
/// <b>「開いたまま＝枠を握る」は時間で切れる。</b> 6 秒・320x240 の変換は
/// <c>sync=false</c> で 1〜2 秒で EOS に達し、本文を読まなくてもサーバー側は
/// <c>Ended</c> で読み手を閉じて猶予（<c>TranscodeLimits.GraceMs</c>＝10 秒）へ移る
/// ── 枠が埋まっていることを断定できるのは<b>開いてから EOS ＋ 猶予の窓の内側だけ</b>なので、
/// 409 の表明はすべて開いた直後に済ませる。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class TranscodeTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>テスト用の固定トークン（製品が生成する形＝Base64Url・43 文字に合わせてある）。</summary>
    private const string Token = "E2E-transcode-token-01234567890-abcdefghijk";

    /// <summary>変換を掛ける画質。ソース（320x240）より高いので<b>高さは 240 へ丸められる</b>。</summary>
    private const string Quality = "360p";

    /// <summary>枠が空いていない（<c>TranscodeReasons.Busy</c>／DASH と同じ文字列）。</summary>
    private const string BusyReason = "auxiliary encoder busy";

    /// <summary>DASH の mux がまだ立っていない（<c>DashPreviewReasons.Starting</c>）。</summary>
    private const string StartingReason = "dash preview is starting";

    /// <summary>2 台目のレコーダーの DASH（枠を取り合う相手）。</summary>
    private const string SecondManifest = "api/recorders/R2/dash/manifest.mpd";

    /// <summary>ソースの大きさ（<see cref="SettingsFile.SmallVideoTestSrc"/>）。</summary>
    private const int SourceWidth = 320;

    /// <inheritdoc cref="SourceWidth"/>
    private const int SourceHeight = 240;

    /// <summary><c>remote.start</c> が出るまでの待ち。</summary>
    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(30);

    /// <summary>1 回の HTTP 要求の打ち切り（変換は init が揃うまでヘッダーを書かない）。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>変換元を作るための録画の長さ。</summary>
    private static readonly TimeSpan RecordingWindow = TimeSpan.FromSeconds(6);

    /// <summary>シークで飛ばす位置（<see cref="RecordingWindow"/> のほぼ中間）。</summary>
    private const double SeekSeconds = 3;

    /// <summary>
    /// 枠が返るのを待つ長さ。<b>猶予（10 秒）は EOS から数え始める</b>ので、
    /// 応答を閉じた時刻から測れば必ず足りる。
    /// </summary>
    private static readonly TimeSpan GraceWait = TimeSpan.FromSeconds(14);

    /// <summary>枠が埋まっていることを断定するあいだの間隔（窓が短いので細かく引く）。</summary>
    private static readonly TimeSpan BusyPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 枠が埋まった状態を見に行くときの締め切り（開いた直後の窓の内側）。
    /// <b>窓は EOS（1〜2 秒）＋猶予（<c>TranscodeLimits.GraceMs</c>＝10 秒）</b>で、
    /// その内側で SSE（5 秒）とこれの合計 10 秒を使い切る勘定にしてある
    /// ── 足して窓を超える予算にすると、見たかった状態が先に消えて赤くなる。
    /// </summary>
    private static readonly TimeSpan BusyBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 枠の貸出が続く猶予（製品側の <c>TranscodeLimits.GraceMs</c>。E2E からは参照できない）。
    /// 保持開始からこれを超えて 409 が出なければ、製品の欠陥ではなく窓の外である。
    /// </summary>
    private static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(10);

    /// <summary>枠が返るまで引き続ける締め切り。</summary>
    private static readonly TimeSpan RecoveryBudget = TimeSpan.FromSeconds(60);

    /// <summary>録画が一覧へ出るまでの待ち。</summary>
    private static readonly TimeSpan ListingBudget = TimeSpan.FromSeconds(30);

    /// <summary>2 台とも初期化されるまでの待ち。</summary>
    private static readonly TimeSpan InitBudget = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Regex BindPattern = new(@"\bbind=([0-9.]+):(\d+)\b", RegexOptions.Compiled);

    /// <summary>
    /// 変換が成立する構成。<b>配信 root を録画の書き先へ揃える</b>
    /// （録ったものをそのまま <c>/api/recordings</c> から引くため）。
    /// </summary>
    private static SettingsFile TranscodeSettings(int limit, int recorders)
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

        for (int i = 1; i <= recorders; i++)
        {
            // **事前バッファは短くする。** 既定（3000ms）のままだと「6 秒録った」ファイルが
            // 9 秒になり、シークの 3 秒が中間ではなくなる（moof の数の比較が成り立たない）。
            settings.AddRecorder("R" + i.ToString(CultureInfo.InvariantCulture)).BufferDuration = 500;
        }

        return settings;
    }

    /// <summary>ソフトウェアのデコーダーを持つランタイムで起こし、配信 root を録画先に揃える。</summary>
    private static void Configure(AppInstance instance)
    {
        instance.Settings.OutputDirectory = instance.RecordingsDir;
        SoftwareDecoderRuntime.Apply(instance);
    }

    /// <summary><c>activity.log</c> の <c>remote.start</c> から実ポートを読む。</summary>
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
                    return int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                }
            }
            Thread.Sleep(200);
        }

        Assert.Fail(
            $"remote.start が {StartBudget.TotalSeconds:F0} 秒以内に現れませんでした。"
            + Environment.NewLine + instance.DiagnosticDump());
        return 0;
    }

    /// <summary>トークンを毎回付ける（ゲスト読み取りに頼らず Viewer として通す）。</summary>
    private static HttpClient CreateClient(int port)
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = RequestTimeout,
        };
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + Token);
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-PRApp-Client", "1");
        return client.SendAsync(request, Ct);
    }

    /// <summary>応答を本文つきで読む（失敗したときに何が返ったのかが分かるように）。</summary>
    private async Task<JsonDocument> ExpectAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        string text = await response.Content.ReadAsStringAsync(Ct);
        output.WriteLine($"{(int)response.StatusCode} {text}");
        Assert.Equal(expected, response.StatusCode);
        return JsonDocument.Parse(text);
    }

    /// <summary>
    /// 変換が成立するランタイムで走っていることの<b>断定</b>。
    ///
    /// <para>
    /// 落ちたときに読むべきものは 3 つ ── どの <c>bin</c> を PATH の先頭に置いたか
    /// （<c>gst.runtime</c> が実際にどこから読んだか）、名指しが採られたか
    /// （<c>gst.decoders</c> の <c>preferred=</c> / <c>used=</c>）、変換が起きたか
    /// （<c>transcode.start</c>）── なので全部メッセージに入れる。
    /// </para>
    /// </summary>
    private async Task AssertTranscodeIsAvailableAsync(HttpClient client, AppInstance instance)
    {
        using var response = await client.GetAsync("api/capabilities", Ct);
        using var body = await ExpectAsync(response, HttpStatusCode.OK);
        var root = body.RootElement;

        bool transcode = root.GetProperty("transcode").GetBoolean();
        var decoder = root.GetProperty("decoder");
        string reported = decoder.ValueKind == JsonValueKind.Null ? "null" : decoder.GetString()!;
        string expected = SoftwareDecoderRuntime.Decoder;

        if (!transcode || !string.Equals(reported, expected, StringComparison.Ordinal))
        {
            Assert.Fail(
                $"変換が成立していません: transcode={transcode} decoder={reported}"
                + $"（期待は transcode=true decoder={expected}）。"
                + Environment.NewLine + SoftwareDecoderRuntime.Describe()
                + Environment.NewLine + RuntimeDiagnostics(instance));
        }
    }

    /// <summary>ランタイムの解決・デコーダーの確認・変換の開始をログから抜き出す。</summary>
    private static string RuntimeDiagnostics(AppInstance instance)
    {
        var log = instance.ReadActivityLog();
        var text = new StringBuilder();

        foreach (string name in new[] { "gst.runtime", "gst.decoders", "transcode.start" })
        {
            var lines = ActivityLogFile.Events(log, name);
            if (lines.Count == 0)
            {
                text.AppendLine($"{name}: 0 行");
                continue;
            }

            foreach (string line in lines)
                text.AppendLine(line);
        }

        return text.ToString();
    }

    /// <summary>一覧から、望む状態の 1 本目の相対パスを取る。</summary>
    private async Task<JsonElement> WaitForRecordingAsync(HttpClient client, bool inProgress)
    {
        var deadline = Stopwatch.StartNew();
        string last = "(1 度も一覧を読めていない)";

        while (deadline.Elapsed < ListingBudget)
        {
            using var response = await client.GetAsync("api/recordings", Ct);
            last = await response.Content.ReadAsStringAsync(Ct);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                using var body = JsonDocument.Parse(last);
                foreach (var file in body.RootElement.GetProperty("files").EnumerateArray())
                {
                    if (file.GetProperty("inProgress").GetBoolean() == inProgress)
                    {
                        output.WriteLine($"inProgress={inProgress}: {file}");
                        return file.Clone();
                    }
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }

        Assert.Fail(
            $"inProgress={inProgress} の録画が {ListingBudget.TotalSeconds:F0} 秒以内に一覧へ出ませんでした。"
            + $"最後の応答: {last}");
        return default;
    }

    /// <summary>
    /// 1 本録って、その相対パスを返す。
    /// <b>sidecar の幅・高さも表明する</b> ── 変換の高さがソースへ丸められることは
    /// sidecar が読めていて初めて成り立つので、読めていないなら別の失敗として出したい。
    /// </summary>
    private async Task<string> RecordOnceAsync(HttpClient client)
    {
        using (var response = await PostAsync(client, "api/recorders/0/start"))
            (await ExpectAsync(response, HttpStatusCode.OK)).Dispose();

        // 検査で落ちても録画は必ず止める（止め損なうと書き続ける）。
        try
        {
            Thread.Sleep(RecordingWindow);
        }
        finally
        {
            using var stop = await PostAsync(client, "api/recorders/0/stop");
            output.WriteLine($"stop={(int)stop.StatusCode}");
        }

        var finished = await WaitForRecordingAsync(client, inProgress: false);
        Assert.Equal(SourceWidth, finished.GetProperty("width").GetInt32());
        Assert.Equal(SourceHeight, finished.GetProperty("height").GetInt32());
        return finished.GetProperty("path").GetString()!;
    }

    private static string TranscodePath(string recording, double start, string session)
        => $"api/recording-transcode/{recording}"
         + $"?start={start.ToString("0.###", CultureInfo.InvariantCulture)}&q={Quality}&session={session}";

    /// <summary>
    /// 変換を 1 本開き、<b>ヘッダーで止める</b>。
    /// <b>ヘッダーが返った時点で init（<c>ftyp</c>＋<c>moov</c>）は既に出来ている</b>
    /// （<c>X-Codecs</c> はそこからしか作れないので、サーバーは揃うまで書かない）
    /// ── つまり枠はこの時点から握られている。
    /// </summary>
    private Task<HttpResponseMessage> OpenAsync(
        HttpClient client, string recording, double start, string session)
        => client.GetAsync(
            TranscodePath(recording, start, session), HttpCompletionOption.ResponseHeadersRead, Ct);

    private static string HeaderOf(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : response.Content.Headers.TryGetValues(name, out var contentValues)
                ? string.Join(",", contentValues)
                : "(無し)";

    /// <summary>断られたことを本文の理由まで含めて表明する（応答は読み切って閉じる）。</summary>
    private async Task ExpectBusyAsync(HttpResponseMessage response, string label)
    {
        using (response)
        {
            using var body = await ExpectAsync(response, HttpStatusCode.Conflict);
            Assert.Equal(BusyReason, body.RootElement.GetProperty("error").GetString());
            output.WriteLine($"{label}: 409 {BusyReason}");
        }
    }

    /// <summary>
    /// DASH の manifest が 409 <c>auxiliary encoder busy</c> になるまで引く。
    ///
    /// <para>
    /// <b>503 <c>starting</c> を経由するのは正常である</b> ── 枠を試すのは最初のサンプルが
    /// 届いたときなので、要求の直後は「まだ始まっていない」になる。他の応答（特に 200）は
    /// 待たずに落とす: 枠が空いていたということであり、それはこのケースの前提が
    /// 崩れたということである。
    /// </para>
    /// <para>
    /// <b>失敗の本文には「保持を開始してからの経過」を入れる。</b> 枠が埋まっている窓は
    /// EOS＋猶予（<see cref="GraceWindow"/>）で時間切れになるので、経過がそれを超えていれば
    /// 製品が 409 を返さなかったのではなく、こちらが窓の外まで来ている。
    /// </para>
    /// </summary>
    /// <param name="window">保持（変換を開いた時点）から測っている時計。</param>
    private async Task PollDashUntilBusyAsync(HttpClient client, Stopwatch window)
    {
        var watch = Stopwatch.StartNew();
        string last = "(1 度も応答を読めていない)";

        string Held() =>
            window.Elapsed <= GraceWindow
                ? $"保持開始から {window.Elapsed.TotalSeconds:F1}s（窓の内側）"
                : $"保持開始から {window.Elapsed.TotalSeconds:F1}s ── "
                    + $"猶予 {GraceWindow.TotalSeconds:F0}s を過ぎており、"
                    + "枠が埋まっているという前提そのものが窓の外で崩れている"
                    + "（製品の欠陥ではなく、この検査の予算配分の問題）";

        while (watch.Elapsed < BusyBudget)
        {
            using var response = await client.GetAsync(SecondManifest, Ct);
            string body = await response.Content.ReadAsStringAsync(Ct);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                using var reason = JsonDocument.Parse(body);
                Assert.Equal(BusyReason, reason.RootElement.GetProperty("error").GetString());
                // 409 に Retry-After は付けない（空くのは時間ではなく他人が止めたとき）。
                Assert.Null(response.Headers.RetryAfter);
                output.WriteLine($"R2-dash: 409 after {watch.Elapsed.TotalSeconds:F1}s");
                return;
            }

            Assert.True(
                response.StatusCode == HttpStatusCode.ServiceUnavailable,
                $"R2-dash: 待てない応答が返りました: {(int)response.StatusCode} {body}"
                    + $"（{Held()}）");
            using (var reason = JsonDocument.Parse(body))
            {
                Assert.Equal(StartingReason, reason.RootElement.GetProperty("error").GetString());
            }

            last = body;
            await Task.Delay(BusyPollInterval, Ct);
        }

        Assert.Fail(
            $"R2-dash: 409 が {BusyBudget.TotalSeconds:F0} 秒以内に返りませんでした"
                + $"（{Held()}）。最後の応答: {last}");
    }

    /// <summary>
    /// 変換が 200 で開けるまで引く（待ってよいのは 409 <c>busy</c> だけ）。
    /// <b>返す応答は開いたまま</b>なので、呼び出し側が閉じる。
    /// </summary>
    private async Task<HttpResponseMessage> PollTranscodeUntilOpenAsync(
        HttpClient client, string recording, string session)
    {
        var watch = Stopwatch.StartNew();
        string last = "(1 度も応答を読めていない)";

        while (watch.Elapsed < RecoveryBudget)
        {
            var response = await OpenAsync(client, recording, 0, session);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                output.WriteLine($"{session}: 200 after {watch.Elapsed.TotalSeconds:F1}s");
                return response;
            }

            using (response)
            {
                string body = await response.Content.ReadAsStringAsync(Ct);
                Assert.True(
                    response.StatusCode == HttpStatusCode.Conflict,
                    $"{session}: 待てない応答が返りました: {(int)response.StatusCode} {body}");
                using var reason = JsonDocument.Parse(body);
                Assert.Equal(BusyReason, reason.RootElement.GetProperty("error").GetString());
                last = body;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        Assert.Fail(
            $"{session}: 200 が {RecoveryBudget.TotalSeconds:F0} 秒以内に返りませんでした。最後の応答: {last}");
        return null!;
    }

    /// <summary>
    /// 応答の本文を最後まで読み、<b>箱の境界で閉じたところまで</b>を返す。
    /// 末尾が途中で切れている可能性は <see cref="Fmp4File"/> が吸収する。
    /// </summary>
    private async Task<(Fmp4Probe Stream, Mp4Probe File)> ReadAndProbeAsync(
        HttpResponseMessage response, string label)
    {
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        var stream = Fmp4File.Probe(label, bytes);
        output.WriteLine(stream.ToString());

        var file = Mp4File.Probe(label, bytes[..stream.ParsedLength]);
        output.WriteLine(file.ToString());
        return (stream, file);
    }

    // ---- (1) 変換された本文そのもの ----

    /// <summary>
    /// <b>要求した位置から、fragmented MP4 が実際に出てくること。</b>
    ///
    /// <para>
    /// 先頭は <c>ftyp</c>＋<c>moov</c> が 1 回ずつで、その後ろは <c>moof</c>／<c>mdat</c> の
    /// 繰り返し ── ブラウザの MSE が受け取れる形はこれだけである。
    /// </para>
    /// <para>
    /// <b>高さはソースへ丸められる。</b> 320x240 の録画に <c>360p</c> を要求しても
    /// 出てくるのは 240 で、<c>X-Transcode-Quality</c> は<b>要求どおり</b>の
    /// <c>360p</c> が返る（クライアントは <c>timestampOffset</c> の対応付けに使う）。
    /// </para>
    /// <para>
    /// <b>同じ session での位置違いは枠を引き継ぐ</b>ので、2 本目は 409 にならない。
    /// 中間から始めた本文は先頭からのものより短い ── シークが黙って無視されていれば
    /// 同じ長さが返る。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheTranscodeStreamsAFragmentedMp4FromTheRequestedPosition()
    {
        using var instance = AppInstance.Create(app, TranscodeSettings(limit: 2, recorders: 1), configure: Configure);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        await AssertTranscodeIsAvailableAsync(client, instance);
        string recording = await RecordOnceAsync(client);

        int fromStart;
        using (var response = await OpenAsync(client, recording, 0, "s1"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            output.WriteLine(
                $"codecs={HeaderOf(response, "X-Codecs")} "
                + $"quality={HeaderOf(response, "X-Transcode-Quality")} "
                + $"start={HeaderOf(response, "X-Transcode-Start")}");

            Assert.StartsWith("avc1.", HeaderOf(response, "X-Codecs"), StringComparison.Ordinal);
            Assert.Equal(Quality, HeaderOf(response, "X-Transcode-Quality"));
            Assert.Equal("0", HeaderOf(response, "X-Transcode-Start"));

            var (stream, file) = await ReadAndProbeAsync(response, "transcode start=0");

            Assert.True(stream.StartsWithInitSegment, $"先頭が ftyp+moov ではありません: {stream}");
            Assert.Equal(1, stream.Boxes.Count(b => b == "ftyp"));
            Assert.Equal(1, stream.Boxes.Count(b => b == "moov"));
            Assert.True(4 <= stream.MoofCount, $"fragment が少なすぎます: {stream}");
            Assert.Equal(stream.MoofCount, stream.Boxes.Count(b => b == "mdat"));

            // 高さはソース（240）へ丸められ、幅はソースの縦横比から 320 になる。
            Assert.Equal(SourceHeight, file.FrameHeight);
            Assert.Equal(SourceWidth, file.FrameWidth);

            fromStart = stream.MoofCount;
        }

        using (var response = await OpenAsync(client, recording, SeekSeconds, "s1"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("3", HeaderOf(response, "X-Transcode-Start"));

            var (stream, _) = await ReadAndProbeAsync(response, "transcode start=3");
            Assert.True(1 <= stream.MoofCount, $"fragment が 1 つも出ていません: {stream}");

            // fragment は 1 秒（TranscodeSession.FragmentDurationMs）なので、
            // 先頭からの数 fromStart はおおよそ実尺の秒数になる。**実尺は 6 秒とは限らない**
            // ので、比ではなく差で見る ── SeekSeconds（3 秒）ぶん短くなるのが理想だが、
            // GOP は 2 秒で SnapBefore が直前のキーフレーム（2 秒）へ戻すため、
            // 実際は 1 秒ぶん長くなりうる。よって [fromStart-3, fromStart-1] を許す。
            Assert.True(
                fromStart - 3 <= stream.MoofCount && stream.MoofCount <= fromStart - 1,
                $"中間から始めた fragment の数が [{fromStart - 3}, {fromStart - 1}] の外です: "
                    + $"{stream.MoofCount}（先頭から {fromStart}）");
        }
    }

    // ---- (2) ライブ DASH と枠を取り合う ----

    /// <summary>
    /// <b>変換とライブ DASH は 1 つの計数器を取り合う。</b> 枠が 1 つしか無ければ、
    /// 変換が握っているあいだ DASH も別の変換も 409 <c>auxiliary encoder busy</c> になり、
    /// 手放して猶予が過ぎれば通るようになる。
    ///
    /// <para>
    /// <b>409 の表明は開いた直後にまとめて済ませる。</b> 変換は 1〜2 秒で EOS に達して
    /// 猶予（10 秒）へ移るので、握っている状態は<b>時間で切れる</b>
    /// ── 遅い往復（DASH の 503→409）を先に置くと、見たかった状態が途中で消える。
    /// </para>
    /// <para>
    /// <b>回復は引き続けて待つ。</b> 枠が返る瞬間には R2 の DASH がまだ貸出を持っていて
    /// そちらが先に取ることがある ── 1 回きりの要求で断定すると、製品の欠陥ではない
    /// 順序で赤くなる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ATranscodeAndTheLiveDashShareTheAuxiliaryEncoders()
    {
        using var instance = AppInstance.Create(app, TranscodeSettings(limit: 1, recorders: 2), configure: Configure);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        await AssertTranscodeIsAvailableAsync(client, instance);

        // 2 台とも初期化されるまで待つ（1 台目だけでは 2 台目の DASH が別の理由で 503 になる）。
        var initialized = Stopwatch.StartNew();
        while (initialized.Elapsed < InitBudget
            && ActivityLogFile.Events(instance.ReadActivityLog(), "recorder.init ok").Count < 2)
        {
            Thread.Sleep(200);
        }

        Assert.True(
            2 <= ActivityLogFile.Events(instance.ReadActivityLog(), "recorder.init ok").Count,
            "recorder.init ok が 2 件現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        string recording = await RecordOnceAsync(client);

        var held = await OpenAsync(client, recording, 0, "s1");
        var window = Stopwatch.StartNew();
        try
        {
            Assert.Equal(HttpStatusCode.OK, held.StatusCode);

            // 空きが 0 になったことは SSE にだけ出る（開いて最初の state を読むだけ）。
            using (var events = await client.GetAsync(
                "api/events", HttpCompletionOption.ResponseHeadersRead, Ct))
            {
                Assert.Equal(HttpStatusCode.OK, events.StatusCode);
                using var stream = await events.Content.ReadAsStreamAsync(Ct);
                using var reader = new StreamReader(stream);

                string state = await ServerSentEvents.ReadDataAsync(
                    reader, "state", TimeSpan.FromSeconds(5), Ct);
                output.WriteLine(state);
                using var snapshot = JsonDocument.Parse(state);
                Assert.Equal(1, snapshot.RootElement.GetProperty("auxiliaryEncoderLimit").GetInt32());
                Assert.Equal(0, snapshot.RootElement.GetProperty("auxiliaryEncodersFree").GetInt32());
            }

            // 別 session の変換は 1 回で断られる（DASH と違い「始まりかけ」の段が無い）。
            await ExpectBusyAsync(await OpenAsync(client, recording, 0, "s2"), "s2");

            // ライブ DASH も同じ枠を待つ。
            await PollDashUntilBusyAsync(client, window);

            output.WriteLine($"busy の断定を終えるまで {window.Elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            held.Dispose();
        }

        // **ここから先は DASH を引かない。** 引き続けると、返ってきた枠をそちらが取る。
        await Task.Delay(GraceWait, Ct);

        using var reopened = await PollTranscodeUntilOpenAsync(client, recording, "s2");
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.StartsWith("avc1.", HeaderOf(reopened, "X-Codecs"), StringComparison.Ordinal);
    }

    // ---- (3) シークは枠を手放さない ----

    /// <summary>
    /// <b>同じ <c>session</c> での位置違いは、枠を握ったまま引き継ぐ。</b>
    /// 枠が 1 つしか無い状態でシークできることがその証拠で、直後の別 session は
    /// 依然として 409 になる ── 引き継ぎが効いていなければ、シーク自身が
    /// 「自分の枠」に断られる（あるいは 2 つ目の枠を食う）。
    /// </summary>
    [Fact]
    public async Task ASeekKeepsTheSessionsSlot()
    {
        using var instance = AppInstance.Create(app, TranscodeSettings(limit: 1, recorders: 1), configure: Configure);
        int port = WaitForPort(instance);
        using var client = CreateClient(port);

        await AssertTranscodeIsAvailableAsync(client, instance);
        string recording = await RecordOnceAsync(client);

        using var first = await OpenAsync(client, recording, 0, "s1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // 開いたまま同じ session で位置を変える（前のパイプラインは replaced で畳まれる）。
        using var seeked = await OpenAsync(client, recording, 2, "s1");
        Assert.Equal(HttpStatusCode.OK, seeked.StatusCode);
        Assert.Equal("2", HeaderOf(seeked, "X-Transcode-Start"));

        // 引き継いだだけなので、枠は 1 つのまま埋まっている。
        await ExpectBusyAsync(await OpenAsync(client, recording, 0, "s2"), "s2");
    }
}
