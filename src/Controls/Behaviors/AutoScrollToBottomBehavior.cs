using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Behaviors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;

namespace ProcessRecorderApp.Behaviors
{
    public class AutoScrollToBottomBehavior : BehaviorBase<ListView>
    {
        /// <summary>
        /// 自動スクロール（最下部追従）の ON/OFF 状態を表す添付プロパティ。
        /// ToggleButton などからの双方向バインドを想定。false → true に変わると最下部へスクロールする。
        /// タブ切替（Unloaded/Loaded）をまたいで ListView 上に状態が保持される。
        /// </summary>
        public static readonly DependencyProperty IsAutoScrollEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsAutoScrollEnabled",
                typeof(bool),
                typeof(AutoScrollToBottomBehavior),
                new PropertyMetadata(true));

        public static bool GetIsAutoScrollEnabled(DependencyObject obj) => (bool)obj.GetValue(IsAutoScrollEnabledProperty);

        public static void SetIsAutoScrollEnabled(DependencyObject obj, bool value) => obj.SetValue(IsAutoScrollEnabledProperty, value);

        /// <summary>
        /// ListView が実際に表示されているか（Visibility ベースのパネル切替用）。
        /// Collapsed な親配下では Loaded/SizeChanged が発火しない（または初回レイアウトから
        /// サイズが変わらず検知できない）ため、表示状態を外部から明示的にバインドしてもらい、
        /// true への遷移で最下部追従を行う。既定 true（TabView 型の Loaded/Unloaded 切替では不要）。
        /// </summary>
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(
                nameof(IsActive),
                typeof(bool),
                typeof(AutoScrollToBottomBehavior),
                new PropertyMetadata(true, (d, e) => ((AutoScrollToBottomBehavior)d).OnIsActiveChanged((bool)e.NewValue)));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        private void OnIsActiveChanged(bool value)
        {
            if (!value)
                return;

            // 表示に切り替わった直後はレイアウト未確定のため、レイアウト反映後に追従する
            AssociatedObject?.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    EnsureScrollViewer();
                    ScrollToBottom(jump: true);
                });
        }

        private ScrollViewer? _scrollViewer;
        private double _lastVerticalOffset = 0;      // 上方向スクロール（ユーザー操作）判定用の前回オフセット
        private bool _isScrollScheduled = false;     // 追従スクロールの多重スケジュール防止
        private long _itemsSourceCallbackToken = -1; // ItemsSource 変更コールバックの解除用トークン
        private long _autoScrollCallbackToken = -1;  // IsAutoScrollEnabled 変更コールバックの解除用トークン

        // 自動スクロール状態（実体は AssociatedObject 上の添付プロパティ）
        private bool IsAutoScrollEnabled
        {
            get => AssociatedObject != null && GetIsAutoScrollEnabled(AssociatedObject);
            set
            {
                if (AssociatedObject != null)
                {
                    SetIsAutoScrollEnabled(AssociatedObject, value);
                }
            }
        }

        public AutoScrollToBottomBehavior()
        {

        }

        private bool _isSetUp; // Setup/Teardown の冪等化用

        // ※ BehaviorBase(8.2.251219) は AssociatedObject の Loaded を待って Initialize を呼ぶが、
        //    Collapsed な親配下（Visibility ベースのパネル切替）では Loaded が発火せず
        //    Initialize が一度も呼ばれない。そのためアタッチ時点で自前のセットアップを行う。
        protected override void OnAttached()
        {
            base.OnAttached();
            Setup();
        }

        protected override void OnDetaching()
        {
            Teardown();
            base.OnDetaching();
        }

        protected override bool Initialize()
        {
            var result = base.Initialize();
            Setup();
            return result;
        }

        protected override bool Uninitialize()
        {
            Teardown();
            return base.Uninitialize();
        }

        private void Setup()
        {
            if (_isSetUp || AssociatedObject == null)
                return;
            _isSetUp = true;

            AssociatedObject.Loaded += AssociatedObject_Loaded;
            AssociatedObject.Unloaded += AssociatedObject_Unloaded;
            // Visibility ベースのパネル切替では Loaded/Unloaded が発火しないため、
            // 非表示→表示（高さ 0 → 正値）を SizeChanged で検知して再フック・追従する
            AssociatedObject.SizeChanged += AssociatedObject_SizeChanged;
            _itemsSourceCallbackToken = AssociatedObject.RegisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, OnItemsSourceChanged);
            _autoScrollCallbackToken = AssociatedObject.RegisterPropertyChangedCallback(IsAutoScrollEnabledProperty, OnIsAutoScrollEnabledChanged);
            SubscribeCollectionChanged(AssociatedObject.ItemsSource);

            // 既にロード済みの場合は Loaded イベントが発火しないため、直接実行する
            if (AssociatedObject.IsLoaded)
            {
                AssociatedObject_Loaded(AssociatedObject, new RoutedEventArgs());
            }
        }

        private void Teardown()
        {
            if (!_isSetUp)
                return;
            _isSetUp = false;

            if (AssociatedObject != null)
            {
                AssociatedObject.Loaded -= AssociatedObject_Loaded;
                AssociatedObject.Unloaded -= AssociatedObject_Unloaded;
                AssociatedObject.SizeChanged -= AssociatedObject_SizeChanged;
                if (_itemsSourceCallbackToken != -1)
                {
                    AssociatedObject.UnregisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, _itemsSourceCallbackToken);
                    _itemsSourceCallbackToken = -1;
                }
                if (_autoScrollCallbackToken != -1)
                {
                    AssociatedObject.UnregisterPropertyChangedCallback(IsAutoScrollEnabledProperty, _autoScrollCallbackToken);
                    _autoScrollCallbackToken = -1;
                }
            }
            UnsubscribeCollectionChanged();

            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
                _scrollViewer = null;
            }
        }

        private void AssociatedObject_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureScrollViewer();

            // 自動スクロール ON のときのみ最下部へ移動する（OFF のときはユーザーの位置を尊重）
            ScrollToBottom(jump: true);

            // 非表示中に増えた行の分は Loaded 直後のレイアウトに反映されておらず
            // 上のスクロールでは最下部まで届かないことがあるため、レイアウト確定後にもう一度実行する
            AssociatedObject?.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => ScrollToBottom(jump: true));
        }

        private void AssociatedObject_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Collapsed（高さ 0）から表示に切り替わったタイミングを Loaded 相当として扱う。
            // Collapsed 中はテンプレート未適用で ScrollViewer が取得できないため、ここで再フックする。
            if (e.PreviousSize.Height == 0 && e.NewSize.Height > 0)
            {
                AssociatedObject_Loaded(sender, new RoutedEventArgs());
            }
        }

        /// <summary>
        /// ListView 内部の ScrollViewer を（未解決なら）解決してイベントを購読する。
        /// Collapsed 状態の Loaded ではテンプレート未適用で取得できないため、遅延解決を許容する。
        /// </summary>
        private ScrollViewer? EnsureScrollViewer()
        {
            if (AssociatedObject != null && _scrollViewer == null)
            {
                _scrollViewer = AssociatedObject.FindDescendant<ScrollViewer>();
                _scrollViewer?.ViewChanged += ScrollViewer_ViewChanged;
                _lastVerticalOffset = _scrollViewer?.VerticalOffset ?? 0;
            }
            return _scrollViewer;
        }

        private void AssociatedObject_Unloaded(object sender, RoutedEventArgs e)
        {
            // 再表示時に再フックできるよう、参照を必ず破棄する
            // （IsAutoScrollEnabled はリセットせず、タブ切替をまたいで状態を保持する）
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
                _scrollViewer = null;
            }
        }

        // ⬇️ スクロール位置が変化した時に走るイベント
        private void ScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_scrollViewer == null) return;

            double offset = _scrollViewer.VerticalOffset;

            // 現在のスクロール位置が、最大スクロール可能位置（最下部）に近いか判定
            // 誤差を許容するため、あえて 5px のバッファを持たせています
            bool atBottom = offset >= _scrollViewer.ScrollableHeight - 5;

            if (atBottom)
            {
                // 最下部に到達したら追従を再開する
                IsAutoScrollEnabled = true;
            }
            else if (offset < _lastVerticalOffset)
            {
                // 上方向への移動（ユーザーのスクロール操作）のみ追従を解除する。
                // 行追加でコンテンツ高さが伸びた場合はオフセットが変わらないため、ここでは解除しない。
                IsAutoScrollEnabled = false;
            }

            _lastVerticalOffset = offset;
        }

        private void OnIsAutoScrollEnabledChanged(Microsoft.UI.Xaml.DependencyObject sender, Microsoft.UI.Xaml.DependencyProperty dp)
        {
            // トグルボタン操作などで ON に切り替わったら最下部までスクロールする
            if (IsAutoScrollEnabled)
            {
                ScrollToBottom(jump: true);
            }
        }

        /// <summary>
        /// 実際に購読中のコレクション。<b>「現在の ItemsSource」から解除してはいけない</b> ──
        /// <c>RegisterPropertyChangedCallback</c> のコールバックは値が差し替わった<b>後</b>に
        /// 呼ばれるため、現在値に対する解除は未購読の新コレクションへの no-op で、
        /// 旧コレクションの購読が残る（旧側の Add で無関係なスクロールが走り、
        /// 旧コレクションが生きている限り behavior と ListView が回収されない）。
        /// </summary>
        private INotifyCollectionChanged? _subscribedCollection;

        private void OnItemsSourceChanged(Microsoft.UI.Xaml.DependencyObject sender, Microsoft.UI.Xaml.DependencyProperty dp)
        {
            UnsubscribeCollectionChanged();
            SubscribeCollectionChanged(AssociatedObject?.ItemsSource);
        }

        private void SubscribeCollectionChanged(object? itemsSource)
        {
            if (itemsSource is INotifyCollectionChanged collection)
            {
                collection.CollectionChanged += Collection_CollectionChanged;
                _subscribedCollection = collection;
            }
        }

        private void UnsubscribeCollectionChanged()
        {
            if (_subscribedCollection is { } collection)
            {
                collection.CollectionChanged -= Collection_CollectionChanged;
                _subscribedCollection = null;
            }
        }

        private void Collection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                ScheduleScrollToBottom(); // アイテム追加時はレイアウト反映後にまとめて追従する
            }
        }

        /// <summary>
        /// 追従スクロールをレイアウト反映後に1回だけ実行するようスケジュールする。
        /// （追加のたびに同期でスクロールすると再レイアウトが多発し、表示がちらつくため）
        /// </summary>
        private void ScheduleScrollToBottom()
        {
            if (_isScrollScheduled || AssociatedObject == null) return;

            _isScrollScheduled = true;

            AssociatedObject.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    _isScrollScheduled = false;
                    ScrollToBottom(jump: false);
                });
        }

        /// <summary>
        /// 自動スクロールが ON の場合に最下部へスクロールする。
        /// </summary>
        /// <param name="jump">
        /// true: タブ復帰時など大きく移動する場合。ScrollIntoView で末尾のコンテナを
        /// 実体化させてから移動する。false: 追記への軽量な追従（ChangeView のみ）。
        /// </param>
        private void ScrollToBottom(bool jump)
        {
            if (!IsAutoScrollEnabled) return;

            var listView = AssociatedObject;
            if (listView?.Items == null || listView.Items.Count == 0) return;

            // 非表示中（別パネル表示中・Collapsed など）はスクロールしても無意味なため何もしない
            // （再表示時に Loaded / SizeChanged / IsActive の各ハンドラーがスクロールする）
            // ※ IsLoaded は Collapsed な親配下では true にならないことがあるため判定に使わない
            if (!IsActive || listView.ActualHeight == 0) return;

            if (jump)
            {
                listView.UpdateLayout();
                listView.ScrollIntoView(listView.Items[^1]);
            }

            // 追加直後はレイアウト未確定で ScrollableHeight が古い値（追加行の高さを含まない）の
            // ことがあり、そのままでは1行分手前までしかスクロールされない。
            // レイアウトを確定させてから ScrollViewer で最下部へ移動させる。
            listView.UpdateLayout();
            var scrollViewer = EnsureScrollViewer();
            scrollViewer?.ChangeView(null, scrollViewer.ScrollableHeight, null, disableAnimation: true);
        }
    }
}
