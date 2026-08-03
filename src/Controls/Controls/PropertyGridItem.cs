using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.Controls;

/// <summary>値カラムにどの編集コントロールを使うか。</summary>
public enum PropertyEditKind
{
    Text,
    Bool,
    Enum,
    Command,
    Collection,
    Builder,

    /// <summary>
    /// ホストが供給する選択肢からコンボボックスで選ぶ（<c>ChoiceListAttribute</c> 由来）。
    /// <see cref="Enum"/> との違いは<b>表示名と保存値を分けられる</b>ことと、
    /// 選択肢が型ではなく実行時の状況（その PC に何が在るか）で決まること。
    /// </summary>
    Choice
}

/// <summary>
/// PropertyGridView に表示する 1 プロパティ分のデータ。
///
/// 対象オブジェクト(target)と PropertyInfo を保持している場合、
/// Value の変更時に PropertyValueConverter で型変換した上で PropertyInfo.SetValue を
/// 呼び出し、対象オブジェクトへ書き戻す(コミットする)。
/// 変換や書き込みに失敗した場合は元の値に戻し、HasError/ErrorMessage を通じて通知する。
/// </summary>
public sealed partial class PropertyGridItem : INotifyPropertyChanged
{
    private readonly object? _target;
    private readonly PropertyInfo? _property;

    private string _value = "";
    private bool _hasError;
    private string? _errorMessage;

    /// <summary>Category が未指定の場合の既定値(リソースキー)。PropertyGridView.BuildItems の既定値と揃える。</summary>
    internal const string DefaultCategoryKey = "PropCat_General";

    public required string Name { get; init; }
    public string Category { get; init; } = DefaultCategoryKey;
    public string? Description { get; init; }

    /// <summary>プロパティ名セルの ToolTip 用。長い名前でも全文を確認できるように Description と結合する。</summary>
    public string ToolTipText => string.IsNullOrEmpty(Description) ? Name : $"{Name}\n{Description}";

    /// <summary>
    /// true の場合、利用者は値を直接編集できない（<see cref="Value"/> の setter もコミットしない）。
    ///
    /// <para>
    /// 見え方は編集の種類で異なる。<see cref="PropertyEditKind.Builder"/> の行だけは
    /// テキストを無効化せず読み取り専用にし（選択・コピーを残す）、「…」ボタンも押せる
    /// ── <c>[ReadOnly(true)]</c> ＋ <c>[ValueBuilder]</c> は「直接は編集できないが
    /// ビルダーでなら変更できる」という意味だからである。ほかの種類は無効化される。
    /// </para>
    /// </summary>
    public bool IsReadOnly { get; init; }
    public bool IsEditable => !IsReadOnly;

    public PropertyEditKind EditKind { get; init; } = PropertyEditKind.Text;

    /// <summary>EditKind が Enum の場合の選択肢一覧。</summary>
    public IReadOnlyList<string>? EnumValues { get; init; }

    /// <summary>
    /// EditKind が Choice の場合の選択肢一覧（表示名と保存値の対）。
    ///
    /// <para>
    /// <b>この一覧は項目の生成時に1回だけ作って保持する。</b>
    /// 評価のたびに作り直すと、<see cref="SelectedChoice"/> が返す実体が
    /// 一覧の要素と<b>参照が一致しなくなり、コンボボックスが空を表示する</b>
    /// ── しかもエラーは出ない。
    /// </para>
    /// </summary>
    public IReadOnlyList<PropertyGridChoice>? Choices { get; init; }

