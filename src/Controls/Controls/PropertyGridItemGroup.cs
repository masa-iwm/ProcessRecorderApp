using System.Collections.Generic;

namespace ProcessRecorderApp.Controls;

/// <summary>
/// ListView の IsSourceGrouped 用グループコンテナ。
/// CollectionViewSource(IsSourceGrouped=True) にそのまま渡せる形。
/// </summary>
public sealed partial class PropertyGridItemGroup(string category, IEnumerable<PropertyGridItem> items) : List<PropertyGridItem>(items)
{
    public string Category { get; } = category;
}
