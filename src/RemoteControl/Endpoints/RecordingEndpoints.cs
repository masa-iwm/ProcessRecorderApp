using System;
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
/// </summary>
internal static class RecordingEndpoints
{
    /// <summary>配信する MIME 型。録画は必ず MP4（<c>RecordingCleanup.Extension</c> で絞ってある）。</summary>
    private const string VideoContentType = "video/mp4";

    public static void Map(WebApplication app, IRemoteControlBackend backend, RemoteAuth auth)
    {
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
                    RemoteApiRules.ToUrlPath(f.RelativePath), f.Length, f.LastWriteTimeUtc, f.InProgress)).ToArray());

            // 一覧は毎回作り直す（録画中のファイルは長さが動き続ける）。
            ctx.Response.Headers.CacheControl = "no-store";
            await ApiResponse.WriteJsonAsync(ctx, 200, dto, RemoteApiJsonContext.Default.RecordingsDto);
        });

        app.MapGet("/api/recordings/{*path}", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            string root = await backend.GetRecordingsRootAsync(ctx.RequestAborted);
            string urlPath = ctx.Request.RouteValues["path"]?.ToString() ?? string.Empty;

            if (!RecordingFiles.TryOpen(root, RemoteApiRules.FromUrlPath(urlPath), out FileStream? stream, out string? reason))
            {
                // **要求の側に非が無いもの（無い・開けない）が 404 で、規則で断ったもの
                // （root の外・拡張子違い・リパースポイント）は 400。** 断り方の違いで
                // root の外のファイルの存在を当てられないようにする。
                // `unavailable`（在るのに開けない）を 400 にすると、同じ要求が
                // ディスクの状態だけで 400 と 200 の間を行き来することになる。
                int status = reason is "not found" or "unavailable" ? 404 : 400;
                await ApiResponse.WriteErrorAsync(ctx, status, ApiResponse.HttpLayerExitCode, reason);
                return;
            }

            using (stream)
            {
                // **長さと更新時刻は開いた後のものを使う。** 録画中のファイルは
                // 列挙してから開くまでの間にも伸びており、一覧の値で ETag を作ると
                // 実際に返す本文と食い違う。更新時刻は開いてあるハンドルから取る
                // ── パスから取り直すと、その間に差し替えられた別の実体を見うる。
                //
                // **既知の制約**: ETag の長さと本文の Content-Length は別々の
                // `stream.Length` の読み取りであり、伸び続けるファイルでは両者が
                // 食い違いうる（結果型が本文を書くときにもう一度読む）。
                // 出力が `faststart` なら録画中の長さは 0 のままなので影響は無い。
                long length = stream.Length;
                DateTime lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                string etag = RemoteApiRules.RecordingETag(length, lastWrite);

                bool download = string.Equals(ctx.Request.Query["download"], "1", StringComparison.Ordinal);

                // **no-store ではなく no-cache。** ETag を持たせてある以上、
                // 再検証（If-None-Match → 304）を使わせた方が転送が減る。
                ctx.Response.Headers.CacheControl = "no-cache";

                // Content-Disposition は組み立てない ── `filename*=UTF-8''…` の符号化まで
                // 含めてフレームワークが書く（日本語のファイル名がそのまま通る）。
                // Range と If-None-Match の評価も同じ結果型が行う。
                var result = TypedResults.Stream(
                    stream,
                    VideoContentType,
                    fileDownloadName: download ? Path.GetFileName(stream.Name) : null,
                    lastModified: lastWrite,
                    entityTag: new EntityTagHeaderValue(etag),
                    enableRangeProcessing: true);

                await result.ExecuteAsync(ctx);
            }
        });
    }
}
