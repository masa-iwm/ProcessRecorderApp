using System.Collections.Generic;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// ブラウザのセッション（Cookie <c>prapp_session</c> の値）を保持するメモリ内のストア。
///
/// <para>
/// <b>永続化しない。</b> プロセスが終われば全セッションが失効する ── トークンを
/// ディスクへ写す経路を作らないための決定であって、利便性の妥協ではない。
/// </para>
/// <para>
/// 上限は <see cref="Capacity"/> 件で、超えたら<b>最も古い</b>ものから捨てる
/// （挿入順の <see cref="Queue{T}"/> と照合用の <see cref="HashSet{T}"/> を
/// 同じロックの下で動かす。片方だけに残ると、捨てたはずのセッションで通るか、
/// 二度と回収されない項目が増え続けるかのどちらかになる）。
/// </para>
/// </summary>
internal sealed class SessionStore
{
    /// <summary>保持するセッションの上限。</summary>
    public const int Capacity = 64;

    private readonly Queue<string> _order = new();
    private readonly HashSet<string> _ids = new(System.StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>新しいセッションを発行して ID を返す。</summary>
    public string Issue()
    {
        string id = RemoteApiRules.GenerateAccessToken();
        lock (_gate)
        {
            if (_ids.Add(id))
                _order.Enqueue(id);

            while (Capacity < _order.Count)
                _ids.Remove(_order.Dequeue());
        }
        return id;
    }

    /// <summary>その ID が有効なセッションか。</summary>
    public bool Contains(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return false;
        lock (_gate)
            return _ids.Contains(id);
    }
}
