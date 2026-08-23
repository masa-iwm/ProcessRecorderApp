using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ProcessRecorderApp.GStreamer;
using ProcessRecorderApp.ViewModels;

namespace ProcessRecorderApp;

/// <summary>
/// <c>status</c> が返す 1 レコーダー分の状態。<b>文言は持たない</b>
/// ── 列の整形（<see cref="RecorderCliRules.FormatStatusLine"/>）も健全性の文（15 の理由）も
/// 呼び出し側（<see cref="ActivationCommands"/>）の仕事。
/// </summary>
/// <param name="IsRecording">
/// <b><see cref="GStreamer.EventRecorder.IsRecording"/> の実体。</b>
/// ビューモデルの同名プロパティは<b>表示用</b>で、復帰待ちを録画中に畳んである。
/// </param>
/// <param name="ContinuousState"><see cref="RecorderCliRules.ContinuousState"/> の結果（1 語）。</param>
/// <param name="LastError">生の文字列。1 行へ潰すのは呼び出し側。</param>
internal sealed record RecorderStatus(
    string Name,
    bool IsInitialized,
    bool IsRecording,
    bool IsAwaitingRecoveryResume,
    string? LastFilename,
    string ContinuousState,
    string? ContinuousLastFilename,
    string? LastError);

/// <summary>
/// <see cref="RecorderControlService.GetStatusAsync"/> の結果。
/// <see cref="ExitCode"/> は 12（コントローラ未準備）か 0 のみ
/// ── <b>15（不健全）は付けない</b>。あれは <c>status</c> 専用の規則で、
/// <see cref="RecorderCliRules.IsHealthy"/> から呼び出し側が導く。
/// </summary>
internal sealed record StatusResult(
    int ExitCode,
    IReadOnlyList<RecorderStatus> Statuses,
    bool CanStartAll,
    bool CanStopAll,
    bool IsIdleAll);

/// <summary>
/// <see cref="RecorderControlService.StartAsync"/> の結果。
/// <see cref="ExitCode"/> は 12 / 13（対象が見つからない）/ 14（開始できない状態）/ 0。
/// </summary>
internal sealed record StartResult(int ExitCode, string? RecorderName, string? LastFilename);

/// <summary>
/// <see cref="RecorderControlService.StopAsync"/> の結果。
/// <see cref="ExitCode"/> は 12 / 13 / 14（停止できない状態）/ 0。
/// <b><see cref="Outcome"/> が <see cref="RecordingStopOutcome.Empty"/> /
/// <see cref="RecordingStopOutcome.NotFinalized"/> でも <see cref="ExitCode"/> は 0</b>
/// ── 16 / 17 への写像は呼び出し側（<c>ActivationCommands.ExitCodeFor</c>）が行う。
/// </summary>
internal sealed record StopResult(
    int ExitCode, string? RecorderName, string? LastFilename, RecordingStopOutcome Outcome);

/// <summary>
/// <see cref="RecorderControlService.StartAllAsync"/> の結果。
/// <see cref="ExitCode"/> は 12 / 14（開始可能なレコーダーが無い、または 1 台も開始できなかった）/ 0。
/// </summary>
/// <param name="Started"><b>今回開始した分だけ</b>。全件を返すと、開始しなかったレコーダーの
/// <c>LastFilename</c>（＝前回の録画）が今回の成果物として運ばれる。</param>
/// <param name="FailedRecorders"><b>開始できるはずだったのに落ちた分だけ</b>の名前。
/// 既に録画中のレコーダーは含まない（トリガ運用では日常であり、失敗ではない）。</param>
internal sealed record StartAllResult(
    int ExitCode,
    IReadOnlyList<(string Name, string? LastFilename)> Started,
    IReadOnlyList<string> FailedRecorders);

/// <summary>
/// <see cref="RecorderControlService.StopAllAsync"/> の結果。
/// <see cref="ExitCode"/> は 12 / 14（停止可能なレコーダーが無い）/ 0。
/// <b>畳み込み（<c>RecordingStopRules.Stronger</c>）と 16 / 17 への写像は呼び出し側</b>。
/// </summary>
/// <param name="Stopped"><b>今回停止した分だけ</b>を停止順で。止めていないレコーダーの
/// <c>LastStopOutcome</c> / <c>LastFilename</c> は前回の録画のものなので混ぜてはいけない。</param>
internal sealed record StopAllResult(
    int ExitCode,
    IReadOnlyList<(string Name, string? LastFilename, RecordingStopOutcome Outcome)> Stopped);

