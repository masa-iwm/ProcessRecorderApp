namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// この実機で何が使えるか（<c>GET /api/capabilities</c>）。
///
/// <para>
/// <b>クライアントは起動時に 1 回だけ読む。</b> 中身はプロセスの寿命のあいだ変わらない
/// ── デコーダーの有無は <c>Controller.StaticInitialize</c> で 1 回だけ確かめており、
/// 枠数は設定で変わるが、<b>変化は SSE の <c>state</c> が運ぶ</b>ので、
/// ここを引き直させる理由が無い。
/// </para>
/// </summary>
/// <param name="Transcode">
/// 録画トランスコードを提供できるか。<b>ハードウェア H.264 デコーダーのある PC でだけ true</b>
/// （同梱ランタイムにソフトウェアの H.264 デコーダーは無い）。
/// </param>
/// <param name="Decoder">
/// 使う H.264 デコーダーの要素名（<paramref name="Transcode"/> が false なら null）。
/// <b>診断のために出す</b> ── false だったときに「候補を 1 つも見つけられなかった」と
/// 「見つけたのに使えなかった」を、ログを見ずに区別できる。
/// </param>
/// <param name="AuxiliaryEncoderLimit">
/// 補助エンコーダー枠の上限（<c>RemoteAuxiliaryEncoderLimit</c>）。空き数は
/// SSE の <c>state</c>（<c>auxiliaryEncodersFree</c>）にだけ出る。
/// </param>
public sealed record CapabilitiesDto(bool Transcode, string? Decoder, int AuxiliaryEncoderLimit);
