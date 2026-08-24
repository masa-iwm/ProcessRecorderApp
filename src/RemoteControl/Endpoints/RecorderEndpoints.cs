using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>レコーダーの状態を読む経路。</summary>
internal static class RecorderEndpoints
{
    public static void Map(WebApplication app, IRemoteControlBackend backend, RemoteAuth auth)
    {
        app.MapGet("/api/recorders", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            var snapshot = await backend.GetRecordersAsync(ctx.RequestAborted);
            await ApiResponse.WriteJsonAsync(
                ctx, 200, snapshot, RemoteApiJsonContext.Default.RecordersSnapshot);
        });

        app.MapGet("/api/recorders/{id}", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            string id = ctx.Request.RouteValues["id"]?.ToString() ?? string.Empty;
            var snapshot = await backend.GetRecordersAsync(ctx.RequestAborted);

            var recorder = Resolve(snapshot, id);
            if (recorder is null)
            {
                // 13 = 対象のレコーダーが見つからない（CLI と同じ番号）。
                await ApiResponse.WriteExitCodeErrorAsync(ctx, 13, "recorder not found");
                return;
            }

            await ApiResponse.WriteJsonAsync(
                ctx, 200, recorder, RemoteApiJsonContext.Default.RecorderStatusDto);
        });
    }

    /// <summary>
    /// <c>{id}</c> の解決。<b>数値はインデックス（0 始まり）で、名前へはフォールバックしない。</b>
    /// それ以外は序数での完全一致・先勝ち。
    ///
    /// <para>
    /// <b>CLI の対象解決（<c>RecorderCliRules.ResolveTargetIndex</c>。L1 の
    /// <c>RecorderCliRulesTests</c> が固定している）と同じ規則である。</b>
    /// あちらは別プロジェクトにあってここからは参照できないが、
    /// <b>規則を書き写すのではなく、同じ並びの一覧に対して同じ選び方をする</b>形にしてある
    /// ── 順序は <see cref="RecordersSnapshot.Recorders"/> が持っており、
    /// ここが独自に並べ替えることはない。
    /// </para>
    /// </summary>
    private static RecorderStatusDto? Resolve(RecordersSnapshot snapshot, string id)
    {
        if (int.TryParse(id, out int index))
            return 0 <= index && index < snapshot.Recorders.Count ? snapshot.Recorders[index] : null;

        foreach (var recorder in snapshot.Recorders)
        {
            if (string.Equals(recorder.Name, id, StringComparison.Ordinal))
                return recorder;
        }
        return null;
    }
}
