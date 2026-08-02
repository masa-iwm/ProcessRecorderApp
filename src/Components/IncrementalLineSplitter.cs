using System.Text;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 増分デコードされたテキストを行へ切り出す。
///
/// <para>
/// <b>用途はデバッグログファイルと元標準エラーへの複写だけ。</b>
/// 表示（端末）はデコード済みテキストをそのまま受け取るので、ここを通らない。
/// どちらの用途も既存の契約（「突然死の直前の1行」「E2E ハーネスの観測面」）を持つので、
/// <b>行の内容は <see cref="StreamReader.ReadLine"/> とバイト等価に保つ</b>。
/// </para>
/// <para>
/// 区切りは <c>\n</c> / <c>\r</c> / <c>\r\n</c>。行終端の文字は行に含めない。
/// <b><see cref="StreamReader.ReadLine"/> と違い、チャンク末尾の孤立した <c>\r</c> では
/// その場で行を確定する</b> ── あちらは次の1バイトが来るまで返さない。
/// 出るタイミングだけが違い、行の内容は同一になる（次のチャンクの先頭が
/// <c>\n</c> なら読み飛ばすので、空行が増えることもない）。
/// この改修では <c>\r</c> 進捗表示が主役になるため、待たずに出す方が
/// 「1行ごとに Flush する」というデバッグログの設計意図に合う。
/// </para>
/// <para>スレッドセーフではない。読み取りスレッド1本から使うこと。</para>
/// </summary>
public sealed class IncrementalLineSplitter
{
    private readonly StringBuilder _pending = new();

    /// <summary>直前のチャンクが孤立した <c>\r</c> で終わっていた（行は確定済み）</summary>
    private bool _sawCarriageReturn;

    /// <summary>チャンクを流し込み、確定した行ごとに <paramref name="emit"/> を呼ぶ</summary>
    public void Push(ReadOnlySpan<char> chunk, LogChunkHandler emit)
    {
        if (_sawCarriageReturn)
        {
            _sawCarriageReturn = false;
            // 直前の \r で行は確定済み。続きが \n なら \r\n の後半なので読み飛ばす
            if (!chunk.IsEmpty && chunk[0] == '\n')
            {
                chunk = chunk[1..];
            }
        }

        while (!chunk.IsEmpty)
        {
            var index = chunk.IndexOfAny('\r', '\n');
            if (index < 0)
            {
                _pending.Append(chunk);
                return;
            }

            _pending.Append(chunk[..index]);
            var terminator = chunk[index];
            chunk = chunk[(index + 1)..];
            Emit(emit);

            if (terminator != '\r')
            {
                continue;
            }
            if (chunk.IsEmpty)
            {
                // チャンクの末尾。\r\n かどうかは次のチャンクの先頭で決まる
                _sawCarriageReturn = true;
            }
            else if (chunk[0] == '\n')
            {
                chunk = chunk[1..];
            }
        }
    }

    /// <summary>ストリーム終端。未終端のまま残っている文字があれば最後の1行として吐く</summary>
    public void Eof(LogChunkHandler emit)
    {
        _sawCarriageReturn = false;
        if (_pending.Length > 0)
        {
            Emit(emit);
        }
    }

    private void Emit(LogChunkHandler emit)
    {
        // StringBuilder の内容を span で渡す（1チャンクに収まるのが通常）
        foreach (var chunk in _pending.GetChunks())
        {
            if (chunk.Length == _pending.Length)
            {
                emit(chunk.Span);
                _pending.Clear();
                return;
            }
            break;
        }

        var text = _pending.ToString();
        _pending.Clear();
        emit(text);
    }
}
