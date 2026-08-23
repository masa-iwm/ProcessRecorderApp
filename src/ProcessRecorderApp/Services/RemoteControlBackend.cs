using Microsoft.UI.Dispatching;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.RemoteControl;
using ProcessRecorderApp.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessRecorderApp.Services;

/// <summary>
/// リモート操作 API から見たアプリの状態（<see cref="IRemoteControlBackend"/> の実装）。
///
/// <para>
/// <b>この型の存在理由は UI スレッドへの乗り換えである。</b> ビューモデルとその
/// コレクションは UI スレッド所有で、<see cref="RecorderControlService"/> も
/// 「全メンバーを UI スレッド上で呼ぶこと」を要求する。HTTP のハンドラは
/// スレッドプール上で走るので、越境はここでしか行えない
/// （<c>RemoteControl</c> プロジェクトは UI の型を参照できない）。
/// </para>
/// <para>
/// <b>結果は不変の DTO へ写してから返す。</b> ビューモデルの参照を HTTP 側へ渡すと、
/// 応答の直列化が UI スレッド外でビューモデルを読むことになる。
/// </para>
/// </summary>
// partial なのは CsWinRT1028 のため（UiaTriggerService と同じ理由）。
internal sealed partial class RemoteControlBackend(DispatcherQueue dispatcherQueue) : IRemoteControlBackend
{
    /// <summary>
    /// 状態変化を畳む時間。<b>録画の開始は 1 操作で数個の <c>PropertyChanged</c> を出す</b>ので、
    /// 畳まないと同じ内容の SSE が連続して飛ぶ。
    /// </summary>
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// UI スレッドが無い（＝ウィンドウが既に壊れている）ときの終了コード。
    /// CLI の「録画エンジンがまだ使えない」と同じ 12 ── 呼び出し側から見た意味が同じで、
    /// <c>Retry-After</c> 付きの 503 になるのも同じでよい。
    /// </summary>
    private const int NotAvailableExitCode = 12;

    private readonly DispatcherQueue _dispatcherQueue = dispatcherQueue;

    /// <summary>
    /// UI スレッドで <paramref name="work"/> を実行して結果を受け取る。
    ///
    /// <para>
    /// <b><see cref="DispatcherQueue.TryEnqueue(DispatcherQueueHandler)"/> の戻り値を捨てない。</b>
    /// 失敗したまま待つと、この要求は永久に返らない（HTTP のクライアントから見れば
    /// 「応答しないサーバー」になる）。
    /// </para>
    /// <para>
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> は必須 ──
    /// 付けないと、待っている HTTP 側の継続が<b>UI スレッド上で</b>走り、
    /// 応答の直列化と送信で UI が塞がる。
    /// </para>
    /// </summary>
    private Task<T> RunOnUiAsync<T>(Func<Task<T>> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                completion.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!enqueued)
            throw new RemoteApiException(NotAvailableExitCode, "ui thread unavailable");

