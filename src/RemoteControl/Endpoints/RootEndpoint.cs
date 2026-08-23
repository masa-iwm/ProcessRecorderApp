using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// ルート。<c>?token=</c> 付きのときだけブラウザ用のセッションを発行する。
///
/// <para>
/// <b>発行したら 302 でクエリを落とす。</b> トークンがアドレスバーと履歴に
/// 残り続けないようにするためで、以後の要求は Cookie が運ぶ。
/// </para>
/// </summary>
internal static class RootEndpoint
{
    /// <summary>波 5 で Web UI に置き換わるまでの仮の本文。</summary>
    public const string Placeholder = "ProcessRecorderApp remote control";

    public static void Map(WebApplication app, RemoteAuth auth)
    {
        app.MapGet("/", async (HttpContext ctx) =>
        {
            if (ctx.Request.Query.TryGetValue("token", out var presented))
            {
                string? session = auth.TryIssueSession(presented.ToString());
                if (session is null)
                {
                    auth.ReportFailure(ctx);
                    await ApiResponse.WriteErrorAsync(
                        ctx, 401, ApiResponse.HttpLayerExitCode, "invalid token");
                    return;
                }

                ctx.Response.Cookies.Append(RemoteAuth.CookieName, session, new CookieOptions
                {
                    // HTTP のみ（HTTPS は v1 の対象外）なので Secure は付けない。
                    // HttpOnly はスクリプトから読ませないため、SameSite=Strict は
                    // 他所のページからの遷移で Cookie を送らせないため。
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                });
                ctx.Response.Headers.Location = "/";
                ctx.Response.StatusCode = 302;
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(Placeholder);
        });
    }
}
