using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

    /// <summary>
    /// 開いたファイルについて、同じ 1 回の読み取りから採った値。
    /// </summary>
    /// <param name="InProgress"><c>filesink</c> が書き込みで握っているか。</param>
    /// <param name="Length">長さ。</param>
    /// <param name="LastWriteUtc">更新時刻。</param>
    /// <param name="ETag">この 2 つから作った本文の <c>ETag</c>（引用符込み）。</param>
    private readonly record struct RecordingSnapshot(
        bool InProgress, long Length, DateTime LastWriteUtc, string ETag);

    public static void Map(WebApplication app, IRemoteControlBackend backend, RemoteAuth auth)
    {
        // 索引はプロセスに 1 つ。録画中のファイルは 1 秒ごとに引き直されるので、
        // 覚えておかないと毎回ファイル全体を辿ることになる。
        var index = new Fmp4FragmentIndexCache();

        app.MapGet("/api/recordings", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            string root = await backend.GetRecordingsRootAsync(ctx.RequestAborted);

            // **列挙はスレッドプールの別スレッドへ逃がす。** 1 件ずつ開いて
            // 「録画中か」を判定するので、件数に比例した同期 IO になる。
            var files = await Task.Run(() => RecordingFiles.Enumerate(root), ctx.RequestAborted);

            var dto = new RecordingsDto(
                root,
                files.Select(f => new RecordingFileDto(
                    RemoteApiRules.ToUrlPath(f.RelativePath),
                    f.Length, f.LastWriteTimeUtc, f.InProgress, f.Fragmented)).ToArray());

            // 一覧は毎回作り直す（録画中のファイルは長さが動き続ける）。
            ctx.Response.Headers.CacheControl = "no-store";
            await ApiResponse.WriteJsonAsync(ctx, 200, dto, RemoteApiJsonContext.Default.RecordingsDto);
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
