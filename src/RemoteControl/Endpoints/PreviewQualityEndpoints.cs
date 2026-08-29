using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// ライブ画質プリセットの読み取りと切り替え。
///
/// <para>
/// <b>読みは <see cref="RemoteRole.Viewer"/>、書きは <see cref="RemoteRole.Operator"/>。</b>
/// 切り替えは<b>そのレコーダーの全視聴者に効く</b>（最後勝ち）ので、
/// 読み取りだけの利用者に触らせない ── ただし<b>永続はしない</b>ので、
/// settings.json を書く <c>PATCH /api/recorders/{id}/settings</c>（Admin）とは別物である。
/// </para>
/// <para>
/// <b>読みは配信エンジンを起こさない。</b> 選択肢を出すだけで第 2 パイプラインが立つと、
/// 画面を開いただけのレコーダーが再エンコードを始めることになる
/// （<c>DashEndpoints</c> の manifest だけが「見ている」の表明である）。
/// </para>
/// <para>
/// <b>知らない id は経路で断る。</b> 検査は
/// <see cref="PreviewQualityPresets.IsValidId"/> の 1 か所で、供給側は
/// 検査済みの値しか受け取らない。
/// </para>
/// </summary>
internal static class PreviewQualityEndpoints
{
    /// <summary>id が表にも <c>custom</c> にも無いとき（本文が空のときも同じ）の文言。</summary>
    private const string UnknownQuality = "unknown preview quality";

    public static void Map(WebApplication app, IRemoteControlBackend backend, RemoteAuth auth)
    {
        app.MapGet("/api/recorders/{id}/preview/qualities", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            var state = await backend.GetPreviewQualityAsync(
                ControlEndpoints.RouteId(ctx), ctx.RequestAborted);
            await WriteStateAsync(ctx, state);
        });

        app.MapPost("/api/recorders/{id}/preview/quality", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Operator, write: true))
                return;

            if (await ApiResponse.ReadJsonAsync(
                    ctx, RemoteApiJsonContext.Default.PreviewQualityRequestDto) is not { } request)
            {
                return;
            }

            if (request.Id is not { } qualityId || !PreviewQualityPresets.IsValidId(qualityId))
            {
                await ApiResponse.WriteErrorAsync(
                    ctx, 400, ApiResponse.HttpLayerExitCode, UnknownQuality);
                return;
            }

            var state = await backend.SetPreviewQualityAsync(
                ControlEndpoints.RouteId(ctx), qualityId, ctx.RequestAborted);
            await WriteStateAsync(ctx, state);
        });
    }

    /// <summary>
    /// 姿を 1 つ書く。<b>両方の経路で同じ本体</b>
    /// ── 切り替えた側が結果をもう一度取りに来ないで済む。
    /// </summary>
    private static Task WriteStateAsync(HttpContext ctx, PreviewQualityState state)
    {
        // 指示は誰でも変えられる（最後勝ち）ので、途中に置かせない。
        ctx.Response.Headers.CacheControl = "no-store";
        return ApiResponse.WriteJsonAsync(
            ctx, 200, ToDto(state), RemoteApiJsonContext.Default.PreviewQualityStateDto);
    }

    private static PreviewQualityStateDto ToDto(PreviewQualityState state)
    {
        var qualities = new List<PreviewQualityOptionDto>(state.Qualities.Count);
        foreach (var option in state.Qualities)
        {
            qualities.Add(new PreviewQualityOptionDto(
                option.Id, option.Label,
                option.Quality.Width, option.Quality.Height,
                option.Quality.Fps, option.Quality.BitrateKbps));
        }

        PreviewSourceDto? source = state.Source is { } known
            ? new PreviewSourceDto(known.Width, known.Height, known.Fps)
            : null;

        PreviewEffectiveQualityDto? effective =
            state is { EffectiveId: { } effectiveId, Effective: { } quality }
                ? new PreviewEffectiveQualityDto(
                    effectiveId, quality.Width, quality.Height, quality.Fps, quality.BitrateKbps)
                : null;

        return new PreviewQualityStateDto(state.Current, source, effective, qualities);
    }
}
