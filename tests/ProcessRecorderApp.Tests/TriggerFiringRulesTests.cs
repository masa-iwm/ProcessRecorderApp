using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// UIA トリガ発火 → テンプレート変数／録画アクションの写像（<see cref="TriggerFiringRules"/>）。
///
/// 実際の発火は UiaTrigger 側（監視ワーカースレッド）で起こり、このアプリの E2E では
/// 相手アプリが要るため自動化していない。だから「発火 1 回が何を書き、何を実行するか」の
/// 規則だけを純粋関数へ切り出し、ここで守る。規則を UiaTriggerService（WinUI アプリの
/// プロジェクト）に書くと L1 から参照できない。
/// </summary>
public class TriggerFiringRulesTests
{
    [Fact]
    public void BuildVariables_WritesNewValueUnderTheTriggerId()
    {
        var variables = TriggerFiringRules.BuildVariables("Login", "ready", (TriggerClauseValue[])[]);

        var single = Assert.Single(variables);
        Assert.Equal("Login", single.Key);
        Assert.Equal("ready", single.Value);
    }

    /// <summary>
    /// 句が 1 つのトリガは NewValue がその句の値そのものなので、
    /// <c>&lt;ID&gt;.&lt;句名&gt;</c> を重複して書かない。
    /// </summary>
    [Fact]
    public void BuildVariables_DoesNotDuplicateTheOnlyClauseOfASimpleTrigger()
    {
        var variables = TriggerFiringRules.BuildVariables("Login", "ready",
            (TriggerClauseValue[])[new("Value", "ready", TriggerClauseOutcome.Matched)]);

        var single = Assert.Single(variables);
        Assert.Equal("Login", single.Key);
    }

    [Fact]
    public void BuildVariables_WritesDotSeparatedClauseVariablesForCompositeTriggers()
    {
        var variables = TriggerFiringRules.BuildVariables("composite", "True",
        (TriggerClauseValue[])
        [
            new("login", "ready", TriggerClauseOutcome.Matched),
            new("busy", "False", TriggerClauseOutcome.NotMatched),
        ]);

        Assert.Equal(3, variables.Count);
        Assert.Contains(new("composite", "True"), variables);
        Assert.Contains(new("composite.login", "ready"), variables);
        Assert.Contains(new("composite.busy", "False"), variables);
    }

    /// <summary>
    /// Unreadable は「読めなかった」、NotEvaluated は「短絡で一度も見ていない」。
    /// どちらも値を持たないので、書き込むと発火前の有効な値を空文字で潰してしまう。
    /// </summary>
    [Fact]
    public void BuildVariables_SkipsClausesThatWereUnreadableOrNotEvaluated()
    {
        var variables = TriggerFiringRules.BuildVariables("composite", "True",
        (TriggerClauseValue[])
        [
            new("login", "ready", TriggerClauseOutcome.Matched),
            new("gone", "", TriggerClauseOutcome.Unreadable),
            new("skipped", "", TriggerClauseOutcome.NotEvaluated),
        ]);

        Assert.Equal(2, variables.Count);
        Assert.DoesNotContain(variables, v => v.Key == "composite.gone");
        Assert.DoesNotContain(variables, v => v.Key == "composite.skipped");
    }

    /// <summary>
    /// .NET の <c>\w</c> は Unicode の単語文字なので日本語のトリガ ID はそのまま
    /// <c>{キー}</c> でファイル名テンプレートから参照できる。ドットは複合トリガの句
    /// （<c>&lt;ID&gt;.&lt;句名&gt;</c>）、ハイフンは UiaTrigger のトリガ ID に自然に入るため
    /// 受ける。この集合が変わると「これらの ID に警告を出さない」という
    /// UiaTriggerService の挙動が誤りになるため、ここで固定する。
    /// </summary>
    [Fact]
    public void IsTemplateReferencable_AcceptsWordCharactersDotsHyphensAndJapanese()
    {
        Assert.True(TriggerFiringRules.IsTemplateReferencable("Login"));
        Assert.True(TriggerFiringRules.IsTemplateReferencable("composite.login"));
        Assert.True(TriggerFiringRules.IsTemplateReferencable("ログイン完了"));
        Assert.True(TriggerFiringRules.IsTemplateReferencable("trigger_1"));
        Assert.True(TriggerFiringRules.IsTemplateReferencable("my-trigger"));
    }

