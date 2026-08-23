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

    public static void Map(WebApplication app, IRemoteControlBackend backend)
    {
        app.MapGet("/api/settings", async (HttpContext ctx) =>
        {
            var settings = await backend.GetAppSettingsAsync(ctx.RequestAborted);
            settings.Remove(AccessTokenKey);
            await ApiResponse.WriteJsonAsync(
                ctx, 200, settings, RemoteApiJsonContext.Default.JsonObject);
        });
    }
}
