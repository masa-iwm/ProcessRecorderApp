using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using ProcessRecorderApp.GStreamer;
using ProcessRecorderApp.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;

namespace ProcessRecorderApp.ViewModels
{
    public partial class GstControllerViewModel : ObservableObject, IDisposable
    {
        protected Controller Model { get; } = new();

        /// <summary>
        /// 録画エンジン（Controller + 全 EventRecorder + 常時稼働 sink パイプライン + 設定ミラーリング）。
        /// プロセス寿命で <see cref="App"/> が所有し、コマンドライン処理からの唯一の入口でもある。
        /// 生成は <see cref="Start"/>（<c>App.OnLaunched</c>）、破棄は <c>AppWindow.Destroying</c> の
        /// いずれも UI スレッドで行われるため排他制御は不要。
        ///
        /// <c>MainPage</c> はこのインスタンスに**バインドするだけ**で、破棄はしない
        /// （画面の生成・破棄で録画が途切れないようにするため）。
        /// </summary>
        public static GstControllerViewModel? Current { get; private set; }

        /// <summary>
        /// 録画エンジンを生成して <see cref="Current"/> に登録する。既に生成済みならそれを返す。
        /// <c>App.OnLaunched</c> で <c>new MainWindow()</c> より前に呼ぶ。
        /// </summary>
        public static GstControllerViewModel Start(DispatcherQueue dispatcherQueue)
            => Current ??= new(dispatcherQueue);

        /// <summary>
        /// レコーダー VM の一覧が利用可能になったか（コマンドライン処理の待ち合わせ用）。
        ///
        /// <see cref="Recorders"/> は <see cref="Model"/> 側コレクションのディスパッチャ経由のミラーであり、
        /// 挿入が同期的に完了するのは CommunityToolkit の <c>EnqueueAsync</c> が
        /// <c>HasThreadAccess</c> の高速パスを持つ実装詳細に依存している。
        /// それに依存せず「準備完了」を明示するため、ctor 末尾の <c>TryEnqueue</c>（FIFO なので
        /// 挿入群の後に走る）でこのフラグを立てる。
        /// </summary>
        public bool IsReady { get; private set; }

        public GstEventEventRecorderCollectionViewModel Recorders { get; init; }

        /// <summary>
        /// いま生きているパイプラインのグラフを <c>.dot</c> として書き出し、書いた絶対パスを返す。
        /// <see cref="Model"/> は protected なので、View 層からはここを通す。
        /// </summary>
        public IReadOnlyList<string> WriteDebugGraphs(string directory)
            => Model.WriteDebugGraphs(directory);

        /// <summary>Settings_Recorders_CollectionChanged の遅延実行用（Initialize を呼んだ UI スレッドのキュー）。</summary>
        private readonly DispatcherQueue _dispatcherQueue;

        private GstControllerViewModel(DispatcherQueue dispatcherQueue)
        {
            _dispatcherQueue = dispatcherQueue;
            Recorders = new(Model.Recorders, dispatcherQueue);
            ((INotifyPropertyChanged)Recorders).PropertyChanged += (_, __) =>
            {
                if (SelectedRecorder is null && Recorders.Count > 0)
                    Recorders.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => SelectedRecorder = Recorders.First());
            };
            Recorders.CollectionChanged += (_, e) =>
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        if (e.NewItems is not null)
                            foreach (GstEventRecorderViewModel r in e.NewItems)
                                SubscribeRecorder(r);
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        if (e.OldItems is not null)
                            foreach (GstEventRecorderViewModel r in e.OldItems)
                                UnsubscribeRecorder(r);
                        break;
                    case NotifyCollectionChangedAction.Replace:
                        if (e.OldItems is not null && e.NewItems is not null
                            && !e.OldItems.OfType<GstEventRecorderViewModel>().SequenceEqual(
                                e.NewItems.OfType<GstEventRecorderViewModel>()))
                        {
                            foreach (GstEventRecorderViewModel r in e.OldItems)
                                UnsubscribeRecorder(r);
                            foreach (GstEventRecorderViewModel r in e.NewItems)
                                SubscribeRecorder(r);
                        }
                        break;
                    case NotifyCollectionChangedAction.Reset:
                        UnsubscribeAllRecorders();
                        break;
                }

