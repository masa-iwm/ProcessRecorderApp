namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// <c>POST /api/login</c> の本文。
///
/// <para>
/// <b>キーは <c>user</c> と <c>password</c></b>（封筒の命名規則どおり camelCase）。
/// 欠けていれば空文字として扱い、<b>照合に掛かる時間は変わらない</b>
/// ── 早く断ると「その名前は無い」を答えることになる。
/// </para>
/// </summary>
public sealed record LoginRequestDto(string? User, string? Password);

/// <summary>
/// ログインの成功を表す本文。<see cref="Role"/> は
/// <c>Viewer</c> / <c>Operator</c> / <c>Admin</c> の<b>文字列</b>
/// ── 数値だと画面側が並びの意味を焼き込むことになる。
/// </summary>
public sealed record LoginResultDto(string Name, string Role);

/// <summary>
/// <c>GET /api/me</c> の本文。
/// </summary>
/// <param name="Guest">
/// <b>ゲスト読み取りで通っている（名乗っていない）</b>とき true。そのとき
/// <c>name</c> は空文字・<c>role</c> は <c>Viewer</c> になる ── 画面はこの値だけを見て
/// 「ログイン」ボタンを出すか「ログアウト」を出すかを決められる。
/// </param>
public sealed record MeDto(string Name, string Role, bool Guest);
