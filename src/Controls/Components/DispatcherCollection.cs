using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ProcessRecorderApp.Components;

/// <summary>
/// <see cref="DispatcherQueue"/> を持ち運ぶ <see cref="ObservableCollection{T}"/>。
///
/// <para>
/// <b>外部ソースをミラーする経路は持たない（削除済み）。</b> かつての実装は
/// (1) 弱参照による自動解除がインスタンスメソッド購読では発動不能（source 側の
/// デリゲートが this を強参照するため、回収済み判定の枝が到達不能のデッドコード）、
/// (2) 複数要素イベントの先頭 1 件しか適用しない、(3) Remove/Replace の Dispose を
/// 発火側スレッドで行うためキュー反映前の別要素を壊しうる、という欠陥を抱えたまま、
/// 唯一の利用箇所が空の内部ソースで使っていて経路自体が休眠していた。
/// ミラーが要る場合は <c>GstEventEventRecorderCollectionViewModel</c> の実装を参照して
/// 目的に合わせて作ること（汎用の器としてここへ戻さない）。
/// </para>
/// </summary>
public partial class DispatcherCollection<T> : ObservableCollection<T>
{
    public DispatcherQueue DispatcherQueue { get; init; }

    public DispatcherCollection(DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        DispatcherQueue = dispatcherQueue;
    }
}

/// <summary>
/// <see cref="DispatcherCollection{T}"/> の読み取り専用ビュー。
/// 内側のコレクションの変更通知をそのまま外へ転送する（内外は同一寿命なので
/// 購読の解除機構は持たない）。
/// </summary>
public class ReadOnlyDispatcherCollection<T> : ReadOnlyCollection<T>, INotifyCollectionChanged, INotifyPropertyChanged
{
    public ReadOnlyDispatcherCollection(DispatcherCollection<T> collection) : base(collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, args) => PropertyChanged?.Invoke(this, args);
        collection.CollectionChanged += (_, args) => CollectionChanged?.Invoke(this, args);
    }

    public DispatcherQueue DispatcherQueue => ((DispatcherCollection<T>)Items).DispatcherQueue;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
}
