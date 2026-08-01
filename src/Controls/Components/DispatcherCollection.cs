using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;

namespace ProcessRecorderApp.Components;

public partial class DispatcherCollection<T> : ObservableCollection<T>, IDisposable
{
    public DispatcherQueue DispatcherQueue { get; init; }

    private readonly WeakReference<DispatcherCollection<T>> _weakThis;
    private readonly INotifyCollectionChanged _notifier;
    private bool disposedValue;

    public DispatcherCollection(DispatcherQueue dispatcherQueue) : this(new ObservableCollection<T>(), dispatcherQueue) { }

    public DispatcherCollection(IList<T> source, DispatcherQueue dispatcherQueue)
        : base(source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        if (source is not INotifyCollectionChanged notifier)
            throw new ArgumentException($"{nameof(source)} must implement {nameof(INotifyCollectionChanged)}.", nameof(source));

        DispatcherQueue = dispatcherQueue;

        _weakThis = new(this);
        _notifier = notifier;
        notifier.CollectionChanged += OnCollectionChanged;
    }

    private async void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_weakThis.TryGetTarget(out var target))
        {
            if (sender is INotifyCollectionChanged collection)
                collection.CollectionChanged -= OnCollectionChanged;
            return;
        }

        ArgumentNullException.ThrowIfNull(e);
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                await DispatcherQueue.EnqueueAsync(() => Insert(e.NewStartingIndex, (T)e.NewItems?[0]!));
                break;
            case NotifyCollectionChangedAction.Move:
                await DispatcherQueue.EnqueueAsync(() => Move(e.OldStartingIndex, e.NewStartingIndex));
                break;
            case NotifyCollectionChangedAction.Remove:
                (Items[e.OldStartingIndex] as IDisposable)?.Dispose();
                await DispatcherQueue.EnqueueAsync(() => RemoveAt(e.OldStartingIndex));
                break;
            case NotifyCollectionChangedAction.Replace:
                (Items[e.NewStartingIndex] as IDisposable)?.Dispose();
                await DispatcherQueue.EnqueueAsync(() => SetItem(e.NewStartingIndex, (T)e.NewItems?[0]!));
                break;
            case NotifyCollectionChangedAction.Reset:
                foreach (var item in Items)
                    (item as IDisposable)?.Dispose();

                await DispatcherQueue.EnqueueAsync(ClearItems);
                break;
            default:
                throw new ArgumentException("Unknown NotifyCollectionChangedAction", nameof(e));
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                _notifier.CollectionChanged -= OnCollectionChanged;
            }

            disposedValue = true;
        }
    }

    // アンマネージドリソースを持たないためファイナライザは定義しない。
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

public class ReadOnlyDispatcherCollection<T> : ReadOnlyCollection<T>, INotifyCollectionChanged, INotifyPropertyChanged
{
    private readonly WeakReference<ReadOnlyDispatcherCollection<T>> _weakThis;

    public ReadOnlyDispatcherCollection(DispatcherCollection<T> collection) : base(collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        _weakThis = new(this);
        ((INotifyPropertyChanged)collection).PropertyChanged += OnPropertyChanged;
        collection.CollectionChanged += OnCollectionChanged;
    }

    public DispatcherQueue DispatcherQueue => ((DispatcherCollection<T>)Items).DispatcherQueue;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!_weakThis.TryGetTarget(out var target))
        {
            if (sender is INotifyCollectionChanged collection)
                collection.CollectionChanged -= OnCollectionChanged;
            return;
        }

        var threadSafeHandler = Interlocked.CompareExchange(ref CollectionChanged, null, null);

        threadSafeHandler?.Invoke(this, args);
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!_weakThis.TryGetTarget(out var target))
        {
            if (sender is INotifyPropertyChanged collection)
                collection.PropertyChanged -= OnPropertyChanged;
            return;
        }
        var threadSafeHandler = Interlocked.CompareExchange(ref PropertyChanged, null, null);

        threadSafeHandler?.Invoke(this, args);
    }
}
