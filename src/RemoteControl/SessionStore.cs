using System;
using System.Collections.Generic;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// 1 本のセッションが持つ中身。<b>役割まで持つ</b> ── 発行のときに決まった役割で
/// 以後の要求を判定する。設定を変えても既存のセッションが昇格しないのは
/// ホストごと作り直す（＝全セッション失効）ためである。
/// </summary>
/// <param name="ExpiresUtc">絶対期限（<see cref="SessionStore.SessionLifetime"/> 後）。延長しない。</param>
internal readonly record struct SessionEntry(string Name, RemoteRole Role, DateTimeOffset ExpiresUtc);

/// <summary>
/// ブラウザのセッション（Cookie <c>prapp_session</c> の値）を保持するメモリ内のストア。
///
/// <para>
/// <b>永続化しない。</b> プロセスが終われば全セッションが失効する ── トークンを
/// ディスクへ写す経路を作らないための決定であって、利便性の妥協ではない。
/// </para>
/// <para>
/// <b>期限は絶対で、使っても延びない</b>（<see cref="SessionLifetime"/>）。
/// 延長式にすると、開いたままのタブが 1 つあるだけでセッションが永久に生き残る。
/// </para>
/// <para>
/// 上限は <see cref="Capacity"/> 件で、超えたら<b>最も古い</b>ものから捨てる
/// （挿入順の <see cref="Queue{T}"/> と中身の <see cref="Dictionary{TKey,TValue}"/> を
/// 同じロックの下で動かす。片方だけに残ると、捨てたはずのセッションで通るか、
/// 二度と回収されない項目が増え続けるかのどちらかになる）。
/// </para>
/// <para>
/// <b>時計は <see cref="TimeProvider"/> 経由</b>で、既定は
/// <see cref="TimeProvider.System"/>。期限だけは時刻を進めて確かめられる必要がある。
/// </para>
/// </summary>
internal sealed class SessionStore(TimeProvider? timeProvider = null)
{
    /// <summary>保持するセッションの上限。</summary>
    public const int Capacity = 64;

    /// <summary>発行から失効までの絶対時間。</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, SessionEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>新しいセッションを発行して ID を返す。</summary>
    public string Issue(string name, RemoteRole role)
    {
        string id = RemoteApiRules.GenerateAccessToken();
        var entry = new SessionEntry(name, role, _time.GetUtcNow() + SessionLifetime);

        lock (_gate)
        {
            _entries[id] = entry;
            _order.Enqueue(id);

            // 追い出しは「辞書の件数」で測る。待ち行列には logout や期限切れで
            // 消えた ID が残りうるので、そちらの長さでは測れない。
            while (Capacity < _entries.Count)
                _entries.Remove(_order.Dequeue());
        }

        return id;
    }

    /// <summary>
    /// その ID が有効なセッションか。<b>期限切れは false ＋ その場で削除</b>
    /// ── 「読むたびに掃除する」以外に、開かれなくなったセッションを回収する契機が無い。
    /// </summary>
    public bool TryGet(string? id, out SessionEntry entry)
    {
        entry = default;
        if (string.IsNullOrEmpty(id))
            return false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry))
                return false;

            if (RemoteAuthRules.IsExpired(entry.ExpiresUtc, _time.GetUtcNow()))
            {
                DropLocked(id);
                entry = default;
                return false;
            }

            return true;
        }
    }

    /// <summary>そのセッションを失効させる（ログアウト）。無い ID は何もしない。</summary>
    public void Remove(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        lock (_gate)
            DropLocked(id);
    }

    /// <summary>
    /// 1 件を落とし、待ち行列を辞書に揃え直す（<see cref="_gate"/> の中で呼ぶ）。
    /// 揃え直すのは、logout と期限切れを繰り返すだけで待ち行列が伸び続けないようにするため。
    /// </summary>
    private void DropLocked(string id)
    {
        if (!_entries.Remove(id))
            return;

        var kept = new Queue<string>(_entries.Count);
        while (0 < _order.Count)
        {
            string queued = _order.Dequeue();
            if (_entries.ContainsKey(queued))
                kept.Enqueue(queued);
        }

        foreach (string queued in kept)
            _order.Enqueue(queued);
    }
}