    [Fact]
    public void IsTemplateReferencable_RejectsSpacesAndBraces()
    {
        Assert.False(TriggerFiringRules.IsTemplateReferencable("my trigger"));
        Assert.False(TriggerFiringRules.IsTemplateReferencable("{trigger}"));
        Assert.False(TriggerFiringRules.IsTemplateReferencable(""));
    }

    /// <summary>
    /// 保存値は設定 JSON に生で入るため、手編集や将来の値で未知の文字列になり得る。
    /// そのとき録画を動かす側（Start/Stop/While）へ倒すと誤動作なので None へ倒す。
    /// </summary>
    [Fact]
    public void ParseAction_MapsUnknownStringsToNone()
    {
        Assert.Equal(TriggerAssignmentAction.None, TriggerFiringRules.ParseAction(""));
        Assert.Equal(TriggerAssignmentAction.None, TriggerFiringRules.ParseAction(null));
        Assert.Equal(TriggerAssignmentAction.None, TriggerFiringRules.ParseAction("start"));
        Assert.Equal(TriggerAssignmentAction.None, TriggerFiringRules.ParseAction("Restart"));
        Assert.Equal(TriggerAssignmentAction.Start, TriggerFiringRules.ParseAction("Start"));
        Assert.Equal(TriggerAssignmentAction.Stop, TriggerFiringRules.ParseAction("Stop"));
        Assert.Equal(TriggerAssignmentAction.While, TriggerFiringRules.ParseAction("While"));
    }

    [Fact]
    public void ResolveActions_ReturnsOnlyAssignmentsOfTheFiredTrigger()
    {
        UiaTriggerAssignment[] assignments =
        [
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionStart },
            new() { TriggerId = "b", Action = UiaTriggerAssignment.ActionStop },
        ];

        var requests = TriggerFiringRules.ResolveActions("a", TriggerFireEdge.Rising, assignments);

