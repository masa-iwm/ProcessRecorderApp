using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// 録画ファイルの一覧と配信。<b>読み取りなので <see cref="RemoteRole.Viewer"/> が要る</b>
/// （他の GET と同じ規律 ── ゲスト読み取りが ON なら未認証でも通る）。
/// <b>ここに出るのは録画そのものである</b>: 画面に写っていたものが読める以上、
/// 「GET だから誰でも」にはしない。配信 root の外へは出ない
/// （<c>RemoteApiRules.TryResolveUnderRoot</c>）。
///
/// <para>
/// <b>列挙と開封は <c>Components.RecordingFiles</c> が行う。</b> 保存先を持っているのは
/// アプリ側だが、そこは UI スレッドであり IO を載せる場所ではない
/// ── backend から受け取るのは<b>解決済みの root（文字列）だけ</b>で、
/// ディスクに触るのはこのスレッドプール側である。
/// </para>
/// <para>
/// <b>1 ファイルから派生する API は <c>/api/recording-&lt;kind&gt;/{*path}</c> に揃える。</b>
/// <c>{*path}</c> は末尾まで捕まえるので <c>/api/recordings/{*path}/fragments</c> の形は
/// 書けない ── 種別を前に出す以外に、相対パスをそのまま載せる道が無い。
/// </para>
/// </summary>
internal static class RecordingEndpoints
{
    /// <summary>配信する MIME 型。録画は必ず MP4（<c>RecordingCleanup.Extension</c> で絞ってある）。</summary>
    private const string VideoContentType = "video/mp4";

    /// <summary>サムネイルの MIME 型。書くのは <c>Components.PngWriter</c> ひとつだけ。</summary>
    private const string ThumbnailContentType = "image/png";

    /// <summary>
    /// トランスコードの 1 回の読み取りで待つ上限(ms)。<b>この時間だけスレッドプールの
    /// スレッドが 1 本塞がる</b>ので、長くすると同時視聴者数がそのまま占有本数になる
    /// （短くしても取りこぼしは無い ── 取れなければもう一度呼ぶだけである）。
    /// </summary>
    private const int TranscodeReadTimeoutMs = 1000;

    /// <summary>
    /// init（<c>ftyp</c>＋<c>moov</c>）を待つあいだに溜めてよい上限(バイト)。
    /// 実測の init は 1 KiB 程度なので、ここへ達するのは中身が init でないときだけである。
    /// </summary>
    private const int TranscodeInitMaxBytes = 1024 * 1024;

    /// <summary>
    /// トランスコードを開始できなかったときの終了コード（<c>ExitCode_RecorderNotAvailable</c> と同じ 12
    /// ＝ 503 ＋ <c>Retry-After</c>）。<b>待てば直りうる失敗だから</b>この番号で、
    /// 枠の不足（409）とは区別する。
    /// </summary>
    private const int TranscodeNotReadyExitCode = 12;

    /// <summary>
    /// 開いたファイルについて、同じ 1 回の読み取りから採った値。
    /// </summary>
    /// <param name="InProgress"><c>filesink</c> が書き込みで握っているか。</param>
    /// <param name="Length">長さ。</param>
    /// <param name="LastWriteUtc">更新時刻。</param>
    /// <param name="ETag">この 2 つから作った本文の <c>ETag</c>（引用符込み）。</param>
    private readonly record struct RecordingSnapshot(
        bool InProgress, long Length, DateTime LastWriteUtc, string ETag);

    public static void Map(
        WebApplication app, IRemoteControlBackend backend, RemoteAuth auth, RecordingIndex recordings)
    {
        // 索引はプロセスに 1 つ。録画中のファイルは 1 秒ごとに引き直されるので、
        // 覚えておかないと毎回ファイル全体を辿ることになる。
        var index = new Fmp4FragmentIndexCache();

        app.MapGet("/api/recordings", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            if (!TryReadWindow(ctx, out DateTimeOffset? from, out DateTimeOffset? to, out string? badWindow))
            {
                await ApiResponse.WriteErrorAsync(ctx, 400, ApiResponse.HttpLayerExitCode, badWindow);
                return;
            }

            if (!TryReadPaging(ctx, out int? limit, out int offset, out string? badPaging))
            {
                await ApiResponse.WriteErrorAsync(ctx, 400, ApiResponse.HttpLayerExitCode, badPaging);
                return;
            }

            var (root, all) = await SnapshotAsync(ctx, backend, recordings, from, to);

            var page = RecordingQuery.Page(all, limit, offset);
            var dto = new RecordingsDto(
                root,
                all.Count,
                offset + page.Count < all.Count,
                page.Select(ToDto).ToArray());

            // 一覧は毎回作り直す（録画中のファイルは長さが動き続ける）。
            ctx.Response.Headers.CacheControl = "no-store";
            await ApiResponse.WriteJsonAsync(ctx, 200, dto, RemoteApiJsonContext.Default.RecordingsDto);
        });

