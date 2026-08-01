using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessRecorderApp.ViewModels;

namespace ProcessRecorderApp.Views;

/// <summary>
/// PipelineFieldRow.EditKind に応じて編集テンプレートを切り替える。
/// 行ごとに 1 種類のエディタのみを実体化することで、非表示エディタの
/// TwoWay バインディングが値を上書きする問題(SelectedItem→null 等)を避ける。
/// </summary>
public sealed partial class FieldRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BoolTemplate { get; set; }
    public DataTemplate? ChoiceTemplate { get; set; }
    public DataTemplate? TextTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => item is PipelineFieldRow row
            ? row.EditKind switch
            {
                FieldEditKind.Bool => BoolTemplate,
                FieldEditKind.Choice => ChoiceTemplate,
                _ => TextTemplate,
            }
            : TextTemplate;
}
