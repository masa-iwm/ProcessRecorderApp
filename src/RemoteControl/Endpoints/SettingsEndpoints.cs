using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>アプリ設定を読む経路。</summary>
internal static class SettingsEndpoints
{
    /// <summary>
    /// 応答から必ず落とすキー。<b>拒否リストによる絞り込みとは別に、ここでも落とす。</b>
    /// アクセストークンが読み取り（認証不要）で漏れると、
    /// 「読み取りは誰でも・書き込みはトークン」という分け方そのものが無意味になる。
    /// </summary>
    public const string AccessTokenKey = "RemoteControlAccessToken";

    public static void Map(WebApplication app, IRemoteControlBackend backend, RemoteAuth auth)
    {
        app.MapGet("/api/settings", async (HttpContext ctx) =>
        {
            var settings = await backend.GetAppSettingsAsync(ctx.RequestAborted);
            settings.Remove(AccessTokenKey);
            await ApiResponse.WriteJsonAsync(
                ctx, 200, settings, RemoteApiJsonContext.Default.JsonObject);
        });

        app.MapPatch("/api/settings", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth))
                return;

            if (await ApiResponse.ReadJsonObjectAsync(ctx) is not { } patch)
                return;

            var result = await backend.PatchAppSettingsAsync(patch);
            await ApiResponse.WriteJsonAsync(
                ctx, 200, result, RemoteApiJsonContext.Default.PatchResultDto);
        });

        // **レコーダー設定の読み取りは認証不要**（GET は全部そうである）。
        // ここに秘密は無い ── アクセストークンはアプリ設定側にあり、そちらは
        // 許可リストにも載っていない。
        app.MapGet("/api/recorders/{id}/settings", async (HttpContext ctx) =>
        {
            var settings = await backend.GetRecorderSettingsAsync(ControlEndpoints.RouteId(ctx));
            await ApiResponse.WriteJsonAsync(
                ctx, 200, settings, RemoteApiJsonContext.Default.RecorderSettingsDto);
        });

        app.MapPatch("/api/recorders/{id}/settings", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth))
                return;

            if (await ApiResponse.ReadJsonObjectAsync(ctx) is not { } patch)
                return;

            var result = await backend.PatchRecorderSettingsAsync(ControlEndpoints.RouteId(ctx), patch);
            await ApiResponse.WriteJsonAsync(
                ctx, 200, result, RemoteApiJsonContext.Default.PatchResultDto);
        });
    }
}
