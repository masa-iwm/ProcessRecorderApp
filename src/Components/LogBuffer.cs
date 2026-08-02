using System.Text;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 読み取りスレッドから同期に呼ばれる出力の受け口。
/// <b>ブロックしてはならず、<paramref name="chunk"/> を保持してもいけない</b>
/// （呼び出しから戻ると内容は無効になる）
/// </summary>
public delegate void LogChunkHandler(ReadOnlySpan<char> chunk);

/// <summary>
/// <see cref="LogBuffer.Read"/> の結果。
/// <paramref name="NextPos"/> / <paramref name="NextLine"/> / <paramref name="Epoch"/> を
/// そのまま次回の引数へ渡すことで、取りこぼしなく読み進められる
/// </summary>
/// <param name="Text">読み出したテキスト（ANSI エスケープ・CR を含む生のまま）</param>
/// <param name="NextPos">次回の開始位置（絶対文字位置）</param>
/// <param name="NextLine">次回の開始行（絶対行番号）</param>
/// <param name="DroppedLines">
/// <b>一度も読み出されないまま上限で捨てられた行数。</b>
/// 読み出し済みの行が押し出されても 0 のままである（それは欠落ではない）
/// </param>
/// <param name="Cleared">この読み出しの前に <see cref="LogBuffer.Clear"/> があった</param>
/// <param name="Epoch">次回に渡す世代番号</param>
public readonly record struct LogRead(
    string Text, long NextPos, long NextLine, long DroppedLines, bool Cleared, int Epoch);

/// <summary>
/// アプリ内 Log 画面が表示するテキストの<b>唯一の保管場所</b>。上限付きのリングで、
/// 超えた分は古い側から捨てる。UI に依存しないので L1 で全面的に検証できる。
///
/// <para>
/// <b>保持単位は「<c>'\n'</c> で終わるセグメント」＋「まだ改行が来ていないテール」。</b>
/// チャンク境界で捨てると ANSI エスケープや CRLF の途中で切れて表示が壊れ、
/// 逆に「行」へ正規化してしまうと <c>'\r'</c> による行上書き（進捗表示）が死ぬ。
/// <c>'\n'</c> は端末の行を進める唯一の文字なので、セグメントを捨てることが
/// スクロールバックの行を捨てることに厳密に一致する。
/// </para>
/// <para>
/// 位置は<b>絶対値</b>（追記した総文字数・総行数）で持つ。読み手はカーソルだけを覚えればよく、
/// 「読み出す前に押し出された」ことを行番号の差だけで正確に検出できる。
/// </para>
/// <para>
/// <see cref="Append"/> は読み取りスレッドから、それ以外は UI スレッドから呼ばれる。
/// すべてをロックで直列化する。
/// </para>
/// </summary>
public sealed class LogBuffer
{
    /// <summary>保持する行数の既定値</summary>
    public const int DefaultMaxLines = 10_000;

    /// <summary>行数上限として受け付ける下限</summary>
    public const int MinMaxLines = 100;

    /// <summary>行数上限として受け付ける上限</summary>
    public const int MaxMaxLines = 1_000_000;

    /// <summary>
    /// 保持する総文字数の上限。行数だけでは有界にならない
    /// （<c>'\n'</c> を一切吐かない生産者ではテールが無限に伸びる）ため必ず併用する
    /// </summary>
    public const int MaxChars = 8 * 1024 * 1024;

    /// <summary>1 セグメントの最大長。超えたら改行を待たずに確定して次のテールを開く</summary>
    public const int MaxSegmentChars = 64 * 1024;

    /// <summary>プロセス全体で共有する実体（標準出力の捕捉先はプロセスに1つ）</summary>
    public static LogBuffer Shared { get; } = new();

    /// <summary>
    /// 破棄マーカーの文言。既定は不変英語（L1 を決定的に保つため）で、
    /// アプリ層が起動時にローカライズ済みの書式へ差し替える
    /// </summary>
    public static Func<long, string> DropMarkerFormatter { get; set; }
        = dropped => $"-- {dropped} older lines were dropped --";

