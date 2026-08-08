using Microsoft.UI.Dispatching;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;

namespace ProcessRecorderApp.Services;

/// <summary>
/// UIA トリガの監視サービス。別アプリの UI 変化（UiaTrigger の <see cref="TriggerMonitor"/>）を
/// テンプレート変数と録画アクションへ橋渡しする常駐タスク。
///
/// <para>
/// <b>発火の処理順は「変数 → アクション」。</b> ファイル名テンプレートの展開は録画開始の
/// 瞬間（<c>EventRecorder.Start</c>）なので、この順でだけ「発火した値がその録画のファイル名に
/// 載る」が成立する。変数の反映は割り当て（<see cref="UiaTriggerAssignment"/>）に関係なく
/// <b>常に</b>行う。
/// </para>
/// <para>
/// <b>スレッドの規律:</b> <see cref="TriggerMonitor.TriggerFired"/> は監視ワーカースレッドで
/// 直列に届く。変数ストア（<c>EventRecorder.SetTemplateVariable</c>）と
/// <see cref="ActivityLog"/> はスレッドセーフなのでそこから直接呼び、録画アクションだけを
/// <see cref="DispatcherQueue.TryEnqueue(DispatcherQueueHandler)"/> で UI スレッドへ運ぶ
/// （戻り値を捨てない ── <c>SingleInstanceManager.OnActivationRedirected</c> と同じ規律）。
/// </para>
/// <para>
/// <b>設定変更は「新モニタを起動できてから旧を破棄」。</b> 定義エラー
/// （<see cref="TriggerMonitor.StartAsync"/> の <see cref="ArgumentException"/>）や壊れた設定では
/// 現状を維持し、アプリの中核（録画）を殺さない。トリガ 0 件・無効スイッチでは監視スレッド
/// 自体を作らない（E2E 環境では設定が無いので自動的に完全不活性になる）。
/// </para>
/// </summary>
// partial なのは CsWinRT1028 のため（RecordingCleanupScheduler と同じ理由）。
public sealed partial class UiaTriggerService : IDisposable
{
    /// <summary>プロセスで唯一のインスタンス（<c>GstControllerViewModel.Current</c> と同じ形）。</summary>
    public static UiaTriggerService? Current { get; private set; }

    /// <summary>サービスを生成して最初の読み込みを予約する。冪等。</summary>
    public static UiaTriggerService Start(DispatcherQueue dispatcherQueue)
    {
        if (Current is null)
        {
            Current = new UiaTriggerService(dispatcherQueue);
            Current.RequestReload();
        }
        return Current;
    }

    private readonly DispatcherQueue _dispatcherQueue;
    /// <summary>Reload と Dispose の直列化。モニタの入れ替えを競わせない。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>テンプレートから参照できないキーの警告を 1 回に抑える。lock して使う。</summary>
    private readonly HashSet<string> _warnedKeys = [];

    /// <summary>
    /// 「条件成立中のみ録画」で自動開始した録画。<b>UI スレッドでのみ触る</b>
    /// （<see cref="ExecuteActionsAsync"/> 系と <see cref="ReconcileAutoStartedAsync"/> だけが読み書きする）。
    /// 監視が条件を追えなくなったときに止めてよいのはここに載っているものだけである。
    /// </summary>
    private readonly HashSet<(string TriggerId, string Recorder)> _autoStarted = [];

    /// <summary>不成立化を通知できないトリガへの警告を 1 回に抑える。<b>UI スレッド専用</b>。</summary>
    private readonly HashSet<string> _warnedWhileTriggers = [];

