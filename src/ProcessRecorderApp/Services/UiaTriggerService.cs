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
    private TriggerMonitor? _monitor;
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

            bool enabled = Settings.AppSettings.Default.UiaTriggersEnabled;
            // UiaTriggers は差し替え運用（AppSettings 側のコメント参照）なので参照読みで安全
            List<TriggerDefinition> definitions = Settings.AppSettings.Default.UiaTriggers;

            if (!enabled || definitions.Count == 0)
            {
                if (_monitor is { } old)
                {
                    _monitor = null;
                    Unsubscribe(old);
                    await old.DisposeAsync().ConfigureAwait(false);
                    ActivityLog.Info("trigger.monitor stop");
                }
                return;
            }

            var monitor = new TriggerMonitor(new TriggerMonitorOptions
            {
                Session = new UiaTrigger.UiaSessionOptions { ThreadName = "UiaTriggerMonitor" },
            });
            monitor.TriggerFired += OnTriggerFired;
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
                Unsubscribe(monitor);
                await monitor.DisposeAsync().ConfigureAwait(false);
                ActivityLog.Error("trigger.monitor fail", ex.Message);
                return;
            }

            var previous = _monitor;
            _monitor = monitor;
            if (previous is not null)
            {
                Unsubscribe(previous);
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            ActivityLog.Info("trigger.monitor start", $"count={definitions.Count}");

            foreach (var definition in definitions)
            {
                if (!TriggerFiringRules.IsTemplateReferencable(definition.Id))
                    WarnKeyOnce(definition.Id);
            }
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

    /// <summary>発火（監視ワーカースレッド）。①変数を常に反映 → ②アクションを UI スレッドへ。</summary>
    private void OnTriggerFired(object? sender, TriggerFiredEventArgs e)
    {
        try
        {
            var clauses = new TriggerClauseValue[e.Clauses.Count];
            for (int i = 0; i < e.Clauses.Count; i++)
            {
                ClauseReading reading = e.Clauses[i];
                clauses[i] = new(reading.Name, reading.Value.Value ?? "", MapOutcome(reading.Outcome));
            }

            foreach (var pair in TriggerFiringRules.BuildVariables(e.TriggerId, e.NewValue.Value ?? "", clauses))
            {
                GStreamer.EventRecorder.SetTemplateVariable(pair.Key, pair.Value);
                if (!TriggerFiringRules.IsTemplateReferencable(pair.Key))
                    WarnKeyOnce(pair.Key);
            }
            ActivityLog.Info("trigger.fire", $"id='{e.TriggerId}' value='{e.NewValue.Value}'");

            if (!_dispatcherQueue.TryEnqueue(() => _ = ExecuteActionsAsync(e.TriggerId)))
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
    private async Task ExecuteActionsAsync(string triggerId)
    {
        try
        {
            if (_disposed)
                return;

            // ObservableCollection は UI スレッド所有なので、読むのもここでだけ行う
            var requests = TriggerFiringRules.ResolveActions(
                triggerId, Settings.AppSettings.Default.UiaTriggerAssignments);
            if (requests.Count == 0)
                return;

            var controller = await ActivationCommands.WaitForControllerAsync();
            if (controller is null)
            {
                ActivityLog.Warn("trigger.action fail", $"id='{triggerId}' engine not ready");
                return;
            }

            foreach (var request in requests)
            {
                if (request.TargetRecorder.Length == 0)
                    await ExecuteForAllAsync(triggerId, request.Kind, controller);
                else
                    await ExecuteForSingleAsync(triggerId, request, controller);
            }
        }
        catch (Exception ex)
        {
            ActivityLog.Error("trigger.error", ex.ToString());
        }
    }

    private static async Task ExecuteForAllAsync(string triggerId, TriggerActionKind kind, GstControllerViewModel controller)
    {
        if (kind == TriggerActionKind.Start)
        {
            // Can* ガードは必須 ── 通さないと InvalidOperationException か、
            // 排出待ち（WaitForPendingStop）で UI スレッドが最大約 10 秒固まる。
            if (!controller.CanStartRecordingAll)
            {
                ActivityLog.Info("trigger.action skip", $"id='{triggerId}' start target=all: no recorder can start");
                return;
            }
            controller.StartRecordingAll();
            ActivityLog.Info("trigger.start", $"id='{triggerId}' target=all");
            return;
        }

        if (!controller.CanStopRecordingAll)
        {
            ActivityLog.Info("trigger.action skip", $"id='{triggerId}' stop target=all: no recorder can stop");
            return;
        }
        // 停止対象をここで確定してから待つ（停止後に LastStopOutcome を読む相手を間違えない）
        var stopping = controller.Recorders.Where(r => r.CanStopRecording).ToList();
        await controller.StopRecordingAllAsync();
        foreach (var recorder in stopping)
            LogStopOutcome(triggerId, recorder);
    }

    private static async Task ExecuteForSingleAsync(string triggerId, TriggerActionRequest request, GstControllerViewModel controller)
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
            return;
        }

        if (!recorder.CanStopRecording)
        {
            ActivityLog.Info("trigger.action skip", $"id='{triggerId}' stop '{recorder.Name}': not stoppable");
            return;
        }
        await recorder.StopRecordingAsync();
        LogStopOutcome(triggerId, recorder);
    }

    /// <summary>停止は成果物の使える/使えないをイベント名で分けて記録する（cleanup.* と同じ規則）。</summary>
    private static void LogStopOutcome(string triggerId, GstEventRecorderViewModel recorder)
    {
        if (GStreamer.RecordingStopRules.IsUsableArtifact(recorder.LastStopOutcome))
            ActivityLog.Info("trigger.stop", $"id='{triggerId}' target='{recorder.Name}' file='{recorder.LastFilename}'");
        else
            ActivityLog.Warn("trigger.stop failed", $"id='{triggerId}' target='{recorder.Name}' outcome={recorder.LastStopOutcome} file='{recorder.LastFilename}'");
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

    private void Unsubscribe(TriggerMonitor monitor)
    {
        monitor.TriggerFired -= OnTriggerFired;
        monitor.ResolutionChanged -= OnResolutionChanged;
        monitor.UnhandledException -= OnMonitorException;
    }

    /// <summary>監視を止めて解放する。複数回呼んでも安全。終了経路なので待ちはすべて有界。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (ReferenceEquals(Current, this))
            Current = null;
        Settings.AppSettings.Default.PropertyChanged -= Settings_PropertyChanged;

        // Reload と競わない。取れなければ諦める ── UIA スレッドが固まっていても
        // 終了経路を塞がない（プロセス終了で回収される）。
        if (!_gate.Wait(TimeSpan.FromSeconds(5)))
            return;
        try
        {
            if (_monitor is { } monitor)
            {
                _monitor = null;
                Unsubscribe(monitor);
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
