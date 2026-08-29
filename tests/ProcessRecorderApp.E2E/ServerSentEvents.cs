using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <c>GET /api/events</c>（SSE）の読み取り。<b>空き枠（<c>auxiliaryEncodersFree</c>）が
/// 出る経路はここだけ</b>なので、補助エンコーダー枠を見るケースが共通で使う。
/// </summary>
public static class ServerSentEvents
{
    /// <summary>
    /// 次に届く <paramref name="eventName"/> の <c>data:</c> 行を返す。
    ///
    /// <para>
    /// <b>開いてすぐ読み、長く待たない。</b> 枠を握っている側を引き続けられないあいだに
    /// 貸出が切れると、見たかった状態（枠が埋まっている）が途中で消える。
    /// </para>
    /// </summary>
    public static async Task<string> ReadDataAsync(
        StreamReader reader, string eventName, TimeSpan budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var deadline = System.Diagnostics.Stopwatch.StartNew();
        string? current = null;

        while (deadline.Elapsed < budget)
        {
            var line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(budget - deadline.Elapsed, ct);
            if (line is null)
                break;

            if (line.StartsWith("event: ", StringComparison.Ordinal))
                current = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && current == eventName)
                return line["data: ".Length..];
        }

        Assert.Fail($"SSE の '{eventName}' が {budget.TotalSeconds:F0} 秒以内に届きませんでした。");
        return "";
    }
}