    /// <summary>
    /// モニタの世代。<see cref="ReloadAsync"/> の冒頭と <see cref="Dispose"/>
    /// （いずれも <see cref="_gate"/> の中）で増やす。
    ///
    /// <para>
    /// 発火の処理は <c>WaitForControllerAsync</c> を待つので、
    /// 「立ち上がりの開始が積まれた直後に監視が止まる → 後始末が先に走り抜ける →
    /// その後で開始が再開する」順序が成立しうる。そうなると<b>トリガを切ったのに録画が回り続け、
    /// しかも追跡もされていない</b>。世代が変わっていたら実行しないことで塞ぐ。
    /// </para>
    /// <para>
    /// <b>各モニタの世代は生成前に確定し、発火ハンドラへ固定で渡す</b>
    /// （<see cref="ReloadAsync"/> がラムダに閉じ込める）。発火時に現在値を読むと、
    /// 新モニタの初回発火（<c>StartAsync</c> 中〜直後に届く <c>FireOnInitialMatch</c>）が
    /// 「後から進んだ世代」と食い違って捨てられる ── 条件が既に成立している状態で
    /// トリガを追加すると録画が始まらない、という形で現れる。
    /// 不変条件は<b>「現用モニタのハンドラは常に現行世代を持つ」</b>で、
    /// <c>StartAsync</c> 失敗で旧モニタが続投する経路では張り替えて維持する。
    /// </para>
    /// </summary>
    private int _monitorEpoch;

    private TriggerMonitor? _monitor;

    /// <summary>現在の <see cref="_monitor"/> に張った発火ハンドラ（世代を閉じ込めたラムダ）。解除用。</summary>
    private EventHandler<TriggerFiredEventArgs>? _monitorFired;
    private volatile bool _disposed;

