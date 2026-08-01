using CommunityToolkit.WinUI.Behaviors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.ViewModels;
using System;
using System.Collections.Specialized;
using System.Windows.Input;

namespace ProcessRecorderApp.Behaviors;

/// <summary>
/// NavigationView と Recorder コレクションを連携させる Behavior。
/// - PreviewItem 配下に Recorder サブメニュー（+ 末尾「Add recorder」）を生成し、
///   コレクション変更を同一インデックスでミラーリングする
/// - 選択変更を SelectedRecorder / SelectedSection へ反映する（TwoWay バインド想定）
/// - 「Add recorder」項目のクリックで AddRecorderCommand を実行する
/// </summary>
public partial class RecorderNavViewBehavior : BehaviorBase<NavigationView>
{
    /// <summary>サブメニューのソースとなる Recorder コレクション。</summary>
    public static readonly DependencyProperty RecordersProperty
        = DependencyProperty.Register(nameof(Recorders), typeof(GstEventEventRecorderCollectionViewModel), typeof(RecorderNavViewBehavior),
            new PropertyMetadata(null, (d, _) => ((RecorderNavViewBehavior)d).TryBuildMenu()));
    public GstEventEventRecorderCollectionViewModel? Recorders
    {
        get => (GstEventEventRecorderCollectionViewModel?)GetValue(RecordersProperty);
        set => SetValue(RecordersProperty, value);
    }

    /// <summary>Recorder 項目の表示テンプレート（NavigationViewItem の ContentTemplate に適用）。</summary>
    public static readonly DependencyProperty RecorderTemplateProperty
        = DependencyProperty.Register(nameof(RecorderTemplate), typeof(DataTemplate), typeof(RecorderNavViewBehavior),
            new PropertyMetadata(null));
    public DataTemplate? RecorderTemplate
    {
        [WinRT.DynamicWindowsRuntimeCast(typeof(DataTemplate))]
        get => (DataTemplate?)GetValue(RecorderTemplateProperty);
        set => SetValue(RecorderTemplateProperty, value);
    }

    /// <summary>「Add recorder」項目クリック時に実行するコマンド。</summary>
    public static readonly DependencyProperty AddRecorderCommandProperty
        = DependencyProperty.Register(nameof(AddRecorderCommand), typeof(ICommand), typeof(RecorderNavViewBehavior),
            new PropertyMetadata(null));
    public ICommand? AddRecorderCommand
    {
        get => (ICommand?)GetValue(AddRecorderCommandProperty);
        set => SetValue(AddRecorderCommandProperty, value);
    }

    /// <summary>選択中の Recorder（TwoWay バインド想定）。VM 側の変更は NavigationView の選択状態へ反映する。</summary>
    public static readonly DependencyProperty SelectedRecorderProperty
        = DependencyProperty.Register(nameof(SelectedRecorder), typeof(GstEventRecorderViewModel), typeof(RecorderNavViewBehavior),
            new PropertyMetadata(null, (d, _) => ((RecorderNavViewBehavior)d).ApplySelectedRecorder()));
    public GstEventRecorderViewModel? SelectedRecorder
    {
        get => (GstEventRecorderViewModel?)GetValue(SelectedRecorderProperty);
        set => SetValue(SelectedRecorderProperty, value);
    }

    /// <summary>選択中の画面区分（TwoWay バインド想定。本 Behavior からの通知が主目的）。</summary>
    public static readonly DependencyProperty SelectedSectionProperty
        = DependencyProperty.Register(nameof(SelectedSection), typeof(MainSection), typeof(RecorderNavViewBehavior),
            new PropertyMetadata(MainSection.Preview));
    public MainSection SelectedSection
    {
        get => (MainSection)GetValue(SelectedSectionProperty);
        set => SetValue(SelectedSectionProperty, value);
    }

    /// <summary>Recorder サブメニューを保持する親項目（Preview）。</summary>
    public static readonly DependencyProperty PreviewItemProperty
        = DependencyProperty.Register(nameof(PreviewItem), typeof(NavigationViewItem), typeof(RecorderNavViewBehavior),
            new PropertyMetadata(null, (d, _) => ((RecorderNavViewBehavior)d).TryBuildMenu()));
    public NavigationViewItem? PreviewItem
    {
        [WinRT.DynamicWindowsRuntimeCast(typeof(NavigationViewItem))]
        get => (NavigationViewItem?)GetValue(PreviewItemProperty);
        set => SetValue(PreviewItemProperty, value);
    }

