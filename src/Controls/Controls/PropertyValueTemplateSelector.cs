using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProcessRecorderApp.Controls;

/// <summary>
/// PropertyGridItem.EditKind に応じて、値カラムの編集用テンプレートを切り替える。
/// 純粋な C# コードのみで判定しており、リフレクションは使用していない。
/// </summary>
public sealed partial class PropertyValueTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? BoolTemplate { get; set; }
    public DataTemplate? EnumTemplate { get; set; }
    public DataTemplate? CommandTemplate { get; set; }
    public DataTemplate? CollectionTemplate { get; set; }
    public DataTemplate? BuilderTemplate { get; set; }
    public DataTemplate? ChoiceTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => item is PropertyGridItem pgi
            ? pgi.EditKind switch
            {
                PropertyEditKind.Bool => BoolTemplate,
                PropertyEditKind.Enum => EnumTemplate,
                PropertyEditKind.Command => CommandTemplate,
                PropertyEditKind.Collection => CollectionTemplate,
                PropertyEditKind.Builder => BuilderTemplate,
                PropertyEditKind.Choice => ChoiceTemplate,
                _ => TextTemplate
            }
            : TextTemplate;
}
