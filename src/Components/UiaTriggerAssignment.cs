using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ProcessRecorderApp.Components;

/// <summary>
/// UIA トリガ 1 件ぶんの割り当て（録画開始・終了するかどうかと、その対象）。
///
/// トリガで取得した値のテンプレート変数への反映は<b>全トリガで常に行われる</b>ため、
/// ここが持つのは録画アクションの設定だけである。行の増減はトリガ一覧の編集結果に
/// <c>TriggerAssignmentReconciler</c> が追随させる（手動で行を増減する UI は出さない）。
/// </summary>
public partial class UiaTriggerAssignment : ObservableObject, IPropertyAccess
{
    /// <summary>
    /// <see cref="Action"/> の選択肢一覧の識別キー。
    /// <c>PropCat_</c> / <c>PropDesc_</c> で始めてはいけない（<see cref="ChoiceListAttribute.Key"/>）。
    /// </summary>
    public const string ActionChoiceListKey = "TriggerAction";

    /// <summary><see cref="TargetRecorder"/> の選択肢一覧の識別キー。</summary>
    public const string TargetRecorderChoiceListKey = "TriggerTargetRecorder";

    /// <summary><see cref="Action"/> の保存値: 録画アクションなし（変数の反映だけは常に行われる）。</summary>
    public const string ActionNone = "";

    /// <summary><see cref="Action"/> の保存値: 録画を開始する。</summary>
    public const string ActionStart = "Start";

    /// <summary><see cref="Action"/> の保存値: 録画を停止する。</summary>
    public const string ActionStop = "Stop";

    /// <summary>
    /// <see cref="Action"/> の保存値: 条件が成立している間だけ録画する
    /// （成立で開始・不成立で停止）。
    ///
    /// <para>
    /// これが効くのは<b>トリガが不成立化を通知できるときだけ</b> ── UiaTrigger 側で
    /// ライフサイクルが <c>WhileMatching</c> かつ「停止時も通知」が入っている必要がある
    /// （満たさない割り当ては開始しても止まらないので、監視の起動時に警告を出す）。
    /// </para>
    /// </summary>
    public const string ActionWhile = "While";

    /// <summary>PropertyGridView 向けのプロパティ列挙(自型の public インスタンスプロパティを返す)。</summary>
    public IEnumerable<PropertyInfo> GetProperties()
        => typeof(UiaTriggerAssignment).GetProperties(BindingFlags.Instance | BindingFlags.Public);

    /// <summary>
    /// 対応するトリガ定義の Id。行の同一性はこの値で決まり、増減は Reconciler が管理するため
    /// 画面上は表示のみ（編集させると割り当てが迷子になる）。
    /// </summary>
    [ReadOnly(true)]
    [Description("PropDesc_Trigger_TriggerId")]
    [ObservableProperty]
    public partial string TriggerId { get; set; } = "";

    /// <summary>
    /// 発火時の録画アクション。保存値は <see cref="ActionNone"/> / <see cref="ActionStart"/> /
    /// <see cref="ActionStop"/> / <see cref="ActionWhile"/>（表示名はホストの ChoiceProvider が
    /// ローカライズする。enum にしない理由は <see cref="ChoiceListAttribute"/> を参照）。
    /// </summary>
    [Description("PropDesc_Trigger_Action")]
    [ChoiceList(ActionChoiceListKey)]
    [ObservableProperty]
    public partial string Action { get; set; } = ActionNone;

    /// <summary>アクションの対象レコーダー名。空なら全レコーダー一括。</summary>
    [Description("PropDesc_Trigger_TargetRecorder")]
    [ChoiceList(TargetRecorderChoiceListKey)]
    [ObservableProperty]
    public partial string TargetRecorder { get; set; } = "";

    /// <summary>
    /// コレクション表示の見出し（PropertyGridCollectionElement の Name 規約）。
    /// 計算プロパティなので設定 JSON には出さない。
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public string Name => TriggerId;

    partial void OnTriggerIdChanged(string value) => OnPropertyChanged(nameof(Name));
}
