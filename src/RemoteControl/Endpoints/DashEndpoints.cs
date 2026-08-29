using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// DASH プレビューの配信（<c>GET /api/recorders/{id}/dash/{file}</c>）。
/// <b>要る役割は <see cref="RemoteRole.Viewer"/></b>（<c>preview.mp4</c> と同じ読み取りの規律）。
///
/// <para>
/// <b>経路は 1 本だけ。</b> manifest・init・セグメントは同じディレクトリに居なければならず
/// （MPD の URL は相対）、3 つに分けるとその不変条件がルーティングの表に散る。
/// 名前の解釈は <see cref="DashRoutes"/> の純関数 1 つで、そこが false を返したものは
/// <b>存在しない経路</b>として <c>MapFallback</c> と同じ形の 404 にする。
/// </para>
/// <para>
/// <b>要求 1 本が姿 1 枚に対応する。</b> <c>GetDashPreviewSnapshotAsync</c> は不変の姿を返し、
/// <b>呼ぶこと自体が貸出を延ばす</b> ── 誰も引かなくなれば第 2 パイプラインは
/// <see cref="DashPreviewLimits.LeaseMs"/> 後に畳まれる。したがって
/// <b>クライアントが manifest を引き続けることが「見ている」の唯一の表明</b>である。
/// </para>
/// <para>
/// <b>「まだ始まっていない」を特別扱いしない。</b> 供給側が返す
/// <see cref="DashPreviewReasons.Starting"/> はそのまま 503 の本文の <c>error</c> になり
/// （<c>Retry-After: 5</c> 付き）、再試行するかどうかはクライアントが決める
/// ── ここで待つと、要求 1 本が最大 1 GOP ぶんスレッドを占める。
/// </para>
/// <para>
/// <b>補助エンコーダー枠が空いていない（<see cref="DashPreviewReasons.Busy"/>）だけは
/// 409 で返す。</b> 待てば直る点は 503 と同じだが、<b>空くのは他人が止めたとき</b>であって
/// 時間ではない ── <c>Retry-After</c> の秒数を書くと、待つ根拠の無い数字を配ることになる。
/// クライアントは SSE の <c>state</c> の <c>auxiliaryEncodersFree</c> で解除を知る。
/// </para>
/// <para>
/// <b><c>ETag</c> も <c>Range</c> も使わない。</b> セグメントはリングから落ちれば
/// 二度と戻らないので、条件付き要求に意味のある「同じ表現」が存在しない
/// （すべて <c>Cache-Control: no-store</c>）。
/// </para>
/// </summary>
internal static class DashEndpoints
{
    /// <summary>MPD の MIME 型（本文は常に UTF-8）。</summary>
    private const string ManifestContentType = "application/dash+xml; charset=utf-8";

    /// <summary>Init セグメントの MIME 型（<c>ftyp</c>＋<c>moov</c>）。</summary>
    private const string InitContentType = "video/mp4";

    /// <summary>メディアセグメントの MIME 型（<c>moof</c>＋<c>mdat</c>）。</summary>
    private const string MediaContentType = "video/iso.segment";

    /// <summary>
    /// この応答が属する連続体の通し番号。<b>MPD の <c>Period@id</c> と同じ値</b>で、
    /// 変わったらクライアントは <c>MediaSource</c> ごと作り直す。
    /// </summary>
    private const string GenerationHeader = "X-Dash-Generation";

    /// <summary>
    /// この連続体を組んだときのライブ画質の id（<c>custom</c> なら設定 4 値そのまま）。
    /// <b>指示ではなく実際に配信しているもの</b>で、クライアントはこれで
    /// 「切り替えが効いたか」を判定する。
    /// </summary>
    private const string QualityHeader = "X-Dash-Quality";

