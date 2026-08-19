using Gst;
using System;
using System.Threading;
using System.Threading.Tasks;

// Gst.Task（ストリーミングスレッドのタスク）と名前が衝突するので、ここでは明示する。
using Task = System.Threading.Tasks.Task;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// 「デバイスが戻ってきた」を観測して、自動復帰の待ちを早期に打ち切らせる。
///
/// <para>
/// <b>これは新しい復帰の仕組みではない。</b> 復帰そのものは
/// <see cref="RestartPolicy"/> と <c>EventRecorder.RestartLoopAsync</c> のままで、
/// ここが変えるのは<b>「次の試行まで待ち切るかどうか」だけ</b>である。
/// 到着が観測できない構成（プロバイダが無い・通知を出せない）では、
/// 何も起きずタイマーだけの従来どおりの挙動になる。
/// </para>
///
/// <para>
/// <b>通知は上流が既に持っているものを購読するだけ。</b> 自前のウィンドウも
/// メッセージポンプも作らない ── <c>mfdeviceprovider</c> は自前のスレッドと隠しウィンドウで
/// <c>RegisterDeviceNotificationW</c> を張り、<c>d3d12screencapturedeviceprovider</c> は
/// 自前のメッセージポンプで <c>WM_DISPLAYCHANGE</c> を受ける。どちらも差分を
/// <c>device-added</c> / <c>device-removed</c> / <c>device-changed</c> として
/// プロバイダのバスへ post する（詳細は docs/environment-facts.md）。
/// </para>
///
/// <para>
/// <b>プロバイダを started に保つのは復帰待ちのあいだだけ。</b> 参照カウント式で、
/// 誰も待っていなければ止める。<b>永続的に握ってはいけない</b> ──
/// <c>gst_device_provider_get_devices</c> は started の間だけプロバイダのキャッシュを返し、
/// 抜き差しの後は probe の順（＝<c>monitor-index</c> の順）と並びがずれるため、
/// <see cref="GstIntrospect.GetMonitorResolutions"/> の index と解像度の対応が壊れる。
/// </para>
///
/// <para>
/// <b><see cref="_lock"/> は葉である。</b> <c>_stateLock</c> / <c>_busLock</c> /
/// <c>_restartLock</c> を保持したまま取らないこと。逆にこのロックの下から
/// それらを取ることもない。バスの同期ハンドラはこのロックを取るので、
/// 内側は「世代を進めて待ち手を起こす」だけに保つ。
/// </para>
/// </summary>
internal sealed partial class DeviceArrivalWatcher
{
    /// <summary>プロセスに 1 つ。プロバイダはどのみちプロセス内シングルトンである。</summary>
    public static DeviceArrivalWatcher Instance { get; } = new();

    /// <summary>
    /// 待ち手が居なくなってから実際にプロバイダを止めるまでの猶予(ms)。
    ///
    /// <para>
    /// <b>連鎖の受け渡しを吸収するためにある。</b> パイプライン再生成が失敗すると
    /// 古い連鎖は新しい連鎖を張ってから終わるので、参照カウントが一瞬 0 を通る。
    /// 猶予が無いと、そのたびにプロバイダを止めて張り直すことになり、
    /// <b>ちょうどその隙間に来た到着を取りこぼす</b>。
    /// </para>
    /// </summary>
    public const int ProviderLingerMs = 5_000;

    private static readonly IDisposable _noScope = new NullScope();

    private readonly object _lock = new();
    private readonly KindState[] _kinds;

    private EventWaitHandle? _testEvent;
    private RegisteredWaitHandle? _testRegistration;
    private bool _testArmAttempted;

    private DeviceArrivalWatcher()
    {
        // 種別を足したときに配列の長さを直し忘れないよう、最後の要素から導く
        // （足りないと Acquire が IndexOutOfRange で落ち、復帰の連鎖が黙って死ぬ）。
        _kinds = new KindState[(int)DeviceKind.Monitor + 1];
        for (int i = 0; i < _kinds.Length; i++)
            _kinds[i] = new KindState { Kind = (DeviceKind)i };
    }

