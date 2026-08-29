using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// この実機で何が使えるか（<c>GET /api/capabilities</c>）。要る役割は
/// <see cref="RemoteRole.Viewer"/>（読み取りの規律は他の GET と同じ）。
///
/// <para>
/// <b>クライアントは起動時に 1 回だけ引く。</b> 中身は変わらないもの（デコーダーの有無）と、
/// 変わっても SSE の <c>state</c> が運ぶもの（枠の上限・空き）だけである
/// ── 能力を要求ごとに引き直させると、メニューを組むたびに往復が増える。
/// </para>
/// <para>
/// <b>枠の上限は <see cref="AuxiliaryEncoderSlots.Shared"/> から読む。</b> 設定
/// （<c>RemoteAuxiliaryEncoderLimit</c>）は setter で <c>Shared.Limit</c> へ写されており、
/// クランプ後の値を持っているのはこちらである ── 設定の生の値を読むと、
/// 範囲外を書いた直後だけ応答と実際の枠数が食い違う。
/// </para>
/// </summary>
internal static class CapabilitiesEndpoint
{
    public static void Map(WebApplication app, IRemoteControlBackend backend, RemoteAuth auth)
    {
        app.MapGet("/api/capabilities", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            var capability = await backend.GetCapabilitiesAsync(ctx.RequestAborted);

            ctx.Response.Headers.CacheControl = "no-store";
            await ApiResponse.WriteJsonAsync(
                ctx, 200,
                new CapabilitiesDto(
                    capability.Transcode, capability.Decoder, AuxiliaryEncoderSlots.Shared.Limit),
                RemoteApiJsonContext.Default.CapabilitiesDto);
        });
    }
}
