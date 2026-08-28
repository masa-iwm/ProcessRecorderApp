using System;
using System.Collections.Generic;
using System.IO;

namespace ProcessRecorderApp.Components;

/// <summary>
/// <see cref="Fmp4FragmentIndex"/> の結果を、ファイルのフルパスごとに少しだけ覚えておく。
///
/// <para>
/// <b>録画中のファイルは 1 秒ごとに 1 フラグメント伸びる。</b> ブラウザはその周期で索引を
/// 引き直すので、毎回ファイル全体を辿り直すと長さの 2 乗の仕事になる ── 覚えてある
/// <see cref="Fmp4FragmentIndex.ScanResult.NextOffset"/> から読み足せば、1 回の仕事は
/// 増えたぶんだけで済む。
/// </para>
/// <para>
/// <b>長さと更新時刻の両方で見分ける。</b> どちらかが戻った（＝切り詰められた・別の実体に
/// 差し替わった）ものは覚えている続きが意味を失うので、捨てて全部を辿り直す。
/// </para>
/// <para>スレッド安全。要求は別々のスレッドから同時に来る。</para>
/// </summary>
public sealed class Fmp4FragmentIndexCache
{
    /// <summary>
    /// 覚えておくファイルの数。同時に追いかけられるのはブラウザのタブの数だけで、
    /// 溢れたものは全走査に戻るだけである（正しさは変わらない）。
    /// </summary>
    public const int MaxEntries = 8;

    private sealed record Entry(long Length, DateTime LastWriteUtc, Fmp4FragmentIndex.ScanResult Result);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>直近に使ったものが末尾。溢れたら先頭から捨てる。</summary>
    private readonly List<string> _order = [];

    /// <summary>
    /// <paramref name="path"/> の索引を返す。<paramref name="stream"/> は開いてあるファイルで、
    /// <b>位置は動かされる</b>（呼び出し側はこの後の位置に依存しないこと）。
    /// </summary>
    /// <param name="length">
    /// 呼び出し側が採った長さ。<b>覚えているものが使えるかの判定にだけ使う</b>
    /// ── 実際にどこまで辿るかは走査が <see cref="Stream.Length"/> で決める。
    /// </param>
    /// <param name="lastWriteUtc">同じ読み取りの更新時刻（同じ判定に使う）。</param>
    public Fmp4FragmentIndex.ScanResult Get(string path, Stream stream, long length, DateTime lastWriteUtc)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (_gate)
        {
            Fmp4FragmentIndex.ScanResult result;

            if (_entries.TryGetValue(path, out Entry? entry)
                && entry.Length <= length && entry.LastWriteUtc <= lastWriteUtc)
            {
                // **trex の既定フラグを持ち越す。** 差分走査は `moov` より後ろしか見ないので、
                // 渡さないと同期の判定の最後の拠り所が 0 に戻り、同じフラグメントの `Sync` が
                // 全走査と食い違う。
                result = entry.Length == length && entry.LastWriteUtc == lastWriteUtc
                    ? entry.Result
                    : Fmp4FragmentIndex.Scan(
                        stream, entry.Result.NextOffset, entry.Result.Fragments,
                        entry.Result.TrexDefaultSampleFlags);
            }
            else
            {
                result = Fmp4FragmentIndex.Scan(stream, 0, null, 0);
            }

            Store(path, new Entry(length, lastWriteUtc, result));
            return result;
        }
    }

    private void Store(string path, Entry entry)
    {
        _entries[path] = entry;
        _order.Remove(path);
        _order.Add(path);

        while (MaxEntries < _order.Count)
        {
            _entries.Remove(_order[0]);
            _order.RemoveAt(0);
        }
    }
}