/// <summary>
/// CLI の録画コマンドの<b>実処理</b>（コントローラの待ち受け・対象の解決・開始／停止・
/// テンプレート変数の読み書き）。<see cref="ActivationCommands"/> には引数解析・文言・
/// <see cref="SingleInstance.CommandOutcome"/> の整形だけを残す。
///
/// <para>
/// <b>結果型は文言を持たない。</b> 失敗は <c>ActivationCommands.ExitCode_*</c> の値
/// （0 = 成功）で表す ── 番号は CLI とその他の呼び出し面で同じものを使う。
/// </para>
/// <para>
/// <b>スレッド規律: 全メンバーを UI スレッド（DispatcherQueue）上で呼ぶこと。</b>
/// ビューモデルとそのコレクションは UI スレッド所有であり、ここでの <c>await</c> の継続も
/// UI スレッド（<c>DispatcherQueueSynchronizationContext</c>）へ戻る前提で書かれている。
/// UI スレッドへの乗り換えは呼び出し側の責務。
/// </para>
/// </summary>
internal static class RecorderControlService
{
    /// <summary>
    /// 録画コマンド実行時、まだ録画エンジン（<see cref="GstControllerViewModel.Current"/>）が
    /// 準備完了（<see cref="GstControllerViewModel.IsReady"/>）でない場合に待つ最大時間。
    /// 常駐ワーカー未起動から起動された直後（コールドスタート）に、
    /// アプリの初期化完了を待ってから録画を実行するために使用する。
    /// ランチャー側の結果待ちタイムアウト（<b>60 秒</b>）より
    /// 短い値とし、結果通知の猶予を残す。
    /// </summary>
    private static readonly TimeSpan ReadyWaitTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 録画エンジン（<see cref="GstControllerViewModel.Current"/>）がレコーダー一覧まで
    /// 用意し終わるのを待って取得する。
    ///
    /// 常駐ワーカーが未起動から起動された直後（コールドスタート）は、リダイレクトされた録画コマンドが
    /// <c>App.OnLaunched</c> のエンジン生成より先に走ることがある。加えて、生成直後は
    /// <see cref="GstControllerViewModel.Current"/> が非 <see langword="null"/> でも
    /// <see cref="GstControllerViewModel.Recorders"/> への挿入がディスパッチャキューに残っている
    /// 可能性があるため、<see cref="GstControllerViewModel.IsReady"/> が立つまで待つ
    /// （待たないと <c>start-recording-all</c> が「開始できるレコーダーが無い」と誤判定しうる）。
    ///
    /// 待ちは <see cref="ReadyWaitTimeout"/> の範囲で UI スレッドを解放しながら行い、
    /// タイムアウトした場合は <see langword="null"/> を返す。
    /// </summary>
    // internal なのは UiaTriggerService が同じ「IsReady まで待つ」を再利用するため（同一アセンブリ）。
    internal static async Task<GstControllerViewModel?> WaitForControllerAsync()
    {
        long deadline = Environment.TickCount64 + (long)ReadyWaitTimeout.TotalMilliseconds;
        while (GstControllerViewModel.Current is not { IsReady: true })
        {
            if (Environment.TickCount64 >= deadline)
                return null;
            // await の間に message loop が回り、App.OnLaunched でエンジンが生成され、
            // 続いて ctor 末尾の TryEnqueue で IsReady が立つ。
            // 継続は UI スレッド（DispatcherQueueSynchronizationContext）へ戻る。
            await Task.Delay(50);
        }
        return GstControllerViewModel.Current;
    }