                // レコーダーが増減すれば「1台も動いていないか」も変わりうる
                // （録画中の1台を消した直後など）。個々の状態変化だけでは追えない。
                OnPropertyChanged(nameof(IsIdleAll));
            };
            SelectedRecorder = Recorders[Model.SelectedRecorder];
            Model.PropertyChanged += Model_PropertyChanged;

            foreach (var s in AppSettings.Default.Recorders.ToArray())
                AddRecorderFor(s, Model.Recorders.Count);
            AppSettings.Default.Recorders.CollectionChanged += Settings_Recorders_CollectionChanged;

            // Recorders への挿入通知の後（TryEnqueue は投入順に実行される）に準備完了を立てる。
            // Current への登録は Start() が行う（真実の所在を1箇所にするため ctor では設定しない）。
            _dispatcherQueue.TryEnqueue(() => IsReady = true);
        }

        /// <summary>
        /// PropertyChanged を購読済みのレコーダー VM。
        /// Reset の通知はコレクションが既に空になった後に来るため、コレクション自体を
        /// 走査しても購読解除できない（＝購読が漏れる）。購読対象を別途保持して解除する。
        /// </summary>
        private readonly List<GstEventRecorderViewModel> _subscribedRecorders = [];

        private void SubscribeRecorder(GstEventRecorderViewModel r)
        {
            if (_subscribedRecorders.Contains(r))
                return;
            r.PropertyChanged += EventRecorder_PropertyChanged;
            _subscribedRecorders.Add(r);
        }

        private void UnsubscribeRecorder(GstEventRecorderViewModel r)
        {
            if (!_subscribedRecorders.Remove(r))
                return;
            r.PropertyChanged -= EventRecorder_PropertyChanged;
        }

        private void UnsubscribeAllRecorders()
        {
            foreach (var r in _subscribedRecorders)
                r.PropertyChanged -= EventRecorder_PropertyChanged;
            _subscribedRecorders.Clear();
        }

        private void EventRecorder_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(GstEventRecorderViewModel.CanStartRecording): StartRecordingAllCommand.NotifyCanExecuteChanged(); break;
                case nameof(GstEventRecorderViewModel.CanStopRecording): StopRecordingAllCommand.NotifyCanExecuteChanged(); break;
                case nameof(GstEventRecorderViewModel.IsRecording):
                    OnPropertyChanged(nameof(IsRecordingAll));
                    OnPropertyChanged(nameof(IsIdleAll));
                    break;
                // 排出が終わるまで CanStartRecording は false のままなので、
                // 「すべて録画開始」も戻せない。IsStopping の変化でも問い直す。
                case nameof(GstEventRecorderViewModel.IsStopping):
                    StartRecordingAllCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(IsIdleAll));
                    break;
            }
        }

        /// <summary>
        /// プレビュー用パイプライン（d3d12swapchainsink）を初期化する。冪等。
        /// プレビュー面はページ寿命であり、画面の生成ごとに呼ばれる。
        /// </summary>
        public void InitializePreview()
            => Model.InitializePreview();

        /// <summary>
        /// プレビュー用パイプラインを解放する。冪等。
        /// 録画エンジン本体（レコーダーと常時稼働 sink パイプライン）はここでは触らない。
        /// </summary>
        public void ShutdownPreview()
            => Model.ShutdownPreview();

        /// <summary>プレビュー用スワップチェーンのハンドルを取得する（SwapChainPanel へのバインド用）。</summary>
        public nint GetSwapChainHandle()
            => Model.GetSwapChainHandle();

        /// <summary>プレビュー用スワップチェーンの解像度を更新する（SwapChainPanel のサイズ追従用）。</summary>
        public void ResizeSwapChain(int width, int height)
            => Model.ResizeSwapChain(width, height);

        /// <summary>設定から EventRecorder を生成して Model.Recorders の指定位置へ追加する。</summary>
        private void AddRecorderFor(EventRecorderSettings s, int index)
        {
            EventRecorder r = new(s);
            Model.Recorders.Insert(index, r);
            try
            {
                r.Initialize();
                Components.ActivityLog.Info("recorder.init ok", $"recorder='{r.Name}' type={r.Type} src='{r.SrcPipeline}'");
            }
            catch (Exception ex)
            {
                // DebugLogEx は GST_DEBUG が未設定だと何も出力しない。初期化失敗こそ
                // ユーザーに届く必要がある（「録画できない」の唯一の手がかり）ため activity.log にも残す。
                Components.ActivityLog.Error("recorder.init fail", $"recorder='{r.Name}' type={r.Type} {ex.Message}");
                DebugLogEx.Log(Gst.DebugLevel.Error, $"Recorders add instance failed!\n{ex}");
            }
        }

        private void Settings_Recorders_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // CollectionChanged イベント中に EventRecorder を生成すると、ctor の settings.Name 書き戻しが
            // RecordersChild_PropertyChanged の自己代入 Replace を誘発し、AppSettings.Recorders への
            // 再入 (CheckReentrancy) 例外となるため、イベントの外へ遅延して同期する。
            // TryEnqueue は投入順に実行されるため、イベント発生時点のインデックスのまま適用できる。
            _dispatcherQueue.TryEnqueue(() =>
            {
                // AppSettings.Recorders と Model.Recorders はインデックスが常に一致する前提でミラーリングする
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        AddRecorderFor((EventRecorderSettings)e.NewItems?[0]!, e.NewStartingIndex);
                        break;
                    case NotifyCollectionChangedAction.Move:
                        Model.Recorders.Move(e.OldStartingIndex, e.NewStartingIndex);
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        {
                            var r = Model.Recorders[e.OldStartingIndex];
                            Model.Recorders.RemoveAt(e.OldStartingIndex);
                            r.Dispose();
                            break;
                        }
                    case NotifyCollectionChangedAction.Replace:
                        {
                            // 子プロパティ変更通知のための自己代入 Replace（同一インスタンス）は無視する
                            if (e.OldItems is not null && e.NewItems is not null
                                && e.OldItems.OfType<EventRecorderSettings>().SequenceEqual(
                                    e.NewItems.OfType<EventRecorderSettings>()))
                                break;
                            var r = Model.Recorders[e.NewStartingIndex];
                            Model.Recorders.RemoveAt(e.NewStartingIndex);
                            r.Dispose();
                            AddRecorderFor((EventRecorderSettings)e.NewItems?[0]!, e.NewStartingIndex);
                            break;
                        }
                    case NotifyCollectionChangedAction.Reset:
                        {
                            var removed = Model.Recorders.ToArray();
                            Model.Recorders.Clear();
                            foreach (var r in removed)
                                r.Dispose();
                            break;
                        }
                }
            });
        }

        private void Model_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(Model.SelectedRecorder): { var vm = Recorders[Model.SelectedRecorder]; if (SelectedRecorder != vm) SelectedRecorder = vm; break; }
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanRemoveRecorder))]
        [NotifyCanExecuteChangedFor(nameof(RemoveSelectedRecorderCommand))]
        public partial GstEventRecorderViewModel? SelectedRecorder { get; set; }
        partial void OnSelectedRecorderChanged(GstEventRecorderViewModel? value)
        {
            if (Model.SelectedRecorder != value?.Model)
                Model.SelectedRecorder = value?.Model;
        }

        /// <summary>Remove ボタンの有効状態（Recorder 選択中のみ有効）。</summary>
        public bool CanRemoveRecorder => SelectedRecorder is not null;

        /// <summary>Recorder を新規追加して選択する。</summary>
        [RelayCommand]
        public void AddRecorder()
        {
            var added = new EventRecorderSettings();

            // AppSettings.Recorders への追加が CollectionChanged 経由で Model.Recorders / Recorders に反映される
            AppSettings.Default.Recorders.Add(added);
            // Model への反映は遅延同期のため、反映完了後（同一キューの後続）に新規 Recorder を選択する
            Recorders.DispatcherQueue.TryEnqueue(() =>
            {
                if (Recorders.Count > 0)
                    SelectedRecorder = Recorders[^1];

                // 既定名のまま追加を繰り返すと同名のレコーダーが並ぶ。CLI の名前解決は
                // 「完全一致・先勝ち」なので、2つ目には CLI から永久に到達できない。
                // 規則は RecorderNaming.MakeUnique（L1 が守る）。
                //
                // **一意化はここで行う（追加の直前ではない）。** EventRecorder の ctor は
                // 「名前が既定値 "Recorder" なら」という条件で Recorder #N（プロセス通算の連番）へ
                // 上書きするため、追加前に一意化しても**その結果ごと捨てられる**。
                // 上書き後の名前は既存と照合されないので、連番が既存の名前に追いつくと衝突した
                // ── 例: settings.json に "Recorder #2" が1件ある状態で UI から追加すると、
                // 連番も 2 になり同名が2つ並んでいた（L3 の
                // AddingARecorder_NeverProducesADuplicateName が赤で再現）。
                added.Name = RecorderNaming.MakeUnique(
                    added.Name,
                    AppSettings.Default.Recorders.Where(r => r != added).Select(r => r.Name));
            });
        }

        /// <summary>
        /// 削除確認を View に委譲するためのコールバック（ダイアログ表示は View の責務）。
        /// true を返した場合のみ削除を実行する。未設定なら確認なしで削除する。
        /// </summary>
        public Func<GstEventRecorderViewModel, Task<bool>>? ConfirmRecorderRemovalAsync { get; set; }

        /// <summary>選択中の Recorder を削除する（AppSettings.ConfirmRecorderRemoval が有効なら確認後に削除）。</summary>
        [RelayCommand(CanExecute = nameof(CanRemoveRecorder))]
        private async Task RemoveSelectedRecorderAsync()
        {
            if (SelectedRecorder is not { } recorder)
                return;

            if (AppSettings.Default.ConfirmRecorderRemoval
                && ConfirmRecorderRemovalAsync is not null
                && !await ConfirmRecorderRemovalAsync(recorder))
                return;

            RemoveRecorder(recorder);
        }

        /// <summary>指定した Recorder を設定ごと削除する。</summary>
        public void RemoveRecorder(GstEventRecorderViewModel recorder)
        {
            int index = Recorders.IndexOf(recorder);
            if (index < 0)
                return;

            // 削除後の選択先を先に決めておく（次 > 前 > なし）
            GstEventRecorderViewModel? next =
                index + 1 < Recorders.Count ? Recorders[index + 1] :
                0 <= index - 1 ? Recorders[index - 1] :
                null;

            AppSettings.Default.Recorders.RemoveAt(index);

            SelectedRecorder = next;
        }

        [RelayCommand(CanExecute = nameof(CanStartRecordingAll))]
        public void StartRecordingAll()
        {
            foreach (var r in Recorders)
                if (r.CanStartRecording)
                    r.StartRecording();
        }
        public bool CanStartRecordingAll => Recorders.Any(r => r.CanStartRecording);

        [RelayCommand(CanExecute = nameof(CanStopRecordingAll))]
        public void StopRecordingAll()
        {
            foreach (var r in Recorders)
                if (r.CanStopRecording)
                    r.StopRecording();
        }

        /// <summary>
        /// 全レコーダの録画を停止し、<b>全ファイルが確定するまで待つ</b>。CLI 経路用。
        /// 排出は互いに独立なので直列にせず <see cref="System.Threading.Tasks.Task.WhenAll(System.Threading.Tasks.Task[])"/> で並行に待つ
        /// ── 直列にすると、レコーダ数 × <c>StopFinalizeTimeoutMs</c> でランチャーの
        /// 結果待ち（<b>60 秒</b>）を超えて終了コード 2 になる。
        /// </summary>
        public System.Threading.Tasks.Task StopRecordingAllAsync()
            => System.Threading.Tasks.Task.WhenAll(
                Recorders.Where(r => r.CanStopRecording).Select(r => r.StopRecordingAsync()));

        public bool CanStopRecordingAll => Recorders.Any(r => r.CanStopRecording);

        /// <summary>いずれかのRecorderが録画中か（録画中インジケータ表示用）。</summary>
        public bool IsRecordingAll => Recorders.Any(r => r.IsRecording);

        /// <summary>
        /// どのレコーダーも録画中でも排出中でもないか。
        /// <b>レコーダーを丸ごと作り直すような破壊的操作の可否</b>に使う
        /// （設定の再読み込みなど）。
        ///
        /// <para>
        /// <b><see cref="IsRecordingAll"/> の否定では足りない。</b> 停止を受け付けた時点で
        /// <c>IsRecording</c> は同期的に false になるが、その後の排出
        /// （<c>IsStopping</c>）が終わるまでパイプラインは生きている
        /// ── そこで破棄すると、停止直後という一番踏みやすい瞬間に
        /// UI スレッドが <c>StopFinalizeTimeoutMs</c> だけ固まる。
        /// </para>
        /// <para>
        /// <b><c>CanStartRecording</c> の否定も使わない。</b> あちらは
        /// <c>IsInitialized</c> を含むので、初期化に失敗したレコーダーが1台でも居ると
        /// 「永久に動いている」ことになってしまう。
        /// </para>
        /// </summary>
        public bool IsIdleAll => !Recorders.Any(r => r.IsRecording || r.IsStopping);


        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    IsReady = false;

                    // 自身が現在インスタンスとして登録されている場合のみ解除する
                    // （リロードで新インスタンスが既に登録済みの場合はそれを消さない）
                    if (ReferenceEquals(Current, this))
                        Current = null;

                    // 設定 → Model のミラーと、Model → VM のミラーを先に切り離す。
                    // Model.Dispose() は Recorders.Clear() で Reset を発火させ、
                    // Recorders のミラー（async void + EnqueueAsync）を走らせてしまうため、
                    // 破棄中のディスパッチャへ再入させない。
                    AppSettings.Default.Recorders.CollectionChanged -= Settings_Recorders_CollectionChanged;
                    UnsubscribeAllRecorders();
                    Recorders.Detach();

                    Model.PropertyChanged -= Model_PropertyChanged;
                    Model.Dispose();
                }

                disposedValue = true;
            }
        }

        // ファイナライザは定義しない（Dispose(false) が何も解放しないため）。
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
