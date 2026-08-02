using CommunityToolkit.WinUI.Behaviors;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace ProcessRecorderApp.Behaviors
{
    public class ListViewCopyBehavior : BehaviorBase<ListView>
    {
        private MenuFlyout? _menuFlyout;
        private bool _isDragging = false;
        private int _anchorIndex = -1;                                       // ドラッグ開始行のインデックス（-1 は未確定）
        private Microsoft.UI.Xaml.Data.ItemIndexRange? _lastRange = null;    // 前回のドラッグで選択した範囲
        private bool _pendingDeselect = false;                               // 唯一の選択行を再クリック中（release で解除する）
        private bool _dragMoved = false;                                     // 押下後にアンカー行以外へドラッグしたか
        private KeyboardAccelerator? _copyAccelerator;                       // Ctrl+C のコピー用アクセラレータ

        /// <summary>
        /// この ListView のコピーを有効にするか。既定は true（従来の利用者の挙動は変わらない）。
        ///
        /// <para>
        /// <b><see cref="KeyboardAccelerator"/> は <c>ScopeOwner</c> を持たない＝ウィンドウ全域に効く。</b>
        /// Log 画面が WebView2 の端末を表示しているあいだにこれが有効だと、
        /// 端末側の選択コピー（Ctrl+C）を横取りして <c>Handled</c> にしてしまう
        /// ── 利用者からは「選択したのにコピーされない」ようにしか見えない。
        /// リスト表示へフォールバックしているときだけ true にすること。
        /// </para>
        /// </summary>
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool),
                typeof(ListViewCopyBehavior), new PropertyMetadata(true));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        protected override bool Initialize()
        {
            var result = base.Initialize();

            if (AssociatedObject != null)
            {
                // ⭕ キーボードと右クリックメニューの設定
                // ※ KeyDown はフォーカスが ListView 配下にあるときしか届かない
                //    （仮想化のコンテナリサイクルでフォーカス行が破棄されると効かなくなる）ため、
                //    フォーカスに依存しない KeyboardAccelerator で Ctrl+C を処理する。
                _copyAccelerator = new KeyboardAccelerator
                {
                    Key = Windows.System.VirtualKey.C,
                    Modifiers = Windows.System.VirtualKeyModifiers.Control,
                };
                _copyAccelerator.Invoked += CopyAccelerator_Invoked;
                AssociatedObject.KeyboardAccelerators.Add(_copyAccelerator);
                SetupContextMenu();

                // ⭕ マウスドラッグ選択用のイベント購読
                AssociatedObject.AddHandler(UIElement.PointerPressedEvent,
                              new PointerEventHandler(AssociatedObject_PointerPressed), handledEventsToo: true);
                AssociatedObject.AddHandler(UIElement.PointerMovedEvent,
                    new PointerEventHandler(AssociatedObject_PointerMoved), handledEventsToo: true);
                AssociatedObject.AddHandler(UIElement.PointerReleasedEvent,
                    new PointerEventHandler(AssociatedObject_PointerReleased), handledEventsToo: true);
            }

            return result;
        }

        [WinRT.DynamicWindowsRuntimeCast(typeof(MenuFlyoutItem))]
        protected override bool Uninitialize()
        {
            var result = base.Uninitialize();

            if (AssociatedObject != null)
            {
                if (_copyAccelerator != null)
                {
                    _copyAccelerator.Invoked -= CopyAccelerator_Invoked;
                    AssociatedObject.KeyboardAccelerators.Remove(_copyAccelerator);
                    _copyAccelerator = null;
                }
                AssociatedObject.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(AssociatedObject_PointerPressed));
                AssociatedObject.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(AssociatedObject_PointerMoved));
                AssociatedObject.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(AssociatedObject_PointerReleased));
                AssociatedObject.ContextFlyout = null;
            }

            if (_menuFlyout != null && _menuFlyout.Items.Count > 0 && _menuFlyout.Items[0] is MenuFlyoutItem copyItem)
            {
                copyItem.Click -= CopyItem_Click;
            }

            return result;
        }

        #region ドラッグ範囲選択ロジック

        private void AssociatedObject_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.GetCurrentPoint(AssociatedObject);
            // マウスの左クリックかつ、アイテムの無い余白（端など）からドラッグを開始した場合を想定
            if (pointer.Properties.IsLeftButtonPressed)
            {
                // ※ ポインターキャプチャは行わない。キャプチャすると ListViewItem が
                //    PointerReleased を受け取れず、クリック選択が完了しない・フォーカスが
                //    確定せず Ctrl+C が効かない・押下状態の表示が残る、といった問題が起きる。
                _isDragging = true;
                _lastRange = null;
                _pendingDeselect = false;
                _dragMoved = false;

                // 押下位置の行をドラッグ開始行（アンカー）として記録（行外なら -1 のまま）
                var container = GetItemContainerAt(e);
                _anchorIndex = container != null ? AssociatedObject.IndexFromContainer(container) : -1;

                // Ctrl+C を受け付けられるよう、押下した行へフォーカスを移す
                container?.Focus(FocusState.Pointer);

                // Ctrlキーが押されていなければ、ドラッグ開始時に一度選択をクリアし、押下行を選択する
                // （Ctrl 押下時は ListView 組み込みの Ctrl+クリックのトグル動作に任せる）
                var ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
                if (!ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                {
                    // 押下行が唯一の選択行なら、再クリックによるトグル解除の候補とし、
                    // ここでは選択状態を変更しない（release 時に解除を確定する）
                    var itemData = container != null ? AssociatedObject.ItemFromContainer(container) : null;
                    if (itemData != null &&
                        AssociatedObject.SelectedItems.Count == 1 &&
                        AssociatedObject.SelectedItems[0] == itemData)
                    {
                        _pendingDeselect = true;
                    }
                    else
                    {
                        AssociatedObject.SelectedItems.Clear();

                        if (_anchorIndex >= 0)
                        {
                            var anchorRange = new Microsoft.UI.Xaml.Data.ItemIndexRange(_anchorIndex, 1);
                            AssociatedObject.SelectRange(anchorRange);
                            _lastRange = anchorRange;
                        }
                    }
                }
            }
        }

        private void AssociatedObject_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging || AssociatedObject == null) return;

            // マウスカーソルの現在位置にある行を特定する
            var currentContainer = GetItemContainerAt(e);
            if (currentContainer == null) return;
            var currentIndex = AssociatedObject.IndexFromContainer(currentContainer);
            if (currentIndex < 0) return;

            // 余白から開始した場合は、最初にヒットした行をアンカーとする
            if (_anchorIndex < 0)
            {
                _anchorIndex = currentIndex;
            }

            // アンカー行以外へ動いたらドラッグ操作とみなし、再クリックによるトグル解除は取り消す
            if (currentIndex != _anchorIndex)
            {
                _dragMoved = true;
                _pendingDeselect = false;
            }

            // アンカー行〜現在行の連続範囲を選択する
            // （1件ずつの追加では高速ドラッグ時に PointerMoved の間引きで行が飛ぶため）
            var first = Math.Min(_anchorIndex, currentIndex);
            var last = Math.Max(_anchorIndex, currentIndex);
            var newRange = new Microsoft.UI.Xaml.Data.ItemIndexRange(first, (uint)(last - first + 1));

            // 前回の範囲を解除してから選択し直すことで、範囲を縮める方向の操作にも追従させる
            if (_lastRange != null)
            {
                AssociatedObject.DeselectRange(_lastRange);
            }
            AssociatedObject.SelectRange(newRange);
            _lastRange = newRange;
        }

        private void AssociatedObject_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging && AssociatedObject != null)
            {
                // 唯一の選択行を（ドラッグせずに）再クリックした場合は選択を解除する。
                // ListView 組み込みのクリック選択が release 処理で再選択するため、
                // ディスパッチャー経由でその後に確実に解除する。
                if (_pendingDeselect && !_dragMoved)
                {
                    var listView = AssociatedObject;
                    listView.DispatcherQueue.TryEnqueue(() => listView.SelectedItems.Clear());
                }

                _isDragging = false;
                _anchorIndex = -1;
                _lastRange = null;
                _pendingDeselect = false;
                _dragMoved = false;
            }
        }

        /// <summary>
        /// ポインターイベントの位置にある行のコンテナを返す（行外なら null）。
        /// </summary>
        [WinRT.DynamicWindowsRuntimeCast(typeof(ListViewItem))]
        private ListViewItem? GetItemContainerAt(PointerRoutedEventArgs e)
        {
            if (AssociatedObject == null) return null;

            // FindElementsInHostCoordinates はウィンドウ（ホスト）基準の座標を要求するため、
            // relativeTo に null を指定してホスト基準の位置を取得する
            var hostPosition = e.GetCurrentPoint(null).Position;
            var elements = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(hostPosition, AssociatedObject);
            foreach (var element in elements)
            {
                if (element is ListViewItem listViewItem)
                {
                    return listViewItem;
                }
            }

            return null;
        }

        #endregion

        #region コピー・メニュー処理

        private void SetupContextMenu()
        {
            _menuFlyout = new MenuFlyout();
            var copyItem = new MenuFlyoutItem
            {
                Text = Localization.GetString("Controls/ControlsResources/Common_Copy"),
                Icon = new SymbolIcon(Symbol.Copy)
            };
            copyItem.Click += CopyItem_Click;
            _menuFlyout.Items.Add(copyItem);
            AssociatedObject!.ContextFlyout = _menuFlyout;
        }

        private void CopyAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            // Log タブ非表示中は処理せず、他のコントロールの Ctrl+C を妨げない。
            // IsActive が false のあいだも Handled を立てずに素通しする
            // ── アクセラレータはウィンドウ全域に効くので、
            //    端末（WebView2）表示中に握ると向こうの選択コピーが死ぬ
            if (AssociatedObject == null || !AssociatedObject.IsLoaded || !IsActive) return;

            args.Handled = true;
            CopySelectedItems();
        }

        private void CopyItem_Click(object sender, RoutedEventArgs e)
        {
            CopySelectedItems();
        }

        private void CopySelectedItems()
        {
            var listView = AssociatedObject;
            if (listView == null || listView.SelectedItems.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var item in listView.SelectedItems)
            {
                // 行データに含まれる ANSI エスケープは除去してプレーンテキストでコピーする
                sb.AppendLine(Components.AnsiEscape.Strip(item?.ToString() ?? string.Empty));
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(sb.ToString().TrimEnd());
            Clipboard.SetContent(dataPackage);
        }

        #endregion
    }
}
