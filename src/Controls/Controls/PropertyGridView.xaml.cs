using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.Controls;

/// <summary>
/// プロパティ名/値を一覧表示し、値の編集もできる PropertyGrid 風コントロール。
///
/// SelectedObject に IPropertyAccess を実装したオブジェクトを設定すると、
/// そのオブジェクト自身が GetProperties() で返した PropertyInfo をもとに一覧を構築する。
/// 値の編集は各 PropertyGridItem が保持する PropertyInfo.SetValue を通じて
/// 対象オブジェクトへ直接コミットされる。
///
/// 「型からどうプロパティを収集するか」はコントロールの外（IPropertyAccess の実装側）の責務であり、
/// このコントロール自体は GetType() 等による動的な型解決を一切行わないため、
/// x:Bind と合わせて Native AOT / トリミングと親和性が高い。
/// </summary>
public sealed partial class PropertyGridView : UserControl
{
    public static readonly DependencyProperty SelectedObjectProperty =
        DependencyProperty.Register(
            nameof(SelectedObject),
            typeof(IPropertyAccess),
            typeof(PropertyGridView),
            new PropertyMetadata(null, OnSelectedObjectChanged));

    /// <summary>表示・編集対象のオブジェクト。プロパティ列挙は対象オブジェクト自身に委譲される。</summary>
    public IPropertyAccess? SelectedObject
    {
        get => (IPropertyAccess?)GetValue(SelectedObjectProperty);
        set => SetValue(SelectedObjectProperty, value);
    }

    /// <summary>
    /// ValueBuilderAttribute が付いたプロパティの「…」ボタン押下時に呼ばれる、ホスト提供の値ビルダー。
    /// 引数は (ビルダー識別キー, 現在値)。戻り値が非 null なら新しい値としてコミットし、null(キャンセル)なら変更しない。
    /// GStreamer 等の特定ドメイン依存はこのデリゲートの実装側(ホスト)に閉じる。
    /// </summary>
    public Func<string, string, System.Threading.Tasks.Task<string?>>? ValueBuilder { get; set; }

    /// <summary>
    /// <c>ChoiceListAttribute</c> が付いたプロパティの選択肢を供給する、ホスト提供のデリゲート。
    /// 引数は (選択肢一覧の識別キー, 現在値)。
    ///
    /// <para>
    /// <b>現在値を渡すのは、一覧に無い値を失わせないため。</b>
    /// 古い名前・打ち間違い・手で編集された設定ファイルの値がそのままだと、
    /// コンボボックスは<b>空を表示し、他の行に触れた瞬間にその値が消える</b>
    /// ── しかも編集不可なので打ち直せない。プロバイダーは現在値を見て、
    /// 必要なら「不明」印つきの選択肢として差し込める。
    /// </para>
    /// <para>
    /// <b>未設定（null）なら、その属性が付いていても通常のテキスト編集になる。</b>
    /// 選択肢を出せないのに編集不可のコンボボックスを出すと、
    /// <b>利用者が値を一切変更できなくなる</b>ため。
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>設定されたら、既に組み立て済みの項目を作り直す。</b>
    /// <see cref="ValueBuilder"/> と違ってこちらは<b>項目の生成時に消費される</b>ので、
    /// 「後から設定する」と最初の1回に間に合わない。
    ///
    /// <para>
    /// <b>これは実際に踏んだ。</b> <c>SelectedObject="{x:Bind ...}"</c> は
    /// <c>Mode</c> 省略時 <c>OneTime</c> で、<c>InitializeComponent()</c>（＝コンストラクター）で
    /// 評価される。ホストが <c>Loaded</c> でこのデリゲートを設定すると
    /// <b>項目の組み立ての方が先に終わっており、コンボボックスにならずテキスト編集のまま</b>になる
    /// ── しかも例外も警告も出ないので、画面を見るまで気付けない。
    /// </para>
    /// <para>
    /// ホスト側の設定順に依存させないため、<b>コントロール側で作り直す</b>ことにした。
    /// </para>
    /// <para>
    /// <b>同じ値かどうかで早期 return してはいけない。</b> ホストは
    /// 「選択肢の中身が変わったので作り直したい」ときに<b>同じメソッドグループを代入し直す</b>
    /// （選択肢はレコーダー名やトリガ定義を項目の生成時に焼き込むため、
    /// 作り直さないと編集前の内容が残る）。C# のデリゲートの <c>==</c> は
    /// (ターゲット, メソッド) の<b>値比較</b>なので、同一インスタンスの同じメソッドを
    /// 代入し直すと必ず等しくなり、<b>2 回目以降の代入がすべて無視される</b>。
    /// <b>代入は常に作り直す</b>という契約にしてある。
    /// </para>
    /// </remarks>
    public Func<string, string, IPropertyAccess?, IReadOnlyList<PropertyGridChoice>>? ChoiceProvider
    {
        get => _choiceProvider;
        set
        {
            _choiceProvider = value;
            if (SelectedObject is { } source)
                RebuildGroups(source);
        }
    }

