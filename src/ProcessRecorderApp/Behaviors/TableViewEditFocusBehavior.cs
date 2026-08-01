using CommunityToolkit.WinUI.Behaviors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUI.TableView;

namespace ProcessRecorderApp.Behaviors;

/// <summary>
/// <see cref="TableView"/> の <see cref="TableViewTemplateColumn"/> でセル編集を開始したとき、
/// EditingTemplate 内の <see cref="TextBox"/> へフォーカスを移す Behavior。
///
/// TableViewTemplateColumn の編集要素はライブラリ側で ContentControl にラップされ、実際の TextBox は
/// その内側に生成される。ライブラリは編集要素 Loaded 時(OnEditingElementLoaded)やセル選択時
/// (ApplyCurrentCellState、Tab 移動では Task.Delay(20) 後)に「ラッパー ContentControl 自身」を
/// フォーカスするため、IsTabStop=true の ContentControl にフォーカスが入り内側 TextBox にキー入力が届かない
/// （ダブルクリック/F2 直後・Tab 移動直後にワンクリックしないと編集できない）。
///
/// 対策として、編集開始時に内側 TextBox へフォーカスし直すとともに、以降 ContentControl 自身が
/// フォーカスを得るたびに内側 TextBox へフォーカスを転送する（フォーカス奪取の発生順序に依存しない）。
/// </summary>
public partial class TableViewEditFocusBehavior : BehaviorBase<TableView>
{
    /// <summary>現在編集中セルの編集要素（ラッパー ContentControl 相当）。GotFocus/Unloaded 購読中のもの。</summary>
    private FrameworkElement? _editingHost;

    /// <summary>現在編集中の TextBox（フォーカス転送先）。</summary>
    private TextBox? _editingTextBox;

    protected override bool Initialize()
    {
        var result = base.Initialize();

        if (AssociatedObject is not null)
            AssociatedObject.PreparingCellForEdit += OnPreparingCellForEdit;

        return result;
    }

    protected override bool Uninitialize()
    {
        if (AssociatedObject is not null)
            AssociatedObject.PreparingCellForEdit -= OnPreparingCellForEdit;

        DetachEditingHost();

        return base.Uninitialize();
    }

    private void OnPreparingCellForEdit(object? sender, TableViewPreparingCellForEditEventArgs e)
    {
        // 直前の編集ホストが残っていれば解除する（保険。通常は Unloaded で解除済み）
        DetachEditingHost();

        _editingTextBox = FindDescendant<TextBox>(e.EditingElement);
        if (_editingTextBox is null)
            return;

        _editingHost = e.EditingElement;
        _editingHost.GotFocus += OnEditingHostGotFocus;
        _editingHost.Unloaded += OnEditingHostUnloaded;

        // 初回（ダブルクリック/F2）フォーカス。既存文字は全選択して上書き入力できるようにする。
        FocusEditingTextBox();
    }

    private void OnEditingHostGotFocus(object sender, RoutedEventArgs e)
    {
        // TextBox 自身がフォーカスを得た場合（自分の転送によるものを含む）は何もしない（ループ防止）
        if (ReferenceEquals(e.OriginalSource, _editingTextBox))
            return;

        // ラッパー ContentControl 側がフォーカスを奪ったので TextBox へ転送する
        FocusEditingTextBox();
    }

    private void OnEditingHostUnloaded(object sender, RoutedEventArgs e)
    {
        // 編集終了で編集要素がツリーから外れたら購読を解除する（編集ごとに新規要素のためリークしない）
        DetachEditingHost();
    }

    private void FocusEditingTextBox()
    {
        if (_editingTextBox is null)
            return;
        _editingTextBox.Focus(FocusState.Programmatic);
        _editingTextBox.SelectAll();
    }

    private void DetachEditingHost()
    {
        if (_editingHost is not null)
        {
            _editingHost.GotFocus -= OnEditingHostGotFocus;
            _editingHost.Unloaded -= OnEditingHostUnloaded;
            _editingHost = null;
        }
        _editingTextBox = null;
    }

    /// <summary>ビジュアルツリーを深さ優先で辿り、最初に見つかった型 <typeparamref name="T"/> の要素を返す。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }
}
