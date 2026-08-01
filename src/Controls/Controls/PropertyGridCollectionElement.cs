using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.Controls;

/// <summary>
/// PropertyGridView のコレクション項目内で、コレクションの 1 要素分を表示・編集するための VM。
///
/// 要素が IPropertyAccess を実装している場合はネストしたプロパティ一覧(Properties)を構築し、
/// 未実装の場合は ToString() の読み取り専用表示 1 行にフォールバックする。
/// 要素の INotifyPropertyChanged を購読して表示へ追従するため、
/// 不要になったら必ず Detach() を呼んで購読を解除すること(PropertyGridItem.DetachCollection 経由)。
/// </summary>
public sealed partial class PropertyGridCollectionElement : INotifyPropertyChanged
{
    private readonly IList _collection;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly PropertyInfo? _nameProperty;
    private readonly Dictionary<string, PropertyGridItem> _itemsByPropertyName;
    private readonly Action<PropertyGridCollectionElement>? _onExpanded;
    private bool _isExpanded;

    /// <summary>対応するコレクション要素本体。</summary>
    public object Element { get; }

    /// <summary>ネスト表示するプロパティ項目の一覧。</summary>
    public IReadOnlyList<PropertyGridItem> Properties { get; }

    /// <summary>この要素をコレクションから削除するコマンド(Remove ボタン)。</summary>
    public ICommand RemoveCommand { get; }

    /// <summary>
    /// Expander ヘッダに表示する要素名。
    /// 要素が string 型の Name プロパティを持つ場合はその値、なければ ToString()。
    /// </summary>
    public string Header
    {
        get
        {
            string? name = _nameProperty?.GetValue(Element) as string;
            return string.IsNullOrEmpty(name) ? (Element.ToString() ?? "") : name;
        }
    }

    /// <summary>
    /// Expander の展開状態。アコーディオン動作のため、展開時は親(PropertyGridItem)へ通知され、
    /// 同じコレクション内の他要素は折りたたまれる。
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }
            _isExpanded = value;
            OnPropertyChanged();
            if (value)
            {
                _onExpanded?.Invoke(this);
            }
        }
    }

    internal PropertyGridCollectionElement(
        object element,
        IList collection,
        DispatcherQueue dispatcherQueue,
        Action<PropertyGridCollectionElement>? onExpanded = null,
        Func<string, string, IReadOnlyList<PropertyGridChoice>>? choiceProvider = null)
    {
        Element = element;
        _collection = collection;
        _dispatcherQueue = dispatcherQueue;
        _onExpanded = onExpanded;

        if (element is IPropertyAccess access)
        {
            Properties = (PropertyGridItem[])[.. PropertyGridView.BuildItems(access, choiceProvider)];
            _nameProperty = access.GetProperties()
                .FirstOrDefault(p => p.Name == "Name" && p.PropertyType == typeof(string) && p.CanRead);
        }
        else
        {
            // IPropertyAccess 未実装要素: ToString() の読み取り専用表示のみ提供する。
            var fallback = new PropertyGridItem
            {
                Name = Localization.GetString("Controls/ControlsResources/PropGrid_ElementValueName"),
                IsReadOnly = true,
                Value = element.ToString() ?? "null"
            };
            Properties = (PropertyGridItem[])[fallback];
        }

        _itemsByPropertyName = Properties
            .Where(i => i.SourcePropertyName is not null)
            .ToDictionary(i => i.SourcePropertyName!);

        RemoveCommand = new RelayCommand(() => _collection.Remove(Element));

        if (element is INotifyPropertyChanged notifying)
        {
            notifying.PropertyChanged += OnElementPropertyChanged;
        }
    }

    /// <summary>要素の PropertyChanged 購読を解除する。破棄時に必ず呼ぶこと。</summary>
    internal void Detach()
    {
        if (Element is INotifyPropertyChanged notifying)
        {
            notifying.PropertyChanged -= OnElementPropertyChanged;
        }
    }

    /// <summary>
    /// 要素側からの変更通知を該当プロパティ項目へ反映する。
    /// 別スレッドから発火される可能性があるため DispatcherQueue でマーシャリングする。
    /// </summary>
    private void OnElementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => OnElementPropertyChanged(sender, e));
            return;
        }

        if (string.IsNullOrEmpty(e.PropertyName))
        {
            // 空文字/null は「すべてのプロパティが変更された」という慣習的な意味として扱う。
            foreach (PropertyGridItem item in _itemsByPropertyName.Values)
            {
                item.RefreshFromSource();
            }
            OnPropertyChanged(nameof(Header));
            return;
        }

        if (_itemsByPropertyName.TryGetValue(e.PropertyName, out PropertyGridItem? matched))
        {
            matched.RefreshFromSource();
        }

        if (e.PropertyName == _nameProperty?.Name)
        {
            OnPropertyChanged(nameof(Header));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