    public static void Map(WebApplication app, IRemoteControlBackend backend, RemoteAuth auth)
    {
        app.MapGet("/api/recorders/{id}/dash/{file}", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            string file = ctx.Request.RouteValues["file"]?.ToString() ?? string.Empty;
            if (!DashRoutes.TryParse(file, out DashRouteKind kind, out ulong time))
            {
                // 知らない名前は「経路が無い」── MapFallback と同じ終了コード 4 で答える。
                await ApiResponse.WriteErrorAsync(ctx, 404, ApiResponse.HttpLayerExitCode, "not found");
                return;
            }

            string id = ctx.Request.RouteValues["id"]?.ToString() ?? string.Empty;
            var ct = ctx.RequestAborted;

            // **姿を先に取る。** ここで失敗すれば応答はまだ始まっていないので、
            // RemoteControlHost の例外の受け口が 404（13）/ 503（12・Retry-After 付き）の
            // JSON を書ける（PreviewEndpoints と同じ順序）。
            DashPreviewSnapshot snapshot;
            try
            {
                snapshot = await backend.GetDashPreviewSnapshotAsync(id, ct);
            }
            catch (RemoteApiException ex) when (ex.Message == DashPreviewReasons.Busy)
            {
                // **枠が無いのは 409。** 受け口の写像（12 → 503 ＋ Retry-After）へ落とすと、
                // 待つ根拠の無い秒数が付く ── 終了コードは経路そのものの失敗と同じ 4 で、
                // MapFallback の 404 と同じく状態を明示して書く。
                await ApiResponse.WriteErrorAsync(ctx, 409, ApiResponse.HttpLayerExitCode, ex.Message);
                return;
            }

            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers[GenerationHeader] =
                snapshot.Generation.ToString(CultureInfo.InvariantCulture);
            ctx.Response.Headers[QualityHeader] = snapshot.QualityId;

            if (kind == DashRouteKind.Manifest)
            {
                ctx.Response.ContentType = ManifestContentType;
                await WriteBodyAsync(ctx, Encoding.UTF8.GetBytes(BuildManifest(snapshot)), ct);
                return;
            }

            if (kind == DashRouteKind.Init)
            {
                ctx.Response.ContentType = InitContentType;
                ctx.Response.ContentLength = snapshot.Init.Length;
                await WriteBodyAsync(ctx, snapshot.Init, ct);
                return;
            }

            if (!TryFindSegment(snapshot, time, out ReadOnlyMemory<byte> bytes))
            {
                // リングから落ちた／まだ来ていない／別の連続体のもの。**区別しない** ──
                // どれも「この姿には無い」であり、クライアントの対処は同じ（飛ばす）。
                await ApiResponse.WriteErrorAsync(
                    ctx, 404, ApiResponse.HttpLayerExitCode, "segment not available");
                return;
            }

            ctx.Response.ContentType = MediaContentType;
            ctx.Response.ContentLength = bytes.Length;
            await WriteBodyAsync(ctx, bytes, ct);
        });
    }

    /// <summary>
    /// この姿から MPD を 1 枚組む。<c>publishTime</c> だけは姿に無い値
    /// （＝「この応答をいつ作ったか」）なので、ここで入れる。
    /// </summary>
    private static string BuildManifest(DashPreviewSnapshot snapshot)
    {
        var timeline = new List<(ulong Time, ulong Duration)>(snapshot.Segments.Count);
        foreach (var segment in snapshot.Segments)
            timeline.Add((segment.Time, segment.Duration));

        return DashManifest.Build(new DashManifestInput(
            snapshot.Timescale,
            snapshot.Codecs,
            snapshot.Width,
            snapshot.Height,
            snapshot.Fps,
            snapshot.BitrateKbps,
            snapshot.AvailabilityStartTimeUtc,
            DateTimeOffset.UtcNow,
            snapshot.Generation,
            snapshot.PresentationTimeOffset,
            timeline));
    }

    /// <summary>
    /// <paramref name="time"/> と<b>完全に一致する</b> 1 件。近いものは返さない
    /// ── <c>SegmentTimeline</c> の <c>t</c> はクライアントが manifest から読んだ値そのもので、
    /// ずれた中身を返すと復号は続くのに時刻だけが合わなくなる。
    /// </summary>
    private static bool TryFindSegment(DashPreviewSnapshot snapshot, ulong time, out ReadOnlyMemory<byte> bytes)
    {
        foreach (var segment in snapshot.Segments)
        {
            if (segment.Time == time)
            {
                bytes = segment.Bytes;
                return true;
            }
        }

        bytes = default;
        return false;
    }

    /// <summary>
    /// 本文を 1 回で書く。<b>切断は失敗として記録しない</b>
    /// （<c>PreviewEndpoints</c> と同形）── 応答はもう誰も読んでいないので、
    /// 書けなくなったこと自体に意味は無い。
    /// </summary>
    private static async System.Threading.Tasks.Task WriteBodyAsync(
        HttpContext ctx, ReadOnlyMemory<byte> body, System.Threading.CancellationToken ct)
    {
        try
        {
            await ctx.Response.Body.WriteAsync(body, ct);
        }
        catch (OperationCanceledException)
        {
            // クライアントが閉じた。
        }
        catch (Exception) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // 切断は書き込みの失敗としても現れる（IOException / ConnectionResetException）。
        }
    }
}
