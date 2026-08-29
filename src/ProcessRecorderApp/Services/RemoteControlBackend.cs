using Microsoft.UI.Dispatching;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.GStreamer;
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
    public Task<string> GetRecordingsRootAsync(CancellationToken ct)
        => RunOnUiAsync(() =>
            // **録画の書き込み先と同じ式で解決する**（AppSettings が
            // EventRecorder.OutputDirectory へ写すときと同じ）── 別々に書くと、
            // 相対パスの基準がずれた日に「録画はできるのに一覧に出ない」になる。
            Task.FromResult(Components.AppDirectories.ResolveOrBase(Settings.AppSettings.Default.OutputDirectory)));

    // ---- 書き込み ----
    //
    // **CancellationToken を受け取らない**（インターフェイスの doc 参照）。要求元が切っても
    // 開始・停止・設定変更は完遂させる ── ここで畳むと「録画は始まったが誰も知らない」
    // 状態が残る。捨てられるのは応答だけである。
    //
    // **文言は CLI と同じリソースを引く。** 番号（終了コード）だけを揃えて文言を別に書くと、
    // 同じ失敗が呼び出し面ごとに違う説明で出る。

    /// <summary>HTTP API を出どころとする録画の <c>trigger</c>（sidecar に載る）。</summary>
    private const string RemoteTrigger = "remote";

    /// <inheritdoc/>
    public Task<RecorderActionResult> StartAsync(string id)
        => RunOnUiAsync(async () =>
        {
            var result = await RecorderControlService.StartAsync(id, RemoteTrigger);
            if (result.ExitCode != 0)
                throw CommandFailure(result.ExitCode, id, result.RecorderName, start: true);

            return new RecorderActionResult(result.RecorderName ?? id, result.LastFilename);
        });

    /// <inheritdoc/>
    public Task<RecorderActionResult> StopAsync(string id)
        => RunOnUiAsync(async () =>
        {
            var result = await RecorderControlService.StopAsync(id);
            if (result.ExitCode != 0)
                throw CommandFailure(result.ExitCode, id, result.RecorderName, start: false);

            // **使えない成果物を成功として返さない。** 停止処理そのものは成功しているので、
            // 分かるのは「何が残ったか」だけ ── ファイルのパスは載せる（救済できる）。
            int outcomeCode = RecorderControlService.ExitCodeFor(result.Outcome);
            if (outcomeCode != 0)
                throw StopOutcomeFailure(result.Outcome, result.RecorderName, result.LastFilename);

            return new RecorderActionResult(result.RecorderName ?? id, result.LastFilename);
        });

    /// <inheritdoc/>
    public Task<RemoteControl.StartAllResult> StartAllAsync()
        => RunOnUiAsync(async () =>
        {
            var result = await RecorderControlService.StartAllAsync(RemoteTrigger);
            if (result.ExitCode != 0)
            {
                throw new RemoteApiException(result.ExitCode, Localization.GetString(
                    result.ExitCode == ActivationCommands.ExitCode_RecorderNotAvailable
                        ? "Resources/Cli_RecorderNotAvailable"
                        : "Resources/Cli_NoRecorderCanStart"));
            }

            return new RemoteControl.StartAllResult(
                result.Started.Select(r => new RecorderActionResult(r.Name, r.LastFilename)).ToArray(),
                result.FailedRecorders.ToArray());
        });

    /// <inheritdoc/>
    public Task<RemoteControl.StopAllResult> StopAllAsync()
        => RunOnUiAsync(async () =>
        {
            var result = await RecorderControlService.StopAllAsync();
            if (result.ExitCode != 0)
            {
                throw new RemoteApiException(result.ExitCode, Localization.GetString(
                    result.ExitCode == ActivationCommands.ExitCode_RecorderNotAvailable
                        ? "Resources/Cli_RecorderNotAvailable"
                        : "Resources/Cli_NoRecorderCanStop"));
            }

            // **1 本でも使えなければ全体を失敗にする**（CLI の stop-recording-all と同じ）。
            // 200 で返すと、呼び出し側は行ごとの終了コードを見ない限り壊れに気付けない。
            int folded = RecorderControlService.FoldStopExitCode(result.Stopped.Select(r => r.Outcome));
            if (folded != 0)
            {
                var worst = result.Stopped
                    .First(r => RecorderControlService.ExitCodeFor(r.Outcome) == folded);
                throw StopOutcomeFailure(worst.Outcome, worst.Name, worst.LastFilename);
            }

            return new RemoteControl.StopAllResult(
                result.Stopped
                    .Select(r => new StopItemResult(
                        r.Name, r.LastFilename, RecorderControlService.ExitCodeFor(r.Outcome)))
                    .ToArray());
        });

    /// <inheritdoc/>
    public Task<VariablesDto> GetVariablesAsync()
        => RunOnUiAsync(() => Task.FromResult(
            new VariablesDto(RecorderControlService.GetVariableDtos())));

    /// <inheritdoc/>
    public Task<VariableDto> PutVariableAsync(string key, string? value, bool? persist)
        => RunOnUiAsync(() =>
        {
            // **順序は値 → 保存**（CLI の --set → --persist と同じ）。逆にすると、
            // 「新しい変数を作って保存する」1 回の要求が「未定義」で落ちる。
            if (value is not null)
                RecorderControlService.SetVariable(key, value);

            if (persist is bool wanted && !RecorderControlService.TrySetVariablePersistent(key, wanted))
                throw VariableNotDefined(key);

            return Task.FromResult(RecorderControlService.GetVariableDto(key) ?? throw VariableNotDefined(key));
        });

    /// <inheritdoc/>
    public Task<RecorderSettingsDto> GetRecorderSettingsAsync(string id)
        => RunOnUiAsync(async () =>
        {
            var result = await RecorderControlService.GetRecorderSettingsAsync(id);
            return result.Settings ?? throw CommandFailure(result.ExitCode, id, null, start: false);
        });

    /// <inheritdoc/>
    public Task<PatchResultDto> PatchRecorderSettingsAsync(string id, JsonObject patch)
        => RunOnUiAsync(async () =>
        {
            var outcome = await RecorderControlService.PatchRecorderSettingsAsync(id, patch);
            return ToPatchResult(outcome, id);
        });

    /// <inheritdoc/>
    public Task<PatchResultDto> PatchAppSettingsAsync(JsonObject patch)
        => RunOnUiAsync(() => Task.FromResult(
            ToPatchResult(RecorderControlService.PatchAppSettings(patch), string.Empty)));

    /// <summary>
    /// 候補の一覧を作り直さずに配る時間。<b>要求ごとには列挙しない</b> ── モニターとカメラの
    /// 列挙は 1 回ごとに <c>monitor.devices</c> / <c>camera.devices</c> を activity.log へ書き、
    /// しかも UI スレッド上で走る。この経路は <c>Viewer</c>（ゲスト読み取りが ON なら未認証）なので、
    /// 繰り返し叩くだけで直近の記録を押し流せる形と、UI スレッドを占有できる形が同時に立つ。
    /// 一覧の中身が変わるのは機器の抜き差しのときだけなので、この長さで足りる。
    /// </summary>
    private static readonly TimeSpan SourcesCacheLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 一覧の構築を<b>同時 1 本</b>に絞る（束で届いた要求が同じ列挙を並べて走らせない）。
    /// 待ちは要求の中断で打ち切る。
    /// </summary>
    private readonly SemaphoreSlim _sourcesGate = new(1, 1);

    /// <summary>直前に組んだ一覧と、それを組んだ時刻（対で読み書きする）。</summary>
    private readonly object _sourcesLock = new();
    private SourcesDto? _sources;
    private long _sourcesBuiltAt;

    /// <inheritdoc/>
    public async Task<SourcesDto> GetSourcesAsync(CancellationToken ct)
    {
        // **判定は UI スレッドへ乗り換える前に。** 乗り換えてから見ても列挙は避けられるが、
        // 叩かれた数だけ UI スレッドに仕事が積まれる。
        if (CachedSources() is { } fresh)
            return fresh;

        await _sourcesGate.WaitAsync(ct);
        try
        {
            if (CachedSources() is { } cached)
                return cached;

            // **UI スレッドで組む。** カタログの遅延初期化はローカライズ資源を引き、
            // 動的候補は GStreamer のデバイス列挙を通る ── どちらもビルダーのダイアログが
            // UI スレッド上で行っているのと同じ呼び出しである。
            var built = await RunOnUiAsync(() =>
            {
                var catalog = new DynamicSourceChoices();
                return Task.FromResult(new SourcesDto(
                    SrcPipelineBuilder.Sources.Select(def => ToSourceDef(def, catalog)).ToArray()));
            });

            lock (_sourcesLock)
            {
                _sources = built;
                _sourcesBuiltAt = Environment.TickCount64;
            }
            return built;
        }
        finally
        {
            _sourcesGate.Release();
        }
    }

    /// <summary>まだ古くなっていない一覧（無ければ <see langword="null"/>）。</summary>
    private SourcesDto? CachedSources()
    {
        lock (_sourcesLock)
        {
            return _sources is not null
                   && Environment.TickCount64 - _sourcesBuiltAt < (long)SourcesCacheLifetime.TotalMilliseconds
                ? _sources
                : null;
        }
    }

    /// <summary>
    /// このランタイムに<b>実在する</b>プロパティだけ。<c>capture-api</c> のように
    /// ビルド構成で有無が変わるもの（GStreamer の conditionally available）をそのまま配ると、
    /// 適用は 200 で通ったうえで <c>parse_launch</c> が <c>no property</c> で落ちる
    /// ── 同梱の MinGW 版に <c>capture-api</c> は無く、画面は既定値をそのまま送ってくる。
    /// 判定はビルダーのダイアログと同じ（<c>PipelineBuilderViewModel</c>）。
    /// <b>配らないものは検証でも断る</b> ── 一覧も検証も組み立ても、この列だけを見る。
    /// </summary>
    private static IEnumerable<SrcPropertyDef> AvailableProperties(SrcElementDef def)
        => def.Properties.Where(
            p => !p.ConditionallyAvailable || GstIntrospect.ElementHasProperty(def.ElementName, p.Name));

    private static SourceDefDto ToSourceDef(SrcElementDef def, DynamicSourceChoices catalog) => new(
        def.ElementName,
        def.DisplayName,
        def.MemoryFeature,
        SourcePresetRules.RecordingTypeFor(def.MemoryFeature),
        AvailableProperties(def)
            .Select(p => new RemoteControl.SourcePropertyDto(
                p.Name, KindName(p.Kind), p.DefaultValue, ChoicesFor(p, catalog),
                p.Description, p.ConditionallyAvailable))
            .ToArray(),
        def.CapsFields
            .Select(c => new CapsFieldDto(c.Name, c.IsResolution, c.DefaultValue, ChoicesFor(c, catalog)))
            .ToArray());

    /// <summary>
    /// 封筒に出る値の種別の名前。<b><c>Enum.ToString()</c> は使わない</b>
    /// ── Native AOT で列挙のメタデータの保持を要求する（<c>AuthEndpoints.RoleName</c> と同じ理由）。
    /// 綴りの正本は <see cref="SourcePresetRules.PropertyKinds"/> で、
    /// 一致は L1 の <c>SourcePresetRulesTests</c> が縛る。
    /// </summary>
    private static string KindName(SrcPropertyKind kind) => kind switch
    {
        SrcPropertyKind.Bool => SourcePresetRules.KindBool,
        SrcPropertyKind.Int => SourcePresetRules.KindInt,
        SrcPropertyKind.Enum => SourcePresetRules.KindEnum,
        SrcPropertyKind.String => SourcePresetRules.KindString,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// そのプロパティで選べる値。<b>静的な選択肢を優先する</b>
    /// （動的候補は「実行時にしか分からないもの」のためにある）。
    /// </summary>
    private static IReadOnlyList<string>? ChoicesFor(SrcPropertyDef def, DynamicSourceChoices catalog)
        => def.EnumChoices ?? catalog.For(def.DynamicKey);

    /// <inheritdoc cref="ChoicesFor(SrcPropertyDef, DynamicSourceChoices)"/>
    private static IReadOnlyList<string>? ChoicesFor(CapsFieldDef def, DynamicSourceChoices catalog)
        => def.Choices ?? catalog.For(def.DynamicKey);

    /// <inheritdoc/>
    public Task<SourceApplyResultDto> ApplySourceAsync(string id, SourcePresetDto preset)
        => RunOnUiAsync(async () =>
        {
            var properties = preset.Properties ?? EmptyValues;
            var caps = preset.Caps ?? EmptyValues;

            // **要素が無ければ検証そのものが「未知の要素」で断る。** 判定を 2 か所に
            // 分けない（純関数の側だけを見れば、何を通すかが全部読める）。
            SrcElementDef? def = SrcPipelineBuilder.FindSource(preset.Element);
            var catalog = new DynamicSourceChoices();
            if (!SourcePresetRules.Validate(ToSpec(def, catalog), properties, caps, out string? error))
                throw new RemoteApiException(ActivationCommands.ExitCode_InvalidArguments, error);

            // ここまで来た＝カタログに在る（無ければ Validate が「未知の要素」で断っている）。
            SrcElementDef element = def!;

            // 出力の並びはカタログの並び（要求の JSON の並びに左右されない）。
            var ordered = AvailableProperties(element)
                .Where(p => properties.ContainsKey(p.Name))
                .Select(p => (p.Name, properties[p.Name]))
                .ToArray();

            string srcPipeline = SrcPipelineBuilder.Assemble(
                element, capsEnabled: 0 < caps.Count, ordered, caps);
            string recordingType = SourcePresetRules.RecordingTypeFor(element.MemoryFeature);

            var outcome = await RecorderControlService.ApplySourceAsync(id, srcPipeline, recordingType);
            if (outcome.ExitCode == ActivationCommands.ExitCode_RecordingNotExecutable)
                throw new RemoteApiException(outcome.ExitCode, "the recorder is recording");

            var applied = ToPatchResult(outcome, id);
            return new SourceApplyResultDto(
                applied.Applied, applied.Clamped, applied.RequiresReinitialize, srcPipeline);
        });

    /// <summary>要求に片方しか無いときに渡す空の辞書（毎回作らない）。</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyValues =
        new Dictionary<string, string>();

    /// <summary>
    /// カタログ定義を、検証に要る情報だけの軽い記述へ写す
    /// （<c>Components</c> は <c>GStreamer.GstSharpNet</c> を参照できない）。
    /// </summary>
    private static SourceSpec? ToSpec(SrcElementDef? def, DynamicSourceChoices catalog)
        => def is null
            ? null
            : new SourceSpec(
                def.ElementName,
                AvailableProperties(def)
                    .Select(p => new SourcePropertySpec(p.Name, KindName(p.Kind), ChoicesFor(p, catalog)))
                    .ToArray(),
                def.CapsFields.Select(c => new SourceCapsSpec(c.Name, c.IsResolution)).ToArray());

    /// <summary>
    /// 実行時にしか分からない選択肢（モニターとカメラ）。<b>1 回の組み立てのあいだだけ持つ</b>
    /// ── 列挙は要素の数だけ現れるので、都度問い合わせるとカメラの列挙が何度も走る。
    /// <b>失敗も覚える</b>（空の一覧として持つ）ので、1 回の組み立ての中で
    /// 同じ列挙を何度も試すことはない。
    /// 一覧（<see cref="GetSourcesAsync"/>）はさらに組んだ DTO を
    /// <see cref="SourcesCacheLifetime"/>（30 秒）だけ持ち回すので、列挙が走るのは
    /// その間隔に 1 回であり、<b>機器を挿してから選択肢に出るまで最大でその分だけ遅れる</b>。
    ///
    /// <para>
    /// <b>解決するキーはビルダーのダイアログと同じ規則で選ぶ</b>
    /// （<c>PipelineBuilderViewModel.GetDynamicChoices</c>）。ただし
    /// <c>monitor-resolution</c> だけは<b>全モニターの実寸</b>を並べる ── あちらは
    /// 「いま選ばれている <c>monitor-index</c>」のものだけを出すが、候補の一覧に
    /// 「選ばれているもの」は存在しない。
    /// </para>
    /// <para>読めなければ null（＝自由入力）を返す。ここで投げると一覧そのものが 500 になる。</para>
    /// </summary>
    private sealed class DynamicSourceChoices
    {
        private IReadOnlyList<MonitorInfo>? _monitors;
        private IReadOnlyList<VideoDeviceInfo>? _devices;

        /// <summary>
        /// <b>列挙は 1 回だけ試す。失敗も覚える。</b> 例外のときに <c>??=</c> のままだと
        /// 次のキーでもう一度列挙され、同じ失敗が要素の数だけ <c>activity.log</c> へ出る
        /// （組み立てた一覧そのものは <see cref="SourcesCacheLifetime"/> のキャッシュに乗るので、
        /// 間隔をまたぐ再試行は妨げない）。
        /// </summary>
        private IReadOnlyList<MonitorInfo> Monitors => _monitors ??= Enumerate(GstIntrospect.GetMonitors, "monitors");

        /// <inheritdoc cref="Monitors"/>
        private IReadOnlyList<VideoDeviceInfo> Devices
            => _devices ??= Enumerate(GstIntrospect.GetVideoSourceDevices, "video devices");

        /// <summary>失敗を空の一覧として覚える（＝以後このインスタンスでは再試行しない）。</summary>
        private static IReadOnlyList<T> Enumerate<T>(Func<IReadOnlyList<T>> enumerate, string what)
        {
            try
            {
                return enumerate();
            }
            catch (Exception ex)
            {
                ActivityLog.Warn("remote.error", $"source choices ({what}): {ex.Message}");
                return [];
            }
        }

        public IReadOnlyList<string>? For(string? dynamicKey)
        {
            try
            {
                return dynamicKey switch
                {
                    "monitor-index" => Indices(Math.Max(1, GstIntrospect.GetMonitorCount())),
                    "monitor-device-path" => NonEmpty(Monitors.Select(m => m.Path)),
                    "monitor-resolution" => NonEmpty(Monitors.Select(m => m.Resolution)),
                    "mf-device-index" => 0 < Devices.Count ? Indices(Devices.Count) : null,
                    "mf-device-name" => NonEmpty(Devices.Select(d => d.Name)),
                    // **ここから 4 つはビルダーのダイアログと同じ規則**
                    // （<c>PipelineBuilderViewModel.GetDynamicChoices</c>）。
                    // 対象は String のプロパティ（<c>device-path</c>）と caps のフィールド
                    // （<c>format</c> / <c>resolution</c> / <c>framerate</c>）で、
                    // どちらも候補が無くても自由入力は通る。落とすと、カメラを選んだ
                    // ブラウザにだけ選択肢が出ず、<b>値を手で書き写すしかなくなる</b>
                    // ── デバイスのシンボリックリンクや対応解像度は、画面から
                    // 知る手立てが他に無い。
                    "mf-device-path" => NonEmpty(Devices.Select(d => d.Path)),
                    "mf-format" => NonEmpty(Devices.SelectMany(d => d.Formats)),
                    "mf-resolution" => NonEmpty(Devices.SelectMany(d => d.Resolutions)),
                    "mf-framerate" => NonEmpty(Devices.SelectMany(d => d.Framerates)),
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                ActivityLog.Warn("remote.error", $"source choices ({dynamicKey}): {ex.Message}");
                return null;
            }
        }

        private static string[] Indices(int count)
            => [.. Enumerable.Range(0, count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))];

        private static string[]? NonEmpty(IEnumerable<string> values)
        {
            string[] found = [.. values.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal)];
            return 0 < found.Length ? found : null;
        }
    }

    private static PatchResultDto ToPatchResult(SettingsPatchOutcome outcome, string target)
    {
        if (outcome.ExitCode == 0)
            return new PatchResultDto(outcome.Applied, outcome.Clamped, outcome.RequiresReinitialize);

        // **どのキーが駄目だったのかを必ず載せる。** 「要求が不正」だけでは、
        // 呼び出し側は何を直せばよいか分からない（キーの綴りか、値の型か）。
        throw outcome.Rejection switch
        {
            SettingsPatchRejection.UnknownKey =>
                new RemoteApiException(outcome.ExitCode, $"unknown key: {outcome.RejectedKey}"),
            SettingsPatchRejection.NotEditable =>
                new RemoteApiException(outcome.ExitCode, $"key not editable: {outcome.RejectedKey}"),
            // キーが分からないまま「invalid value for 」と尻切れの文を返さない。
            SettingsPatchRejection.InvalidValue => new RemoteApiException(
                outcome.ExitCode,
                outcome.RejectedKey is null ? "invalid value" : $"invalid value for {outcome.RejectedKey}"),
            _ => CommandFailure(outcome.ExitCode, target, null, start: false),
        };
    }

    /// <summary>
    /// 実行される前に断られた開始／停止（12 / 13 / 14）を例外へ写す。
    /// 文言は CLI の <c>RecorderCommandFailure</c> と同じリソース
    /// （末尾の改行だけ付けない ── あれは CLI の出力の組み立てであって文言ではない）。
    /// </summary>
    private static RemoteApiException CommandFailure(
        int exitCode, string target, string? recorderName, bool start) => exitCode switch
        {
            ActivationCommands.ExitCode_RecorderNotAvailable =>
                new RemoteApiException(exitCode, Localization.GetString("Resources/Cli_RecorderNotAvailable")),
            ActivationCommands.ExitCode_RecorderNotFound =>
                new RemoteApiException(exitCode, Localization.GetString("Resources/Cli_RecorderNotFound", target)),
            ActivationCommands.ExitCode_RecordingNotExecutable =>
                new RemoteApiException(exitCode, Localization.GetString(
                    start ? "Resources/Cli_CannotStartInState" : "Resources/Cli_CannotStopInState", recorderName)),
            // 未知の非 0 を成功に化かさない。
            _ => new RemoteApiException(exitCode, "the command failed"),
        };

    /// <summary>
    /// 停止は済んだが成果物が使えない（16 / 17）。<b>ファイルのパスを載せる</b> ──
    /// 呼び出し側はそれで後始末や救済ができる（CLI が標準出力へパスを出すのと同じ）。
    /// </summary>
    private static RemoteApiException StopOutcomeFailure(
        RecordingStopOutcome outcome, string? recorderName, string? filename)
        => new(RecorderControlService.ExitCodeFor(outcome),
               Localization.GetString(
                   RecorderControlService.StopFailureMessageKey(outcome), recorderName ?? string.Empty))
        {
            Filename = filename,
        };

    private static RemoteApiException VariableNotDefined(string key)
        => new(ActivationCommands.ExitCode_VariableNotDefined,
               Localization.GetString("Resources/Cli_VariableNotFound", key));

    /// <inheritdoc/>
    public Task<PreviewSubscription> SubscribePreviewAsync(string id, CancellationToken ct)
        => RunOnUiAsync(() =>
        {
            if (GstControllerViewModel.Current is not { } controller)
                throw new RemoteApiException(NotAvailableExitCode, "the recording engine is not ready yet");

            // **購読の生成だけを UI スレッドで行う。** 以後の読み出し（channel）は
            // 呼び出し側のスレッドで、UI スレッドには一切戻らない。
            if (!controller.PreviewStreams.TrySubscribe(id, out var subscription, out string? reason))
            {
                // 「対象が無い」だけが 13（404）で、残り（まだ動いていない・上限）は 12。
                // 文字列の正本は Components.PreviewStreamReasons（供給側と共有）。
                throw new RemoteApiException(
                    reason == PreviewStreamReasons.RecorderNotFound
                        ? ActivationCommands.ExitCode_RecorderNotFound
                        : NotAvailableExitCode,
                    reason);
            }

            return Task.FromResult(subscription);
        });

    /// <inheritdoc/>
    public Task<DashPreviewSnapshot> GetDashPreviewSnapshotAsync(string id, CancellationToken ct)
        => RunOnUiAsync(() =>
        {
            if (GstControllerViewModel.Current is not { } controller)
                throw new RemoteApiException(NotAvailableExitCode, "the recording engine is not ready yet");

            // **UI スレッドで行うのは取り出しだけ。** 返す姿は不変なので、
            // 呼び出し側はそのまま自分のスレッドで書き出せる。
            if (!controller.DashPreviews.TryGetSnapshot(id, out var snapshot, out string? reason))
            {
                // 「対象が無い」だけが 13（404）で、残り（まだ始まっていない・
                // エンコーダーが無い・まだ動いていない）は 12。
                // 文字列の正本は Components.PreviewStreamReasons（供給側と共有）。
                throw new RemoteApiException(
                    reason == PreviewStreamReasons.RecorderNotFound
                        ? ActivationCommands.ExitCode_RecorderNotFound
                        : NotAvailableExitCode,
                    reason);
            }

            return Task.FromResult(snapshot);
        });

    /// <inheritdoc/>
    public Task<PreviewQualityState> GetPreviewQualityAsync(string id, CancellationToken ct)
        => RunOnUiAsync(() =>
        {
            if (GstControllerViewModel.Current is not { } controller)
                throw new RemoteApiException(NotAvailableExitCode, "the recording engine is not ready yet");

            if (!controller.DashPreviews.TryGetQuality(id, out var state, out string? reason))
                throw PreviewQualityFailure(reason);

            return Task.FromResult(state);
        });

    /// <inheritdoc/>
    public Task<PreviewQualityState> SetPreviewQualityAsync(string id, string qualityId, CancellationToken ct)
        => RunOnUiAsync(() =>
        {
            if (GstControllerViewModel.Current is not { } controller)
                throw new RemoteApiException(NotAvailableExitCode, "the recording engine is not ready yet");

            if (!controller.DashPreviews.TrySetQuality(id, qualityId, out var state, out string? reason))
                throw PreviewQualityFailure(reason);

            return Task.FromResult(state);
        });

    /// <summary>
    /// 画質の読み書きの失敗。<b>「対象が無い」だけが 13（404）</b>で、残りは 12
    /// （<c>GetDashPreviewSnapshotAsync</c> と同じ写像）。
    /// </summary>
    private static RemoteApiException PreviewQualityFailure(string? reason)
        => new(reason == PreviewStreamReasons.RecorderNotFound
                   ? ActivationCommands.ExitCode_RecorderNotFound
                   : NotAvailableExitCode,
               reason ?? "the preview quality is not available");

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
