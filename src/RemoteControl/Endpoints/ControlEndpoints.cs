using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// 録画の開始・停止。
///
/// <para>
/// <b><c>{id}</c> は解決せずにそのまま渡す。</b> 「数値ならインデックス・それ以外は名前」
/// という規則の正本は CLI 側（<c>RecorderCliRules.ResolveTargetIndex</c>）にあり、
/// 実装側がそれを呼ぶ ── 読み取り側（<see cref="RecorderEndpoints"/>）が一覧から
/// 選び直しているのは、あちらが既に全件を取っているからで、
/// <b>書き込みで同じことをすると規則が 2 か所に書かれる</b>。
/// </para>
/// <para>
/// <b><c>RequestAborted</c> は backend へ渡さない。</b> 呼び出し側が切っても
/// 開始・停止は完遂させる ── 途中で畳むと「録画は始まったが誰も知らない」状態が残る。
/// 捨てられるのは応答だけである。
/// </para>
/// <para>
/// <c>start-all</c> / <c>stop-all</c> は <c>{id}</c> と衝突しない ── あちらは
/// <c>/api/recorders/start-all</c>（3 セグメント）で、個別の操作は
/// <c>/api/recorders/{id}/start</c>（4 セグメント）だからである。
/// <c>start-all</c> という名前のレコーダーも <c>/api/recorders/start-all/start</c> で指せる。
/// </para>
/// </summary>
internal static class ControlEndpoints
{
    public static void Map(WebApplication app, IRemoteControlBackend backend, RemoteAuth auth)
    {
        app.MapPost("/api/recorders/start-all", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth))
                return;

            var result = await backend.StartAllAsync();
            await ApiResponse.WriteJsonAsync(
                ctx, 200, result, RemoteApiJsonContext.Default.StartAllResult);
        });

        app.MapPost("/api/recorders/stop-all", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth))
                return;

            var result = await backend.StopAllAsync();
            await ApiResponse.WriteJsonAsync(
                ctx, 200, result, RemoteApiJsonContext.Default.StopAllResult);
        });

        app.MapPost("/api/recorders/{id}/start", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth))
                return;

            var result = await backend.StartAsync(RouteId(ctx));
            await ApiResponse.WriteJsonAsync(
                ctx, 200, result, RemoteApiJsonContext.Default.RecorderActionResult);
        });

        app.MapPost("/api/recorders/{id}/stop", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth))
                return;

            var result = await backend.StopAsync(RouteId(ctx));
            await ApiResponse.WriteJsonAsync(
                ctx, 200, result, RemoteApiJsonContext.Default.RecorderActionResult);
        });
    }

    /// <summary>経路の <c>{id}</c>（欠けることは無いが、無ければ空文字＝どれにも一致しない）。</summary>
    internal static string RouteId(HttpContext ctx)
        => ctx.Request.RouteValues["id"]?.ToString() ?? string.Empty;
}
