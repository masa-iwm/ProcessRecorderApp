using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>アプリ設定を読む経路。</summary>
internal static class SettingsEndpoints
{
    /// <summary>
    /// 応答から必ず落とすキー。<b>拒否リストによる絞り込みとは別に、ここでも落とす。</b>
    ///
    /// <para>
    /// アクセストークンは<b>それ 1 つで Admin として通る秘密</b>で、利用者定義は
    /// <b>パスワードのハッシュ</b>である。読み取りは <see cref="RemoteRole.Viewer"/> で通る
    /// ── ゲスト読み取りを許していれば未認証でも通る ── のだから、
    /// ここに出ると Viewer が Admin へ昇格する道具を手にすることになる。
    /// <c>RemoteUserList</c> は人数の表示文字列だが、<c>RemoteUsers</c> と対で落とす
    /// （片方だけ残すと「何人居るか」だけが漏れる形になり、意図が読めない）。
    /// </para>
    /// </summary>
    public static readonly string[] HiddenKeys =
    [
        "RemoteControlAccessToken",
        "RemoteUsers",
        "RemoteUserList",
    ];

    public static void Map(WebApplication app, IRemoteControlBackend backend, RemoteAuth auth)
    {
        app.MapGet("/api/settings", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            var settings = await backend.GetAppSettingsAsync(ctx.RequestAborted);
            foreach (string hidden in HiddenKeys)
                settings.Remove(hidden);
            await ApiResponse.WriteJsonAsync(
                ctx, 200, settings, RemoteApiJsonContext.Default.JsonObject);
        });

        app.MapPatch("/api/settings", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Admin, write: true))
                return;

            if (await ApiResponse.ReadJsonObjectAsync(ctx) is not { } patch)
                return;

            var result = await backend.PatchAppSettingsAsync(patch);
            await ApiResponse.WriteJsonAsync(
                ctx, 200, result, RemoteApiJsonContext.Default.PatchResultDto);
        });

        // **レコーダー設定に秘密は無い** ── アクセストークンと利用者定義はアプリ設定側に
        // あり、そちらは許可リストにも載らず、応答からも落としてある。
        app.MapGet("/api/recorders/{id}/settings", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            var settings = await backend.GetRecorderSettingsAsync(ControlEndpoints.RouteId(ctx));
            await ApiResponse.WriteJsonAsync(
                ctx, 200, settings, RemoteApiJsonContext.Default.RecorderSettingsDto);
        });

        app.MapPatch("/api/recorders/{id}/settings", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Admin, write: true))
                return;

            if (await ApiResponse.ReadJsonObjectAsync(ctx) is not { } patch)
                return;

            var result = await backend.PatchRecorderSettingsAsync(ControlEndpoints.RouteId(ctx), patch);
            await ApiResponse.WriteJsonAsync(
                ctx, 200, result, RemoteApiJsonContext.Default.PatchResultDto);
        });
    }
}