    private readonly Lock _gate = new();

    private readonly List<Segment> _segments = [];

    /// <summary><see cref="_segments"/> の生きている先頭。捨てた分は都度詰めない</summary>
    private int _head;

    private readonly StringBuilder _open = new();
    private long _openStart;

    /// <summary>これまでに追記した総文字数。<b>単調増加し <see cref="Clear"/> でも戻らない</b></summary>
    private long _writePos;

    /// <summary>確定した総行数（＝テールの絶対行番号）</summary>
    private long _nextLine;

    /// <summary>保持している窓の先頭（絶対文字位置）</summary>
    private long _headPos;

    /// <summary>保持している窓の先頭（絶対行番号）</summary>
    private long _headLine;

    private int _epoch;
    private int _maxLines = DefaultMaxLines;

    private readonly record struct Segment(string Text, long Start, long Line);

    /// <summary>
    /// 保持する行数の上限。<see cref="MinMaxLines"/>〜<see cref="MaxMaxLines"/> へ丸める。
    /// 縮めるとその場で間引く
    /// </summary>
    public int MaxLines
    {
        get { lock (_gate) { return _maxLines; } }
        set
        {
            var clamped = Math.Clamp(value, MinMaxLines, MaxMaxLines);
            lock (_gate)
            {
                if (_maxLines == clamped)
                {
                    return;
                }
                _maxLines = clamped;
                Trim();
            }
        }
    }

    /// <summary>これまでに捨てた行数の累計（診断用）</summary>
    public long DroppedLineCount
    {
        get { lock (_gate) { return _headLine; } }
    }

    /// <summary>
    /// 保持している窓の先頭を指すカーソル。ここから読み直せば
    /// <b>取りこぼしの申告を出さずに</b>全量を流し直せる
    /// （表示を再開したとき・ブラウザーを作り直したときに使う）
    /// </summary>
    public (long Position, long Line, int Epoch) Head
    {
        get { lock (_gate) { return (_headPos, _headLine, _epoch); } }
    }

    /// <summary>追記があったことだけを知らせる。<b>読み取りスレッドから発火する</b></summary>
    public event Action? Appended;

    /// <summary>読み取りスレッドから呼ぶ。追記し、上限を超えた分を古い側から捨てる</summary>
    public void Append(ReadOnlySpan<char> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            while (!chunk.IsEmpty)
            {
                var newline = chunk.IndexOf('\n');
                if (newline < 0)
                {
                    _open.Append(chunk);
                    _writePos += chunk.Length;
                    break;
                }
                _open.Append(chunk[..(newline + 1)]);
                _writePos += newline + 1;
                CloseSegment();
                chunk = chunk[(newline + 1)..];
            }

            // 改行が一度も来ないまま伸び続ける生産者への保険。ここだけは改行境界で切れない
            while (_open.Length >= MaxSegmentChars)
            {
                CloseSegment(MaxSegmentChars);
            }

            Trim();
        }