    /// <summary>
    /// レコーダーごとの状態を読む。準備完了まで待ってから読む
    /// ── 待たないと「レコーダーが1件も無い」という嘘の健全な結果を返しうる。
    ///
    /// <para>
    /// <b>「録画中」は <see cref="GStreamer.EventRecorder.IsRecording"/> の実体を読む</b> ──
    /// ビューモデル側の同名プロパティを使ってはいけない。あちらは<b>表示用</b>で、
    /// 復帰待ちを録画中に畳んである（畳まないとトグルを切る手段が無くなるため）。
    /// 畳むと「いまフレームが録れているか」を機械から判定できなくなるので、
    /// ここでは実体を読み、復帰待ちは<b>独立した値</b>にする。
    /// </para>
    /// <para>
    /// <b>「直近の障害」は今も壊れているという意味ではない。</b>
    /// <see cref="GStreamer.EventRecorder.LastError"/> は次の録画開始で消えるので、
    /// ここに出るのは「最後に起きたことが障害だった」ことを表す。
    /// </para>
    /// </summary>
    internal static async Task<StatusResult> GetStatusAsync()
    {
        var controller = await WaitForControllerAsync();
        if (controller is null)
            return new StatusResult(ActivationCommands.ExitCode_RecorderNotAvailable, [], false, false, false);

        var statuses = controller.Recorders
            .Select(r => new RecorderStatus(
                r.Name,
                r.IsInitialized,
                // **実体を読む。** r.IsRecording（VM）は復帰待ちを畳んだ表示用の値。
                r.Model.IsRecording,
                r.Model.IsAwaitingRecoveryResume,
                r.LastFilename,
                RecorderCliRules.ContinuousState(
                    r.ContinuousRecording, r.IsContinuousRecording, r.ContinuousLastError),
                r.ContinuousLastFilename,
                r.LastError))
            .ToList();

        return new StatusResult(0, statuses,
            controller.CanStartRecordingAll, controller.CanStopRecordingAll, controller.IsIdleAll);
    }

    /// <summary>
    /// 個別レコーダの録画を開始する。<paramref name="target"/> は数値ならインデックス（0始まり）、
    /// それ以外はレコーダ名（規則は <see cref="RecorderCliRules.ResolveTargetIndex"/>）。
    /// </summary>
    internal static async Task<StartResult> StartAsync(string target)
    {
        var controller = await WaitForControllerAsync();
        if (controller is null)
            return new StartResult(ActivationCommands.ExitCode_RecorderNotAvailable, null, null);

        var recorder = Resolve(controller, target);
        if (recorder is null)
            return new StartResult(ActivationCommands.ExitCode_RecorderNotFound, null, null);

        // 実行前に実行可否を確認し、不可の場合は実行せず失敗として返す
        if (!recorder.CanStartRecording)
            return new StartResult(ActivationCommands.ExitCode_RecordingNotExecutable, recorder.Name, null);

        recorder.StartRecording();

        // 返すのは「実際に使われたファイルのパス」。-all 版と揃えるため、
        // 未展開の FilenameTemplate ではなく解決済みの LastFilename を返す。
        return new StartResult(0, recorder.Name, recorder.LastFilename);
    }

    /// <summary>
    /// 個別レコーダの録画を終了する。<b>確定（moov の書き込み）まで待つ</b> ──
    /// <c>stop-recording X</c> の直後に <c>copy</c> するバッチが想定用途であり、
    /// 復帰時にファイルが確定している必要がある。
    /// 対象の解決規則は <see cref="StartAsync"/> と同じ。
    /// </summary>
    internal static async Task<StopResult> StopAsync(string target)
    {
        var controller = await WaitForControllerAsync();
        if (controller is null)
            return new StopResult(
                ActivationCommands.ExitCode_RecorderNotAvailable, null, null, RecordingStopOutcome.Ok);

        var recorder = Resolve(controller, target);
        if (recorder is null)
            return new StopResult(
                ActivationCommands.ExitCode_RecorderNotFound, null, null, RecordingStopOutcome.Ok);

        if (!recorder.CanStopRecording)
            return new StopResult(
                ActivationCommands.ExitCode_RecordingNotExecutable, recorder.Name, null, RecordingStopOutcome.Ok);

        await recorder.StopRecordingAsync();

        // 結果は待ち終わってから読む（LastStopOutcome / LastFilename は排出の完了で確定する）。
        return new StopResult(0, recorder.Name, recorder.LastFilename, recorder.LastStopOutcome);
    }