        var single = Assert.Single(requests);
        Assert.Equal(TriggerActionKind.Start, single.Kind);
    }

    /// <summary>変数は常に反映される設計なので、「なし」の行はアクション列に現れない。</summary>
    [Fact]
    public void ResolveActions_TreatsNoneAssignmentsAsNoRequest()
    {
        UiaTriggerAssignment[] assignments =
        [
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionNone },
        ];

        Assert.Empty(TriggerFiringRules.ResolveActions("a", TriggerFireEdge.Rising, assignments));
        Assert.Empty(TriggerFiringRules.ResolveActions("a", TriggerFireEdge.Falling, assignments));
    }

    /// <summary>
    /// 同じトリガで「レコーダー A を開始・レコーダー B を停止」のような多重割り当てを許す。
    /// 実行順は割り当ての並び順（設定に見えている順）を保存する。
    /// </summary>
    [Fact]
    public void ResolveActions_AllowsMultipleAssignmentsForOneTrigger_PreservingOrder()
    {
        UiaTriggerAssignment[] assignments =
        [
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionStop, TargetRecorder = "Rec2" },
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionStart, TargetRecorder = "Rec1" },
        ];

        var requests = TriggerFiringRules.ResolveActions("a", TriggerFireEdge.Rising, assignments);

        Assert.Equal(2, requests.Count);
        Assert.Equal(new(TriggerActionKind.Stop, "Rec2", false), requests[0]);
        Assert.Equal(new(TriggerActionKind.Start, "Rec1", false), requests[1]);
    }

    // ---- エッジ（「停止時も通知」を入れたトリガは立ち下がりでも発火する） ----

    /// <summary>
    /// 「停止時も通知」を入れたトリガは条件が成立しなくなったときにも発火する。
    /// エッジを見ないと<b>要素が消えた瞬間にも録画が始まる</b> ── ライブラリが
    /// 立ち下がりの通知に対応したことで初めて起きる退行なので、ここで固定する。
    /// </summary>
    [Fact]
    public void ResolveActions_IgnoresStartAndStopAssignmentsOnTheFallingEdge()
    {
        UiaTriggerAssignment[] assignments =
        [
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionStart },
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionStop },
        ];

        Assert.Empty(TriggerFiringRules.ResolveActions("a", TriggerFireEdge.Falling, assignments));
        Assert.Equal(2, TriggerFiringRules.ResolveActions("a", TriggerFireEdge.Rising, assignments).Count);
    }

    /// <summary>
    /// 「条件成立中のみ録画」は、1 つの割り当てがエッジによって別の操作になる唯一の行である。
    /// </summary>
    [Fact]
    public void ResolveActions_StartsOnRisingAndStopsOnFallingForWhileAssignments()
    {
        UiaTriggerAssignment[] assignments =
        [
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionWhile, TargetRecorder = "Rec1" },
        ];

        var rising = Assert.Single(TriggerFiringRules.ResolveActions("a", TriggerFireEdge.Rising, assignments));
        Assert.Equal(new(TriggerActionKind.Start, "Rec1", true), rising);

        var falling = Assert.Single(TriggerFiringRules.ResolveActions("a", TriggerFireEdge.Falling, assignments));
        Assert.Equal(new(TriggerActionKind.Stop, "Rec1", true), falling);
    }

    /// <summary>
    /// 監視が条件を追えなくなったときに自動停止してよいのは「条件成立中のみ録画」だけ。
    /// この印が Start/Stop にも立つと、手動と同じ重みの割り当てまで巻き込んで止めてしまう。
    /// </summary>
    [Fact]
    public void ResolveActions_MarksOnlyWhileRequestsAsTrackingTheCondition()
    {
        UiaTriggerAssignment[] assignments =
        [
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionStart },
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionWhile },
        ];

        var requests = TriggerFiringRules.ResolveActions("a", TriggerFireEdge.Rising, assignments);

        Assert.Equal(2, requests.Count);
        Assert.False(requests[0].TracksCondition);
        Assert.True(requests[1].TracksCondition);
    }

    /// <summary>
    /// 同一トリガに素の割り当てと「条件成立中のみ」が混在しても、立ち下がりでは
    /// While の行だけが残る（並び順も保存する）。
    /// </summary>
    [Fact]
    public void ResolveActions_KeepsWhileAndPlainAssignmentsOfTheSameTriggerInOrder()
    {
        UiaTriggerAssignment[] assignments =
        [
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionWhile, TargetRecorder = "Rec1" },
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionStart, TargetRecorder = "Rec2" },
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionWhile, TargetRecorder = "Rec3" },
        ];

        var falling = TriggerFiringRules.ResolveActions("a", TriggerFireEdge.Falling, assignments);

        Assert.Equal(2, falling.Count);
        Assert.Equal(new(TriggerActionKind.Stop, "Rec1", true), falling[0]);
        Assert.Equal(new(TriggerActionKind.Stop, "Rec3", true), falling[1]);
    }

    // ---- 不成立化の通知を必要とするトリガの抽出 ----

    /// <summary>
    /// 警告と自動停止の判定はこの集合が正本。多重割り当てで二重に警告しないよう重複を排除し、
    /// 順序は設定に見えている順を保つ。
    /// </summary>
    [Fact]
    public void WhileAssignedTriggerIds_ListsEachTriggerOnceInAppearanceOrder()
    {
        UiaTriggerAssignment[] assignments =
        [
            new() { TriggerId = "b", Action = UiaTriggerAssignment.ActionWhile, TargetRecorder = "Rec1" },
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionWhile },
            new() { TriggerId = "b", Action = UiaTriggerAssignment.ActionWhile, TargetRecorder = "Rec2" },
        ];

        Assert.Equal((string[])["b", "a"], TriggerFiringRules.WhileAssignedTriggerIds(assignments));
    }

    /// <summary>
    /// Start/Stop の割り当ては不成立化の通知を必要としない。ここを緩めると
    /// 「通知できない」警告が全トリガに出て、意味を失う。
    /// </summary>
    [Fact]
    public void WhileAssignedTriggerIds_IgnoresAssignmentsThatAreNotWhile()
    {
        UiaTriggerAssignment[] assignments =
        [
            new() { TriggerId = "a", Action = UiaTriggerAssignment.ActionStart },
            new() { TriggerId = "b", Action = UiaTriggerAssignment.ActionStop },
            new() { TriggerId = "c", Action = UiaTriggerAssignment.ActionNone },
        ];

        Assert.Empty(TriggerFiringRules.WhileAssignedTriggerIds(assignments));
    }
}
