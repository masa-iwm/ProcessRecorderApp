using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// ファイル名テンプレートの変数（<c>{Key}</c>）。
///
/// <para>
/// <b>読み取りは認証不要、書き込みは要る。</b> 変数はファイル名に展開されるので、
/// 書ける相手は出力先の名前を決められる（パスの区切りは展開側が弾く）。
/// </para>
/// </summary>
internal static class VariableEndpoints
{
    public static void Map(WebApplication app, IRemoteControlBackend backend, RemoteAuth auth)
    {
        app.MapGet("/api/variables", async (HttpContext ctx) =>
        {
            var variables = await backend.GetVariablesAsync();
            await ApiResponse.WriteJsonAsync(
                ctx, 200, variables, RemoteApiJsonContext.Default.VariablesDto);
        });

        app.MapPut("/api/variables/{key}", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth))
                return;

            if (await ApiResponse.ReadJsonAsync(ctx, RemoteApiJsonContext.Default.VariablePutRequest)
                is not { } request)
            {
                return;
            }

            // **どちらも指定していない要求は断る。** 「何もしない成功」を返すと、
            // 綴りを間違えたキー（value / persist 以外）が黙って通る。
            if (request.Value is null && request.Persist is null)
            {
                await ApiResponse.WriteErrorAsync(
                    ctx, 400, ApiResponse.HttpLayerExitCode, "value or persist is required");
                return;
            }

            string key = ctx.Request.RouteValues["key"]?.ToString() ?? string.Empty;

            // 順序は値 → 保存（CLI の --set → --persist と同じ）。逆にすると、
            // 新しい変数を「作って保存する」1 回の要求が 11 で落ちる。
            var result = await backend.PutVariableAsync(key, request.Value, request.Persist);

            await ApiResponse.WriteJsonAsync(
                ctx, 200, result, RemoteApiJsonContext.Default.VariableDto);
        });
    }
}
