using System;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// ライブプレビューの配信（<c>GET /api/recorders/{id}/preview.mp4</c>）。
/// <b>GET だけなので認証は要らない</b>（読み取りの規律は他の GET と同じ）。
///
/// <para>
/// <b>本文は fMP4 の連続で、終端は無い。</b> 最初の 1 件が init セグメント
/// （<c>ftyp</c>＋<c>moov</c>）で、以後は <c>moof</c>＋<c>mdat</c> の対が続く。
/// 長さは決まらないので <c>Content-Length</c> は付けず chunked で送る
/// ── ブラウザ側は MSE（<c>SourceBuffer.appendBuffer</c>）で受ける。
/// </para>
/// <para>
/// <b>供給が完了したら応答を閉じるだけ</b>で、同じ接続で続きを送らない。
/// 2 つ目の init は契約で禁止されており（<c>PreviewSubscription</c> の doc）、
/// ストリームの作り直しは<b>クライアントの再接続</b>として表現する。
/// </para>
/// <para>
/// <b>脱落（<c>DroppedSegments</c>）はここでは見ない。</b> 捨て過ぎた購読は
/// 供給側が完了させる契約なので、HTTP 層に同じ判定を置くと基準が 2 か所になる。
/// </para>
/// </summary>
internal static class PreviewEndpoints
{
    /// <summary>配信する MIME 型（中身は fragmented MP4）。</summary>
    private const string PreviewContentType = "video/mp4";

    public static void Map(WebApplication app, IRemoteControlBackend backend)
    {
        app.MapGet("/api/recorders/{id}/preview.mp4", async (HttpContext ctx) =>
        {
            string id = ctx.Request.RouteValues["id"]?.ToString() ?? string.Empty;
            var ct = ctx.RequestAborted;

            // **購読を先に取る。** ここで失敗すれば応答はまだ始まっていないので、
            // 例外の受け口が 404 / 503 の JSON を書ける（SSE と同じ順序）。
            using var subscription = await backend.SubscribePreviewAsync(id, ct);

            // 途中の緩衝は全部外す ── 溜められると「ライブ」という約束が成立しない。
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = PreviewContentType;
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            // **ヘッダーは最初のセグメントより前に送る。** mux が起きて init が
            // 出るまでには最大 1 GOP かかるので、これが無いと「繋がったが待っている」と
            // 「繋がっていない」が呼び出し側から区別できない。
            await ctx.Response.StartAsync(ct);

            try
            {
                await foreach (var segment in subscription.Segments.ReadAllAsync(ct))
                {
                    // 1 セグメントごとに必ず flush する。まとめて送ると、
                    // 受け手が append できるようになるまでの遅延がそのまま伸びる。
                    await ctx.Response.Body.WriteAsync(segment.Bytes, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // クライアントが閉じた。席は using が返す。
            }
            catch (Exception) when (ctx.RequestAborted.IsCancellationRequested)
            {
                // **切断は書き込みの失敗としても現れる**（IOException /
                // ConnectionResetException）。ここで握らないと、接続を切るだけで
                // remote.error にスタック付きの記録を無制限に積める
                // ── 応答はもう誰も読んでいないので、書けなくなったこと自体に意味は無い。
            }
        });
    }
}