    /// <summary>
    /// EditKind が Choice のときにコンボボックスの <c>SelectedItem</c> が読み書きする。
    ///
    /// <para>
    /// <b>getter は必ず <see cref="Choices"/> の中の実体を返す。</b>
    /// 同じ内容の別インスタンスを作って返すと参照が一致せず、選択が表示されない。
    /// </para>
    /// <para>
    /// 一覧に無い値のときは <see langword="null"/>（＝未選択表示）になる。
    /// <b>それを避けるのはプロバイダーの責任</b> ── 現在値を選択肢として
    /// 差し込むかどうかはホストが決める（<c>ChoiceListAttribute</c> の説明を参照）。
    /// </para>
    /// </summary>
    public PropertyGridChoice? SelectedChoice
    {
        get
        {
            if (Choices is null)
                return null;

            foreach (var choice in Choices)
            {
                if (string.Equals(choice.Value, Value, StringComparison.Ordinal))
                    return choice;
            }
            return null;
        }
        set
        {
            // コンボボックスは選択解除でも呼んでくるので、null は無視する
            // （無視しないと、項目の作り直しのたびに設定値が空へ書き戻される）。
            if (value is not null)
                Value = value.Value;
        }
    }

    /// <summary>EditKind が Command の場合に実行する ICommand。Button.Command に直接バインドする。</summary>
    public ICommand? Command { get; init; }

    // ---- EditKind が Builder の場合にのみ使用するメンバー ----

    /// <summary>EditKind が Builder の場合に使用するビルダー識別キー(ValueBuilderAttribute 由来)。</summary>
    public string? BuilderKey { get; init; }

    /// <summary>「…」ボタン押下時に実行される、ホスト提供のビルダー起動処理。PropertyGridView が設定する。</summary>
    internal Func<PropertyGridItem, Task>? BuilderRunner { get; set; }

    private IAsyncRelayCommand? _buildCommand;

    /// <summary>
    /// EditKind が Builder のとき「…」ボタンから実行するコマンド。
    /// 実行時に <see cref="BuilderRunner"/>(PropertyGridView が設定)を呼び出す。
    /// </summary>
    public IAsyncRelayCommand BuildCommand => _buildCommand ??=
        new AsyncRelayCommand(() => BuilderRunner?.Invoke(this) ?? Task.CompletedTask);

    // ---- EditKind が Collection の場合にのみ使用するメンバー ----

    /// <summary>編集対象のコレクション本体。Collection 項目以外では null。</summary>
    private readonly IList? _collection;

    /// <summary>
    /// Add ボタンで生成する要素の具象型(CollectionItemTypeAttribute 由来)。
    /// DynamicallyAccessedMembers 注釈により public 無引数コンストラクタがトリミングから保護される。
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    private readonly Type? _collectionItemType;

    private DispatcherQueue? _dispatcherQueue;
    private WeakCollectionChangedListener? _collectionListener;
    private Func<string, string, IPropertyAccess?, IReadOnlyList<PropertyGridChoice>>? _choiceProvider;

    /// <summary>コレクションの各要素に対応する表示用 VM。Collection 項目以外では null。</summary>
    public ObservableCollection<PropertyGridCollectionElement>? Elements { get; }

    /// <summary>Expander ヘッダに表示する件数テキスト。</summary>
    public string CountText => _collection is null ? "" : $"{_collection.Count} items";

    /// <summary>新規要素を追加するコマンド。要素型が宣言されていないコレクションでは null。</summary>
    public ICommand? AddItemCommand { get; }

    /// <summary>Add ボタンの表示可否(要素型が宣言されている場合のみ表示)。</summary>
    public Visibility AddButtonVisibility => AddItemCommand is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 表示専用の項目を手動で作りたい場合に使う既定コンストラクタ。
    /// この経路で作った項目は target/property を持たないため、Value を変更しても
    /// どこにもコミットされず、ローカルの表示値が変わるだけになる。
    /// </summary>
    public PropertyGridItem()
    {
    }

    /// <summary>PropertyGridView が IPropertyAccess から項目を生成する際に使う内部コンストラクタ。</summary>
    private PropertyGridItem(
        object target,
        PropertyInfo property,
        IList? collection,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type? collectionItemType)
    {
        _target = target;
        _property = property;
        _collection = collection;
        _collectionItemType = collectionItemType;

        if (collection is not null)
        {
            Elements = [];
            if (collectionItemType is not null)
            {
                AddItemCommand = new RelayCommand(AddNewItem);
            }
        }
    }