    private Func<string, string, IPropertyAccess?, IReadOnlyList<PropertyGridChoice>>? _choiceProvider;

    private WeakPropertyChangedListener? _weakListener;
    private Dictionary<string, List<PropertyGridItem>>? _itemsByPropertyName;
    private List<PropertyGridItem> _collectionItems = [];

    public PropertyGridView()
    {
        this.InitializeComponent();
        this.Unloaded += (_, _) => DetachSource();
        // Unloaded で全購読を落とすので、再 Loaded では張り直す。
        // **現在のこのアプリでは発火しない保険**（画面切替は Visibility で行うので
        // Loaded/Unloaded は循環せず、ページ破棄はプロセス終了と同時）。
        // TabView や Frame ナビゲーション配下へ置いた瞬間に効き始め、無いと
        // 一度外れた画面が「SelectedObject の変更が反映されず、コレクション行が空のまま」
        // という形で静かに死ぬ（AttachSource は先頭で DetachSource するので二重購読にはならない）。
        this.Loaded += (_, _) =>
        {
            if (SelectedObject is { } source)
                AttachSource(source);
        };
    }

    private static void OnSelectedObjectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PropertyGridView)d;
        self.AttachSource((IPropertyAccess?)e.NewValue);
    }

    private void AttachSource(IPropertyAccess? source)
    {
        DetachSource();

        RebuildGroups(source);

        if (source is INotifyPropertyChanged notifying)
        {
            // source -> listener は強参照、listener -> this(PropertyGridView) は弱参照にすることで、
            // 長寿命の source(例: シングルトンの設定オブジェクト)が this を GC 不能にし続けるのを防ぐ。
            _weakListener = new WeakPropertyChangedListener(this, notifying);
        }
    }

    private void DetachSource()
    {
        _weakListener?.Detach();
        _weakListener = null;

        DetachCollectionItems();
    }

    /// <summary>
    /// 組み立て済みの Collection 項目の購読（コレクションの <c>CollectionChanged</c> と
    /// 各要素の <c>INotifyPropertyChanged</c>）を解除する。
    ///
    /// <para>
    /// <b><see cref="RebuildGroups"/> は必ずこれを通ってから項目を作り直すこと。</b>
    /// 外すと旧項目が長寿命のコレクションに購読を残したまま生き続ける ──
    /// 参照の鎖が「設定オブジェクトの要素 → <c>PropertyChanged</c> →
    /// <see cref="PropertyGridCollectionElement"/> → <c>_onExpanded</c> →
    /// <see cref="PropertyGridItem"/>」となるため、
    /// <c>WeakCollectionChangedListener</c> の「ターゲットが GC 済みなら自分で解除する」も
    /// <b>永久に発動しない</b>。
    /// </para>
    /// </summary>
    private void DetachCollectionItems()
    {
        foreach (PropertyGridItem item in _collectionItems)
        {
            item.DetachCollection();
        }
        _collectionItems = [];
    }

    /// <summary>
    /// SelectedObject 側からの変更通知。別スレッドから発火される可能性があるため、
    /// UIスレッドでなければ DispatcherQueue 経由でマーシャリングする。
    /// </summary>
    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnSourcePropertyChanged(sender, e));
            return;
        }

        if (_itemsByPropertyName is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(e.PropertyName))
        {
            // 空文字/null は「すべてのプロパティが変更された」という慣習的な意味として扱う。
            foreach (List<PropertyGridItem> items in _itemsByPropertyName.Values)
            {
                foreach (PropertyGridItem item in items)
                {
                    item.RefreshFromSource();
                }
            }
            return;
        }

        if (_itemsByPropertyName.TryGetValue(e.PropertyName, out List<PropertyGridItem>? matched))
        {
            foreach (PropertyGridItem item in matched)
            {
                item.RefreshFromSource();
            }
        }
    }

    /// <summary>
    /// INotifyPropertyChanged.PropertyChanged を弱参照モデルで中継するリスナー。
    ///
    /// 通常の "source.PropertyChanged += control.Handler" は source から control への
    /// 強参照チェーンを生む(デリゲートのターゲットが control を強く握るため)。
    /// これにより、source が長寿命(シングルトンの設定オブジェクト等)だと、
    /// 万一 Unloaded が発火しないケースがあっても control が回収されなくなる。
    ///
    /// このリスナーは source には強参照で購読しつつ、control へは WeakReference&lt;T&gt; で
    /// 保持する。イベント発火時に control が既に GC 済みであれば、その場で自分自身を
    /// source から購読解除して自然消滅する。
    /// </summary>
    private sealed class WeakPropertyChangedListener
    {
        private readonly WeakReference<PropertyGridView> _targetRef;
        private readonly INotifyPropertyChanged _source;

        public WeakPropertyChangedListener(PropertyGridView target, INotifyPropertyChanged source)
        {
            _targetRef = new WeakReference<PropertyGridView>(target);
            _source = source;
            _source.PropertyChanged += OnPropertyChanged;
        }

        /// <summary>明示的な購読解除(SelectedObject の切り替え/コントロールの Unload 時に呼ぶ)。</summary>
        public void Detach()
        {
            _source.PropertyChanged -= OnPropertyChanged;
        }

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_targetRef.TryGetTarget(out PropertyGridView? target))
            {
                target.OnSourcePropertyChanged(sender, e);
            }
            else
            {
                // target が既に回収されている: このリスナーも不要になったので自分で購読解除する。
                _source.PropertyChanged -= OnPropertyChanged;
            }
        }
    }


    [WinRT.DynamicWindowsRuntimeCast(typeof(CollectionViewSource))]
    private void RebuildGroups(IPropertyAccess? source)
    {
        // 作り直す前に、前回組み立てた Collection 項目の購読を必ず落とす。
        // AttachSource は DetachSource 経由で既に落としているが、ChoiceProvider の
        // 代入から来る経路にはそれが無い（詳細は DetachCollectionItems の注記）。
        DetachCollectionItems();

        List<PropertyGridItem> items = [.. BuildItems(source, ChoiceProvider)];

        _itemsByPropertyName = items
            .Where(i => i.SourcePropertyName is not null)
            .GroupBy(i => i.SourcePropertyName!)
            .ToDictionary(g => g.Key, g => g.ToList());

        List<PropertyGridItemGroup> groups = [.. items
            .GroupBy(i => i.Category)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PropertyGridItemGroup(g.Key, g))];

        var cvs = (CollectionViewSource)Resources["GroupedItemsSource"];
        cvs.Source = groups;

        // Collection 項目はコレクション本体の CollectionChanged を購読して要素表示を追従させる。
        _collectionItems = [.. items.Where(i => i.EditKind == PropertyEditKind.Collection)];
        foreach (PropertyGridItem item in _collectionItems)
        {
            item.AttachCollection(DispatcherQueue, ChoiceProvider);
        }

        // Builder 項目の「…」ボタン押下時にホスト提供の ValueBuilder へ橋渡しする処理を接続する。
        // ValueBuilder は都度この経路で参照するため、SelectedObject 切替後も最新の設定に追従する。
        foreach (PropertyGridItem item in items.Where(i => i.EditKind == PropertyEditKind.Builder))
        {
            item.BuilderRunner = RunBuilderAsync;
        }
    }

    /// <summary>
    /// 値の <c>TextBox</c> で Enter が押されたら、その場でコミットする。
    ///
    /// <para>
    /// <b><c>TwoWay</c> バインドは既定でフォーカスを失うまでソースへ書き戻さない。</b>
    /// そのため入力して Enter を押しただけでは<b>モデルに何も届かない</b> ──
    /// 画面には入力した値が出ているので、効いたように見えて効いていない。
    /// </para>
    /// <para>
    /// コミット後に <see cref="PropertyGridItem.Value"/> が丸めや差し戻しで変わると、
    /// その通知で <c>TextBox</c> の表示も追随する（＝押した結果がその場で見える）。
    /// フォーカスは動かさない ── Enter は「離れずに確定する」ための操作である。
    /// </para>
    /// </summary>
    // sender は WinRT のランタイムクラスなので、トリミングでメタデータが落ちないよう明示する
    // （同ファイルの RebuildGroups と同じ理由。無いと AOT/トリム解析がエラーにする）。
    [WinRT.DynamicWindowsRuntimeCast(typeof(TextBox))]
    private void ValueTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;

        if (sender is TextBox box && box.DataContext is PropertyGridItem item)
        {
            item.CommitFromEnter(box.Text);
            e.Handled = true;
        }
    }

    /// <summary>Builder 項目の「…」ボタンから呼ばれ、ValueBuilder の結果を項目へコミットする。</summary>
    private async System.Threading.Tasks.Task RunBuilderAsync(PropertyGridItem item)
    {
        if (ValueBuilder is null || item.BuilderKey is null)
        {
            return;
        }

        string? result = await ValueBuilder(item.BuilderKey, item.Value);
        if (result is not null)
        {
            // 読み取り専用の項目でも書き込む経路（`[ReadOnly(true)]` ＋ `[ValueBuilder]` の意味）。
            item.CommitFromBuilder(result);
        }
    }

    internal static IEnumerable<PropertyGridItem> BuildItems(
        IPropertyAccess? source,
        Func<string, string, IPropertyAccess?, IReadOnlyList<PropertyGridChoice>>? choiceProvider = null)
    {
        if (source is null)
        {
            yield break;
        }

        foreach (PropertyInfo prop in source.GetProperties())
        {
            // 標準の BrowsableAttribute(false) が付いているプロパティは表示しない。
            var browsable = prop.GetCustomAttribute<BrowsableAttribute>();
            if (browsable is { Browsable: false })
            {
                continue;
            }

            object? rawValue;
            bool getterFailed = false;
            try
            {
                rawValue = prop.GetValue(source);
            }
            catch (Exception ex)
            {
                rawValue = $"<error: {ex.Message}>";
                getterFailed = true;
            }

            // Category/Description の属性値は「リソースキー」として扱い、resources.pri から解決する。
            // 未登録のキー(または平文がそのまま指定された場合)は、属性値自体をそのまま表示にフォールバックする。
            string categoryKey = prop.GetCustomAttribute<CategoryAttribute>()?.Category ?? PropertyGridItem.DefaultCategoryKey;
            string category = Localization.GetStringOrFallback($"Resources/{categoryKey}", categoryKey);
            string? descriptionKey = prop.GetCustomAttribute<DescriptionAttribute>()?.Description;
            string? description = descriptionKey is null ? null : Localization.GetStringOrFallback($"Resources/{descriptionKey}", descriptionKey);

            bool readOnlyAttr = prop.GetCustomAttribute<ReadOnlyAttribute>()?.IsReadOnly ?? false;
            bool isReadOnly = !prop.CanWrite || readOnlyAttr || getterFailed;

            Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            PropertyEditKind editKind;
            IReadOnlyList<string>? enumValues = null;
            IReadOnlyList<PropertyGridChoice>? choices = null;
            ICommand? command = null;
            IList? collection = null;
            CollectionItemTypeAttribute? collectionItemTypeAttr = null;
            string? builderKey = null;

            if (typeof(ICommand).IsAssignableFrom(propType))
            {
                editKind = PropertyEditKind.Command;
                command = rawValue as ICommand;
                isReadOnly = true; // テキストとしての編集は行わない
            }
            else if (rawValue is INotifyCollectionChanged && rawValue is IList listValue)
            {
                // 変更通知つきコレクション(ObservableCollection<T> 等)は専用の展開表示で扱う。
                // 実行時値ベースの判定のため、ジェネリック型引数のリフレクション解決は不要(AOT 安全)。
                editKind = PropertyEditKind.Collection;
                collection = listValue;
                collectionItemTypeAttr = prop.GetCustomAttribute<CollectionItemTypeAttribute>();
                isReadOnly = true; // テキストとしての編集は行わない
            }
            else if (propType == typeof(bool))
            {
                editKind = PropertyEditKind.Bool;
            }
            else if (prop.GetCustomAttribute<ChoiceListAttribute>() is { } choiceAttr && choiceProvider is not null)
            {
                // **列挙型より先に見る。** 列挙型に付けると「値の名前をそのまま出す」代わりに
                // ホストが訳した表示名を出せる（保存されるのは名前のままなので設定ファイルの形は変わらない）。
                // 供給が空だったときの受け皿は下の 2 分岐（列挙型なら Enum・それ以外は Text）。
                // 選択肢はプロパティの型からは決まらない（その PC に何が在るか等の実行時の状況で
                // 決まる）ので、ホストのプロバイダーに現在値ごと訊く。
                // **対象オブジェクトも渡す** ── コレクションの要素では「どの行の選択肢か」で
                // 出すものが変わりうる（他のプロパティを見て注記を付ける等）。
                // **一覧が空なら Choice にしない** ── 空のコンボボックスを編集不可で出すと、
                // 利用者は値を見ることも直すこともできなくなる。テキスト編集に倒す方が安全。
                var provided = choiceProvider(choiceAttr.Key, rawValue?.ToString() ?? "", source);
                if (0 < provided.Count)
                {
                    editKind = PropertyEditKind.Choice;
                    choices = provided;
                }
                else if (propType.IsEnum)
                {
                    // **列挙型はテキストへ倒さない。** 供給が無いのは「訳が用意できなかった」だけで、
                    // 選択肢そのものは型から決まっている ── 自由入力にすると打ち間違いで
                    // 値を落とす道を新しく作ることになる。
                    editKind = PropertyEditKind.Enum;
                    enumValues = Enum.GetNames(propType);
                }
                else
                {
                    editKind = PropertyEditKind.Text;
                }
            }
            else if (propType.IsEnum)
            {
                editKind = PropertyEditKind.Enum;
                enumValues = Enum.GetNames(propType);
            }
            else if (prop.GetCustomAttribute<ValueBuilderAttribute>() is { } builderAttr)
            {
                // ValueBuilderAttribute が付いた文字列プロパティは、テキスト編集＋「…」ビルダー起動ボタンで扱う。
                editKind = PropertyEditKind.Builder;
                builderKey = builderAttr.Key;
            }
            else
            {
                editKind = PropertyEditKind.Text;
            }

            string displayName = prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                ?? (editKind == PropertyEditKind.Command && prop.Name.EndsWith("Command", StringComparison.InvariantCulture)
                ? prop.Name[0..^7] : prop.Name);

            yield return PropertyGridItem.Create(
                target: source,
                property: prop,
                displayName: displayName,
                category: category,
                description: description,
                isReadOnly: isReadOnly,
                editKind: editKind,
                enumValues: enumValues,
                // null の表示は空文字に統一する（PropertyGridItem の読み直しと同じ規約）
                initialValue: rawValue?.ToString() ?? "",
                command: command,
                collection: collection,
                collectionItemType: collectionItemTypeAttr?.ItemType,
                builderKey: builderKey,
                choices: choices);
        }
    }
}