    /// <summary>Log 画面へ切り替えるトップレベル項目。</summary>
    public static readonly DependencyProperty LogItemProperty
        = DependencyProperty.Register(nameof(LogItem), typeof(NavigationViewItem), typeof(RecorderNavViewBehavior),
            new PropertyMetadata(null));
    public NavigationViewItem? LogItem
    {
        [WinRT.DynamicWindowsRuntimeCast(typeof(NavigationViewItem))]
        get => (NavigationViewItem?)GetValue(LogItemProperty);
        set => SetValue(LogItemProperty, value);
    }

    /// <summary>Variables 画面へ切り替えるトップレベル項目。</summary>
    public static readonly DependencyProperty VariablesItemProperty
        = DependencyProperty.Register(nameof(VariablesItem), typeof(NavigationViewItem), typeof(RecorderNavViewBehavior),
            new PropertyMetadata(null));
    public NavigationViewItem? VariablesItem
    {
        [WinRT.DynamicWindowsRuntimeCast(typeof(NavigationViewItem))]
        get => (NavigationViewItem?)GetValue(VariablesItemProperty);
        set => SetValue(VariablesItemProperty, value);
    }

    /// <summary>
    /// メニュー（サブメニューのフライアウト、または Left 系モードのオーバーレイペイン）が開いているか
    /// （TwoWay バインド想定）。ポップアップは airspace 問題でネイティブウィンドウに隠されるため、
    /// 開いている間はネイティブ窓を退避させる用途で公開する。
    /// </summary>
    public static readonly DependencyProperty IsMenuOpenProperty
        = DependencyProperty.Register(nameof(IsMenuOpen), typeof(bool), typeof(RecorderNavViewBehavior),
            new PropertyMetadata(false));
    public bool IsMenuOpen
    {
        get => (bool)GetValue(IsMenuOpenProperty);
        set => SetValue(IsMenuOpenProperty, value);
    }

    /// <summary>Preview サブメニュー末尾の「Add recorder」項目。</summary>
    private NavigationViewItem? _addRecorderNavItem;
    /// <summary>CollectionChanged 購読中のコレクション（解除用）。</summary>
    private GstEventEventRecorderCollectionViewModel? _subscribedRecorders;

    /// <summary>
    /// 生成済みの Recorder 項目と、その自動化名を追随させるための <c>PropertyChanged</c> 購読。
    ///
    /// <para>
    /// <b>購読側を影リストとして別に持つ。</b> 解除のときに <see cref="Recorders"/> を列挙すると、
    /// <c>Reset</c> ではその時点でコレクションが空なので <c>foreach</c> が1度も回らず、
    /// <b>解除に失敗して購読が残る</b> ── 典型的な形の解除漏れである。
    /// Recorder はプロセス寿命のオブジェクトなので、ページ寿命のこの Behavior が
    /// 購読を残すとページごと参照が生き続ける。
    /// </para>
    /// </summary>
    private readonly Dictionary<GstEventRecorderViewModel, NavigationViewItem> _recorderNavItems = [];

    protected override bool Initialize()
    {
        var result = base.Initialize();

        if (AssociatedObject is not null)
        {
            AssociatedObject.SelectionChanged += NavView_SelectionChanged;
            AssociatedObject.ItemInvoked += NavView_ItemInvoked;
            AssociatedObject.Expanding += NavView_Expanding;
            AssociatedObject.Collapsed += NavView_Collapsed;
            AssociatedObject.PaneOpening += NavView_PaneOpening;
            AssociatedObject.PaneClosed += NavView_PaneClosed;
            TryBuildMenu();
        }

        return result;
    }

    protected override bool Uninitialize()
    {
        if (AssociatedObject is not null)
        {
            AssociatedObject.SelectionChanged -= NavView_SelectionChanged;
            AssociatedObject.ItemInvoked -= NavView_ItemInvoked;
            AssociatedObject.Expanding -= NavView_Expanding;
            AssociatedObject.Collapsed -= NavView_Collapsed;
            AssociatedObject.PaneOpening -= NavView_PaneOpening;
            AssociatedObject.PaneClosed -= NavView_PaneClosed;
        }

        _subscribedRecorders?.CollectionChanged -= Recorders_CollectionChanged;
        _subscribedRecorders = null;
        // Recorder はプロセス寿命。ページ寿命のこの Behavior が購読を残すと、
        // 死んだページごと参照が生き続ける（docs/coverage-gaps.md
        // 「MainPage_Unloaded のデリゲート解除」「ナビ項目の購読解除」と同じ理由）。
        ForgetAllRecorderNavItems();

        return base.Uninitialize();
    }