        Appended?.Invoke();
    }

    /// <summary>
    /// <paramref name="fromPos"/> 以降を最大 <paramref name="maxChars"/> 文字ぶん読み出す。
    /// すでに捨てられた領域を指していれば保持中の先頭から返し、
    /// 捨てられた行数を <see cref="LogRead.DroppedLines"/> で知らせる
    /// </summary>
    public LogRead Read(long fromPos, long fromLine, int fromEpoch, int maxChars)
    {
        lock (_gate)
        {
            var cleared = fromEpoch != _epoch;
            long dropped = 0;
            var start = fromPos;
            var line = fromLine;

            if (cleared || start < _headPos)
            {
                // Clear は利用者が明示的に消したものなので「取りこぼし」として数えない
                if (!cleared && fromLine < _headLine)
                {
                    dropped = _headLine - fromLine;
                }
                start = _headPos;
                line = _headLine;
            }

            if (start >= _writePos)
            {
                return new LogRead(string.Empty, _writePos, _nextLine, dropped, cleared, _epoch);
            }

            var budget = Math.Max(maxChars, 1);
            var builder = new StringBuilder(Math.Min(budget, (int)Math.Min(_writePos - start, budget)));
            var pos = start;
            var index = FindSegmentIndex(pos);

            while (builder.Length < budget && index < _segments.Count)
            {
                var segment = _segments[index];
                var offset = (int)(pos - segment.Start);
                var available = segment.Text.Length - offset;
                var take = Math.Min(available, budget - builder.Length);
                builder.Append(segment.Text, offset, take);
                pos += take;
                if (take == available)
                {
                    line = segment.Line + 1; // このセグメントを '\n' まで読み切った
                    index++;
                }
            }

            if (builder.Length < budget && pos >= _openStart && _open.Length > 0)
            {
                var offset = (int)(pos - _openStart);
                var take = Math.Min(_open.Length - offset, budget - builder.Length);
                if (take > 0)
                {
                    builder.Append(_open, offset, take);
                    pos += take;
                }
            }

            return new LogRead(builder.ToString(), pos, line, dropped, cleared, _epoch);
        }
    }

    /// <summary>保持中の全文（フォールバックの組み直しと「すべてコピー」の正本）</summary>
    public string Snapshot()
    {
        lock (_gate)
        {
            var builder = new StringBuilder((int)Math.Min(_writePos - _headPos, int.MaxValue));
            for (var i = _head; i < _segments.Count; i++)
            {
                builder.Append(_segments[i].Text);
            }
            builder.Append(_open);
            return builder.ToString();
        }
    }

    /// <summary>
    /// 内容を捨てる。<b>絶対位置は戻さず世代番号を進める</b>
    /// ── 戻すと読み手のカーソルと逆転する。
    /// 次の <see cref="Read"/> は <see cref="LogRead.Cleared"/> を立てて先頭から返す
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _segments.Clear();
            _head = 0;
            _open.Clear();
            _headPos = _writePos;
            _headLine = _nextLine;
            _openStart = _writePos;
            _epoch++;
        }
    }

    /// <summary>テスト用。初期状態へ戻す（<see cref="Shared"/> をテスト間で使い回すため）</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            _segments.Clear();
            _head = 0;
            _open.Clear();
            _writePos = 0;
            _nextLine = 0;
            _headPos = 0;
            _headLine = 0;
            _openStart = 0;
            _epoch = 0;
            _maxLines = DefaultMaxLines;
        }
    }

    /// <summary>テールを確定してセグメントにする（<paramref name="length"/> 未指定なら全部）</summary>
    private void CloseSegment(int length = -1)
    {
        if (_open.Length == 0)
        {
            return;
        }
        var take = length < 0 ? _open.Length : Math.Min(length, _open.Length);
        var text = _open.ToString(0, take);
        _open.Remove(0, take);
        _segments.Add(new Segment(text, _openStart, _nextLine));
        _openStart += take;
        _nextLine++;
    }

    private void Trim()
    {
        while (_head < _segments.Count &&
               (_segments.Count - _head > _maxLines || _writePos - _headPos > MaxChars))
        {
            var segment = _segments[_head];
            _segments[_head] = default;
            _head++;
            _headPos = segment.Start + segment.Text.Length;
            _headLine = segment.Line + 1;
        }

        if (_head > 1024 && _head * 2 > _segments.Count)
        {
            _segments.RemoveRange(0, _head);
            _head = 0;
        }
    }

    /// <summary><paramref name="pos"/> を含むセグメントの添字（無ければテール側＝<c>_segments.Count</c>）</summary>
    private int FindSegmentIndex(long pos)
    {
        var low = _head;
        var high = _segments.Count - 1;
        var found = _segments.Count;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var segment = _segments[mid];
            if (pos < segment.Start)
            {
                high = mid - 1;
            }
            else if (pos >= segment.Start + segment.Text.Length)
            {
                low = mid + 1;
            }
            else
            {
                found = mid;
                break;
            }
        }
        return found;
    }
}