        return completion.Task;
    }

    /// <inheritdoc/>
    public Task<RecordersSnapshot> GetRecordersAsync(CancellationToken ct)
        => RunOnUiAsync(async () =>
        {
            var status = await RecorderControlService.GetStatusAsync();
            if (status.ExitCode != 0)
                throw new RemoteApiException(status.ExitCode, "the recording engine is not ready yet");
            return ToSnapshot(status);
        });

    private static RecordersSnapshot ToSnapshot(StatusResult status)
        // 具体型（配列）を明示する ── コレクション式を IReadOnlyList<T> へ直接向けると
        // CsWinRT1032（AOT でどの実装型になるか決まらない）になる。
        => new(
            status.Statuses.Select(s => new RecorderStatusDto(
                s.Name, s.IsInitialized, s.IsRecording, s.IsAwaitingRecoveryResume,
                s.LastFilename, s.ContinuousState, s.ContinuousLastFilename, s.LastError)).ToArray(),
            status.CanStartAll, status.CanStopAll, status.IsIdleAll);

    /// <inheritdoc/>
    public Task<JsonObject> GetAppSettingsAsync(CancellationToken ct)
        => RunOnUiAsync(() =>
        {
            // 保存と同じソース生成の型情報を通す（表現が settings.json と食い違わない）。
            var settings = Settings.AppSettings.ToJsonNode() as JsonObject ?? [];

            // **許可リストに載っていないキーは全部落とす。** 「拒否リストに無ければ出す」に
            // すると、後から増えたプロパティが黙って読み取りに出る。
            foreach (string key in settings.Select(pair => pair.Key).ToArray())
            {
                if (!RemoteApiRules.IsRemoteEditable(key))
                    settings.Remove(key);
            }

            return Task.FromResult(settings);
        });

    /// <inheritdoc/>
    public IDisposable SubscribeState(Action<RecordersSnapshot> onChange)
    {
        var subscription = new StateSubscription(_dispatcherQueue, onChange);

        if (!_dispatcherQueue.TryEnqueue(subscription.AttachOnUi))
        {
            // UI スレッドが無い。購読は空のまま返す ── 呼び出し側（SSE）は
            // 初回の 1 件を出した後、心拍だけを送り続ける形になる。
            ActivityLog.Warn("remote.error", "could not subscribe to state changes (dispatcher unavailable)");
        }

        return subscription;
    }

    /// <summary>
    /// 1 購読ぶんの状態変化の監視。
    ///
    /// <para>
    /// <b>張るのも外すのも UI スレッド上。</b> 監視対象（レコーダー VM のコレクションと
    /// 各要素）は UI スレッド所有なので、購読の追加・削除も同じスレッドで行う。
    /// </para>
    /// <para>
    /// <b>通知は <see cref="DispatcherQueueTimer"/>（単発）で畳む。</b> 1 回の操作で
    /// 複数のプロパティが変わるため、変化のたびに読むと同じ内容を何度も配ることになる。
    /// </para>
    /// </summary>
    private sealed partial class StateSubscription(DispatcherQueue dispatcherQueue, Action<RecordersSnapshot> onChange)
        : IDisposable
    {
        private readonly DispatcherQueue _dispatcherQueue = dispatcherQueue;
        private readonly Action<RecordersSnapshot> _onChange = onChange;
        private readonly List<GstEventRecorderViewModel> _subscribed = [];
        private GstControllerViewModel? _controller;
        private DispatcherQueueTimer? _timer;
        private volatile bool _disposed;

        /// <summary>UI スレッド上で監視を張る。</summary>
        public void AttachOnUi()
        {
            if (_disposed)
                return;

            // ここへ来る時点で最初の状態は読めている（呼び出し側が先に読む）ので、
            // Current が null なら以後も来ない ── 何も張らずに終わる。
            if (GstControllerViewModel.Current is not { } controller)
                return;

            _controller = controller;

            _timer = _dispatcherQueue.CreateTimer();
            _timer.Interval = DebounceInterval;
            _timer.IsRepeating = false;
            _timer.Tick += OnTick;

            controller.Recorders.CollectionChanged += OnRecordersChanged;
            foreach (var recorder in controller.Recorders)
                Subscribe(recorder);
        }

        private void OnRecordersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems is not null)
            {
                foreach (GstEventRecorderViewModel recorder in e.OldItems)
                    Unsubscribe(recorder);
            }

            // Reset はコレクションが空になった後に来るので、走査では解除できない
            // （購読中の一覧を別に持っている理由）。
            if (e.Action == NotifyCollectionChangedAction.Reset)
                UnsubscribeAll();

            if (e.NewItems is not null)
            {
                foreach (GstEventRecorderViewModel recorder in e.NewItems)
                    Subscribe(recorder);
            }

            Schedule();
        }

        private void Subscribe(GstEventRecorderViewModel recorder)
        {
            if (_subscribed.Contains(recorder))
                return;
            recorder.PropertyChanged += OnRecorderPropertyChanged;
            _subscribed.Add(recorder);
        }

        private void Unsubscribe(GstEventRecorderViewModel recorder)
        {
            if (!_subscribed.Remove(recorder))
                return;
            recorder.PropertyChanged -= OnRecorderPropertyChanged;
        }

        private void UnsubscribeAll()
        {
            foreach (var recorder in _subscribed)
                recorder.PropertyChanged -= OnRecorderPropertyChanged;
            _subscribed.Clear();
        }

        private void OnRecorderPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            => Schedule();

        /// <summary>次の通知を予約する（既に走っていれば張り直して畳む）。</summary>
        private void Schedule()
        {
            if (_disposed || _timer is null)
                return;
            _timer.Start();
        }

        private void OnTick(DispatcherQueueTimer sender, object args)
        {
            if (_disposed)
                return;

            // 状態を読むのは UI スレッド上、配るのは呼び出し側の受け口。
            // 受け口は channel へ書くだけなので、ここで UI が塞がることはない。
            _ = PublishAsync();
        }

        private async Task PublishAsync()
        {
            try
            {
                var status = await RecorderControlService.GetStatusAsync();
                if (_disposed || status.ExitCode != 0)
                    return;
                _onChange(ToSnapshot(status));
            }
            catch (Exception ex)
            {
                ActivityLog.Warn("remote.error", "state notification: " + ex.Message);
            }
        }

        /// <summary>購読を解く。どのスレッドから呼んでもよい（実体の解除は UI スレッド上）。</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // 戻り値は捨てる ── UI スレッドがもう無いなら、解除すべき購読も既に無い。
            _dispatcherQueue.TryEnqueue(DetachOnUi);
        }

        private void DetachOnUi()
        {
            if (_timer is { } timer)
            {
                timer.Stop();
                timer.Tick -= OnTick;
                _timer = null;
            }

            if (_controller is { } controller)
            {
                controller.Recorders.CollectionChanged -= OnRecordersChanged;
                _controller = null;
            }

            UnsubscribeAll();
        }
    }
}
