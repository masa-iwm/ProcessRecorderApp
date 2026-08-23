using System.Text.Json.Serialization;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// 失敗の本文。<see cref="ExitCode"/> は CLI の終了コードと同じ番号。
///
/// <para>
/// <b>HTTP 層でしか起きない失敗（401 / 403 / 未知経路の 404）は 4 を使う。</b>
/// CLI に認証系の終了コードは無いので、この 4 は
/// <c>RemoteApiRules.HttpStatusFor</c> を通らない<b>参考値</b>である
/// （4 は「引数が不正」＝要求そのものが受け付けられない、という同じ意味に読める）。
/// </para>
/// </summary>
public sealed record ErrorDto(int ExitCode, string Error)
{
    /// <summary>
    /// 停止が残したファイルのパス（<b>16 / 17 のときだけ載る</b>）。
    ///
    /// <para>
    /// <b>失敗の応答にファイル名を載せるのは矛盾ではない。</b> CLI の
    /// <c>stop-recording</c> が「終了コードは非 0、標準出力にはパス」を返すのと同じで、
    /// 呼び出し側はそのパスを使って後始末や救済を行える
    /// （17 は <c>mdat</c> にデータが残っている）。
    /// </para>
    /// <para>
    /// それ以外の失敗では <see langword="null"/> で、<b>キーごと応答に出さない</b>
    /// ── 常に <c>null</c> で出すと「ファイルがある失敗」と区別が付かない。
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; init; }
}

/// <summary>成功だけを表す本文。</summary>
public sealed record OkDto(bool Ok);
