using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// 認証の疎通確認。<b>v1 の恒久 API</b> ── 書き込み系と同じ規則で認証するので、
/// 「トークンは合っているのにヘッダーを忘れている」のような食い違いを、
/// 副作用のある操作を試さずに切り分けられる。
/// </summary>
internal static class PingEndpoint
{
    public static void Map(WebApplication app, RemoteAuth auth)
    {
        app.MapPost("/api/ping", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth))
                return;

            await ApiResponse.WriteJsonAsync(ctx, 200, new OkDto(true), RemoteApiJsonContext.Default.OkDto);
        });
    }
}

/// <summary>書き込み要求の門。判定と、断るときの応答・記録をまとめる。</summary>
internal static class AuthGate
{
    /// <summary>通してよければ true。false のときは応答を書き終えている。</summary>
    public static async Task<bool> AllowAsync(HttpContext ctx, RemoteAuth auth)
    {
        switch (auth.AuthorizeWrite(ctx))
        {
            case RemoteAuth.Decision.Allow:
                return true;

            case RemoteAuth.Decision.MissingClientHeader:
                // **記録しない。** ここへ来るのは資格が正しかった要求だけで、
                // 足りないのはクライアントヘッダー 1 つ ── remote.auth fail は
                // 「資格を当てにきている」ものを見るための行なので、混ぜると薄まる。
                await ApiResponse.WriteErrorAsync(
                    ctx, 403, ApiResponse.HttpLayerExitCode, "client header required");
                return false;

            default:
                auth.ReportFailure(ctx);
                await ApiResponse.WriteErrorAsync(
                    ctx, 401, ApiResponse.HttpLayerExitCode, "authentication required");
                return false;
        }
    }
}
