using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using ProcessRecorderApp.Components;
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
/// ── 16 / 17 への写像は <see cref="RecorderControlService.ExitCodeFor"/> が行う。
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
/// 部分更新（PATCH）を受け付けられなかった理由。<b>文言は持たない</b>
/// ── 呼び出し面ごとの言い回しは呼び出し側が決める。
/// </summary>
internal enum SettingsPatchRejection
{
    /// <summary>断っていない（成功、または対象そのものが見つからない等の別の失敗）。</summary>
    None,

    /// <summary>そのキーの設定項目が無い。</summary>
    UnknownKey,

    /// <summary>実在するが、リモートからの編集を許していないキー。</summary>
    NotEditable,

    /// <summary>キーは正しいが、値がその型として読めない。</summary>
    InvalidValue,
}

/// <summary>
/// <see cref="RecorderControlService.PatchRecorderSettingsAsync"/> /
/// <see cref="RecorderControlService.PatchAppSettings"/> の結果。
/// <see cref="ExitCode"/> は 4（要求が不正）/ 12 / 13 / 0。
/// </summary>
/// <param name="Clamped">
/// 要求した値と、書き込んだ後の現在値が違うキー。<b>成功のまま値が変わっている</b>ことを
/// 呼び出し側へ伝えるためにある。
/// </param>
internal sealed record SettingsPatchOutcome(
    int ExitCode,
    SettingsPatchRejection Rejection,
    string? RejectedKey,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Clamped,
    IReadOnlyList<string> RequiresReinitialize)
{
    /// <summary>失敗（何も書いていない）。</summary>
    internal static SettingsPatchOutcome Rejected(int exitCode, SettingsPatchRejection rejection, string? key)
        => new(exitCode, rejection, key, [], [], []);
}

