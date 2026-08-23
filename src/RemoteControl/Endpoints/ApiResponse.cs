using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// 応答の書き出し。<b>ソース生成の型情報を明示して渡す経路だけを用意する</b>
/// ── <c>Results.Json</c> や戻り値の推論を使うと、リフレクション前提の
/// オーバーロードに束縛されて Native AOT の警告が出る。
/// </summary>
internal static class ApiResponse
{
    /// <summary>JSON の Content-Type（本文は常に UTF-8）。</summary>
    public const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>ソース生成の型情報で JSON を書く。</summary>
    public static Task WriteJsonAsync<T>(HttpContext ctx, int statusCode, T value, JsonTypeInfo<T> typeInfo)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = JsonContentType;
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(value, typeInfo));
    }

    /// <summary>
    /// 失敗を書く。HTTP ステータスは呼び出し側が決める
    /// （終了コードからの写しは <see cref="WriteExitCodeErrorAsync"/>）。
    /// </summary>
    public static Task WriteErrorAsync(HttpContext ctx, int statusCode, int exitCode, string message)
        => WriteJsonAsync(ctx, statusCode, new ErrorDto(exitCode, message), RemoteApiJsonContext.Default.ErrorDto);

    /// <summary>
    /// CLI の終了コードから HTTP ステータスを導いて失敗を書く。
    /// 「エンジンがまだ使えない」（12）のときだけ <c>Retry-After</c> を付ける
    /// ── 待てば直る唯一の失敗だからである。
    /// </summary>
    public static Task WriteExitCodeErrorAsync(HttpContext ctx, int exitCode, string message)
    {
        int status = RemoteApiRules.HttpStatusFor(exitCode);
        if (status == 503)
        {
            ctx.Response.Headers.RetryAfter =
                RemoteApiRules.RetryAfterSecondsWhenNotReady.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return WriteErrorAsync(ctx, status, exitCode, message);
    }

    /// <summary>
    /// HTTP 層でしか起きない失敗（認証・未知の経路）が本文に載せる番号。
    /// 詳細は <see cref="ErrorDto"/> の doc を参照。
    ///
    /// <para>
    /// <b><c>public const int</c> にしてはいけない。</b> L1 の
    /// <c>DocumentationDriftTests</c> は <c>src/</c> 配下の
    /// <c>public const int *ExitCode* = N;</c> を全部集めて
    /// <c>src/README.md</c> の「終了コードの一覧」に行があることを要求する
    /// ── これは CLI の終了コードではないので、あの表には載らない
    /// （<c>RemoteApiRules</c> の同じ注意と対）。
    /// </para>
    /// </summary>
    internal const int HttpLayerExitCode = 4;
}