    /// <summary>
    /// 必要なプロパティが揃ったタイミングでサブメニューを構築する。
    /// （Behavior のアタッチと x:Bind の評価順序は保証されないため、DP 変更の都度呼び出す）
    /// </summary>
    private void TryBuildMenu()
    {
        if (AssociatedObject is null || PreviewItem is null || Recorders is null)
            return;
        if (_subscribedRecorders == Recorders && _addRecorderNavItem is not null)
            return; // 構築済み

        _subscribedRecorders?.CollectionChanged -= Recorders_CollectionChanged;

        _addRecorderNavItem = new NavigationViewItem
        {
            Content = Localization.GetString("Resources/MainPage_AddRecorder"),
            SelectsOnInvoked = false,
            Icon = new SymbolIcon(Symbol.Add),
        };
        // 表示文言はローカライズされるため、UI 自動化はロケール非依存の AutomationId で特定する。
        // Recorder 項目側には付けない ── 表示名はユーザーデータ（リネーム可能）であり、
        // 生成時に焼き込むと改名後に古い ID が残って却って壊れるため、名前で引く。
        AutomationProperties.SetAutomationId(_addRecorderNavItem, "AddRecorderNavItem");
        // 作り直す前に古い購読を必ず落とす（MenuItems.Clear() は購読を解除しない）。
        ForgetAllRecorderNavItems();
        PreviewItem.MenuItems.Clear();
        foreach (var vm in Recorders)
            PreviewItem.MenuItems.Add(CreateRecorderNavItem(vm));
        PreviewItem.MenuItems.Add(_addRecorderNavItem);

        _subscribedRecorders = Recorders;
        Recorders.CollectionChanged += Recorders_CollectionChanged;

        ApplySelectedRecorder();
    }

    /// <summary>
    /// Recorder 1件ぶんのナビ項目を作る。
    ///
    /// <para>
    /// <b>自動化名を明示する。</b> <see cref="NavigationViewItem"/> の既定の自動化名は
    /// <c>Content.ToString()</c> で、ここでの <c>Content</c> は ViewModel そのものなので、
    /// 明示しないと支援技術にも UI 自動化にも
    /// <c>ProcessRecorderApp.ViewModels.GstEventRecorderViewModel</c> としか見えない
    /// （<c>ContentTemplate</c> が表示している文字列は自動化名には反映されない）。
    /// 表示名はユーザーデータで改名できるため、<b>改名に追随させる</b>。
    /// </para>
    /// </summary>
    private NavigationViewItem CreateRecorderNavItem(GstEventRecorderViewModel recorder)
    {
        var item = new NavigationViewItem
        {
            Content = recorder,
            ContentTemplate = RecorderTemplate,
        };
        // 個々の Recorder 項目は「名前で引く」（AutomationId は焼き込まない ── 改名で古い値が残る）。
        // どれが Recorder 項目かを機械的に選り分けられるよう、種別だけを AutomationId で示す。
        AutomationProperties.SetAutomationId(item, RecorderNavItemAutomationId);
        AutomationProperties.SetName(item, recorder.Name);

        recorder.PropertyChanged += Recorder_PropertyChanged;
        _recorderNavItems[recorder] = item;
        return item;
    }

    /// <summary>Recorder 項目であることを示す AutomationId（個体の識別は自動化名で行う）。</summary>
    internal const string RecorderNavItemAutomationId = "RecorderNavItem";

