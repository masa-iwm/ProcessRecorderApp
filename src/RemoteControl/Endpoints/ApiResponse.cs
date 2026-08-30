using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
    public static Task WriteErrorAsync(
        HttpContext ctx, int statusCode, int exitCode, string message, string? filename = null)
        => WriteJsonAsync(
            ctx, statusCode, new ErrorDto(exitCode, message) { Filename = filename },
            RemoteApiJsonContext.Default.ErrorDto);

    /// <summary>
    /// CLI の終了コードから HTTP ステータスを導いて失敗を書く。
    /// 「エンジンがまだ使えない」（12）のときだけ <c>Retry-After</c> を付ける
    /// ── 待てば直る唯一の失敗だからである。
    /// <paramref name="filename"/> は停止が残したファイル（16 / 17 のときだけ）。
    /// <paramref name="retryAfterSeconds"/> はその秒数の上書き
    /// （UI スレッドが塞がっている 12 は待つ理由が違うので短い）。
    /// </summary>
    public static Task WriteExitCodeErrorAsync(
        HttpContext ctx, int exitCode, string message, string? filename = null,
        int? retryAfterSeconds = null)
    {
        int status = RemoteApiRules.HttpStatusFor(exitCode);
        if (status == 503)
        {
            ctx.Response.Headers.RetryAfter =
                (retryAfterSeconds ?? RemoteApiRules.RetryAfterSecondsWhenNotReady)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return WriteErrorAsync(ctx, status, exitCode, message, filename);
    }

    /// <summary>
    /// 要求本文の上限（64 KiB）。
    ///
    /// <para>
    /// <b>本文を読むより先に掛ける。</b> PATCH / PUT が受け取るのは設定のキーと値だけで、
    /// 数十 KiB を超える正当な要求は存在しない ── 上限が無いと、認証を通した相手が
    /// 無限に送り続けるだけでこのプロセスのメモリを食える。
    /// 超過したものは Kestrel が 413 で断る（こちらの本文は付かない）。
    /// </para>
    /// </summary>
    public const long MaxRequestBodyBytes = 64 * 1024;

    /// <summary>
    /// 要求本文を JSON のオブジェクトとして読む。読めなければ <see langword="null"/> を返し、
    /// 400 を書き終えている。
    ///
    /// <para>
    /// <b><c>Content-Type</c> は見ない。</b> 見て断ると、<c>curl -d</c> のような
    /// 既定で <c>form-urlencoded</c> を名乗る道具から使えなくなる ── 判定は
    /// 「JSON として読めたか」だけで足りる。
    /// </para>
    /// </summary>
    public static async Task<JsonObject?> ReadJsonObjectAsync(HttpContext ctx)
    {
        ApplyBodySizeLimit(ctx);

        try
        {
            // JsonObject の型情報で読むので、オブジェクトでない JSON（配列・数値・文字列）は
            // ここで JsonException になる ── 判定を別に書く必要は無い。
            if (await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body, RemoteApiJsonContext.Default.JsonObject, ctx.RequestAborted)
                is JsonObject body)
            {
                return body;
            }
        }
        catch (JsonException)
        {
            // 下の 400 で答える。
        }

        await WriteErrorAsync(ctx, 400, HttpLayerExitCode, "invalid json body");
        return null;
    }

    /// <summary>
    /// 要求本文をソース生成の型情報で読む。読めなければ <see langword="null"/> を返し、
    /// 400 を書き終えている（<see cref="ReadJsonObjectAsync"/> と同じ規律）。
    /// </summary>
    public static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, JsonTypeInfo<T> typeInfo) where T : class
    {
        ApplyBodySizeLimit(ctx);

        try
        {
            if (await JsonSerializer.DeserializeAsync(ctx.Request.Body, typeInfo, ctx.RequestAborted) is T value)
                return value;
        }
        catch (JsonException)
        {
            // 下の 400 で答える。
        }

        await WriteErrorAsync(ctx, 400, HttpLayerExitCode, "invalid json body");
        return null;
    }

    /// <summary>
    /// <see cref="MaxRequestBodyBytes"/> を掛ける。<b>本文を一度も読む前に呼ぶこと</b>
    /// ── 読み始めた後の変更は拒否される。上限を触れない構成（機能が無効）なら何もしない。
    /// </summary>
    private static void ApplyBodySizeLimit(HttpContext ctx)
    {
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = MaxRequestBodyBytes;
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