    /// <summary>種別ごとの購読・参照カウント・待ち合わせの状態。</summary>
    private sealed class KindState
    {
        public DeviceKind Kind;

        /// <summary>この種別の到着を待っている復帰連鎖の数。</summary>
        public int Holders;

        /// <summary>到着のたびに 1 進む。待ち手はこれを控えて取りこぼしを防ぐ。</summary>
        public long Generation;

        /// <summary>いまの待ち手を起こすための札。到着のたびに新しいものへ差し替える。</summary>
        public TaskCompletionSource Arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>interned な GObject。<b>Dispose しない</b>（参照を落とすだけ）。</summary>
        public DeviceProvider? Provider;

        /// <summary>これは <b>Dispose する</b>（同期ハンドラを外すため）。</summary>
        public IDisposable? Subscription;

        /// <summary>解放の猶予。新しい取得が来たら取り消す。</summary>
        public CancellationTokenSource? Linger;

        /// <summary>到着ログの洪水（解像度変更の連打）を畳む。</summary>
        public readonly BusMessageThrottle Throttle = new();

        /// <summary>プロバイダが無い、または通知を出せないと分かった。以後は試さない。</summary>
        public bool Unavailable;
    }

    /// <summary>
    /// この種別で「到着を待つ意味があるか」。
    /// <see cref="DeviceKind.None"/> は本来の監視対象ではないが、
    /// テスト注入が有効なあいだは注入シグナルだけを待つ（実プロバイダには触れない）。
    /// </summary>
    public static bool IsWatched(DeviceKind kind)
        => kind != DeviceKind.None || Components.AppEnvironment.TestDeviceArrivalEnabled;

    /// <summary>
    /// その種別のプロバイダを started に保つ。戻り値を破棄すると解放される
    /// （実際の停止は <see cref="ProviderLingerMs"/> の猶予の後）。
    ///
    /// <para>
    /// <b>ネイティブの <c>Start()</c> をここで呼ぶ。</b> したがって
    /// <b>バスの同期ハンドラから呼んではいけない</b> ── 呼び出しはプールスレッド
    /// （復帰連鎖の先頭）に限る。
    /// </para>
    /// </summary>
    public IDisposable Acquire(DeviceKind kind)
    {
        EnsureTestInjection();

        if (kind == DeviceKind.None)
            return _noScope;

        lock (_lock)
        {
            KindState state = _kinds[(int)kind];
            state.Holders++;

            state.Linger?.Cancel();
            state.Linger?.Dispose();
            state.Linger = null;

            if (state.Subscription is null && !state.Unavailable)
                StartLocked(state);
        }

        return new Scope(this, kind);
    }

    /// <summary>いまの到着世代。待つ前に控えて <see cref="WaitForArrivalAsync"/> へ渡す。</summary>
    public long CurrentGeneration(DeviceKind kind)
    {
        EnsureTestInjection();
        lock (_lock)
            return _kinds[(int)kind].Generation;
    }

    /// <summary>
    /// 次の到着まで完了しないタスクを返す。
    /// <paramref name="seenGeneration"/> より世代が進んでいれば<b>即座に完了済み</b>を返す
    /// ── 待ち始める前に来た到着を取りこぼさないための仕掛けで、これが無いと
    /// 「復帰を試している最中に挿し直された」が次の待ちで消える。
    ///
    /// <para>
    /// キャンセルは受け取らない。呼び出し側は <c>Task.WhenAny</c> の相手側
    /// （キャンセル可能な <c>Task.Delay</c>）でそれを行う。
    /// </para>
    /// </summary>
    public Task WaitForArrivalAsync(DeviceKind kind, long seenGeneration)
    {
        EnsureTestInjection();
        lock (_lock)
        {
            KindState state = _kinds[(int)kind];
            return seenGeneration < state.Generation ? Task.CompletedTask : state.Arrival.Task;
        }
    }

    // ---- 購読 ----

