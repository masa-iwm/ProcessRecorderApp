using System;
using System.Collections.Generic;
using System.Linq;

namespace ProcessRecorderApp.Components;

/// <summary>
/// トリガ一覧の編集結果に、割り当て行（<see cref="UiaTriggerAssignment"/>）を追随させる。
///
/// 行の正本はトリガ定義の側にある ── 割り当て行を手で増減させる UI は無く、
/// トリガの追加・削除のたびにここが差分を適用する。既存行の設定値
/// （Action / TargetRecorder）は保持する（トリガを録り直しても割り当てが消えないため）。
/// </summary>
public static class TriggerAssignmentReconciler
{
    /// <summary>
    /// <paramref name="assignments"/> を <paramref name="triggerIds"/> に合わせる。
    /// 消えた ID の行は削除し、新しい ID には既定行（Action=なし・対象=全レコーダー）を
    /// 末尾へ追加する。同一 ID の複数行（多重割り当て）はそのまま残す。
    /// ObservableCollection を渡せば Add/Remove がそのまま変更通知になる。
    /// </summary>
    public static void Reconcile(IList<UiaTriggerAssignment> assignments, IReadOnlyList<string> triggerIds)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(triggerIds);

        var known = new HashSet<string>(triggerIds, StringComparer.Ordinal);

        // 後ろから消す。前から消すと残りの index がずれる
        for (int i = assignments.Count - 1; i >= 0; i--)
        {
            if (!known.Contains(assignments[i].TriggerId))
                assignments.RemoveAt(i);
        }

        var existing = new HashSet<string>(assignments.Select(a => a.TriggerId), StringComparer.Ordinal);
        foreach (var id in triggerIds)
        {
            if (existing.Add(id))
                assignments.Add(new UiaTriggerAssignment { TriggerId = id });
        }
    }
}