    /// <summary>
    /// 全レコーダの録画を開始する。開始可能なレコーダーが1つも無い場合と、
    /// 1台も開始できなかった場合は <c>ActivationCommands.ExitCode_RecordingNotExecutable</c>
    /// ── 単体の開始が同じ失敗で非 0 を返すのに、こちらだけ 0 を返すと
    /// バッチは「全部落ちた」を成功として通す。
    /// </summary>
    internal static async Task<StartAllResult> StartAllAsync()
    {
        var controller = await WaitForControllerAsync();
        if (controller is null)
            return new StartAllResult(ActivationCommands.ExitCode_RecorderNotAvailable, [], []);

        if (!controller.CanStartRecordingAll)
            return new StartAllResult(ActivationCommands.ExitCode_RecordingNotExecutable, [], []);

        var started = controller.StartRecordingAllAndReport(out var failed);

        return new StartAllResult(
            started.Count == 0 ? ActivationCommands.ExitCode_RecordingNotExecutable : 0,
            started.Select(r => (r.Name, r.LastFilename)).ToArray(),
            failed.Select(r => r.Name).ToArray());
    }

    /// <summary>
    /// 全レコーダの録画を終了し、<b>全ファイルが確定するまで待つ</b>（排出は並行に走る）。
    /// 返すのは<b>今回停止した分だけ</b> ── 止めていないレコーダーの結果は前回の録画のもので、
    /// 混ぜると「今回は正常に録れたのに前回の失敗で失敗が返る」ことになる。
    /// </summary>
    internal static async Task<StopAllResult> StopAllAsync()
    {
        var controller = await WaitForControllerAsync();
        if (controller is null)
            return new StopAllResult(ActivationCommands.ExitCode_RecorderNotAvailable, []);

        if (!controller.CanStopRecordingAll)
            return new StopAllResult(ActivationCommands.ExitCode_RecordingNotExecutable, []);

        var stopped = await controller.StopRecordingAllAsync();

        return new StopAllResult(0,
            stopped.Select(r => (r.Name, r.LastFilename, Outcome: r.LastStopOutcome)).ToArray());
    }

    /// <summary>テンプレート変数を設定する（セッション限り。永続化は
    /// <see cref="TrySetVariablePersistent"/> が別に決める）。</summary>
    internal static void SetVariable(string key, string value)
        => EventRecorder.SetTemplateVariable(key, value);

    /// <summary>テンプレート変数を1件読む。未定義なら <see langword="false"/>。</summary>
    internal static bool TryGetVariable(string key, out string? value)
    {
        if (EventRecorder.TemplateVariables.TryGetValue(key, out string? existing))
        {
            value = existing;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>テンプレート変数を全件、<b>キーの序数順</b>で返す（機械可読な出力の並びを固定するため）。</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> GetVariables()
        => EventRecorder.TemplateVariables.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// そのテンプレート変数を settings.json に残すかどうかを設定する。
    /// <b>未定義のキーなら何もせず <see langword="false"/></b>（呼び出し側はこれを
    /// <c>ActivationCommands.ExitCode_VariableNotDefined</c> にする）。
    /// 既に同じ状態だった場合は <see langword="true"/> ── 「指定が通った」かどうかだけを表し、
    /// 変化の有無は区別しない。
    /// </summary>
    internal static bool TrySetVariablePersistent(string key, bool persistent)
    {
        if (!EventRecorder.TemplateVariables.ContainsKey(key))
            return false;

        Settings.AppSettings.Default.SetTemplateVariablePersistent(key, persistent);
        return true;
    }

    /// <summary>
    /// <c>start-recording &lt;target&gt;</c> / <c>stop-recording &lt;target&gt;</c> の対象解決。
    /// 数値はインデックス（0始まり）、それ以外は名前
    /// （規則は <see cref="RecorderCliRules.ResolveTargetIndex"/>。L1 が守っている）。
    /// </summary>
    private static GstEventRecorderViewModel? Resolve(GstControllerViewModel controller, string target)
    {
        int index = RecorderCliRules.ResolveTargetIndex(
            controller.Recorders.Select(r => r.Name).ToArray(), target);
        return 0 <= index ? controller.Recorders[index] : null;
    }
}
