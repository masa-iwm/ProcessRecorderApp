using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace ProcessRecorderApp.Components;

/// <summary>補助エンコーダー枠の上限値。</summary>
public static class AuxiliaryEncoderLimits
{
    /// <summary>枠数の下限。0 にはしない ── 0 はライブ画質切替も録画トランスコードも
    /// 「常に busy」にするだけで、機能を切る手段としては <c>RemoteControlEnabled</c> がある。</summary>
    public const int MinLimit = 1;

    /// <summary>枠数の上限。1 枠につき H.264 エンコーダーが 1 本走る。</summary>
    public const int MaxLimit = 8;

    /// <summary>既定の枠数。</summary>
    public const int DefaultLimit = 2;
}

/// <summary>
/// 枠 1 つぶんの貸出。<b><see cref="Dispose"/> は冪等</b>で、2 度目以降は何もしない
/// ── 解放の経路（正常終了・切断・例外・猶予失効・停止）が複数あるので、
/// 二重解放で <see cref="AuxiliaryEncoderSlots.InUse"/> が負へ回らないことが要る。
/// </summary>
public sealed partial class AuxiliaryEncoderLease : IDisposable
{
    private AuxiliaryEncoderSlots? _slots;

    internal AuxiliaryEncoderLease(AuxiliaryEncoderSlots slots, string owner)
    {
        _slots = slots;
        Owner = owner;
    }

    /// <summary>この枠を取った者の名前（<c>dash:&lt;レコーダー名&gt;</c> / <c>transcode:&lt;session&gt;</c>。診断用）。</summary>
    public string Owner { get; }

    /// <summary>枠を返す。冪等。</summary>
    public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
}

/// <summary>
/// <b>補助エンコーダー枠</b> ── 録画そのもの以外で H.264 エンコーダーを走らせる仕事
/// （レコーダーごとのライブ DASH と、録画トランスコードのセッション）の同時本数を、
/// プロセス全体で 1 か所に絞る計数器。
///
/// <para>
/// <b>取れなければ即座に false。</b> <see cref="TryAcquire"/> は待たない
/// ── 呼ぶのは録画のストリーミングスレッドと HTTP のスレッドで、
/// どちらも「空くまで待つ」をしてよい場所ではない（呼び出し側が busy を返す）。
/// </para>
/// <para>
/// <b><see cref="Limit"/> を下げても、既に出ている貸出は続く。</b>
/// 下げた瞬間に走っているエンコーダーを止める道は無いので、
/// <see cref="Free"/> が 0 に張り付いて新規が取れなくなるだけである。
/// </para>
/// </summary>
public sealed class AuxiliaryEncoderSlots
{
    /// <summary>
    /// プロセス全体の実体。<b>ここが唯一の計数器である</b>
    /// ── 上限を 2 か所で数えると、片方だけが減り損ねたときに
    /// 「空いているのに取れない」が再現しない形で起きる。
    /// </summary>
    public static readonly AuxiliaryEncoderSlots Shared = new();

    /// <summary><see cref="_limit"/> と <see cref="_inUse"/> を対で守るロック（1 つだけ）。</summary>
    private readonly object _gate = new();

    private int _limit = AuxiliaryEncoderLimits.DefaultLimit;
    private int _inUse;

    /// <summary>
    /// 取得・解放・<see cref="Limit"/> の変更の<b>後</b>に発火する。
    /// <b>呼び手のスレッドで、ロックの外で呼ぶ</b> ── 発火元は録画の
    /// ストリーミングスレッドにも HTTP のスレッドにもなるので、
    /// 購読側はここで待たず、自分の待ち行列へ積むだけにすること。
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// 枠数の上限。範囲外は <see cref="AuxiliaryEncoderLimits.MinLimit"/>〜
    /// <see cref="AuxiliaryEncoderLimits.MaxLimit"/> へ丸める。
    /// </summary>
    public int Limit
    {
        get
        {
            lock (_gate)
                return _limit;
        }
        set
        {
            int clamped = Math.Clamp(value, AuxiliaryEncoderLimits.MinLimit, AuxiliaryEncoderLimits.MaxLimit);
            lock (_gate)
            {
                if (_limit == clamped)
                    return;
                _limit = clamped;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>いま出ている貸出の数。</summary>
    public int InUse
    {
        get
        {
            lock (_gate)
                return _inUse;
        }
    }

    /// <summary>まだ取れる数（<see cref="Limit"/> − <see cref="InUse"/>。負にはしない）。</summary>
    public int Free
    {
        get
        {
            lock (_gate)
                return Math.Max(0, _limit - _inUse);
        }
    }

    /// <summary>
    /// 枠を 1 つ取る。<b>待たない</b> ── 空きが無ければ false で、呼び出し側は
    /// busy を返すか次の機会に試し直す。
    /// </summary>
    /// <param name="owner">診断用の名前（<c>dash:&lt;レコーダー名&gt;</c> / <c>transcode:&lt;session&gt;</c>）。</param>
    /// <param name="lease">取れたときの貸出。使い終わったら <see cref="IDisposable.Dispose"/>。</param>
    public bool TryAcquire(string owner, [NotNullWhen(true)] out AuxiliaryEncoderLease? lease)
    {
        ArgumentNullException.ThrowIfNull(owner);

        lock (_gate)
        {
            if (_limit <= _inUse)
            {
                lease = null;
                return false;
            }

            _inUse++;
            lease = new AuxiliaryEncoderLease(this, owner);
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>枠を 1 つ返す（<see cref="AuxiliaryEncoderLease.Dispose"/> からだけ呼ばれる）。</summary>
    internal void Release()
    {
        lock (_gate)
        {
            if (_inUse <= 0)
                return;
            _inUse--;
        }

        Changed?.Invoke();
    }
}