    /// <summary><see cref="_lock"/> の下で呼ぶこと。</summary>
    private void StartLocked(KindState state)
    {
        string kindName = DeviceKindRules.LogName(state.Kind);
        string? providerName = DeviceKindRules.ProviderName(state.Kind);
        if (providerName is null)
            return;

        if (!DebugLogEx.IsGstInitialized)
        {
            // gst_init より前に Gst.* を呼ぶと、ローダーがネイティブを解決して固定してしまう。
            // ここへ来るのは初期化前に復帰連鎖が動いた場合だけで、そのときはタイマーだけで復帰する。
            state.Unavailable = true;
            Components.ActivityLog.Warn("device.watch",
                $"kind={kindName} GStreamer is not initialized yet; falling back to the timer only");
            return;
        }

        try
        {
            DeviceProvider? provider = DeviceProviderFactory.GetByName(providerName);
            if (provider is null)
            {
                state.Unavailable = true;
                Components.ActivityLog.Warn("device.watch",
                    $"kind={kindName} provider='{providerName}' missing; falling back to the timer only");
                return;
            }

            // **通知を出せるかを先に訊く。** false なら probe しか実装しておらず、
            // Start しても device-added は二度と post されない（d3d11 の画面キャプチャがこれ）。
            if (!provider.CanMonitor())
            {
                state.Unavailable = true;
                Components.ActivityLog.Warn("device.watch",
                    $"kind={kindName} provider='{providerName}' monitor=no; falling back to the timer only");
                return;
            }

            // interned な GObject。Dispose しない。
            Bus bus = provider.GetBus();

            // **購読より先に Start する。** Start は現に在るデバイス全部について
            // device-added を post する（＝偽の到着の束）ので、購読を先に張ると
            // 起動した瞬間に到着として数えてしまう。キューへ溜めてから捨てる。
            if (!provider.Start())
            {
                state.Unavailable = true;
                Components.ActivityLog.Warn("device.watch",
                    $"kind={kindName} provider='{providerName}' failed to start; falling back to the timer only");
                return;
            }

            IDisposable subscription = bus.SubscribeSyncDrop((_, message) =>
            {
                try { HandleProviderMessage(state, message); }
                catch (Exception ex)
                {
                    DebugLogEx.Log(DebugLevel.Error, $"device arrival handler failed! kind={kindName}\n{ex}");
                }
            });

            // Start が積んだ分を捨てる。Pop の戻りは所有権つきなので解放する。
            // **捨てられるのは購読より前に post された分だけ** ── Start と購読のあいだに
            // 来た本物の到着も巻き込むが、その隙間はタイマーだけの従来の挙動へ落ちる。
            while (bus.Pop() is { } backlog)
                backlog.Dispose();

            state.Provider = provider;
            state.Subscription = subscription;

            Components.ActivityLog.Info("device.watch",
                $"kind={kindName} provider='{providerName}' monitor=yes");
        }
        catch (Exception ex)
        {
            state.Unavailable = true;
            Components.ActivityLog.Warn("device.watch",
                $"kind={kindName} provider='{providerName}' could not be watched: {ex.Message}");
        }
    }

    private void HandleProviderMessage(KindState state, Message message)
    {
        string? deviceName;
        switch (message.Type)
        {
            case MessageType.DeviceAdded:
                message.ParseDeviceAdded(out Device? added);
                // interned な GObject。Dispose しない
                // （GObject の Dispose はプロセス全体で手放す意味になる）。
                deviceName = added?.GetDisplayName();
                break;

            case MessageType.DeviceChanged:
                message.ParseDeviceChanged(out Device? changed, out _);
                deviceName = changed?.GetDisplayName();
                break;

            default:
                // DeviceRemoved は到着ではない。復帰の待ちを早める理由にはならない。
                return;
        }

        string kindName = DeviceKindRules.LogName(state.Kind);
        var (emit, repeatedBefore) = state.Throttle.Observe($"{kindName} {deviceName}");
        if (emit)
        {
            string repeated = 0 < repeatedBefore ? $" repeated={repeatedBefore}" : "";
            Components.ActivityLog.Info("device.arrive",
                $"kind={kindName} device='{deviceName}'{repeated}");
        }

        SignalArrival(state.Kind);
    }

