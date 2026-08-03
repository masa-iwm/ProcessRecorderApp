using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.Controls;

/// <summary>
/// PropertyGridView.xaml の DataTemplate 内で使うローカライズ済み文字列。
///
/// DataTemplate 内の要素に付けた x:Uid は自動解決されない(UserControl.Resources で
/// 定義されたテンプレートはページの通常の Connect() 経路を通らないため)。
/// そのため、これらのボタン文言は x:Bind の静的プロパティ参照で解決する。
/// </summary>
internal static class PropertyGridStrings
{
    public static string Exec => Localization.GetString("Controls/ControlsResources/PropGrid_Exec");
    public static string Add => Localization.GetString("Controls/ControlsResources/PropGrid_Add");
    public static string Remove => Localization.GetString("Controls/ControlsResources/PropGrid_Remove");

    /// <summary>「…」ビルダー起動ボタンの AutomationProperties.Name(アイコンのみで文字が無いため必須)。</summary>
    public static string Build => Localization.GetString("Controls/ControlsResources/PropGrid_Build");

    /// <summary>
    /// 入力を対象プロパティの型へ変換できなかったときのエラー文言
    /// (<see cref="PropertyGridItem.Value"/> から使う。テンプレートからではないが、
    ///  <b>利用者に見える文字列を直書きしない</b>という点で扱いは同じ)。
    /// </summary>
    public static string ValueConversionFailed
        => Localization.GetString("Controls/ControlsResources/PropGrid_ValueConversionFailed");
}
