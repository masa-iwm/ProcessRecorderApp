using System;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// 同一内容が連続するバスメッセージを畳んで、<c>activity.log</c> が洪水で潰れるのを防ぐ。
///
/// <para>
/// <b>これは飾りではない。</b> GPU 実機の <c>nvh264enc</c> で観測した障害では、
/// <c>h264parse</c> が<b>捨てた NAL 1個ごとに</b>
/// <c>broken/invalid nal ... will be dropped</c> を出していた。15fps でも毎秒数十行、
/// 高フレームレートなら数百行になる。素で書き出すと 1MB のローテーションを数秒で食い潰し、
/// **原因を突き止めるためのログが、その原因自身によって流し去られる**。
/// </para>
///
/// <para>
/// 畳み方は「直前と同じ (要素名, メッセージ) なら出さずに数える」。異なる内容が来た時点で
/// 溜まっていた件数を <c>repeated=N</c> として吐き出す。連続していない再発は別扱いにする
/// ── 「1回だけ出た」と「毎フレーム出続けている」は診断上まったく意味が違うため。
/// </para>
///
/// <para>
/// 純粋なロジックなので GStreamer に依存せず、L1 から直接検証できる。
/// </para>
/// </summary>
internal sealed class BusMessageThrottle
{
    /// <summary>
    /// 同一内容を連続で抑制し続ける上限。これを超えたら1行出して数え直す
    /// （延々と沈黙し続けると「まだ続いているのか、止まったのか」が分からなくなるため）。
    /// </summary>
    public const int MaxSuppressedInARow = 200;

    private string? _lastKey;
    private int _suppressed;

    /// <summary>
    /// 内部状態の直列化。<see cref="Observe"/> は pull スレッドから、<see cref="Flush"/> は
    /// <c>StartCore</c>（UI/CLI スレッド・<c>_stateLock</c> 下）と停止タスク（プールスレッド）
    /// からも呼ばれる ── 無同期だと <c>repeated=N</c> の件数が失われたり二重計上されたりして、
    /// 洪水を畳んでも件数は失わないというこの仕組みの存在意義（診断の正確さ）を静かに損なう。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>抑制中の件数（テスト・診断用）。</summary>
    public int SuppressedCount { get { lock (_gate) return _suppressed; } }

    /// <summary>
    /// メッセージを1件与え、実際に出力すべき行（の付加情報）を返す。
    /// </summary>
    /// <returns>
    /// <c>Emit</c> が false なら出力しない。true の場合、<c>RepeatedBefore</c> は
    /// 「この行の**前に**抑制されていた同一内容の件数」で、0 でなければ
    /// <c>repeated=N</c> として添える。
    /// </returns>
    public (bool Emit, int RepeatedBefore) Observe(string key)
    {
        lock (_gate)
        {
            if (_lastKey == key)
            {
                _suppressed++;
                if (_suppressed < MaxSuppressedInARow)
                    return (false, 0);

                // 上限に達したので1行出して数え直す（沈黙し続けない）
                int repeated = _suppressed;
                _suppressed = 0;
                return (true, repeated);
            }

            int pending = _suppressed;
            _lastKey = key;
            _suppressed = 0;
            return (true, pending);
        }
    }

    /// <summary>
    /// 抑制されたまま残っている件数を取り出して状態を消す
    /// （録画終了時・破棄時に「最後の N 件」を取りこぼさないため）。
    /// </summary>
    public int Flush()
    {
        lock (_gate)
        {
            int pending = _suppressed;
            _suppressed = 0;
            _lastKey = null;
            return pending;
        }
    }
}
