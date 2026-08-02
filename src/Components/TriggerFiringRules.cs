using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 句の読み取り結果の行き先。UiaTrigger の <c>ClauseOutcome</c> のミラー
/// （Components は UiaTrigger を参照しないため。変換はアプリ層が網羅 switch で行う）。
/// </summary>
public enum TriggerClauseOutcome
{
    /// <summary>条件が成立した。</summary>
    Matched,
    /// <summary>条件が成立しなかった（値は読めている）。</summary>
    NotMatched,
    /// <summary>値が読めなかった。</summary>
    Unreadable,
    /// <summary>式が短絡し、評価されなかった（一度も読んでいない）。</summary>
    NotEvaluated,
}

/// <summary>句 1 件ぶんの読み値（名前・値・行き先）。</summary>
public readonly record struct TriggerClauseValue(string Name, string Value, TriggerClauseOutcome Outcome);

/// <summary>
/// 発火が条件の<b>立ち上がり</b>か<b>立ち下がり</b>か。
///
/// <para>
/// 立ち下がりは「条件が成立しなくなった」通知（UiaTrigger の <c>TriggerOn.StoppedMatching</c>）だけで、
/// それ以外のライフサイクルはすべて立ち上がり扱いにする ── 上流の列挙が増えても
/// 安全側（＝録画を止める側ではなく通常の発火側）に落ちるようにするため。
/// </para>
/// </summary>
public enum TriggerFireEdge
{
    /// <summary>条件が成立した（通常の発火）。</summary>
    Rising,
    /// <summary>条件が成立しなくなった。</summary>
    Falling,
}

/// <summary>
/// 割り当て（設定）が選んでいる操作。<see cref="TriggerActionKind"/>（実際に行う操作）とは別物である。
///
/// <para>
/// <b>2 つを 1 つの列挙にまとめてはいけない。</b> <see cref="While"/> は「立ち上がりなら開始・
/// 立ち下がりなら停止」であって、それ自体は実行できる操作ではない。同じ列挙に混ぜると
/// 実行側の <c>if (種別 == Start) … else 停止</c> が <see cref="While"/> を<b>停止として黙って実行</b>する。
/// 分けてあれば <see cref="While"/> が <see cref="TriggerActionRequest"/> に現れる余地が型から消える。
/// </para>
/// </summary>
public enum TriggerAssignmentAction
{
    /// <summary>録画は動かさない（変数の反映は常に行われる）。</summary>
    None,
    /// <summary>条件が成立したら録画を開始する。</summary>
    Start,
    /// <summary>条件が成立したら録画を停止する。</summary>
    Stop,
    /// <summary>条件が成立している間だけ録画する（成立で開始・不成立で停止）。</summary>
    While,
}

/// <summary>実際に行う録画操作の種別。</summary>
public enum TriggerActionKind
{
    /// <summary>何もしない（<see cref="TriggerFiringRules.ResolveActions"/> は返さない）。</summary>
    None,
    /// <summary>録画を開始する。</summary>
    Start,
    /// <summary>録画を停止する。</summary>
    Stop,
}

/// <summary>
/// 実行すべき録画アクション 1 件。<paramref name="TargetRecorder"/> が空なら全レコーダー一括。
/// <paramref name="TracksCondition"/> は「条件成立中のみ録画」由来であること ──
/// 監視が条件を追えなくなったときに自動停止してよいのはこれだけである。
/// </summary>
public readonly record struct TriggerActionRequest(
    TriggerActionKind Kind, string TargetRecorder, bool TracksCondition);

/// <summary>
/// トリガ発火 1 回を「書き込むべき変数の列」と「実行すべきアクションの列」へ写す規則。
///
/// 規則を WinUI アプリのプロジェクト（UiaTriggerService）に置くと L1 から参照できないので、
/// UiaTrigger の型に依存しない純粋関数としてここへ切り出してある
/// （<c>RecordingCommandState</c> / <c>FilenameTemplate</c> と同じ構図）。
/// </summary>
public static partial class TriggerFiringRules
{
    /// <summary>
    /// ファイル名テンプレートから <c>{キー}</c> で参照できるキーの形。
    /// <c>FilenameTemplate.PlaceholderRegex</c> のキー文字クラス <c>[\w.-]</c> と揃えてある
    /// （.NET の <c>\w</c> は Unicode の単語文字＝日本語を含む。ドットは複合トリガの句、
    /// ハイフンは UIA トリガの ID に自然に入るため受ける。空白・波括弧などの記号は外れる）。
    /// </summary>
    [GeneratedRegex(@"^[\w.-]+$")]
    private static partial Regex TemplateKeyRegex();