    /// <summary>
    /// 改名を自動化名へ反映する。
    /// <see cref="GstEventRecorderViewModel"/> の <c>PropertyChanged</c> は UI スレッドで発火する
    /// （モデル発の変更は <c>Model_PropertyChanged</c> がディスパッチャへマーシャリングする）ので、
    /// ここから XAML の添付プロパティを触ってよい。
    /// </summary>
    private void Recorder_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && e.PropertyName != nameof(GstEventRecorderViewModel.Name))
            return;
        if (sender is GstEventRecorderViewModel recorder && _recorderNavItems.TryGetValue(recorder, out var item))
            AutomationProperties.SetName(item, recorder.Name);
    }

    /// <summary>1件ぶんの購読を解除する（項目が消えたとき）。</summary>
    private void ForgetRecorderNavItem(GstEventRecorderViewModel? recorder)
    {
        if (recorder is null)
            return;
        recorder.PropertyChanged -= Recorder_PropertyChanged;
        _recorderNavItems.Remove(recorder);
    }

    /// <summary>全件ぶんの購読を解除する（作り直し・Reset・Uninitialize）。</summary>
    private void ForgetAllRecorderNavItems()
    {
        foreach (var recorder in _recorderNavItems.Keys.ToArray())
            recorder.PropertyChanged -= Recorder_PropertyChanged;
        _recorderNavItems.Clear();
    }

    [WinRT.DynamicWindowsRuntimeCast(typeof(NavigationViewItem))]
    private NavigationViewItem? FindRecorderNavItem(GstEventRecorderViewModel? recorder)
    {
        if (recorder is null || PreviewItem is null)
            return null;
        foreach (var item in PreviewItem.MenuItems)
            if (item is NavigationViewItem nvi && ReferenceEquals(nvi.Content, recorder))
                return nvi;
        return null;
    }

    private void Recorders_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (PreviewItem is null)
            return;

        // 末尾の「Add recorder」項目を維持しつつ、Recorders の変更を同一インデックスで反映する
        var menuItems = PreviewItem.MenuItems;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                menuItems.Insert(e.NewStartingIndex, CreateRecorderNavItem((GstEventRecorderViewModel)e.NewItems?[0]!));
                break;
            case NotifyCollectionChangedAction.Remove:
                menuItems.RemoveAt(e.OldStartingIndex);
                ForgetRecorderNavItem(e.OldItems?[0] as GstEventRecorderViewModel);
                break;
            case NotifyCollectionChangedAction.Replace:
                // **先に解除してから作る。** このコレクションには
                // **同一インスタンスの Replace** が流れうる（`GstControllerViewModel` の
                // CollectionChanged が、まさにそのケースを `SequenceEqual` で弾いている）。
                // 逆順にすると、同一インスタンスのときに**今張ったばかりの購読を外して**しまい、
                // 以後その項目が改名に追随しなくなる ── 例外が出ないので静かに壊れる。
                //
                // なお **改名の経路ではこの Replace は起きない**ことを実測で確認している
                // （購読を外す注入で L3 が赤くなる＝改名の反映は下の PropertyChanged 経由で、
                //  項目の作り直しではない）。この順序は**予防**であって、
                // 現時点でこれを落とせるテストは無い。
                ForgetRecorderNavItem(e.OldItems?[0] as GstEventRecorderViewModel);
                menuItems[e.NewStartingIndex] = CreateRecorderNavItem((GstEventRecorderViewModel)e.NewItems?[0]!);
                break;
            case NotifyCollectionChangedAction.Move:
                var moved = menuItems[e.OldStartingIndex];
                menuItems.RemoveAt(e.OldStartingIndex);
                menuItems.Insert(e.NewStartingIndex, moved);
                break;
            case NotifyCollectionChangedAction.Reset:
                // Reset の時点で Recorders は空になりうる。解除は影リスト側から行う
                // （コレクションを列挙する形にすると1件も解除できない ── #14 と同じ罠）。
                ForgetAllRecorderNavItems();
                while (menuItems.Count > 1)
                    menuItems.RemoveAt(0);
                if (Recorders is not null)
                    foreach (var vm in Recorders)
                        menuItems.Insert(menuItems.Count - 1, CreateRecorderNavItem(vm));
                break;
        }
    }

    /// <summary>VM 側の選択変更（自動先頭選択、追加/削除後の選択移動）を NavigationView に反映する。</summary>
    private void ApplySelectedRecorder()
    {
        if (AssociatedObject is null)
            return;
        var navItem = FindRecorderNavItem(SelectedRecorder);
        if (!ReferenceEquals(AssociatedObject.SelectedItem, navItem))
            AssociatedObject.SelectedItem = navItem;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            SelectedSection = MainSection.Settings;
        }
        else if (args.SelectedItem is NavigationViewItem { Content: GstEventRecorderViewModel recorder })
        {
            if (SelectedRecorder != recorder)
                SelectedRecorder = recorder;
            SelectedSection = MainSection.Preview;
        }
        else if (ReferenceEquals(args.SelectedItem, LogItem))
        {
            SelectedSection = MainSection.Log;
        }
        else if (ReferenceEquals(args.SelectedItem, VariablesItem))
        {
            SelectedSection = MainSection.Variables;
        }
        else
        {
            // 選択解除（Recorder 全削除時など）はプレビュー画面のままとする
            SelectedSection = MainSection.Preview;
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // Preview サブメニュー末尾の「Add recorder」項目
        if (ReferenceEquals(args.InvokedItemContainer, _addRecorderNavItem))
        {
            AddRecorderCommand?.Execute(null);
            PreviewItem?.IsExpanded = false;
        }
    }

    // サブメニューのフライアウト開閉（Top モード）とオーバーレイペイン開閉（Left 系モード）を
    // IsMenuOpen に反映する
    private void NavView_Expanding(NavigationView sender, NavigationViewItemExpandingEventArgs args) => IsMenuOpen = true;
    private void NavView_Collapsed(NavigationView sender, NavigationViewItemCollapsedEventArgs args) => IsMenuOpen = false;
    private void NavView_PaneOpening(NavigationView sender, object args) => IsMenuOpen = true;
    private void NavView_PaneClosed(NavigationView sender, object args) => IsMenuOpen = false;
}
