using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// 認証の疎通確認。<b>v1 の恒久 API</b> ── 「名乗れているか」だけを見たいので
/// 要る役割は <see cref="RemoteRole.Viewer"/> だが、<b>書き込み扱い</b>にしてある
/// （クライアントヘッダーまで含めて確かめられる ── 「トークンは合っているのに
/// ヘッダーを忘れている」を、副作用のある操作を試さずに切り分けるための経路）。
/// </summary>
internal static class PingEndpoint
{
    public static void Map(WebApplication app, RemoteAuth auth)
    {
        app.MapPost("/api/ping", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: true))
                return;

            await ApiResponse.WriteJsonAsync(ctx, 200, new OkDto(true), RemoteApiJsonContext.Default.OkDto);
        });
    }
}

/// <summary>
/// 要求の門。<see cref="RemoteAuth.Authorize"/> の判定と、断るときの応答・記録をまとめる。
/// <b>静的資産（<c>/</c>・<c>/{name}</c>）と <c>MapFallback</c> 以外のすべての経路がここを通る。</b>
/// </summary>
internal static class AuthGate
{
    /// <summary>通してよければ true。false のときは応答を書き終えている。</summary>
    public static async Task<bool> AllowAsync(HttpContext ctx, RemoteAuth auth, RemoteRole required, bool write)
    {
        switch (auth.Authorize(ctx, required, write))
        {
            case RemoteAuthDecision.Allow:
                return true;

            case RemoteAuthDecision.ClientHeaderRequired:
                // **記録しない。** ここへ来るのは名乗りが通った要求だけで、
                // 足りないのはクライアントヘッダー 1 つ ── remote.auth fail は
                // 「資格を当てにきている」ものを見るための行なので、混ぜると薄まる。
                await ApiResponse.WriteErrorAsync(
                    ctx, 403, ApiResponse.HttpLayerExitCode, "client header required");
                return false;

            case RemoteAuthDecision.InsufficientRole:
                // **記録しない。** 同じく名乗りは通っている ── 足りないのは役割で、
                // これは「権限の無い操作を押した」であって侵入の試行ではない。
                await ApiResponse.WriteErrorAsync(
                    ctx, 403, ApiResponse.HttpLayerExitCode, "insufficient role");
                return false;

            default:
                auth.ReportFailure(ctx);
                await ApiResponse.WriteErrorAsync(
                    ctx, 401, ApiResponse.HttpLayerExitCode, "authentication required");
                return false;
        }
    }
}
