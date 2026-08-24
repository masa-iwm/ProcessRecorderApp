using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// ソース候補の一覧と、テンプレートからの適用。
///
/// <para>
/// <b><c>SrcPipeline</c> をリモートから書ける唯一の口がここである。</b> 文字列そのものの
/// <c>PATCH</c> は <see cref="RemoteApiRules.RemoteDeniedRecorderSettings"/> が拒み続ける
/// ── あれは<b>アプリが実行する内容そのもの</b>だからで、その判断は変えていない。
/// ここが通すのは「カタログに在る要素・在るプロパティ・在る caps」から組み立てた文字列だけで、
/// 検証は <see cref="SourcePresetRules"/>（純関数、L1 が固定）。
/// </para>
/// <para>
/// 一覧は読み取りなので <see cref="RemoteRole.Viewer"/>、適用は設定の変更なので
/// <see cref="RemoteRole.Admin"/>（<c>PATCH …/settings</c> と同じ）。
/// </para>
/// </summary>
internal static class SourceEndpoints
{
    public static void Map(WebApplication app, IRemoteControlBackend backend, RemoteAuth auth)
    {
        app.MapGet("/api/sources", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            var sources = await backend.GetSourcesAsync(ctx.RequestAborted);
            await ApiResponse.WriteJsonAsync(
                ctx, 200, sources, RemoteApiJsonContext.Default.SourcesDto);
        });

        app.MapPut("/api/recorders/{id}/source", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Admin, write: true))
                return;

            if (await ApiResponse.ReadJsonAsync(ctx, RemoteApiJsonContext.Default.SourcePresetDto)
                is not { } preset)
            {
                return;
            }

            var result = await backend.ApplySourceAsync(ControlEndpoints.RouteId(ctx), preset);
            await ApiResponse.WriteJsonAsync(
                ctx, 200, result, RemoteApiJsonContext.Default.SourceApplyResultDto);
        });
    }
}
