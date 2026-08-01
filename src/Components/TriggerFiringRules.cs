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

/// <summary>トリガ発火時に実行する録画アクションの種別。</summary>
public enum TriggerActionKind
{
    /// <summary>録画アクションなし（変数の反映は常に行われる）。</summary>
    None,
    /// <summary>録画を開始する。</summary>
    Start,
    /// <summary>録画を停止する。</summary>
    Stop,
}

/// <summary>実行すべき録画アクション 1 件。<paramref name="TargetRecorder"/> が空なら全レコーダー一括。</summary>
public readonly record struct TriggerActionRequest(TriggerActionKind Kind, string TargetRecorder);

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
    /// <see cref="UiaTriggerAssignment.Action"/> の保存値をアクション種別へ写す。
    /// 未知の文字列（手で編集された設定・将来の値）は安全側の <see cref="TriggerActionKind.None"/>。
    /// </summary>
    public static TriggerActionKind ParseAction(string? action) => action switch
    {
        UiaTriggerAssignment.ActionStart => TriggerActionKind.Start,
        UiaTriggerAssignment.ActionStop => TriggerActionKind.Stop,
        _ => TriggerActionKind.None,
    };

    /// <summary>
    /// 発火したトリガ ID に対して実行すべきアクション列を返す。
    /// ID は完全一致（Ordinal）。<see cref="TriggerActionKind.None"/> の行は除外。
    /// 同一 ID の複数行（別レコーダーへの多重割り当て）を許し、並び順を保存する。
    /// </summary>
    public static List<TriggerActionRequest> ResolveActions(
        string triggerId, IEnumerable<UiaTriggerAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        var requests = new List<TriggerActionRequest>();
        foreach (var assignment in assignments)
        {
            if (!string.Equals(assignment.TriggerId, triggerId, StringComparison.Ordinal))
                continue;
            var kind = ParseAction(assignment.Action);
            if (kind == TriggerActionKind.None)
                continue;
            requests.Add(new(kind, assignment.TargetRecorder ?? ""));
        }
        return requests;
    }
}
