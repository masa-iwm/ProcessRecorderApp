using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// 事前バッファのリングバッファ（時間・サイズの二本立てで退避する）。
///
/// <para>
/// ロジックを <c>EventRecorder</c> から切り出して総称型にしてあるのは、退避の不変条件を
/// L1 で固定するため ── 実体の要素は <c>Gst.Buffer</c>（ネイティブ）で、L1 からは触れない。
/// 解放は呼び出し側の責務（<see cref="Evict"/> / <see cref="DrainAll"/> が退避した要素を
/// 返すので、呼び出し側が <c>Dispose</c> する）。
/// </para>
/// <para>
/// スレッド契約: 書き込み（<see cref="Enqueue"/> / <see cref="Evict"/>）と列挙は
/// 録画側の <c>appsink</c> コールバック専有（<c>ConcurrentQueue</c> の列挙はその時点の
/// スナップショット）、<see cref="DrainAll"/> は <c>CloseCore</c> が
/// <b>quiesce（sink パイプラインの <c>Null</c> 化＝実行中のコールバックの復帰待ち）の後</b>に呼ぶ。
/// </para>
/// </summary>
public sealed partial class RecordingRingBuffer<T> : IEnumerable<(ulong Pts, T Item)>
{
    private readonly ConcurrentQueue<(ulong Pts, T Item, ulong Size)> _queue = new();
    private ulong _totalBytes;

    /// <summary>保持中の合計バイト数（サイズ基準の退避用）。</summary>
    public ulong TotalBytes => _totalBytes;

    public int Count => _queue.Count;

    public void Enqueue(ulong pts, T item, ulong size)
    {
        _queue.Enqueue((pts, item, size));
        _totalBytes += size;
    }

    /// <summary>
    /// 退避条件は「時間」と「サイズ」の2本立て。
    /// 時間: <paramref name="maxAgeNs"/> より古い要素を捨てる（本来の意図）。
    /// サイズ: <paramref name="maxTotalBytes"/> を超えたら時間に関係なく捨てる
    /// （PTS 巻き戻しによる符号なし減算のアンダーフローや、極端な高ビットレートで
    /// メモリを枯渇させないための二次防御）。
    ///
    /// <para>
    /// <b>直近の1件（いま Enqueue したもの）は必ず残す。</b> <c>maxAgeNs</c>=0 では
    /// 時間条件の <c>(currentPts - pts) &gt;= 0</c> が常に真になるため、このガードが無いと
    /// 最新の要素まで退避され、呼び出し側が解放した直後にそれを押し込むことになる。
    /// 症状は「録画は成功して終了コード 0 なのに、中身の無い MP4 が残る」
    /// ── 0 は UI からも設定できる正当な下限値で、「事前バッファ無しで今から録る」
    /// という意味でなければならない。
    /// </para>
    /// </summary>
    /// <returns>退避した要素（解放は呼び出し側の責務）。</returns>
    public List<T> Evict(ulong currentPts, ulong maxAgeNs, ulong maxTotalBytes)
    {
        var evicted = new List<T>();
        while (_queue.Count > 1 && _queue.TryPeek(out var b))
        {
            bool tooOld = (currentPts - b.Pts) >= maxAgeNs;
            bool tooLarge = _totalBytes > maxTotalBytes;
            if (!tooOld && !tooLarge)
                break;
            if (!_queue.TryDequeue(out b))
                break;
            _totalBytes -= Math.Min(_totalBytes, b.Size);
            evicted.Add(b.Item);
        }
        return evicted;
    }

    /// <summary>全要素を取り出して空にする（終了経路用。解放は呼び出し側の責務）。</summary>
    public List<T> DrainAll()
    {
        var drained = new List<T>();
        while (_queue.TryDequeue(out var b))
            drained.Add(b.Item);
        _totalBytes = 0;
        return drained;
    }

    public IEnumerator<(ulong Pts, T Item)> GetEnumerator()
    {
        foreach (var (pts, item, _) in _queue)
            yield return (pts, item);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