    private void SignalArrival(DeviceKind kind)
    {
        lock (_lock)
        {
            KindState state = _kinds[(int)kind];
            state.Generation++;
            TaskCompletionSource previous = state.Arrival;
            state.Arrival = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // RunContinuationsAsynchronously なので、待ち手の続き（SetState / Initialize）が
            // このスレッド ── GStreamer のデバイス通知スレッド ── で走ることはない。
            previous.TrySetResult();
        }
    }

    // ---- 解放 ----

    private void Release(DeviceKind kind)
    {
        lock (_lock)
        {
            KindState state = _kinds[(int)kind];
            if (state.Holders == 0)
                return;

            state.Holders--;
            if (0 < state.Holders || state.Subscription is null || state.Linger is not null)
                return;

            var linger = new CancellationTokenSource();
            state.Linger = linger;
            _ = LingerThenStopAsync(state, linger);
        }
    }

    private async Task LingerThenStopAsync(KindState state, CancellationTokenSource linger)
    {
        try
        {
            await Task.Delay(ProviderLingerMs, linger.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;     // 新しい取得が来た。購読はそのまま使う
        }

        lock (_lock)
        {
            if (!ReferenceEquals(state.Linger, linger) || 0 < state.Holders)
                return;

            state.Linger = null;
            linger.Dispose();
            StopLocked(state);
        }
    }

    /// <summary><see cref="_lock"/> の下で呼ぶこと。</summary>
    private static void StopLocked(KindState state)
    {
        string kindName = DeviceKindRules.LogName(state.Kind);
        try
        {
            state.Subscription?.Dispose();
            state.Provider?.Stop();
        }
        catch (Exception ex)
        {
            DebugLogEx.Log(DebugLevel.Error, $"stopping the device provider failed! kind={kindName}\n{ex}");
        }

        state.Subscription = null;

        // **Provider は Dispose しない。** interned な GObject なので、
        // Dispose は「プロセス全体としてこのオブジェクトは終わり」を意味する
        // （GetBus が返す Bus も同じ理由で持ち続けない・破棄しない）。
        state.Provider = null;
        state.Throttle.Flush();

        Components.ActivityLog.Info("device.watch", $"kind={kindName} stopped");
    }

    // ---- テスト専用の注入 ----

    /// <summary>
    /// <see cref="Components.AppEnvironment.TestDeviceArrivalVariable"/> が有効なら、
    /// 名前付きイベントを到着シグナルとして待つ。
    ///
    /// <para>
    /// <b>本物の通知経路とは独立している。</b> プロバイダを開始も停止もせず、
    /// シグナルは<b>全種別</b>の到着として扱う ── 開発機にも CI にもカメラが無く、
    /// モニタの抜き差しもできないので、テストからは種別の区別が付けられない。
    /// </para>
    /// </summary>
    private void EnsureTestInjection()
    {
        if (!Components.AppEnvironment.TestDeviceArrivalEnabled)
            return;

        lock (_lock)
        {
            if (_testArmAttempted)
                return;
            _testArmAttempted = true;

            string name = Components.AppEnvironment.TestDeviceArrivalEventName;
            try
            {
                _testEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name);
                _testRegistration = ThreadPool.RegisterWaitForSingleObject(
                    _testEvent,
                    (_, _) => SignalTestArrival(),
                    state: null,
                    millisecondsTimeOutInterval: Timeout.Infinite,
                    executeOnlyOnce: false);

                Components.ActivityLog.Info("device.watch", $"test arrival injection armed event='{name}'");
            }
            catch (Exception ex)
            {
                Components.ActivityLog.Warn("device.watch",
                    $"test arrival injection could not be armed event='{name}': {ex.Message}");
            }
        }
    }

    private void SignalTestArrival()
    {
        Components.ActivityLog.Info("device.arrive", "kind=test injected");
        for (int i = 0; i < _kinds.Length; i++)
            SignalArrival((DeviceKind)i);
    }

    private sealed partial class Scope(DeviceArrivalWatcher owner, DeviceKind kind) : IDisposable
    {
        private DeviceArrivalWatcher? _owner = owner;

        public void Dispose()
        {
            DeviceArrivalWatcher? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(kind);
        }
    }

    private sealed partial class NullScope : IDisposable
    {
        public void Dispose() { }
    }
}
