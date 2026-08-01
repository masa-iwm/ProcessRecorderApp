using System.Collections.ObjectModel;
using System.Linq;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// トリガ一覧の編集結果への割り当て行の追随（<see cref="TriggerAssignmentReconciler"/>）。
///
/// 割り当て行には手動の追加 UI が無く、この追随だけが行を増減させる。
/// ここが壊れると「トリガを消したのに割り当てが残る」（幽霊行）か
/// 「トリガを録り直したら割り当てが消える」（設定消失）のどちらかが静かに起こる。
/// </summary>
public class TriggerAssignmentReconcilerTests
{
    [Fact]
    public void Reconcile_AddsDefaultRowsForNewTriggerIds()
    {
        var assignments = new ObservableCollection<UiaTriggerAssignment>();

        TriggerAssignmentReconciler.Reconcile(assignments, (string[])["a", "b"]);

        Assert.Equal((string[])["a", "b"], assignments.Select(x => x.TriggerId));
        Assert.All(assignments, x =>
        {
            Assert.Equal(UiaTriggerAssignment.ActionNone, x.Action);
            Assert.Equal("", x.TargetRecorder);
        });
    }

    [Fact]
    public void Reconcile_RemovesRowsWhoseTriggerNoLongerExists()
    {
        var assignments = new ObservableCollection<UiaTriggerAssignment>
        {
            new() { TriggerId = "a" },
            new() { TriggerId = "b" },
        };

        TriggerAssignmentReconciler.Reconcile(assignments, (string[])["b"]);

        var single = Assert.Single(assignments);
        Assert.Equal("b", single.TriggerId);
    }

    /// <summary>
    /// トリガの録り直し（同じ ID で再確定）や別トリガの追加・削除では、
    /// 生き残った行の設定値を保持する。ここが外れると編集のたびに割り当てが既定へ戻る。
    /// </summary>
    [Fact]
    public void Reconcile_KeepsActionAndTargetOfSurvivingRows()
    {
        var assignments = new ObservableCollection<UiaTriggerAssignment>
        {
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionStart, TargetRecorder = "Rec1" },
            new() { TriggerId = "gone", Action = UiaTriggerAssignment.ActionStop },
        };

        TriggerAssignmentReconciler.Reconcile(assignments, (string[])["a", "new"]);

        Assert.Equal((string[])["a", "new"], assignments.Select(x => x.TriggerId));
        Assert.Equal(UiaTriggerAssignment.ActionStart, assignments[0].Action);
        Assert.Equal("Rec1", assignments[0].TargetRecorder);
        Assert.Equal(UiaTriggerAssignment.ActionNone, assignments[1].Action);
    }

    /// <summary>同一 ID の多重割り当て（許容している）を Reconcile が畳んでしまわないこと。</summary>
    [Fact]
    public void Reconcile_LeavesDuplicateRowsOfAKnownTriggerAlone()
    {
        var assignments = new ObservableCollection<UiaTriggerAssignment>
        {
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionStart, TargetRecorder = "Rec1" },
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionStop, TargetRecorder = "Rec2" },
        };

        TriggerAssignmentReconciler.Reconcile(assignments, (string[])["a"]);

        Assert.Equal(2, assignments.Count);
    }
}