    /// <summary>
    /// 発火 1 回ぶんの変数 (キー, 値) 列を組み立てる。
    ///
    /// <list type="bullet">
    ///   <item><c>&lt;トリガID&gt;</c> = NewValue（常に書く）</item>
    ///   <item>句が 2 つ以上あるトリガでは、値が読めている句
    ///     （<see cref="TriggerClauseOutcome.Matched"/> / <see cref="TriggerClauseOutcome.NotMatched"/>）
    ///     だけ <c>&lt;トリガID&gt;.&lt;句名&gt;</c> = 句の値 も書く。
    ///     <see cref="TriggerClauseOutcome.Unreadable"/> / <see cref="TriggerClauseOutcome.NotEvaluated"/> は
    ///     書かない ── 読めていない値で既存の変数を潰さないため。</item>
    /// </list>
    /// 句が 1 つだけのトリガはその句の値が NewValue そのものなので、句側は書かない。
    /// </summary>
    public static List<KeyValuePair<string, string>> BuildVariables(
        string triggerId, string newValue, IReadOnlyList<TriggerClauseValue> clauses)
    {
        ArgumentException.ThrowIfNullOrEmpty(triggerId);
        ArgumentNullException.ThrowIfNull(clauses);

        var variables = new List<KeyValuePair<string, string>>(1 + clauses.Count)
        {
            new(triggerId, newValue),
        };
        if (clauses.Count < 2)
            return variables;

        foreach (var clause in clauses)
        {
            if (string.IsNullOrEmpty(clause.Name))
                continue;
            if (clause.Outcome is not (TriggerClauseOutcome.Matched or TriggerClauseOutcome.NotMatched))
                continue;
            variables.Add(new($"{triggerId}.{clause.Name}", clause.Value));
        }
        return variables;
    }

    /// <summary>
    /// キーがファイル名テンプレートの <c>{キー}</c> から参照できる形かどうか。
    /// 外れるキーも変数としては書かれる（Variables 画面には出る）ので、
    /// 呼び出し側は書き込みを止めるのではなく警告ログに使う。
    /// </summary>
    public static bool IsTemplateReferencable(string key)
        => !string.IsNullOrEmpty(key) && TemplateKeyRegex().IsMatch(key);

    /// <summary>
    /// <see cref="UiaTriggerAssignment.Action"/> の保存値を割り当ての操作へ写す。
    /// 未知の文字列（手で編集された設定・将来の値）は安全側の
    /// <see cref="TriggerAssignmentAction.None"/>。
    /// </summary>
    public static TriggerAssignmentAction ParseAction(string? action) => action switch
    {
        UiaTriggerAssignment.ActionStart => TriggerAssignmentAction.Start,
        UiaTriggerAssignment.ActionStop => TriggerAssignmentAction.Stop,
        UiaTriggerAssignment.ActionWhile => TriggerAssignmentAction.While,
        _ => TriggerAssignmentAction.None,
    };

    /// <summary>
    /// 発火したトリガ ID と<b>エッジ</b>に対して、実行すべきアクション列を返す。
    /// ID は完全一致（Ordinal）。同一 ID の複数行（別レコーダーへの多重割り当て）を許し、
    /// 並び順を保存する。
    ///
    /// <list type="table">
    ///   <item><term>None</term><description>どちらのエッジでも何もしない</description></item>
    ///   <item><term>Start / Stop</term><description>立ち上がりでのみ実行する</description></item>
    ///   <item><term>While</term><description>立ち上がりで開始・立ち下がりで停止</description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Start / Stop を立ち下がりで実行しないことが要点。</b> 「停止時も通知」を入れたトリガは
    /// 条件が成立しなくなったときにも発火するので、エッジを見ないと
    /// <b>要素が消えた瞬間にも録画を開始してしまう</b>。
    /// </para>
    /// </summary>
    public static List<TriggerActionRequest> ResolveActions(
        string triggerId, TriggerFireEdge edge, IEnumerable<UiaTriggerAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        var requests = new List<TriggerActionRequest>();
        foreach (var assignment in assignments)
        {
            if (!string.Equals(assignment.TriggerId, triggerId, StringComparison.Ordinal))
                continue;

            (TriggerActionKind Kind, bool Tracks) effect = (ParseAction(assignment.Action), edge) switch
            {
                (TriggerAssignmentAction.Start, TriggerFireEdge.Rising) => (TriggerActionKind.Start, false),
                (TriggerAssignmentAction.Stop, TriggerFireEdge.Rising) => (TriggerActionKind.Stop, false),
                (TriggerAssignmentAction.While, TriggerFireEdge.Rising) => (TriggerActionKind.Start, true),
                (TriggerAssignmentAction.While, TriggerFireEdge.Falling) => (TriggerActionKind.Stop, true),
                _ => (TriggerActionKind.None, false),
            };
            if (effect.Kind == TriggerActionKind.None)
                continue;

            requests.Add(new(effect.Kind, assignment.TargetRecorder ?? "", effect.Tracks));
        }
        return requests;
    }

    /// <summary>
    /// 「条件成立中のみ録画」の割り当てが 1 件以上あるトリガ ID（重複排除・出現順）。
    ///
    /// <para>
    /// これらのトリガは<b>不成立化を通知できなければならない</b>。通知できないトリガでは
    /// 開始した録画が止まらないので、呼び出し側は監視の起動時にこの集合と定義側の能力を
    /// 突き合わせて警告し、条件を追えなくなった録画を自動停止する判断にも使う。
    /// 定義側の能力（ライフサイクルとフラグ）の判定は UiaTrigger の型に触るので呼び出し側に残す。
    /// </para>
    /// </summary>
    public static List<string> WhileAssignedTriggerIds(IEnumerable<UiaTriggerAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (ParseAction(assignment.Action) != TriggerAssignmentAction.While)
                continue;
            if (seen.Add(assignment.TriggerId))
                ids.Add(assignment.TriggerId);
        }
        return ids;
    }
}
