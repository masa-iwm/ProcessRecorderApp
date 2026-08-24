using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// 名乗る・降りる・今の自分（<c>POST /api/login</c>・<c>POST /api/logout</c>・<c>GET /api/me</c>）。
///
/// <para>
/// <b><c>/api/login</c> だけは <see cref="AuthGate"/> を通さない</b> ── ここは
/// 名乗る前に叩く経路だからである。それでも <c>X-PRApp-Client</c> は要求する:
/// CSRF 対策は「Cookie を持っている相手」ではなく「他所のページからの送信」を止める
/// もので、ログインもセッションを<b>作る</b>以上その対象に入る。
/// </para>
/// <para>
/// <b>成功だけを記録する。</b> 失敗は既存の <c>remote.auth fail</c>（1 分に 1 行へ間引く）
/// が受け持ち、利用者名は書かない ── activity.log は貼り付けて共有される。
/// </para>
/// </summary>
internal static class AuthEndpoints
{
    public static void Map(WebApplication app, RemoteAuth auth)
    {
        app.MapPost("/api/login", async (HttpContext ctx) =>
        {
            // **本文を読むより先にヘッダーを見る。** 他所のページからの送信は
            // ここで止まり、パスワードの照合（PBKDF2 60 万回）まで到達しない。
            if (!RemoteAuth.HasClientHeader(ctx))
            {
                await ApiResponse.WriteErrorAsync(
                    ctx, 403, ApiResponse.HttpLayerExitCode, "client header required");
                return;
            }

            if (await ApiResponse.ReadJsonAsync(ctx, RemoteApiJsonContext.Default.LoginRequestDto)
                is not { } request)
            {
                return;
            }

            if (auth.TryLogin(request.User ?? "", request.Password ?? "") is not { } session)
            {
                auth.ReportFailure(ctx);
                await ApiResponse.WriteErrorAsync(
                    ctx, 401, ApiResponse.HttpLayerExitCode, "invalid credentials");
                return;
            }

            ctx.Response.Cookies.Append(
                RemoteAuth.CookieName, session.SessionId, RemoteAuth.SessionCookieOptions());

            string role = RoleName(session.Principal.Role);
            ActivityLog.Info("remote.auth login", $"user={session.Principal.Name} role={role}");

            await ApiResponse.WriteJsonAsync(
                ctx, 200, new LoginResultDto(session.Principal.Name, role),
                RemoteApiJsonContext.Default.LoginResultDto);
        });

        app.MapPost("/api/logout", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: true))
                return;

            // 名前は失効させる前に読む（Bearer で来た相手には Cookie が無い）。
            string name = auth.Resolve(ctx)?.Name ?? "";

            auth.RemoveSession(ctx.Request.Cookies[RemoteAuth.CookieName]);
            // **削除も同じ属性で出す。** Path や SameSite が違うと、ブラウザは
            // 別の Cookie を消したことにして元の 1 本を残す。
            ctx.Response.Cookies.Delete(RemoteAuth.CookieName, RemoteAuth.SessionCookieOptions());

            ActivityLog.Info("remote.auth logout", $"user={name}");

            await ApiResponse.WriteJsonAsync(ctx, 200, new OkDto(true), RemoteApiJsonContext.Default.OkDto);
        });

        app.MapGet("/api/me", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            // ここへ来た未認証の要求は「ゲスト読み取りが許可されている」ものだけ。
            MeDto me = auth.Resolve(ctx) is { } principal
                ? new MeDto(principal.Name, RoleName(principal.Role), Guest: false)
                : new MeDto("", RoleName(RemoteRole.Viewer), Guest: true);

            await ApiResponse.WriteJsonAsync(ctx, 200, me, RemoteApiJsonContext.Default.MeDto);
        });
    }

    /// <summary>
    /// 役割の名前。<b>switch で書く</b> ── <c>Enum.ToString()</c> は
    /// Native AOT でメタデータの保持を要求するので、封筒に出る文字列は明示する
    /// （settings.json 側の名前と同じ綴りであること）。
    /// </summary>
    internal static string RoleName(RemoteRole role) => role switch
    {
        RemoteRole.Admin => "Admin",
        RemoteRole.Operator => "Operator",
        _ => "Viewer",
    };
}