        // 日付ごとの件数。**源はメモリの索引なので ETag は付けない**
        // ── 本文と違って「同じバイト列がまた返る」ことに賭けられる材料が無い。
        app.MapGet("/api/recording-days", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            if (!TryReadWindow(ctx, out DateTimeOffset? from, out DateTimeOffset? to, out string? badWindow))
            {
                await ApiResponse.WriteErrorAsync(ctx, 400, ApiResponse.HttpLayerExitCode, badWindow);
                return;
            }

            if (!RecordingQuery.TryResolveTimeZone(ctx.Request.Query["tz"], out TimeZoneInfo? zone))
            {
                await ApiResponse.WriteErrorAsync(
                    ctx, 400, ApiResponse.HttpLayerExitCode, "tz is not a UTC offset or a Windows time zone id");
                return;
            }

            var (_, all) = await SnapshotAsync(ctx, backend, recordings, from, to);

            ctx.Response.Headers.CacheControl = "no-store";
            await ApiResponse.WriteJsonAsync(
                ctx, 200, new RecordingDaysDto(RecordingQuery.CountDays(all, zone!)),
                RemoteApiJsonContext.Default.RecordingDaysDto);
        });

        app.MapGet("/api/recordings/{*path}", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            string root = await backend.GetRecordingsRootAsync(ctx.RequestAborted);
            FileStream? stream = await OpenRequestedAsync(ctx, root);
            if (stream is null)
                return;

            using (stream)
            {
                var snapshot = Snapshot(stream);

                bool download = string.Equals(ctx.Request.Query["download"], "1", StringComparison.Ordinal);

                // **ヘッダーは結果型を実行する前に置く。** 200 でも 206 でも 304 でも
                // 416 でも同じ値が載る ── 追いかけ再生の側は「まだ伸びるのか」を
                // 416 の応答からも読む必要がある。
                //
                // `X-In-Progress` の判定は一覧の `inProgress` と同じ関数
                // （共有読み取りで開けるか）で、ブラウザはこれが false になった時点で
                // 残りを取り切って `endOfStream()` する。
                ctx.Response.Headers["X-In-Progress"] = snapshot.InProgress ? "true" : "false";

                // MSE の `codecs` パラメータ。読めなければヘッダーを付けない
                // （ブラウザ側が既定値へ倒す）── 付ける値が違うと
                // `isTypeSupported` が false になり、再生そのものが始まらない。
                if (Fmp4Probe.CodecString(RecordingFiles.ReadHeader(stream)) is { } codecs)
                    ctx.Response.Headers["X-Codecs"] = codecs;

                // **no-store ではなく no-cache。** ETag を持たせてある以上、
                // 再検証（If-None-Match → 304）を使わせた方が転送が減る。
                ctx.Response.Headers.CacheControl = "no-cache";

                // Content-Disposition は組み立てない ── `filename*=UTF-8''…` の符号化まで
                // 含めてフレームワークが書く（日本語のファイル名がそのまま通る）。
                // Range と If-None-Match の評価も同じ結果型が行う。
                var result = TypedResults.Stream(
                    new LengthCappedStream(stream, snapshot.Length),
                    VideoContentType,
                    fileDownloadName: download ? Path.GetFileName(stream.Name) : null,
                    lastModified: snapshot.LastWriteUtc,
                    entityTag: new EntityTagHeaderValue(snapshot.ETag),
                    enableRangeProcessing: true);

                await result.ExecuteAsync(ctx);
            }
        });

        // 1 ファイルの fragment 索引。**本文とは別の表現**なので ETag も別値にする。
        app.MapGet("/api/recording-fragments/{*path}", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            string root = await backend.GetRecordingsRootAsync(ctx.RequestAborted);
            FileStream? stream = await OpenRequestedAsync(ctx, root);
            if (stream is null)
                return;

            using (stream)
            {
                var snapshot = Snapshot(stream);
                byte[] header = RecordingFiles.ReadHeader(stream);

                // fragmented でない・timescale が読めないものには索引が無い。
                // **400 ではなく 404** ── 要求の書き方ではなく、その資源が無いという答えである。
                if (!Fmp4Probe.IsFragmented(header)
                    || !Fmp4Probe.TryReadMediaTimescale(header, out uint timescale))
                {
                    await ApiResponse.WriteErrorAsync(
                        ctx, 404, ApiResponse.HttpLayerExitCode, "not fragmented");
                    return;
                }

                // 長さと更新時刻は「覚えてある索引が使えるか」の判定に渡す（伸びていれば
                // `NextOffset` から読み足す）。**走査そのものはその場の長さまで進む**ので、
                // `fragments` の末尾と `nextOffset` は必ず同じ走査から出る。
                var scan = index.Get(stream.Name, stream, snapshot.Length, snapshot.LastWriteUtc);

                long from = ParseFrom(ctx.Request.Query["from"]);
                var fragments = new List<RecordingFragmentDto>();
                foreach (var fragment in scan.Fragments)
                {
                    if (from <= fragment.Offset)
                    {
                        fragments.Add(new RecordingFragmentDto(
                            fragment.Offset, fragment.Size, fragment.Time, fragment.Duration, fragment.Sync));
                    }
                }

                // 尺は**全部**の末尾から採る（`from` で切った側からではない）。
                ulong totalDuration = 0;
                if (0 < scan.Fragments.Count)
                {
                    var last = scan.Fragments[^1];
                    totalDuration = last.Time + last.Duration;
                }

                var dto = new RecordingFragmentsDto(
                    timescale,
                    Fmp4Probe.CodecString(header),
                    snapshot.InProgress,
                    scan.InitSize,
                    scan.NextOffset,
                    totalDuration,
                    [.. fragments]);

                ctx.Response.Headers.ETag = IndexETag(snapshot.ETag);
                ctx.Response.Headers.CacheControl = "no-store";
                await ApiResponse.WriteJsonAsync(
                    ctx, 200, dto, RemoteApiJsonContext.Default.RecordingFragmentsDto);
            }
        });

        // 1 ファイルのサムネイル。**要求に載るのは本体 mp4 の相対パス**で、
        // `.png` はサーバー側で足す（クライアントに sidecar の命名規則を持たせない）。
        app.MapGet("/api/recording-thumbnails/{*path}", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            string root = await backend.GetRecordingsRootAsync(ctx.RequestAborted);
            FileStream? stream = await OpenThumbnailAsync(ctx, root);
            if (stream is null)
                return;

            using (stream)
            {
                // PNG は書かれたきり伸びないので、長さと更新時刻は 1 回読めば足りる。
                long length = stream.Length;
                DateTime lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle);

                // **本体と同じ no-cache。** 再検証（If-None-Match → 304）で済ませられる。
                ctx.Response.Headers.CacheControl = "no-cache";

                // `X-In-Progress` / `X-Codecs` は付けない ── どちらも動画の本文の性質で、
                // 静止画には意味が無い。Range も断る（一度に返る大きさしかない）。
                var result = TypedResults.Stream(
                    stream,
                    ThumbnailContentType,
                    lastModified: lastWrite,
                    entityTag: new EntityTagHeaderValue(RemoteApiRules.RecordingETag(length, lastWrite)),
                    enableRangeProcessing: false);

                await result.ExecuteAsync(ctx);
            }
        });

        // 1 ファイルを別の解像度・fps へ変換し直して流す。**検査の順序が API の約束である**
        // ── クエリ（400）→ ファイル（404 / 400 / 409）→ 能力（404）→ 枠（409）の順で、
        // L1（`RecordingTranscodeEndpointTests`）がソーステキストでこの並びを固定する。
        // 前に置いた検査ほど「相手の手元だけで直せる失敗」である。
        app.MapGet("/api/recording-transcode/{*path}", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            // **省略は受けない。** 既定を 0（先頭から）に畳むと、`start` の綴りを間違えた
            // 要求が「先頭から再生された」という形で静かに成功する。
            // 無限大・NaN も弾く（`NumberStyles.Float` は `Infinity` を読む）。
            if (!double.TryParse(
                    ctx.Request.Query["start"], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double start)
                || !double.IsFinite(start) || start < 0)
            {
                await ApiResponse.WriteErrorAsync(
                    ctx, 400, ApiResponse.HttpLayerExitCode, "invalid start");
                return;
            }

            // **`custom` は受けない。** カスタムはレコーダー設定の 4 値であり、
            // 録画済みのファイルには対応する設定が存在しない（元のまま観たいなら
            // `/api/recordings/{*path}` を引けばよい）。
            string quality = ctx.Request.Query["q"].ToString();
            if (!PreviewQualityPresets.IsValidId(quality)
                || string.Equals(quality, PreviewQualityPresets.Custom, StringComparison.Ordinal))
            {
                await ApiResponse.WriteErrorAsync(
                    ctx, 400, ApiResponse.HttpLayerExitCode, "unknown transcode quality");
                return;
            }

            // **セッションはクライアントが名乗る**（同じ id での再要求＝シークが枠を引き継ぐ）。
            // id はログにも枠の Owner にも出るので、綴りをここで絞る。
            string session = ctx.Request.Query["session"].ToString();
            if (!TranscodeOpen.IsValidSessionId(session))
            {
                await ApiResponse.WriteErrorAsync(
                    ctx, 400, ApiResponse.HttpLayerExitCode, "invalid session");
                return;
            }

            string root = await backend.GetRecordingsRootAsync(ctx.RequestAborted);
            FileStream? stream = await OpenRequestedAsync(ctx, root);
            if (stream is null)
                return;

            string filePath;
            PreviewSourceInfo? source;
            using (stream)
            {
                // **録画中は断る。** `filesrc` は開いた時点の長さで終わりを決めるので、
                // 変換の途中で伸びた分は出ないまま EOS になる（＝黙って途中で切れる）。
                if (Snapshot(stream).InProgress)
                {
                    await ApiResponse.WriteErrorAsync(
                        ctx, 409, ApiResponse.HttpLayerExitCode, TranscodeReasons.InProgress);
                    return;
                }

                // **開いたハンドルはここで手放す。** 変換側は `filesrc` で開き直すので、
                // 握ったままにすると同じファイルを 2 本のハンドルで持つことになる。
                filePath = stream.Name;
                source = ReadSource(filePath);
            }

            // **能力はファイルの後に見る。** 逆順にすると、存在しないファイルの要求まで
            // 「この PC ではできない」になり、綴りの誤りに気付けない。
            if (!(await backend.GetCapabilitiesAsync(ctx.RequestAborted)).Transcode)
            {
                await ApiResponse.WriteErrorAsync(
                    ctx, 404, ApiResponse.HttpLayerExitCode, TranscodeReasons.Unavailable);
                return;
            }

            var opened = await backend.OpenTranscodeAsync(
                new TranscodeOpen(session, filePath, start, quality, source), ctx.RequestAborted);

            if (opened.Reader is not { } reader)
            {
                string reason = opened.Reason ?? TranscodeReasons.StartFailed;

                // 枠の不足だけが 409（`Retry-After` は付けない ── 空くのは他人が
                // 止めたときであって時間ではない）。残りは 503 ＋ `Retry-After`。
                if (string.Equals(reason, TranscodeReasons.Busy, StringComparison.Ordinal))
                    await ApiResponse.WriteErrorAsync(ctx, 409, ApiResponse.HttpLayerExitCode, reason);
                else
                    await ApiResponse.WriteExitCodeErrorAsync(ctx, TranscodeNotReadyExitCode, reason);

                return;
            }

            using (reader)
                await StreamTranscodeAsync(ctx, reader, start, quality);
        });
    }

    /// <summary>
    /// 変換したものを流す。<b>init（<c>ftyp</c>＋<c>moov</c>）が揃うまで応答ヘッダーを書かない</b>
    /// ── <c>X-Codecs</c> はそこからしか作れず、これが無いとブラウザ側の
    /// <c>isTypeSupported</c> が false になって再生そのものが始まらない。揃わなければ
    /// まだ 503 を書ける（ヘッダーを送った後では手遅れ）。
    ///
    /// <para>
    /// <b>溜めたバイト列は最初の書き込みでそのまま流す。</b> 解析した init だけを送り直すと、
    /// <c>moov</c> の後ろに並ぶ <c>uuid</c> 箱（mp4mux が書く）が落ちる ──
    /// この経路は中身を作らず、順序も境界も変えない。
    /// </para>
    /// <para>
    /// <b><c>TryRead</c> は呼び手のスレッドを最大 <see cref="TranscodeReadTimeoutMs"/> 塞ぐ</b>ので、
    /// 必ず <see cref="Task.Run(Func{object})"/> 越しに呼ぶ（非同期の待ちに変える）。
    /// 終端の判定は <b><c>TryRead</c> が false を返した後</b>に <c>Ended</c> を見る
    /// ── <c>Ended</c> はロックが取れなければ false を返す。
    /// </para>
    /// </summary>
    private static async Task StreamTranscodeAsync(
        HttpContext ctx, TranscodeReader reader, double start, string quality)
    {
        var ct = ctx.RequestAborted;
        var buffered = new MemoryStream();
        var clock = Stopwatch.StartNew();
        Fmp4InitInfo info = default;
        bool ready = false;

        while (!ready && !ct.IsCancellationRequested)
        {
            byte[]? chunk = await ReadChunkAsync(reader);
            if (chunk is null)
            {
                // 待ち時間切れ（まだ続きがある）か、終端。
                if (reader.Ended || TranscodeLimits.FirstChunkTimeoutMs <= clock.ElapsedMilliseconds)
                    break;
                continue;
            }

            buffered.Write(chunk, 0, chunk.Length);
            ReadOnlySpan<byte> span = buffered.GetBuffer().AsSpan(0, (int)buffered.Length);

            if (Fmp4InitInfo.TryParse(span, out info))
            {
                ready = true;
                break;
            }

            // **`moof` が出たら待つ意味は無い。** init は必ず最初の `moof` より前に揃うので、
            // ここまで来た時点で読めない init を待ち続けても答えは変わらない。
            if (HasTopLevelMoof(span)
                || TranscodeInitMaxBytes < buffered.Length
                || TranscodeLimits.FirstChunkTimeoutMs <= clock.ElapsedMilliseconds)
            {
                break;
            }
        }

        if (ct.IsCancellationRequested)
            return;

        if (!ready)
        {
            await ApiResponse.WriteExitCodeErrorAsync(
                ctx, TranscodeNotReadyExitCode, TranscodeReasons.StartFailed);
            return;
        }

        // 途中の緩衝は全部外す（ライブプレビューと同じ規律 ── 溜められると
        // 先頭が届くまでの時間がそのまま伸びる）。
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = VideoContentType;
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Codecs"] = info.Codecs;

        // **要求どおりの値をそのまま返す**（実際にどのキーフレームから始まったかではない）
        // ── クライアントは `timestampOffset` にこれを使うので、要求と一致していることが要る。
        ctx.Response.Headers["X-Transcode-Quality"] = quality;
        ctx.Response.Headers["X-Transcode-Start"] = start.ToString("0.###", CultureInfo.InvariantCulture);

        try
        {
            await ctx.Response.Body.WriteAsync(
                buffered.GetBuffer().AsMemory(0, (int)buffered.Length), ct);
            await ctx.Response.Body.FlushAsync(ct);

            while (true)
            {
                byte[]? chunk = await ReadChunkAsync(reader);
                if (chunk is null)
                {
                    // **切断はチャンクの合間にも見る。** 書き込みでしか気付かないと、
                    // 読み手が居なくなった後にパイプラインが EOS もエラーも出さずに
                    // 詰まった場合（`transcode.leak` が在る理由そのもの）に、この経路が
                    // 1 秒ごとの空振りを回り続けて枠を猶予より長く握ったままになる。
                    if (reader.Ended || ct.IsCancellationRequested)
                        return;
                    continue;
                }

                await ctx.Response.Body.WriteAsync(chunk, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // クライアントが閉じた。読み出し口は呼び出し側の using が返す。
        }
        catch (Exception) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // 切断は書き込みの失敗としても現れる（PreviewEndpoints と同形）。
        }
    }

    /// <summary>
    /// チャンクを 1 つ引く（取れなければ <see langword="null"/>）。
    /// <b>ブロッキングをスレッドプールへ逃がすためだけの包み</b>である。
    /// </summary>
    private static Task<byte[]?> ReadChunkAsync(TranscodeReader reader)
        => Task.Run(() => reader.TryRead(TranscodeReadTimeoutMs, out byte[]? chunk) ? chunk : null);

    /// <summary>
    /// 最上位の箱を辿って <c>moof</c> が現れているか。
    ///
    /// <para>
    /// <b>バイト列の素の探索にしない。</b> <c>moov</c> の中身（<c>avcC</c> の SPS など）に
    /// 同じ 4 バイトが偶然並びうるので、素の一致は「init が読めないまま `moof` が来た」を
    /// 誤って報告する。<b>箱の走査を書いているのはここだけで、深さは最上位の 1 段だけである</b>
    /// ── 木を辿る読み方は <c>Components.Fmp4SegmentSplitter</c> に一本化されている。
    /// </para>
    /// </summary>
    private static bool HasTopLevelMoof(ReadOnlySpan<byte> data)
    {
        int position = 0;

        while (position + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(position, 4));

            if (data.Slice(position + 4, 4).SequenceEqual("moof"u8))
                return true;

            if (size == 1)
            {
                // largesize（64bit）。読み切れなければ、まだ判定できない。
                if (data.Length < position + 16)
                    return false;
                size = BinaryPrimitives.ReadInt64BigEndian(data.Slice(position + 8, 8));
            }

            // size == 0 は「ファイルの末尾まで」、8 未満は壊れている ── どちらも次が無い。
            if (size < 8)
                return false;

            long next = position + size;
            if (data.Length < next)
                return false;

            position = (int)next;
        }

        return false;
    }

    /// <summary>
    /// 録画の形（幅・高さ・fps）。<b>sidecar にしか無い</b>
    /// ── fMP4 のヘッダーから読む道はこのリポジトリに無く、無ければ null
    /// （プリセットはソース未知として解決される）。
    /// </summary>
    private static PreviewSourceInfo? ReadSource(string recordingPath)
    {
        if (RecordingSidecar.TryRead(RecordingSidecar.PathFor(recordingPath))
            is not { Width: { } width, Height: { } height } sidecar)
        {
            return null;
        }

        // fps は 0 ＝「読めていない」（`PreviewQualityPresets.Resolve` は
        // 0 以下をソース未知として扱い、プリセットの fps をそのまま使う）。
        int fps = sidecar.Fps is { } value && 0 < value ? (int)Math.Round(value) : 0;
        return new PreviewSourceInfo(width, height, fps);
    }

    /// <summary>
    /// 配信 root を解決し直してから、絞り込み済みの一覧を採る。
    ///
    /// <para>
    /// <b>root の解決は要求ごとに行う。</b> 保存先は設定でいつでも変わり、
    /// 索引はそれを知る手立てを持たない ── <c>Rebind</c> は<b>同じ root で監視が
    /// 張れていて、初回の構築が終わっているあいだは</b>ロックも取らずに返るので、毎回呼んで構わない
    /// （初回の構築中はその完了を待つ）。
    /// 監視が無いあいだは要求ごとに <c>Directory.Exists</c> を 1 回引く。
    /// </para>
    /// <para>
    /// <b><c>Rebind</c> はスレッドプールへ逃がす。</b> root が変わった回と、
    /// <b>同じ root でも監視が無く、そのフォルダーが現れていた回</b>は、
    /// その場で全走査（1 件ずつ開く同期 IO）になる。
    /// </para>
    /// </summary>
    private static async Task<(string Root, IReadOnlyList<RecordingEntry> Entries)> SnapshotAsync(
        HttpContext ctx, IRemoteControlBackend backend, RecordingIndex recordings,
        DateTimeOffset? from, DateTimeOffset? to)
    {
        // **応答へ載せるのは backend が答えた文字列そのもの**（索引が正規化したものではない）
        // ── 1 ファイルの配信が root の内側かを判定するのに使うのもこちらである。
        string root = await backend.GetRecordingsRootAsync(ctx.RequestAborted);
        await Task.Run(() => recordings.Rebind(root), ctx.RequestAborted);

        return (root, RecordingQuery.Filter(
            recordings.Snapshot(), from, to, ctx.Request.Query["recorder"].ToString()));
    }

    /// <summary>索引の 1 件を応答の形へ。相対パスだけが URL 用に組み替わる。</summary>
    private static RecordingFileDto ToDto(RecordingEntry entry)
        => new(
            RemoteApiRules.ToUrlPath(entry.RelativePath),
            entry.Length, entry.LastWriteTimeUtc, entry.InProgress, entry.Fragmented,
            entry.StartTimeUtc, entry.Recorder, entry.Trigger, entry.DurationMs,
            entry.Width, entry.Height, entry.HasThumbnail);

    /// <summary>
    /// <c>from</c>（含む）と <c>to</c>（含まない）。どちらも省略できる。
    ///
    /// <para>
    /// <b>オフセットが書かれていなければ UTC として読む。</b> 既定のタイムゾーンは
    /// <c>tz</c> の側も UTC であり、片方だけ開発機のローカル時刻に倒すと、
    /// 同じ問い合わせが機械ごとに違う窓を指すことになる。
    /// </para>
    /// <para>
    /// <b>受ける書式は <see cref="InstantFormats"/> に挙げたものだけである。</b>
    /// 緩く読むと <c>10:00</c>（今日の 10 時）や <c>08/28/2026</c> まで通り、
    /// 「書式不正は 400」という約束が成立しなくなる。
    /// </para>
    /// </summary>
    private static bool TryReadWindow(
        HttpContext ctx, out DateTimeOffset? from, out DateTimeOffset? to, [NotNullWhen(false)] out string? error)
    {
        to = null;
        error = null;

        if (!TryParseInstant(ctx.Request.Query["from"], out from))
        {
            error = "from is not a valid ISO-8601 timestamp";
            return false;
        }

        if (!TryParseInstant(ctx.Request.Query["to"], out to))
        {
            error = "to is not a valid ISO-8601 timestamp";
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>from</c> / <c>to</c> が受ける ISO-8601 の形。日付だけ・秒まで・小数秒つきの 3 つと、
    /// それぞれに末尾のオフセット（<c>Z</c> または <c>±hh:mm</c>）が付いたもの。
    /// </summary>
    private static readonly string[] InstantFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-ddK",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
    ];

    private static bool TryParseInstant(string? value, out DateTimeOffset? instant)
    {
        instant = null;
        if (string.IsNullOrEmpty(value))
            return true;

        if (!DateTimeOffset.TryParseExact(
                value, InstantFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            return false;
        }

        instant = parsed;
        return true;
    }

    /// <summary>
    /// <c>limit</c>（省略で全件 ── 従来の応答と同じ）と <c>offset</c>。
    ///
    /// <para>
    /// <b>範囲外は畳まずに 400 で断る。</b> <c>limit=0</c> を「全件」に、
    /// <c>limit=100000</c> を上限に丸めると、呼び出し側は自分が何件受け取るのかを
    /// 応答を数えるまで知れない。
    /// </para>
    /// </summary>
    private static bool TryReadPaging(HttpContext ctx, out int? limit, out int offset, [NotNullWhen(false)] out string? error)
    {
        limit = null;
        offset = 0;
        error = null;

        string? rawLimit = ctx.Request.Query["limit"];
        if (!string.IsNullOrEmpty(rawLimit))
        {
            if (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || value < RecordingQuery.MinLimit || RecordingQuery.MaxLimit < value)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"limit must be between {RecordingQuery.MinLimit} and {RecordingQuery.MaxLimit}");
                return false;
            }

            limit = value;
        }

        string? rawOffset = ctx.Request.Query["offset"];
        if (!string.IsNullOrEmpty(rawOffset))
        {
            if (!int.TryParse(rawOffset, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
            {
                error = "offset must be a non-negative integer";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 要求された経路の録画ファイルを開く。開けなければ<b>応答を書き終えて</b>
    /// <see langword="null"/> を返す。
    ///
    /// <para>
    /// **要求の側に非が無いもの（無い・開けない）が 404 で、規則で断ったもの
    /// （root の外・拡張子違い・リパースポイント）は 400。** 断り方の違いで
    /// root の外のファイルの存在を当てられないようにする。
    /// `unavailable`（在るのに開けない）を 400 にすると、同じ要求が
    /// ディスクの状態だけで 400 と 200 の間を行き来することになる。
    /// </para>
    /// </summary>
    private static async Task<FileStream?> OpenRequestedAsync(HttpContext ctx, string root)
    {
        string urlPath = ctx.Request.RouteValues["path"]?.ToString() ?? string.Empty;

        if (RecordingFiles.TryOpen(
                root, RemoteApiRules.FromUrlPath(urlPath), out FileStream? stream, out string? reason))
        {
            return stream;
        }

        int status = reason is "not found" or "unavailable" ? 404 : 400;
        await ApiResponse.WriteErrorAsync(ctx, status, ApiResponse.HttpLayerExitCode, reason);
        return null;
    }

    /// <summary>
    /// 要求された経路の録画に対応するサムネイル（<c>&lt;録画ファイル名&gt;.png</c>）を開く。
    /// 開けなければ<b>応答を書き終えて</b> <see langword="null"/> を返す。
    ///
    /// <para>
    /// <b>要求に載るのは本体の <c>.mp4</c> の相対パスで、解決の規則も本体と同じ</b>
    /// （<see cref="RemoteApiRules.TryResolveUnderRoot"/> ── 拡張子 <c>.mp4</c> まで含めて
    /// 同じ規則を通す）。<c>.png</c> を足すのは解決の後である。
    /// </para>
    /// <para>
    /// <b>本体が在ることは要求しない。</b> サムネイルは録画ファイル名が決まった直後に
    /// 書かれるもので、本体が失敗して残らなくても PNG だけが残りうる
    /// （本体が消えれば <c>RecordingCleanup</c> の孤児処理が PNG も消す）。
    /// </para>
    /// <para>
    /// 断り方は本体と同じ区分 ── 規則で断ったものが 400、無い・開けないものが 404。
    /// <b>リパースポイントの検査も本体と共有する</b>
    /// （<see cref="RecordingFiles.TryOpenCompanion"/> ── 書く側は一時ファイルを
    /// 置き換える形なので、共有指定も本体と同じでなければならない）。
    /// </para>
    /// </summary>
    private static async Task<FileStream?> OpenThumbnailAsync(HttpContext ctx, string root)
    {
        string urlPath = ctx.Request.RouteValues["path"]?.ToString() ?? string.Empty;

        if (RecordingFiles.TryOpenCompanion(
                root, RemoteApiRules.FromUrlPath(urlPath), RecordingSidecar.ThumbnailExtension,
                out FileStream? stream, out string? reason))
        {
            return stream;
        }

        int status = reason is "not found" or "unavailable" ? 404 : 400;
        await ApiResponse.WriteErrorAsync(ctx, status, ApiResponse.HttpLayerExitCode, reason);
        return null;
    }

    /// <summary>
    /// 開いてあるファイルの「録画中か・長さ・更新時刻・ETag」を<b>1 組で</b>採る。
    ///
    /// <para>
    /// **「録画中か」は長さより先に採る。** 逆順にすると、2 つの読み取りの
    /// 隙間で録画が確定したときに「長さは古い・録画中は false」の組が返り、
    /// ブラウザは末尾の fragment を取り切らないまま `endOfStream()` する。
    /// この順なら最悪でも「長さは新しい・録画中は true」になり、
    /// 次の要求が 416 と `X-In-Progress: false` で終端を伝える。
    /// </para>
    /// <para>
    /// **長さと更新時刻は開いた後のものを使う。** 録画中のファイルは
    /// 列挙してから開くまでの間にも伸びており、一覧の値で ETag を作ると
    /// 実際に返す本文と食い違う。更新時刻は開いてあるハンドルから取る
    /// ── パスから取り直すと、その間に差し替えられた別の実体を見うる。
    /// </para>
    /// <para>
    /// **そして長さを読むのはこの 1 回だけ。** 要求の処理中も伸びるので、
    /// ETag・`Content-Length`・`Content-Range` の total が別々の読み取りに
    /// なると同じ応答の中で食い違う。この値で `LengthCappedStream` が
    /// 本文を切るので、3 つとも必ず一致する。
    /// </para>
    /// </summary>
    private static RecordingSnapshot Snapshot(FileStream stream)
    {
        bool inProgress = RecordingFiles.IsInProgress(stream.Name);
        long length = stream.Length;
        DateTime lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle);

        return new RecordingSnapshot(
            inProgress, length, lastWrite, RemoteApiRules.RecordingETag(length, lastWrite));
    }

    /// <summary>
    /// 索引の <c>ETag</c>。本文のものへ <c>-idx</c> を<b>引用符の内側で</b>足す
    /// ── entity-tag は引用符まで含めて 1 つの値であり、外へ足すと壊れる。
    /// 同じ経路の別の表現なので、本文と同じ値にしてはいけない。
    /// </summary>
    private static string IndexETag(string bodyETag)
        => string.Concat(bodyETag.AsSpan(0, bodyETag.Length - 1), "-idx\"");

    /// <summary>
    /// <c>from</c>（バイトオフセット）。読めない・負のものは 0 に畳む
    /// ── 索引は全件返しても正しく、断る意味が無い。
    /// </summary>
    private static long ParseFrom(string? value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long from) ? from : 0;
}
