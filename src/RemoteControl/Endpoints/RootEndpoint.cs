using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// ルートと Web UI の資産。<c>?token=</c> 付きのときだけブラウザ用のセッションを発行する。
///
/// <para>
/// <b>発行したら 302 でクエリを落とす。</b> トークンがアドレスバーと履歴に
/// 残り続けないようにするためで、以後の要求は Cookie が運ぶ。
/// </para>
/// <para>
/// <b>1 セグメントの GET だけを資産として扱う。</b> <c>/api/...</c> は 2 セグメント以上なので
/// <c>/{name}</c> とは競合しない ── 逆に、ここで一致してしまった要求は
/// <c>MapFallback</c> へは戻れないので、未知の名前の 404 はこの中で書く。
/// </para>
/// </summary>
internal static class RootEndpoint
{
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

            await WriteAssetAsync(ctx, IndexAsset);
        });

        app.MapGet("/{name}", async (HttpContext ctx) =>
        {
            string name = ctx.Request.RouteValues["name"]?.ToString() ?? string.Empty;
            if (!await WriteAssetAsync(ctx, name))
            {
                await ApiResponse.WriteErrorAsync(
                    ctx, 404, ApiResponse.HttpLayerExitCode, "unknown endpoint");
            }
        });
    }

    /// <summary>ルートが返す資産。</summary>
    private const string IndexAsset = "index.html";

    /// <summary>
    /// 資産を書く。<see cref="WebAssets.Manifest"/> に無い名前なら何も書かずに
    /// <see langword="false"/>（呼び出し側が 404 を書く）。
    ///
    /// <para>
    /// <c>Cache-Control: no-cache</c> と <c>ETag</c> の組で、
    /// <b>毎回問い合わせるが変わっていなければ本文を送らない</b>形にする
    /// ── 資産は発行のたびに変わりうるので、期限で持たせるわけにはいかない。
    /// </para>
    /// </summary>
    private static async Task<bool> WriteAssetAsync(HttpContext ctx, string name)
    {
        if (!WebAssets.TryGet(name, out byte[] bytes, out string contentType))
            return false;

        string etag = WebAssets.ETagFor(name, bytes);

        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers.CacheControl = "no-cache";

        if (MatchesETag(ctx.Request.Headers.IfNoneMatch.ToString(), etag))
        {
            // 304 に本文は付けられない（Content-Length も書かない）。
            ctx.Response.StatusCode = 304;
            return true;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        return true;
    }

    /// <summary>
    /// <c>If-None-Match</c> が <paramref name="etag"/> を含むか。
    /// <c>*</c> は「その資源が在るなら一致」の意味なので、ここでは常に真。
    ///
    /// <para>
    /// 比較は<b>弱い比較</b>（RFC 9110）── 先頭の <c>W/</c> を落としてから突き合わせる。
    /// 中継が弱い ETag に書き換えて返してくることがあり、そのまま比べると
    /// 再検証が常に外れて本文を配り直すことになる。
    /// </para>
    /// </summary>
    private static bool MatchesETag(string ifNoneMatch, string etag)
    {
        if (ifNoneMatch.Length == 0)
            return false;

        foreach (string candidate in ifNoneMatch.Split(','))
        {
            string trimmed = candidate.Trim();
            if (trimmed.StartsWith("W/", StringComparison.Ordinal))
                trimmed = trimmed[2..];

            if (trimmed is "*" || string.Equals(trimmed, etag, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
