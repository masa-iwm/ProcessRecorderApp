using System.Collections.ObjectModel;
using System.Text;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.Controls;

/// <summary>
/// <see cref="LogBuffer"/> を <c>ListView</c> 用の行コレクションへ落とす。
/// <b>WebView2 が使えないときのフォールバック経路でしか動かない。</b>
///
/// <para>
/// <b>再現できないもの</b>: <c>'\r'</c> による行の部分上書き。ListView は行を書き換えられないので
/// 「最後の <c>'\r'</c> 以降」を採る（<see cref="LogText.TakeAfterLastCr"/>）。
/// 短い上書きで前の内容の末尾が残るケースは端末表示でしか正しく見えない。
/// </para>
/// <para>
/// <b><c>DispatcherCollection</c> は使わない。</b> あれは 1 行ごとに
/// <c>DispatcherQueue.EnqueueAsync</c> し、<c>async void</c> で
/// <c>Insert(e.NewStartingIndex, …)</c> を再生する ── 今回直している欠陥そのものである。
/// </para>
/// <para>UI スレッド専有。</para>
/// </summary>
internal sealed class LogListAdapter
{
    /// <summary>上限を超えたときにまとめて刈る量（1 行ずつ消すと通知が同数飛んで ListView が持たない）</summary>
    private const int TrimSlackDivisor = 10;

    private readonly StringBuilder _open = new();
    private bool _hasOpenRow;
    private long _pos;
    private long _line;
    private int _epoch;

    /// <summary>ListView にバインドする行</summary>
    public ObservableCollection<string> Items { get; } = [];

    /// <summary>全量で組み直す（フォールバックへ切り替えた直後）</summary>
    public void Reset(LogBuffer buffer)
    {
        Items.Clear();
        _open.Clear();
        _hasOpenRow = false;
        _pos = 0;
        _line = 0;
        _epoch = 0;
        Pump(buffer, int.MaxValue);
    }

    /// <summary>前回の続きから取り込む（描画タイマーの tick から呼ぶ）</summary>
    public void Pump(LogBuffer buffer, int maxChars)
    {
        var read = buffer.Read(_pos, _line, _epoch, maxChars);
        (_pos, _line, _epoch) = (read.NextPos, read.NextLine, read.Epoch);

        if (read.Cleared)
        {
            Items.Clear();
            _open.Clear();
            _hasOpenRow = false;
        }

        if (read.DroppedLines > 0)
        {
            // 書きかけの行があればそれを閉じてから、破棄の印を独立した行として出す
            if (_hasOpenRow || _open.Length > 0)
            {
                FinishRow();
            }
            Items.Add(LogBuffer.DropMarkerFormatter(read.DroppedLines));
        }

        AppendText(read.Text);
        Trim(buffer.MaxLines);
    }

    private void AppendText(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                _open.Append(text, start, text.Length - start);
                break;
            }
            _open.Append(text, start, newline - start);
            FinishRow();
            start = newline + 1;
        }

        if (_open.Length > 0)
        {
            // 未終端の行も見えている必要がある（進捗表示はこの状態のまま続く）
            SetOpenRow(Format(_open.ToString()));
        }
    }

    /// <summary>行を確定させる。<b>空行でも必ず 1 行出す</b>（改行だけの行を落とさない）</summary>
    private void FinishRow()
    {
        SetOpenRow(Format(_open.ToString()));
        _open.Clear();
        _hasOpenRow = false;
    }

    private void SetOpenRow(string row)
    {
        if (_hasOpenRow)
        {
            Items[^1] = row;
        }
        else
        {
            Items.Add(row);
            _hasOpenRow = true;
        }
    }

    /// <summary>
    /// 行末の <c>'\r'</c> と余白を落とし、空行はスペース 1 文字にする
    /// （ListView は高さ 0 の項目を潰せないため）
    /// </summary>
    private static string Format(string row)
    {
        var text = LogText.TakeAfterLastCr(row).TrimEnd();
        return text.Length == 0 ? " " : text;
    }

    private void Trim(int maxLines)
    {
        var slack = Math.Max(maxLines / TrimSlackDivisor, 1);
        if (Items.Count <= maxLines + slack)
        {
            return;
        }

        // Clear + 再投入は通知 2 回で済む。RemoveAt(0) を N 回やると N 個飛ぶ
        var kept = Items.Skip(Items.Count - maxLines).ToArray();
        Items.Clear();
        foreach (var row in kept)
        {
            Items.Add(row);
        }
    }
}