    private UiaTriggerService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        Settings.AppSettings.Default.PropertyChanged += Settings_PropertyChanged;
    }

    private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 割り当て（UiaTriggerAssignments）の変更は発火ごとに読み直すので再起動不要。
        // 監視の作り直しが要るのはトリガ定義と有効スイッチだけ。
        if (e.PropertyName is nameof(Settings.AppSettings.UiaTriggersEnabled)
            or nameof(Settings.AppSettings.UiaTriggers))
        {
            RequestReload();
        }
    }

    /// <summary>設定から監視を作り直す（バックグラウンドで実行、失敗は activity.log へ）。</summary>
    public void RequestReload()
    {
        if (_disposed)
            return;
        _ = Task.Run(ReloadAsync);
    }

    private async Task ReloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            // **世代はここ（_gate の中・モニタ生成前）で確定する。** 旧モニタ由来の発火は
            // 旧世代を握っているので以後すべて退役し、新モニタの初回発火
            // （StartAsync 中〜直後に届く FireOnInitialMatch）は最初から新世代を持つ。
            // 後段（EnqueueReconcile）で増やすと、新モニタの発火が旧世代を掴んだまま
            // インクリメントに追い越され、正当な初回発火が世代検査で捨てられる。
            int epoch = Interlocked.Increment(ref _monitorEpoch);

            bool enabled = Settings.AppSettings.Default.UiaTriggersEnabled;
            // UiaTriggers は差し替え運用（AppSettings 側のコメント参照）なので参照読みで安全。
            // null は手で編集された settings.json（"UiaTriggers": null）で起こりうる。
            List<TriggerDefinition> definitions = Settings.AppSettings.Default.UiaTriggers ?? [];

            if (!enabled || definitions.Count == 0)
            {
                if (_monitor is { } old)
                {
                    _monitor = null;
                    Unsubscribe(old, _monitorFired);
                    _monitorFired = null;
                    await old.DisposeAsync().ConfigureAwait(false);
                    ActivityLog.Info("trigger.monitor stop");
                }
                // 条件を追える定義が 1 つも無くなったので、自動開始した録画はすべて止める
                EnqueueReconcile(new HashSet<string>(StringComparer.Ordinal), epoch);
                return;
            }

            // FireOnInitialMatch は既定 true のまま使う（明示していない既定値への依存）。
            // これが効くので、トリガ編集による入れ替えの直後に条件が成立していれば
            // 立ち上がりが届き、「条件成立中のみ録画」が録り直される。
            var monitor = new TriggerMonitor(new TriggerMonitorOptions
            {
                Session = new UiaTrigger.UiaSessionOptions { ThreadName = "UiaTriggerMonitor" },
            });
            EventHandler<TriggerFiredEventArgs> fired = (_, e) => OnTriggerFired(e, epoch);
            monitor.TriggerFired += fired;
            monitor.ResolutionChanged += OnResolutionChanged;
            monitor.UnhandledException += OnMonitorException;
            try
            {
                await monitor.StartAsync(definitions).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 定義エラー（ArgumentException）を含め、新モニタを捨てて現状維持
                // ── 旧モニタが動いていればそのまま続投する。
                Unsubscribe(monitor, fired);
                await monitor.DisposeAsync().ConfigureAwait(false);
                ActivityLog.Error("trigger.monitor fail", ex.Message);

                // **続投する旧モニタへ現行世代を持たせ直す。** 冒頭で世代を進めているため、
                // 張り替えないと旧モニタの発火（旧世代のラムダ）が以後すべて世代検査で捨てられ、
                // 「trigger.fire は出るのに録画アクションだけ実行されない」状態が
                // 次の成功リロードまで続く。
                if (_monitor is { } surviving)
                {
                    if (_monitorFired is { } oldFired)
                        surviving.TriggerFired -= oldFired;
                    EventHandler<TriggerFiredEventArgs> refreshed = (_, e2) => OnTriggerFired(e2, epoch);
                    surviving.TriggerFired += refreshed;
                    _monitorFired = refreshed;
                }
                return;
            }

            var previous = _monitor;
            var previousFired = _monitorFired;
            _monitor = monitor;
            _monitorFired = fired;
            if (previous is not null)
            {
                Unsubscribe(previous, previousFired);
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            ActivityLog.Info("trigger.monitor start", $"count={definitions.Count}");

            foreach (var definition in definitions)
            {
                if (!TriggerFiringRules.IsTemplateReferencable(definition.Id))
                    WarnKeyOnce(definition.Id);
            }

            // 不成立化を通知できる定義だけを渡す。割り当ての側（UI スレッド所有の
            // ObservableCollection）はここでは読まず、突き合わせは UI スレッドで行う。
            EnqueueReconcile(definitions
                .Where(NotifiesOnStoppedMatching)
                .Select(d => d.Id)
                .ToHashSet(StringComparer.Ordinal), epoch);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("trigger.error", ex.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 発火（監視ワーカースレッド）。①変数を常に反映 → ②アクションを UI スレッドへ。
    /// <paramref name="epoch"/> は発火元モニタの世代（購読時にラムダへ閉じ込めた固定値。
    /// 現在値をここで読まない理由は <see cref="_monitorEpoch"/> の doc を参照）。
    /// </summary>
    private void OnTriggerFired(TriggerFiredEventArgs e, int epoch)
    {
        try
        {
            var clauses = new TriggerClauseValue[e.Clauses.Count];
            for (int i = 0; i < e.Clauses.Count; i++)
            {
                ClauseReading reading = e.Clauses[i];
                clauses[i] = new(reading.Name, reading.Value.Value ?? "", MapOutcome(reading.Outcome));
            }

            // 変数の反映はエッジに関係なく常に行う。立ち下がりでも NewValue は入る
            // （要素が消えた場合は最後に見えた値）。
            foreach (var pair in TriggerFiringRules.BuildVariables(e.TriggerId, e.NewValue.Value ?? "", clauses))
            {
                GStreamer.EventRecorder.SetTemplateVariable(pair.Key, pair.Value);
                if (!TriggerFiringRules.IsTemplateReferencable(pair.Key))
                    WarnKeyOnce(pair.Key);
            }

            TriggerFireEdge edge = MapEdge(e.On);
            // エッジを出さないと、立ち下がりが届いているのかどうかをログから判定できない。
            ActivityLog.Info("trigger.fire", $"id='{e.TriggerId}' edge={edge} value='{e.NewValue.Value}'");

            if (!_dispatcherQueue.TryEnqueue(() => _ = ExecuteActionsAsync(e.TriggerId, edge, epoch)))
            {
                // false はディスパッチャのシャットダウン中＝終了経路。黙って捨てない。
                ActivityLog.Warn("trigger.action drop", $"id='{e.TriggerId}' dispatcher unavailable");
            }
        }
        catch (Exception ex)
        {
            // ハンドラ例外は UnhandledException へ回されるが、二重に守って発火 1 件の失敗で閉じる
            ActivityLog.Error("trigger.error", ex.ToString());
        }
    }

    /// <summary>
    /// 上流のライフサイクルをエッジへ写す。<b>立ち下がり以外はすべて立ち上がり扱い</b>にして、
    /// 上流に値が増えても安全側（通常の発火）へ落ちるようにする。
    /// </summary>
    private static TriggerFireEdge MapEdge(TriggerOn on)
        => on == TriggerOn.StoppedMatching ? TriggerFireEdge.Falling : TriggerFireEdge.Rising;

    /// <summary>
    /// その定義が「条件が成立しなくなった」ことを通知できるか。
    /// これが false のトリガに「条件成立中のみ録画」を割り当てても、<b>開始した録画は止まらない</b>。
    /// </summary>
    private static bool NotifiesOnStoppedMatching(TriggerDefinition definition)
        => definition.On == TriggerOn.WhileMatching && definition.NotifyOnStoppedMatching;

    /// <summary>
    /// トリガ ID を指定して、そのトリガが「条件成立中のみ録画」を完結できるか
    /// （＝不成立化を通知できるか）を調べる。設定画面が選択肢の文言に注記を付けるのに使う。
    /// 定義が見つからないときは true を返す ── 「まだ無いもの」を警告しても意味が無い。
    /// </summary>
    public static bool CanCompleteWhileRecording(string triggerId)
    {
        var definitions = Settings.AppSettings.Default.UiaTriggers;
        if (definitions is null)
            return true;
        foreach (var definition in definitions)
        {
            if (string.Equals(definition.Id, triggerId, StringComparison.Ordinal))
                return NotifiesOnStoppedMatching(definition);
        }
        return true;
    }

    /// <summary>
    /// 上流の <see cref="ClauseOutcome"/> を Components のミラーへ写す。
    /// 未知の値（上流の列挙追加）は「読めていない」側へ倒す ── 読めない値で変数を潰さない。
    /// </summary>
    private static TriggerClauseOutcome MapOutcome(ClauseOutcome outcome) => outcome switch
    {
        ClauseOutcome.Matched => TriggerClauseOutcome.Matched,
        ClauseOutcome.NotMatched => TriggerClauseOutcome.NotMatched,
        ClauseOutcome.Unreadable => TriggerClauseOutcome.Unreadable,
        ClauseOutcome.NotEvaluated => TriggerClauseOutcome.NotEvaluated,
        _ => TriggerClauseOutcome.Unreadable,
    };

    /// <summary>割り当てに従って録画を開始/停止する（UI スレッド）。</summary>
    private async Task ExecuteActionsAsync(string triggerId, TriggerFireEdge edge, int epoch)
    {
        try
        {
            if (_disposed || Volatile.Read(ref _monitorEpoch) != epoch)
                return;

            // ObservableCollection は UI スレッド所有なので、読むのもここでだけ行う
            var requests = TriggerFiringRules.ResolveActions(
                triggerId, edge, Settings.AppSettings.Default.UiaTriggerAssignments);
            if (requests.Count == 0)
                return;

            var controller = await ActivationCommands.WaitForControllerAsync();
            if (controller is null)
            {
                ActivityLog.Warn("trigger.action fail", $"id='{triggerId}' engine not ready");
                return;
            }

            // 待っている間に監視が止まった／入れ替わったなら、この発火はもう我々のものではない。
            if (_disposed || Volatile.Read(ref _monitorEpoch) != epoch)
                return;

            foreach (var request in requests)
            {
                if (request.TargetRecorder.Length == 0)
                    await ExecuteForAllAsync(triggerId, request, controller);
                else
                    await ExecuteForSingleAsync(triggerId, request, controller);
            }
        }
        catch (Exception ex)
        {
            ActivityLog.Error("trigger.error", ex.ToString());
        }
    }

    private async Task ExecuteForAllAsync(string triggerId, TriggerActionRequest request, GstControllerViewModel controller)
    {
        if (request.Kind == TriggerActionKind.Start)
        {
            // Can* ガードは必須 ── 通さないと InvalidOperationException か、
            // 排出待ち（WaitForPendingStop）で UI スレッドが最大約 10 秒固まる。
            if (!controller.CanStartRecordingAll)
            {
                ActivityLog.Info("trigger.action skip", $"id='{triggerId}' start target=all: no recorder can start");
                return;
            }
            // 実際に開始するものをここで確定してから動かす（停止側の stopping と同じ形）
            var starting = controller.Recorders.Where(r => r.CanStartRecording).Select(r => r.Name).ToList();
            controller.StartRecordingAll();
            ActivityLog.Info("trigger.start", $"id='{triggerId}' target=all");
            if (request.TracksCondition)
                foreach (string name in starting)
                    _autoStarted.Add((triggerId, name));
            return;
        }

        if (!controller.CanStopRecordingAll)
        {
            ActivityLog.Info("trigger.action skip", $"id='{triggerId}' stop target=all: no recorder can stop");
            // 止められなかった場合も追跡は畳む（このトリガの録画はもう我々が面倒を見ない）
            if (request.TracksCondition)
                _autoStarted.RemoveWhere(x => x.TriggerId == triggerId);
            return;
        }
        // 停止対象は StopRecordingAllAsync が確定して返す（停止後に LastStopOutcome を
        // 読む相手を間違えないため。CLI の stop-recording-all と同じ集合を使う）
        var stopped = await controller.StopRecordingAllAsync();
        foreach (var recorder in stopped)
            LogStopOutcome(triggerId, recorder);
        if (request.TracksCondition)
            _autoStarted.RemoveWhere(x => x.TriggerId == triggerId);
    }

    private async Task ExecuteForSingleAsync(string triggerId, TriggerActionRequest request, GstControllerViewModel controller)
    {
        // 名前解決は CLI と同じ「完全一致・先勝ち」（ActivationCommands.ExecuteRecorderCommandAsync）
        var recorder = controller.Recorders.FirstOrDefault(r => r.Name == request.TargetRecorder);
        if (recorder is null)
        {
            ActivityLog.Warn("trigger.action fail", $"id='{triggerId}' recorder '{request.TargetRecorder}' not found");
            return;
        }

        if (request.Kind == TriggerActionKind.Start)
        {
            if (!recorder.CanStartRecording)
            {
                ActivityLog.Info("trigger.action skip", $"id='{triggerId}' start '{recorder.Name}': not startable");
                return;
            }
            recorder.StartRecording();
            ActivityLog.Info("trigger.start", $"id='{triggerId}' target='{recorder.Name}'");
            if (request.TracksCondition)
                _autoStarted.Add((triggerId, recorder.Name));
            return;
        }

        if (!recorder.CanStopRecording)
        {
            ActivityLog.Info("trigger.action skip", $"id='{triggerId}' stop '{recorder.Name}': not stoppable");
            if (request.TracksCondition)
                _autoStarted.Remove((triggerId, recorder.Name));
            return;
        }
        await recorder.StopRecordingAsync();
        LogStopOutcome(triggerId, recorder);
        if (request.TracksCondition)
            _autoStarted.Remove((triggerId, recorder.Name));
    }

    /// <summary>停止は成果物の使える/使えないをイベント名で分けて記録する（cleanup.* と同じ規則）。</summary>
    private static void LogStopOutcome(string triggerId, GstEventRecorderViewModel recorder, string? reason = null)
    {
        string tail = reason is null ? "" : $" reason={reason}";
        if (GStreamer.RecordingStopRules.IsUsableArtifact(recorder.LastStopOutcome))
            ActivityLog.Info("trigger.stop", $"id='{triggerId}' target='{recorder.Name}' file='{recorder.LastFilename}'{tail}");
        else
            ActivityLog.Warn("trigger.stop failed", $"id='{triggerId}' target='{recorder.Name}' outcome={recorder.LastStopOutcome} file='{recorder.LastFilename}'{tail}");
    }

    /// <summary>
    /// 監視構成が変わったので、世代を進めて後始末を UI スレッドへ積む（<see cref="_gate"/> の中で呼ぶ）。
    /// </summary>
    /// <param name="fallingCapableIds">
    /// 不成立化を通知できる（＝「条件成立中のみ録画」を完結できる）トリガ ID。監視を止めるときは空。
    /// </param>
    private void EnqueueReconcile(IReadOnlySet<string> fallingCapableIds, int epoch)
    {
        // 世代はここでは増やさない ── ReloadAsync の冒頭で確定した値を使う。
        // ここで増やすと、その手前で新モニタが発火した分（旧値を掴んでいる）が
        // 世代検査に落ちて捨てられる。
        if (!_dispatcherQueue.TryEnqueue(() => _ = ReconcileAutoStartedAsync(fallingCapableIds, epoch)))
            ActivityLog.Warn("trigger.action drop", "reconcile: dispatcher unavailable");
    }

    /// <summary>
    /// 監視構成が変わったあとの後始末（UI スレッド）。
    ///
    /// <para>
    /// 割り当ては UI スレッド所有の <c>ObservableCollection</c> なので、
    /// <see cref="ReloadAsync"/>（バックグラウンド）からは定義側の抽出結果だけを受け取り、
    /// 割り当てを読むのはここでだけ行う。
    /// </para>
    /// <para>
    /// 止める判断は「監視が完全に止まったか」ではなく<b>「そのトリガの不成立化を今も追えるか」</b>で行う。
    /// 1 つの規則で 3 つの場合を覆えるためである ── 監視を止めた（集合が空）／トリガ編集で
    /// 入れ替わったが当のトリガは健在（止めない。既定 true の <c>FireOnInitialMatch</c> が再評価する）／
    /// <b>当のトリガが消えた・通知が外れた・割り当てが変わった（止める）</b>。
    /// 最後の場合は立ち下がりが永久に来ないので、これが無いと録画が残り続ける。
    /// </para>
    /// </summary>
    private async Task ReconcileAutoStartedAsync(IReadOnlySet<string> fallingCapableIds, int epoch)
    {
        try
        {
            if (_disposed || Volatile.Read(ref _monitorEpoch) != epoch)
                return;

            var whileAssigned = TriggerFiringRules.WhileAssignedTriggerIds(
                Settings.AppSettings.Default.UiaTriggerAssignments);

            // 「条件成立中のみ録画」なのに不成立化を通知できないトリガを知らせる。
            // 構成が変わるたびに warn し直す（直った・別のトリガで再発した、が伝わるように）。
            _warnedWhileTriggers.Clear();
            foreach (string id in whileAssigned)
            {
                if (!fallingCapableIds.Contains(id) && _warnedWhileTriggers.Add(id))
                {
                    ActivityLog.Warn("trigger.assign warn",
                        $"id='{id}' is assigned 'record while matching' but the trigger does not notify when it stops matching (needs WhileMatching + notify-on-stopped); a recording it starts will not stop");
                }
            }

            // 今も追えるトリガ＝定義が通知でき、かつ割り当てが「条件成立中のみ」のもの
            var keep = whileAssigned.Where(fallingCapableIds.Contains).ToHashSet(StringComparer.Ordinal);
            var orphans = _autoStarted.Where(x => !keep.Contains(x.TriggerId)).ToList();
            if (orphans.Count == 0)
                return;

            var controller = await ActivationCommands.WaitForControllerAsync();
            if (_disposed || Volatile.Read(ref _monitorEpoch) != epoch)
                return;
            if (controller is null)
            {
                ActivityLog.Warn("trigger.action fail", "reconcile: engine not ready");
                return;
            }

            foreach (var orphan in orphans)
            {
                _autoStarted.Remove(orphan);
                var recorder = controller.Recorders.FirstOrDefault(r => r.Name == orphan.Recorder);
                if (recorder is null)
                    continue;   // レコーダーごと消えた。止めるものが無い
                if (!recorder.CanStopRecording)
                {
                    // 既に手動・CLI・別のトリガで止まっている
                    ActivityLog.Info("trigger.action skip",
                        $"id='{orphan.TriggerId}' stop '{recorder.Name}': not stoppable reason=monitor-stop");
                    continue;
                }
                await recorder.StopRecordingAsync();
                LogStopOutcome(orphan.TriggerId, recorder, reason: "monitor-stop");
            }
        }
        catch (Exception ex)
        {
            ActivityLog.Error("trigger.error", ex.ToString());
        }
    }

    private void OnResolutionChanged(object? sender, TriggerResolutionChangedEventArgs e)
        => ActivityLog.Info("trigger.resolve",
            $"id='{e.TriggerId}' resolved={e.IsResolved}" + (e.Message is null ? "" : $" {e.Message}"));

    /// <summary>
    /// 監視側で他に出口の無い例外（昇格アプリ相手の購読失敗や、発火ハンドラ自身の例外）。
    /// 握り潰すと「トリガーが発火しない理由」が一切残らない。
    /// </summary>
    private void OnMonitorException(Exception ex)
        => ActivityLog.Error("trigger.error", ex.ToString());

    private void WarnKeyOnce(string key)
    {
        lock (_warnedKeys)
        {
            if (!_warnedKeys.Add(key))
                return;
        }
        ActivityLog.Warn("trigger.name warn",
            $"key='{key}' cannot be referenced from the filename template (allowed: word characters, dots and hyphens)");
    }

    private void Unsubscribe(TriggerMonitor monitor, EventHandler<TriggerFiredEventArgs>? fired)
    {
        if (fired is not null)
            monitor.TriggerFired -= fired;
        monitor.ResolutionChanged -= OnResolutionChanged;
        monitor.UnhandledException -= OnMonitorException;
    }

    /// <summary>監視を止めて解放する。複数回呼んでも安全。終了経路なので待ちはすべて有界。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // 世代を進めて、積まれている発火・後始末を無効化する。
        // ここで録画は止めない ── 終了経路では engine.Dispose() が確定させる。
        Interlocked.Increment(ref _monitorEpoch);
        if (ReferenceEquals(Current, this))
            Current = null;
        Settings.AppSettings.Default.PropertyChanged -= Settings_PropertyChanged;

        // Reload と競わない。取れなければ諦める ── UIA スレッドが固まっていても
        // 終了経路を塞がない（プロセス終了で回収される）。
        //
        // **_gate は破棄しない。** ここを抜けた後も ReloadAsync が WaitAsync で
        // 並んでいる可能性があり、破棄するとその finally の Release() が
        // ObjectDisposedException になって未観測タスク例外になる。
        // SemaphoreSlim は AvailableWaitHandle を触らない限りアンマネージド資源を
        // 持たないので、放置して困るものは無い。
        if (!_gate.Wait(TimeSpan.FromSeconds(5)))
            return;
        try
        {
            if (_monitor is { } monitor)
            {
                _monitor = null;
                Unsubscribe(monitor, _monitorFired);
                _monitorFired = null;
                // DisposeAsync が待つのは自分のディスパッチャとイベントキューだけで、
                // こちらの発火ハンドラは TryEnqueue で即返すため行き止まりにならない
                // （UiaTrigger サンプルホストの Dispose と同じ形）。
                monitor.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (AggregateException ex)
        {
            ActivityLog.Warn("trigger.error", ex.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }
}
