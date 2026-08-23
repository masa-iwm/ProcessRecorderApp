namespace ProcessRecorderApp.Components;

/// <summary>
/// <see cref="IPreviewStreamSource.TrySubscribe"/> が返す失敗理由のうち、
/// <b>呼び出し側が意味で分岐するもの</b>。
///
/// <para>
/// <b>ここに在るのは 1 件だけ。</b> 供給側の理由は基本的に人間が読む文字列で、
/// 呼び出し側は素通しすればよい ── 例外は「対象が無い」で、これだけは
/// HTTP の 404（終了コード 13）と 503（12）を分ける判定に使われる。
/// リテラルを両側に書くと、片方の文言を直した日に<b>全部が 503 に化ける</b>
/// （黙って動き続けるので気付けない）。
/// </para>
/// <para>
/// <c>PreviewStream.cs</c> ではなくここに置いてあるのは、あちらが
/// 契約そのもの（型と寿命）を持つファイルで、文言の台帳ではないためである。
/// </para>
/// </summary>
public static class PreviewStreamReasons
{
    /// <summary>対象のレコーダーが見つからない（終了コード 13 / HTTP 404）。</summary>
    public const string RecorderNotFound = "recorder not found";
}