    /// <summary>元になった CLR プロパティ名。SelectedObject の PropertyChanged との突き合わせに使う。</summary>
    internal string? SourcePropertyName => _property?.Name;

    /// <summary>
    /// UI 自動化から行を特定するためのロケール非依存の識別子（＝元の CLR プロパティ名）。
    /// <see cref="Name"/> はローカライズされた表示名なので、OS の表示言語が変わると
    /// それを頼りにした UI テストは壊れる。編集コントロールの
    /// <c>AutomationProperties.AutomationId</c> にはこちらをバインドする。
    /// プロパティ由来でない手動生成項目では表示名にフォールバックする。
    /// </summary>
    public string PropertyName => _property?.Name ?? Name;

    /// <summary>「…」（ビルダー起動）ボタンの AutomationId。行ごとに一意にする。</summary>
    public string BuilderButtonAutomationId => $"{PropertyName}.Build";

    /// <summary>
    /// エラー表示の AutomationId。行ごとに一意にする。
    /// <b>エラーが「出ること」だけでなく「消えること」を UI 自動化から確かめるために要る</b>
    /// ── 消えない不具合はモデルを読むだけでは分からない（表示側で止まりうる）。
    /// </summary>
    public string ErrorAutomationId => $"{PropertyName}.Error";

    internal static PropertyGridItem Create(
        object target,
        PropertyInfo property,
        string displayName,
        string category,
        string? description,
        bool isReadOnly,
        PropertyEditKind editKind,
        IReadOnlyList<string>? enumValues,
        string initialValue,
        ICommand? command = null,
        IList? collection = null,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type? collectionItemType = null,
        string? builderKey = null,
        IReadOnlyList<PropertyGridChoice>? choices = null)
    {
        var item = new PropertyGridItem(target, property, collection, collectionItemType)
        {
            Name = displayName,
            Category = category,
            Description = description,
            IsReadOnly = isReadOnly,
            EditKind = editKind,
            EnumValues = enumValues,
            Choices = choices,
            Command = command,
            BuilderKey = builderKey,
            // 初期値は変換・コミット処理を経由させず、直接フィールドへ設定する。
            _value = initialValue
        };
        return item;
    }