/// <summary>
/// <see cref="RecorderControlService.GetRecorderSettingsAsync"/> の結果。
/// <see cref="ExitCode"/> は 12 / 13 / 0。
/// </summary>
internal sealed record RecorderSettingsResult(int ExitCode, RemoteControl.RecorderSettingsDto? Settings);

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

    // ---- 停止の結果 → 終了コード ----

    /// <summary>
    /// 停止の結果 → 終了コード。<b>規則（どれが失敗か・どちらが強いか）は
    /// <see cref="RecordingStopRules"/> にあり、L1 が守っている</b>
    /// ── ここは番号の割り当てだけを持つ。
    ///
    /// <para>
    /// <b>CLI と HTTP で共有する。</b> 片方だけ直すと、同じ壊れ方が呼び出し面ごとに
    /// 違う番号で出る（終了経路は 2 つあり、片方だけ塞いでも防げない）。
    /// </para>
    /// </summary>
    internal static int ExitCodeFor(RecordingStopOutcome outcome) => outcome switch
    {
        RecordingStopOutcome.NotFinalized => ActivationCommands.ExitCode_RecordingNotFinalized,
        RecordingStopOutcome.Empty => ActivationCommands.ExitCode_RecordingProducedNothing,
        _ => 0,
    };

    /// <summary>停止の結果 → 文言のリソースキー（<see cref="ExitCodeFor"/> と同じく共有）。</summary>
    internal static string StopFailureMessageKey(RecordingStopOutcome outcome) => outcome switch
    {
        RecordingStopOutcome.NotFinalized => "Resources/Cli_RecordingNotFinalized",
        _ => "Resources/Cli_RecordingProducedNothing",
    };

    /// <summary>
    /// 複数レコーダーの停止結果を 1 つの終了コードへ畳む。
    /// <b>返すのは「強い方」</b>（規則は <see cref="RecordingStopRules.Stronger"/>）──
    /// 混在した場合に弱い方を返すと、救済できたはずのデータを捨てさせる。
    /// </summary>
    internal static int FoldStopExitCode(IEnumerable<RecordingStopOutcome> outcomes)
        => ExitCodeFor(outcomes.Aggregate(RecordingStopOutcome.Ok, RecordingStopRules.Stronger));

    // ---- テンプレート変数（リモート操作用の写し） ----

    /// <summary>
    /// テンプレート変数を全件、キーの序数順で <see cref="RemoteControl.VariableDto"/> にする。
    /// 「保存するか」は変数そのものではなく設定側が持っている。
    /// </summary>
    internal static IReadOnlyList<RemoteControl.VariableDto> GetVariableDtos()
        => GetVariables().Select(pair => new RemoteControl.VariableDto(
            pair.Key, pair.Value, Settings.AppSettings.Default.IsTemplateVariablePersistent(pair.Key))).ToArray();

    /// <summary>テンプレート変数を 1 件。未定義なら <see langword="null"/>。</summary>
    internal static RemoteControl.VariableDto? GetVariableDto(string key)
        => TryGetVariable(key, out string? value)
            ? new RemoteControl.VariableDto(
                key, value ?? string.Empty, Settings.AppSettings.Default.IsTemplateVariablePersistent(key))
            : null;

    // ---- レコーダー設定の読み書き ----

    /// <summary>
    /// <paramref name="target"/> の設定オブジェクト（<b>ビューモデルではなく設定側</b>）を得る。
    ///
    /// <para>
    /// <b>対象の解決は <see cref="Resolve"/> と同じ規則で、同じ並びに対して行う。</b>
    /// レコーダーのビューモデルは設定のコレクションと同じ順序で作られ、増減も追随するので、
    /// 名前の一覧で引いたインデックスは設定側でもそのまま使える。
    /// </para>
    /// </summary>
    private static async Task<(int ExitCode, EventRecorderSettings? Live)> ResolveSettingsAsync(string target)
    {
        var controller = await WaitForControllerAsync();
        if (controller is null)
            return (ActivationCommands.ExitCode_RecorderNotAvailable, null);

        int index = RecorderCliRules.ResolveTargetIndex(
            controller.Recorders.Select(r => r.Name).ToArray(), target);

        var recorders = Settings.AppSettings.Default.Recorders;
        if (index < 0 || recorders.Count <= index)
            return (ActivationCommands.ExitCode_RecorderNotFound, null);

        return (0, recorders[index]);
    }

    /// <summary>1 レコーダーの現在値と項目の説明。</summary>
    internal static async Task<RecorderSettingsResult> GetRecorderSettingsAsync(string target)
    {
        var (exitCode, live) = await ResolveSettingsAsync(target);
        if (live is null)
            return new RecorderSettingsResult(exitCode, null);

        var values = JsonSerializer.SerializeToNode(live, Settings.AppSettings.RecorderTypeInfo) as JsonObject ?? [];

        var properties = live.GetProperties()
            .Where(p => p.CanRead && p.CanWrite)
            .Select(DescribeRecorderProperty)
            .ToArray();

        return new RecorderSettingsResult(0, new RemoteControl.RecorderSettingsDto(values, properties));
    }

    /// <summary>
    /// 設定項目 1 つの説明。<b>カテゴリと説明はここで訳す</b> ──
    /// 属性が持っているのはリソースキーで、訳を引けるのはアプリ側だけである
    /// （<c>PropertyGridView.BuildItems</c> と同じ解決）。
    /// </summary>
    private static RemoteControl.SettingPropertyDto DescribeRecorderProperty(PropertyInfo prop)
    {
        Type type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

        string kind =
            type == typeof(bool) ? "bool"
            : type.IsEnum ? "enum"
            : type == typeof(int) ? "int"
            : "string";

        // カテゴリ未指定の既定は PropertyGridItem.DefaultCategoryKey と同じ値。
        // あちらは internal なので参照できず、ここに書き写している。
        string categoryKey = prop.GetCustomAttribute<CategoryAttribute>()?.Category ?? "PropCat_General";
        string? descriptionKey = prop.GetCustomAttribute<DescriptionAttribute>()?.Description;

        (long? min, long? max) = prop.Name switch
        {
            nameof(EventRecorderSettings.BufferDuration) =>
                (EventRecorderSettings.MinBufferDuration, EventRecorderSettings.MaxBufferDuration),
            nameof(EventRecorderSettings.ContinuousSegmentSeconds) =>
                (EventRecorderSettings.MinContinuousSegmentSeconds, EventRecorderSettings.MaxContinuousSegmentSeconds),
            // 丸めの無い項目に範囲を出さない ── 出すと「その範囲なら通る」という嘘になる。
            _ => ((long?)null, (long?)null),
        };

        return new RemoteControl.SettingPropertyDto(
            prop.Name,
            kind,
            Localization.GetStringOrFallback($"Resources/{categoryKey}", categoryKey),
            descriptionKey is null
                ? null
                : Localization.GetStringOrFallback($"Resources/{descriptionKey}", descriptionKey),
            type.IsEnum ? Enum.GetNames(type) : null,
            min,
            max,
            EventRecorderSettings.PropertiesRequiringReinitialize.Contains(prop.Name, StringComparer.Ordinal),
            // **拒否キーかどうかも配る。** 画面が名前を写すと、拒否リストを増やした日に
            // 画面だけが古いまま「編集できるように見える欄」を出し続ける。
            RemoteApiRules.IsRemoteEditableRecorderSetting(prop.Name));
    }

    /// <summary>
    /// 1 レコーダーの設定を部分更新する。<b>拒否リストに載っているキーは断る</b>
    /// （<see cref="RemoteApiRules.IsRemoteEditableRecorderSetting"/>）。
    ///
    /// <para>
    /// <b><c>Recorders[i]</c> を差し替えない。</b> 設定オブジェクトそのものを新しい実体に
    /// すると、コレクションの変更通知が録画エンジンの作り直しを起こす
    /// ── 録画中の PATCH がパイプラインを落とすことになる。書くのは<b>プロパティだけ</b>で、
    /// これは画面の PropertyGrid が行っているのと同じ経路である。
    /// </para>
    /// </summary>
    internal static async Task<SettingsPatchOutcome> PatchRecorderSettingsAsync(string target, JsonObject patch)
    {
        var (exitCode, live) = await ResolveSettingsAsync(target);
        if (live is null)
            return SettingsPatchOutcome.Rejected(exitCode, SettingsPatchRejection.None, null);

        foreach (var pair in patch)
        {
            if (!RemoteApiRules.IsRemoteEditableRecorderSetting(pair.Key))
            {
                return SettingsPatchOutcome.Rejected(
                    ActivationCommands.ExitCode_InvalidArguments, SettingsPatchRejection.NotEditable, pair.Key);
            }
        }

        return ApplyPatch(
            live, patch, Settings.AppSettings.RecorderTypeInfo,
            live.GetProperties().ToArray(),
            EventRecorderSettings.PropertiesRequiringReinitialize);
    }

    /// <summary>
    /// 1 レコーダーの <c>SrcPipeline</c> と <c>Type</c> を<b>同時に</b>書く
    /// （テンプレートから組み立てた値。組み立てと検証は呼び出し側）。
    ///
    /// <para>
    /// <b>拒否リストは通さない。</b> <see cref="RemoteApiRules.RemoteDeniedRecorderSettings"/> が
    /// 断っているのは<b>任意の文字列</b>としての <c>SrcPipeline</c> であって、
    /// カタログの範囲で組み立てた値ではない ── 経路そのものを分けることで、
    /// 「拒否リストに例外を作る」形（＝あとから緩みうる形）にしない。
    /// </para>
    /// <para>
    /// <b>録画中は断る（14）。</b> どちらのキーも
    /// <see cref="EventRecorderSettings.PropertiesRequiringReinitialize"/> に載っており、
    /// 書けばパイプラインが作り直される ── 走っている録画を黙って落とすことになる。
    /// 判定は <c>Model.IsRecording</c>（実体）で行う ── ビューモデルの同名プロパティは
    /// 復帰待ちを畳んだ表示用の値である。
    /// </para>
    /// </summary>
    internal static async Task<SettingsPatchOutcome> ApplySourceAsync(
        string target, string srcPipeline, string recordingType)
    {
        var controller = await WaitForControllerAsync();
        if (controller is null)
        {
            return SettingsPatchOutcome.Rejected(
                ActivationCommands.ExitCode_RecorderNotAvailable, SettingsPatchRejection.None, null);
        }

        var recorder = Resolve(controller, target);
        if (recorder is null)
        {
            return SettingsPatchOutcome.Rejected(
                ActivationCommands.ExitCode_RecorderNotFound, SettingsPatchRejection.None, null);
        }

        if (recorder.Model.IsRecording)
        {
            return SettingsPatchOutcome.Rejected(
                ActivationCommands.ExitCode_RecordingNotExecutable, SettingsPatchRejection.None, null);
        }

        var (exitCode, live) = await ResolveSettingsAsync(target);
        if (live is null)
            return SettingsPatchOutcome.Rejected(exitCode, SettingsPatchRejection.None, null);

        // **2 キーを 1 回の適用で書く。** 別々に書くと、種別だけが変わった状態で
        // パイプラインが作り直される瞬間ができる（メモリ機能と種別が食い違う）。
        var patch = new JsonObject
        {
            [nameof(EventRecorderSettings.SrcPipeline)] = srcPipeline,
            [nameof(EventRecorderSettings.Type)] = recordingType,
        };

        return ApplyPatch(
            live, patch, Settings.AppSettings.RecorderTypeInfo,
            live.GetProperties().ToArray(),
            EventRecorderSettings.PropertiesRequiringReinitialize);
    }

    /// <summary>
    /// アプリ設定を部分更新する。<b>許可リストに載っていないキーは断る</b>
    /// （<see cref="RemoteApiRules.IsRemoteEditable"/>）。
    /// 値の反映は <c>AppSettings.Default</c> のプロパティへの代入で、
    /// 画面からの編集と同じ経路（＝既存のデバウンス保存が settings.json へ落とす）。
    /// </summary>
    internal static SettingsPatchOutcome PatchAppSettings(JsonObject patch)
    {
        foreach (var pair in patch)
        {
            if (!RemoteApiRules.IsRemoteEditable(pair.Key))
            {
                return SettingsPatchOutcome.Rejected(
                    ActivationCommands.ExitCode_InvalidArguments, SettingsPatchRejection.NotEditable, pair.Key);
            }
        }

        return ApplyPatch(
            Settings.AppSettings.Default, patch, Settings.AppSettings.SettingsTypeInfo,
            typeof(Settings.AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            // アプリ設定に「初期化をやり直すまで効かない」項目は無い
            // （そういうものは拒否リスト側にある）。
            []);
    }

    /// <summary>
    /// 部分更新の本体。<b>検証はキー 1 つずつ、その項目の型情報で行う</b>
    /// ── 要求を丸ごと設定オブジェクトへ戻して検証する（一時インスタンスを作る）道は取れない。
    /// 設定のプロパティは変更通知の受け口を持ち、そこから
    /// <b>プロセス全体の状態</b>（GStreamer のログ水準・ログの保持行数・停止の待ち時間）が
    /// 書き換わる。検証のためだけに作った一時インスタンスでも同じ副作用が起き、
    /// <b>断った要求の副作用だけが残る</b>ことになる（1 キーずつ試す拒否の経路ほど濃く踏む）。
    ///
    /// <para>
    /// 値が妥当かどうかは<b>デシリアライズに答えさせる</b>（型ごとの判定をここに書くと、
    /// settings.json が受け付ける値と PATCH が通す値が二重定義になって食い違う）。
    /// </para>
    /// <para>
    /// <b>全キーの検証を先に済ませてから書く。</b> 1 つでも通らなければ 1 つも書かない
    /// ── 半分だけ適用された設定を残さない。
    /// </para>
    /// <para>
    /// 丸めの検出は<b>書いた後の現在値</b>と要求を比べて行う。
    /// 変換の結果と比べても意味が無い ── setter が丸めるので、
    /// そちらも同じように丸まっていて常に一致する。
    /// </para>
    /// </summary>
    private static SettingsPatchOutcome ApplyPatch<T>(
        T live,
        JsonObject patch,
        JsonTypeInfo<T> typeInfo,
        IReadOnlyList<PropertyInfo> properties,
        IReadOnlyList<string> requiresReinitialize) where T : class
    {
        // 1. キーの検査。値の複製は検証用の JsonObject へ取る（現在値の JSON には重ねない
        //    ── 重ねた木を型へ戻すのが一時インスタンスを作る道そのものである）。
        var staged = new JsonObject();
        if (!RemoteApiRules.TryMergeIntoNode(staged, patch, IsPatchableProperty, out string? unknown))
        {
            return SettingsPatchOutcome.Rejected(
                ActivationCommands.ExitCode_InvalidArguments, SettingsPatchRejection.UnknownKey, unknown);
        }

        // 2. 変換と検証（まだ書かない）。
        var pending = new List<(PropertyInfo Property, object? Value)>(staged.Count);
        foreach (var pair in staged)
        {
            if (!TryConvert(FindJsonProperty(pair.Key)!, pair.Value, out object? value))
            {
                return SettingsPatchOutcome.Rejected(
                    ActivationCommands.ExitCode_InvalidArguments,
                    SettingsPatchRejection.InvalidValue,
                    pair.Key);
            }

            pending.Add((FindProperty(pair.Key)!, value));
        }

        // 3. 書き込み（PropertyGrid の編集と同じ、プロパティへの代入）。
        foreach (var (property, value) in pending)
            property.SetValue(live, value);

        var after = JsonSerializer.SerializeToNode(live, typeInfo) as JsonObject ?? [];

        return new SettingsPatchOutcome(
            0,
            SettingsPatchRejection.None,
            null,
            patch.Select(pair => pair.Key).ToArray(),
            patch.Where(pair => !JsonNode.DeepEquals(after[pair.Key], pair.Value))
                .Select(pair => pair.Key).ToArray(),
            patch.Select(pair => pair.Key)
                .Where(key => requiresReinitialize.Contains(key, StringComparer.Ordinal)).ToArray());

        PropertyInfo? FindProperty(string name)
            => properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal) && p.CanWrite);

        JsonPropertyInfo? FindJsonProperty(string name)
            => typeInfo.Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        // 書き込めるプロパティであることと、settings.json に現れるキーであることの両方を要求する
        // ── 保存されない項目を PATCH できても、次の起動で消える値を書くだけである。
        bool IsPatchableProperty(string name) => FindProperty(name) is not null && FindJsonProperty(name) is not null;
    }

    /// <summary>
    /// 要求の値 1 つを、その設定項目の型へ変換する。
    ///
    /// <para>
    /// <b><see langword="null"/> はデシリアライザへ通さない。</b> あちらは C# の null 許容注釈を
    /// 見ないので、非 null のプロパティにも null を書けてしまう
    /// （settings.json へ落ちた null は、以後の読み込みと録画の開始を壊す）。
    /// 可否は<b>型情報が持つ注釈</b>（<see cref="JsonPropertyInfo.IsSetNullable"/>）で決める
    /// ── ソース生成がコンパイル時に埋めるので Native AOT でも読める
    /// （<c>NullabilityInfoContext</c> はトリム時の既定で無効になる）。
    /// </para>
    /// </summary>
    private static bool TryConvert(JsonPropertyInfo jsonProperty, JsonNode? value, out object? converted)
    {
        converted = null;

        if (value is null)
            return jsonProperty.IsSetNullable;

        try
        {
            converted = JsonSerializer.Deserialize(value, ValueTypeInfo(jsonProperty));
        }
        catch (JsonException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 設定項目 1 つぶんの値の型情報。
    ///
    /// <para>
    /// <b>その項目に付いた変換器を必ず通す。</b> 型に付いた変換器
    /// （<c>EventRecordingType</c> の <c>JsonStringEnumConverter</c>）は型情報を引けば効くが、
    /// <b>プロパティに付いたもの</b>（<c>AppSettings.FramingGrid</c>）は効かない
    /// ── そのままだと settings.json が書く文字列を PATCH だけが読めず、
    /// 代わりに数値を受けてしまう。
    /// </para>
    /// </summary>
    private static JsonTypeInfo ValueTypeInfo(JsonPropertyInfo jsonProperty)
    {
        if (jsonProperty.CustomConverter is null)
            return jsonProperty.Options.GetTypeInfo(jsonProperty.PropertyType);

        var options = new JsonSerializerOptions(jsonProperty.Options);
        options.Converters.Add(jsonProperty.CustomConverter);
        options.MakeReadOnly();
        return options.GetTypeInfo(jsonProperty.PropertyType);
    }
}
