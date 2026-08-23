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
public sealed record ErrorDto(int ExitCode, string Error);

/// <summary>成功だけを表す本文。</summary>
public sealed record OkDto(bool Ok);