    /// <summary>表示用の値文字列。設定すると対象オブジェクトへのコミットを試みる。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                // **同じ文字列でもエラー表示は畳む。** 変換に失敗したとき表示値は
                // 差し戻されるので、画面には「正しい値」が出たままエラーだけが残る。
                // そこで利用者が入力し直すと必ずここへ来るため、素通ししていると
                // 「正しい値を入れてもエラーが消えない」になる（L3 の
                // ReenteringTheRevertedValue_ClearsTheError が守る）。
                ClearError();
                return;
            }

            if (_property is null || _target is null || IsReadOnly)
            {
                // コミット先を持たない項目、または読み取り専用項目はローカルの表示値のみ更新する。
                _value = value;
                ClearError();
                RaiseValueRelatedChanged();
                return;
            }

            if (PropertyValueConverter.TryConvert(_property.PropertyType, value, out object? converted))
            {
                try
                {
                    _property.SetValue(_target, converted);

                    // setter 側で値が丸められる可能性があるため、実際の値を読み直して表示する。
                    _value = _property.GetValue(_target)?.ToString() ?? "";
                    ClearError();
                }
                catch (Exception ex)
                {
                    // 書き込み失敗: 表示値は変更前のまま据え置く(=元の値に差し戻す)。
                    SetError(ex.InnerException?.Message ?? ex.Message);
                }
            }
            else
            {
                SetError(PropertyGridStrings.ValueConversionFailed);
            }

            RaiseValueRelatedChanged();
        }
    }

    /// <summary>EditKind が Bool のときに CheckBox.IsChecked から利用する。</summary>
    public bool BoolValue
    {
        get => bool.Parse(Value);
        set => Value = value.ToString();
    }

    /// <summary>
    /// SelectedObject 側(INotifyPropertyChanged)からの変更通知を受けて表示を更新する。
    /// Value セッターと違い、対象オブジェクトへの SetValue は行わない
    /// (自分がコミットした変更が跳ね返ってくるだけの無限ループを避けるため、
    /// 値が変わっていなければ何もしない)。
    /// </summary>
    internal void RefreshFromSource()
    {
        // Collection 項目は Value 文字列を表示に使わないため、ToString() 比較による更新は行わない
        // (コレクション内容の追従は CollectionChanged 経由で行う)。
        if (EditKind == PropertyEditKind.Collection)
        {
            return;
        }

        if (_property is null || _target is null)
        {
            return;
        }

        string newValue;
        try
        {
            newValue = _property.GetValue(_target)?.ToString() ?? "null";
        }
        catch (Exception ex)
        {
            newValue = $"<error: {ex.Message}>";
        }

        if (_value == newValue)
        {
            // ここへ来るのは稀（[ObservableProperty] は等値では通知しないので、
            // 通知が来て文字列が一致するのは setter 側の丸めで ToString() が
            // 揃った場合など）。それでも畳む ── 表示中の値と食い違うエラーが
            // 残る方が害が大きい。
            ClearError();
            return;
        }

        _value = newValue;
        ClearError();
        RaiseValueRelatedChanged();
    }

    /// <summary>エラー表示を畳む。<b>コミットを試みた経路は必ずここか <see cref="SetError"/> を通す。</b></summary>
    private void ClearError()
    {
        HasError = false;
        ErrorMessage = null;
    }

    /// <inheritdoc cref="ClearError"/>
    private void SetError(string message)
    {
        HasError = true;
        ErrorMessage = message;
    }

    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (_hasError == value) return;
            _hasError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ErrorVisibility));
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value) return;
            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public Visibility ErrorVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseValueRelatedChanged()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(BoolValue));
        // Choice 項目は SelectedChoice 経由で表示するので、これを忘れると
        // 外部（CLI・別画面）からの変更がコンボボックスに反映されない。
        OnPropertyChanged(nameof(SelectedChoice));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // ---- Collection 項目のライフサイクル管理 ----

    /// <summary>
    /// コレクションの CollectionChanged 購読を開始し、Elements を現在の内容で構築する。
    /// PropertyGridView.AttachSource から呼ばれる。
    /// choiceProvider を渡すと、要素のネストしたプロパティでも ChoiceListAttribute が
    /// コンボボックスになる（渡さないとテキスト編集に落ちる）。
    /// </summary>
    internal void AttachCollection(
        DispatcherQueue dispatcherQueue,
        Func<string, string, IPropertyAccess?, IReadOnlyList<PropertyGridChoice>>? choiceProvider = null)
    {
        if (EditKind != PropertyEditKind.Collection || _collection is null || Elements is null)
        {
            return;
        }

        DetachCollection();
        _dispatcherQueue = dispatcherQueue;
        _choiceProvider = choiceProvider;
        RebuildElements();

        if (_collection is INotifyCollectionChanged notifying)
        {
            // 長寿命のコレクション(設定オブジェクト保有)がこの項目を GC 不能にしないよう弱参照で購読する。
            _collectionListener = new WeakCollectionChangedListener(this, notifying);
        }
    }

    /// <summary>CollectionChanged の購読解除と、全要素 VM の購読解除を行う。</summary>
    internal void DetachCollection()
    {
        _collectionListener?.Detach();
        _collectionListener = null;

        if (Elements is not null)
        {
            foreach (PropertyGridCollectionElement element in Elements)
            {
                element.Detach();
            }
            Elements.Clear();
        }
    }

    /// <summary>Elements を現在のコレクション内容から作り直す。</summary>
    private void RebuildElements()
    {
        if (_collection is null || Elements is null || _dispatcherQueue is null)
        {
            return;
        }

        foreach (PropertyGridCollectionElement element in Elements)
        {
            element.Detach();
        }
        Elements.Clear();

        foreach (object? element in _collection)
        {
            if (element is not null)
            {
                Elements.Add(new PropertyGridCollectionElement(element, _collection, _dispatcherQueue, OnElementExpanded, _choiceProvider));
            }
        }

        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>
    /// アコーディオン動作: ある要素が展開されたら、同じコレクション内の他要素を折りたたむ。
    /// </summary>
    private void OnElementExpanded(PropertyGridCollectionElement expanded)
    {
        if (Elements is null)
        {
            return;
        }

        foreach (PropertyGridCollectionElement element in Elements)
        {
            if (!ReferenceEquals(element, expanded))
            {
                element.IsExpanded = false;
            }
        }
    }

    /// <summary>新規要素を生成してコレクション末尾に追加する(Add ボタン)。</summary>
    private void AddNewItem()
    {
        if (_collection is null || _collectionItemType is null)
        {
            return;
        }

        _collection.Add(Activator.CreateInstance(_collectionItemType));
    }

    /// <summary>
    /// コレクション側からの変更通知を Elements へ差分適用する。
    /// 別スレッドから発火される可能性があるため DispatcherQueue でマーシャリングする。
    /// </summary>
    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_dispatcherQueue is null || _collection is null || Elements is null)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => OnSourceCollectionChanged(sender, e));
            return;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is not null)
                {
                    for (int i = 0; i < e.NewItems.Count; i++)
                    {
                        if (e.NewItems[i] is object element)
                        {
                            Elements.Insert(e.NewStartingIndex + i,
                                new PropertyGridCollectionElement(element, _collection, _dispatcherQueue, OnElementExpanded, _choiceProvider));
                        }
                    }
                }
                break;

            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                {
                    for (int i = 0; i < e.OldItems.Count; i++)
                    {
                        Elements[e.OldStartingIndex].Detach();
                        Elements.RemoveAt(e.OldStartingIndex);
                    }
                }
                break;

            case NotifyCollectionChangedAction.Move:
                Elements.Move(e.OldStartingIndex, e.NewStartingIndex);
                break;

            case NotifyCollectionChangedAction.Replace:
                // 子要素の PropertyChanged を通知するための同一インスタンスの self-assign Replace は無視する
                // (要素 VM を再構築すると Expander の展開状態や編集中フォーカスが失われるため)。
                if (e.OldItems is not null && e.NewItems is not null
                    && e.OldItems.Cast<object>().SequenceEqual(e.NewItems.Cast<object>()))
                {
                    return;
                }
                if (e.OldItems is not null && e.NewItems is not null)
                {
                    for (int i = 0; i < e.OldItems.Count; i++)
                    {
                        Elements[e.NewStartingIndex + i].Detach();
                        if (e.NewItems[i] is object element)
                        {
                            Elements[e.NewStartingIndex + i] =
                                new PropertyGridCollectionElement(element, _collection, _dispatcherQueue, OnElementExpanded, _choiceProvider);
                        }
                    }
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                RebuildElements();
                break;
        }

        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>
    /// INotifyCollectionChanged.CollectionChanged を弱参照モデルで中継するリスナー。
    /// PropertyGridView.WeakPropertyChangedListener と同じ理由(長寿命 source によるリーク防止)で、
    /// source には強参照で購読しつつ、項目へは WeakReference で保持する。
    /// </summary>
    private sealed class WeakCollectionChangedListener
    {
        private readonly WeakReference<PropertyGridItem> _targetRef;
        private readonly INotifyCollectionChanged _source;

        public WeakCollectionChangedListener(PropertyGridItem target, INotifyCollectionChanged source)
        {
            _targetRef = new WeakReference<PropertyGridItem>(target);
            _source = source;
            _source.CollectionChanged += OnCollectionChanged;
        }

        /// <summary>明示的な購読解除(SelectedObject の切り替え/コントロールの Unload 時に呼ぶ)。</summary>
        public void Detach()
        {
            _source.CollectionChanged -= OnCollectionChanged;
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_targetRef.TryGetTarget(out PropertyGridItem? target))
            {
                target.OnSourceCollectionChanged(sender, e);
            }
            else
            {
                // target が既に回収されている: このリスナーも不要になったので自分で購読解除する。
                _source.CollectionChanged -= OnCollectionChanged;
            }
        }
    }
}
