using System;
using System.CommandLine;
using System.CommandLine.Help;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.SingleInstance;
using ProcessRecorderApp.GStreamer;
using ProcessRecorderApp.ViewModels;

namespace ProcessRecorderApp;

/// <summary>
/// 起動引数（コマンドライン）を System.CommandLine で解析し、
/// 対応する処理を実行して <see cref="CommandOutcome"/> を返す。
///
/// 【新しいコマンドの追加方法】
/// <see cref="BuildRootCommand"/> 内に、他のコマンドと同様に
///   1. Argument/Option を定義
///   2. new Command(...) でサブコマンドを作成し、Argument/Optionを紐付け
///   3. SetAction 内で実処理を行い、setOutcome で結果を設定
///   4. rootCommand.Subcommands.Add(...) で登録
/// を追加するだけでよい。ハンドラーの中で setOutcome に
/// <see cref="CommandOutcome.Activate"/> / <see cref="CommandOutcome.Silent"/> /
/// <see cref="CommandOutcome.SilentWithToast"/> / <see cref="CommandOutcome.ActivateWithToast"/> /
/// <see cref="CommandOutcome.Failure"/> のいずれかを渡すことで、
/// 「ウィンドウを表示するか」「トースト通知するか」「処理が失敗したか」を
/// コマンドごとに自由に指定できる。処理が失敗した場合は <see cref="CommandOutcome.Failure"/>
/// を使うことで、呼び出し元ランチャープロセスの終了コードにその失敗を反映できる。
/// </summary>
internal static class ActivationCommands
{
    /// <summary>
    /// 不明なサブコマンド・引数（パースエラー）によりランチャーが終了する場合の終了コード。
    /// <see cref="SingleInstanceManager"/> が予約している終了コード
    /// （<see cref="SingleInstanceManager.ExitCode_WorkerStartFailed"/> = 1、
    /// <see cref="SingleInstanceManager.ExitCode_WorkerResultTimeout"/> = 2 等）と衝突しない値。
    /// </summary>
    public const int ExitCode_InvalidArguments = 4;

    /// <summary>
    /// <c>--get</c> で指定されたキーが <see cref="EventRecorder.TemplateVariables"/> に
    /// 未定義だった場合の終了コード。ランチャー予約の終了コード（1/2/3/5/6）＋パースエラーの 4、
    /// <see cref="CommandOutcome.DefaultFailureExitCode"/>（10）、
    /// <see cref="SingleInstanceManager.UnexpectedErrorExitCode"/>（99）と衝突しない値。
    /// </summary>
    public const int ExitCode_VariableNotDefined = 11;

    /// <summary>
    /// 録画コマンド実行時、まだアプリ（ViewModel）が初期化されておらず録画を制御できない場合の終了コード。
    /// ランチャー予約の終了コード（1/2/3/5/6）＋パースエラーの 4・10・11・99 と衝突しない値。
    /// </summary>
    public const int ExitCode_RecorderNotAvailable = 12;

    /// <summary>
    /// 個別録画コマンドで指定したインデックス／名前に一致するレコーダが存在しない場合の終了コード。
    /// </summary>
    public const int ExitCode_RecorderNotFound = 13;

    /// <summary>
    /// 現在の状態では録画を開始／終了できない（対応する CanStart/StopRecording が false）場合の終了コード。
    /// </summary>
    public const int ExitCode_RecordingNotExecutable = 14;

    /// <summary>
    /// <c>status</c> で、初期化されていない、または直近の障害が残っているレコーダーが
    /// 1つ以上あった場合の終了コード。ランチャー予約の終了コード（1/2/3/5/6）＋パースエラーの 4・10〜14・99 と衝突しない値。
    ///
    /// <para>
    /// <b>これは <c>status</c> だけが返す。</b> 過去の障害を理由に <c>start-recording</c> を
    /// 失敗させることはしない ── 既存のスクリプトの挙動を変えないため（意図的な仕様）。
    /// </para>
    /// </summary>
    public const int ExitCode_RecorderUnhealthy = 15;

    /// <summary>
    /// <c>stop-recording</c> / <c>stop-recording-all</c> で、停止そのものは成功したが
    /// <b>MP4 に1フレームも入っていなかった</b>場合の終了コード。
    ///
    /// <para>
    /// <b>0 を返さないことが要点。</b> 停止処理としては綺麗に終わる（EOS も返り moov も書かれる）
    /// ので、区別しなければ <b>終了コード 0 ＋ 中身の無い MP4</b> になる
    /// ── ルート README が「バッチが <c>%ERRORLEVEL%</c> で成否を判定する」用途を謳っている以上、
    /// <b>それを渡してしまうのが実害の本体</b>である（<c>stop-recording</c> の直後に
    /// <c>copy</c> するバッチが、空のファイルを成果物として運ぶ）。
    /// </para>
    /// <para>
    /// <b>これは原因の修正ではない。</b> 587 バイトの空 MP4 は再現待ちで
    /// 原因が未特定であり、この終了コードは<b>次に起きたときに黙って通り過ぎないようにする</b>
    /// ものである。切り分けの材料は <c>activity.log</c> の
    /// <c>recording.stop empty</c>（<c>samplesSeen</c> / <c>samplesPushed</c> / <c>srcState</c>）に出る。
    /// </para>
    /// <para>
    /// <b>排出が完了しなかった場合は 16 ではなく
    /// <see cref="ExitCode_RecordingNotFinalized"/>（17）</b>
    /// ── 中身が無いと断定できるのは、排出が綺麗に終わった場合だけである。
    /// </para>
    /// </summary>
    public const int ExitCode_RecordingProducedNothing = 16;

    /// <summary>
    /// <c>stop-recording</c> / <c>stop-recording-all</c> で、
    /// <b>排出（ファイルの確定）が完了しなかった</b>場合の終了コード
    /// ── 打ち切り（<c>recording.stop timeout</c>）・バスのエラー
    /// （<c>recording.stop error</c>。ディスク満杯・書込権限など）・排出中の例外。
    ///
    /// <para>
    /// <b>16 と分けているのは、呼び出し側の扱いが変わるからである。</b>
    /// 16（空）は<b>捨ててよい</b>が、こちらは <c>mdat</c> にデータが入っている一方で
    /// <c>moov</c> が未確定なので<b>救済の余地がある</b> ── 捨てる前に修復を試せる。
    /// 終了コードを分ける基準は「呼び出し側の判断が変わるかどうか」であり、
    /// <c>2</c> と <c>5</c>/<c>6</c>（再試行の可否）と同じ規則。
    /// </para>
    /// <para>
    /// <b>両方に該当する場合はこちらを返す。</b> 排出が完了していないなら、
    /// 押し込みが 0 でも「未確定なので触るな」が正しい答えで、
    /// 「空だから捨てろ」ではない。
    /// </para>
    /// <para>
    /// <b>ここを 0（成功扱い）にしてはいけない。</b> 停止処理としては
    /// 「打ち切ったが必ず <c>SetState(Null)</c> はした」形になるが、
    /// 呼び出し側から見れば<b>16 とまったく同じ実害</b>（使えないファイルを
    /// 成果物として運ぶ）であり、16 だけを非 0 にすると
    /// <b>終了コード表に「空なら 16、不完全なら 0」という非対称が残る</b>。
    /// </para>
    /// </summary>
    public const int ExitCode_RecordingNotFinalized = 17;

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
    /// 起動引数（トークン配列）を解析し、実行結果を返す。
    /// サブコマンド未指定（＝素の起動）の場合はウィンドウを表示せず何もしない（Silent）。
    /// 不明なコマンド・パースエラーの場合は「不明な引数」として失敗（Failure）となる。
    /// このメソッドは常駐ワーカー側（<see cref="App.HandleActivation"/>）からのみ呼び出される。
    /// </summary>
    public static async Task<CommandOutcome> Parse(string[] args)
    {
        // 例外で抜ける経路でも cli の行は必ず残す（そのときの終了コードは 99）。
        // finally にしないと、コマンド処理が投げたときに限って記録が消え、
        // 利用者に見えるのが「終了コード 99」だけになる。
        int exitCode = SingleInstanceManager.UnexpectedErrorExitCode;
        try
        {
            var outcome = await ParseCore(args);
            exitCode = outcome.ExitCode;
            return outcome;
        }
        finally
        {
            // CLI の記録は「常駐ワーカー側で1回だけ」。activity.log の書き手をこのプロセスに限れば、
            // 追記の排他（File.AppendAllText は FileShare.Read で開く）を気にしなくてよくなる。
            // 代償として、ランチャーが自前で処理する --help / --version / パースエラーは記録されない。
            ActivityLog.Info("cli",
                $"args=[{string.Join(" ", args)}] exitCode={exitCode}");
        }
    }

    /// <summary><see cref="Parse"/> の本体（結果の記録を挟むために分離してある）。</summary>
    private static async Task<CommandOutcome> ParseCore(string[] args)
    {
        // 既定値：サブコマンドが無い（＝素の起動）場合はウィンドウを表示せず何もしない。
        // （ウィンドウの表示は、タスクトレイアイコンの右クリックメニュー「表示」から行う）
        CommandOutcome outcome = CommandOutcome.Silent();

        var rootCommand = BuildRootCommand(o => outcome = o, out var setOption, out var getOption,
            out var persistOption, out var noPersistOption);
        var parseResult = rootCommand.Parse(args);

        // 未知のサブコマンドや引数エラーの場合は、Activateせず「不明な引数」として失敗を返す。
        // （通常はランチャー側の TryHandleInLauncher が先に検出してリダイレクト自体を行わないため、
        //   ここに到達するのはランチャーを経由しない経路から呼ばれた場合への防御的な処理）
        if (parseResult.Errors.Count > 0)
        {
            return CommandOutcome.Failure();
        }

        // --set の適用（全コマンド共通の前処理として、コマンド本体の実行より先に行う）
        foreach (var pair in parseResult.GetValue(setOption) ?? [])
        {
            int separatorIndex = pair.IndexOf('=');
            EventRecorder.SetTemplateVariable(pair[..separatorIndex], pair[(separatorIndex + 1)..]);
        }

        // --persist / --no-persist の適用（--set の直後・コマンド本体より前）。
        // 未定義のキーは --get と同じ扱いにする（標準エラーへキー名＋終了コード 11）。
        var persistError = new StringBuilder();
        bool hasUndefinedPersistKey = false;
        foreach (var (keys, persistent) in (( string[] Keys, bool Persistent)[])
                 [(parseResult.GetValue(persistOption) ?? [], true),
                  (parseResult.GetValue(noPersistOption) ?? [], false)])
        {
            foreach (var key in keys)
            {
                if (!EventRecorder.TemplateVariables.ContainsKey(key))
                {
                    hasUndefinedPersistKey = true;
                    persistError.AppendLine(Localization.GetString("Resources/Cli_VariableNotFound", key));
                    continue;
                }
                Settings.AppSettings.Default.SetTemplateVariablePersistent(key, persistent);
            }
        }

        // InvokeAsync() はマッチしたコマンドの SetAction を実行する。
        // 録画コマンドは準備完了待ちのため非同期アクションとして登録しており、await で完了を待つ。
        // **既定の例外ハンドラは無効にする** ── 有効のままだと SetAction 内の例外を
        // System.CommandLine が握って戻り値 1 を返すだけになり、setOutcome が呼ばれないまま
        // 既定値の outcome（Silent ＝ 終了コード 0）が返る。開始に失敗した start-recording が
        // 「終了コード 0・空出力」で成功を装う形になるため、例外は呼び出し側
        // （HandleActivation の終了コード 99 の経路）へ素通しする。
        await parseResult.InvokeAsync(new InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
        });

        if (hasUndefinedPersistKey)
        {
            outcome = outcome with
            {
                ConsoleError = (outcome.ConsoleError ?? "") + persistError.ToString(),
                // コマンド本体が既に失敗しているなら、その終了コードを優先する。
                // 上書きすると、17（未確定・救済の余地あり）のような「呼び出し側の後始末が
                // 変わる」失敗が、--persist のタイプミス1つで 11 に化けて隠れる
                // （強い方を返す規則は RecordingStopRules.Stronger と同じ発想）。
                // 未定義キーの指摘そのものは標準エラーに残る。
                ExitCode = outcome.ExitCode != 0 ? outcome.ExitCode : ExitCode_VariableNotDefined,
            };
        }

        // --get の処理（全コマンド共通の後処理。--set 適用後の値を返す）
        if (parseResult.GetResult(getOption) is not null)
        {
            outcome = ApplyGetVariables(outcome, parseResult.GetValue(getOption) ?? []);
        }

        return outcome;
    }

    /// <summary>
    /// <c>--get</c> の結果（テンプレート変数の内容）を outcome のコンソール出力へ反映する。
    /// キー指定なしの場合は全件を「キー=値」形式で、キー指定ありの場合は値のみを1行ずつ出力する。
    ///
    /// <para>
    /// <b>既存の出力へ「追記」する。上書きしてはいけない。</b> <c>--get</c> は
    /// <c>Recursive = true</c> なのでサブコマンドと併用でき、また <c>--persist</c> の
    /// 未定義キーの指摘（<see cref="ParseCore"/>）が先に標準エラーへ積まれている。
    /// 差し替えると <c>status --get</c> で健全性テーブルが消え、
    /// <c>--persist &lt;未定義&gt; --get &lt;定義済み&gt;</c> では
    /// <b>終了コード 11 だけが返って理由がどこにも出ない</b>状態になる。
    /// </para>
    /// <para>
    /// 未定義のキーが1つでもあった場合は、<b>どのキーが未定義だったかを標準エラー出力に</b>
    /// 1行ずつ出し、専用の終了コード（<see cref="ExitCode_VariableNotDefined"/>）を設定する。
    /// 未定義のキーは標準出力に行を出さないため、<b>行番号と引数の位置を対応付けて
    /// 特定することはできない</b> ── 複数キーをまとめて取得したときに
    /// 「どれが無いのか分からない」状態になるのを避けるには、キー名を書くしかない。
    /// </para>
    /// <para>
    /// キー無指定で変数が1件も無い場合は「変数なし」を標準エラー出力に出す。
    /// これは失敗ではないので終了コードは変えない ── 標準出力は
    /// 「キー=値」だけの機械可読な形のままにしておく。
    /// </para>
    /// </summary>
    private static CommandOutcome ApplyGetVariables(CommandOutcome outcome, string[] keys)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        bool hasUndefinedKey = false;

        if (keys.Length == 0)
        {
            // 全件取得
            foreach (var pair in EventRecorder.TemplateVariables.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                output.AppendLine($"{pair.Key}={pair.Value}");
            }

            if (output.Length == 0)
                error.AppendLine(Localization.GetString("Resources/Cli_NoVariables"));
        }
        else
        {
            // 個別取得（値のみを出力する）
            foreach (var key in keys)
            {
                if (EventRecorder.TemplateVariables.TryGetValue(key, out var value))
                {
                    output.AppendLine($"{value}");
                }
                else
                {
                    hasUndefinedKey = true;
                    error.AppendLine(Localization.GetString("Resources/Cli_VariableNotFound", key));
                }
            }
        }

        return outcome with
        {
            ConsoleOutput = AppendConsoleText(outcome.ConsoleOutput, output),
            ConsoleError = AppendConsoleText(outcome.ConsoleError, error),
            // コマンド本体の失敗コードは上書きしない（--persist と同じ規則。理由はそちらのコメント参照）
            ExitCode = hasUndefinedKey && outcome.ExitCode == 0 ? ExitCode_VariableNotDefined : outcome.ExitCode,
        };
    }

    /// <summary>
    /// 既存のコンソール出力文字列へ追記する。<b>足すものが無ければ既存をそのまま返す</b>
    /// ── <see cref="CommandOutcome.ConsoleOutput"/> は <see langword="null"/> が
    /// 「出力しない」を意味するので、空文字へ倒さない。
    /// </summary>
    private static string? AppendConsoleText(string? existing, StringBuilder added)
        => added.Length == 0 ? existing : (existing ?? "") + added.ToString();

    /// <summary>
    /// レコーダーごとの健全性を機械可読な形で標準出力へ出し、不健全なものが1つでもあれば
    /// <see cref="ExitCode_RecorderUnhealthy"/> と、どれがなぜ不健全なのかを標準エラーへ添える。
    ///
    /// <para>
    /// 標準出力は1行1レコーダーで、列は TAB 区切りの
    /// <c>名前 / 初期化済み / 録画中 / 直近のファイル / 常時録画 / 常時録画のファイル / 直近の障害</c>。
    /// <b>自由記述である「直近の障害」は必ず最後の列に置く。</b> 途中に置くと、
    /// 理由文に TAB が混ざったときに後続の列の意味がずれる。あわせて改行と TAB は空白へ潰し、
    /// <b>1レコーダー＝1行</b>を崩さない（<c>ActivityLog</c> と同じ規約）。
    /// 真偽値は <see cref="bool.ToString()"/>（<c>True</c>/<c>False</c>）で、カルチャに依存しない。
    /// </para>
    /// <para>
    /// <b>「直近の障害」は今も壊れているという意味ではない。</b>
    /// <see cref="EventRecorder.LastError"/> は次の録画開始で消えるので、
    /// ここに出るのは「最後に起きたことが障害だった」ことを表す。
    /// </para>
    /// </summary>
    private static CommandOutcome BuildStatusOutcome(GstControllerViewModel controller)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        bool hasUnhealthy = false;

        foreach (var recorder in controller.Recorders)
        {
            // 列の並びと TAB/改行の潰しは RecorderCliRules（L1 が守っている）
            string lastError = RecorderCliRules.FlattenToOneLine(recorder.LastError);
            output.AppendLine(RecorderCliRules.FormatStatusLine(
                recorder.Name,
                recorder.IsInitialized,
                recorder.IsRecording,
                recorder.LastFilename,
                RecorderCliRules.ContinuousState(
                    recorder.ContinuousRecording, recorder.IsContinuousRecording, recorder.ContinuousLastError),
                recorder.ContinuousLastFilename,
                recorder.LastError));

            if (RecorderCliRules.IsHealthy(recorder.IsInitialized, recorder.LastError))
                continue;

            hasUnhealthy = true;
            error.AppendLine(Localization.GetString("Resources/Cli_RecorderError",
                recorder.Name,
                lastError.Length > 0
                    ? lastError
                    : Localization.GetString("Resources/Cli_RecorderNotInitialized")));
        }

        return CommandOutcome.Silent() with
        {
            // 末尾の改行はランチャー側が Console.Out.Write（WriteLine ではない）で出力するため、
            // AppendLine で行ごとに付いたものをそのまま使う。
            ConsoleOutput = output.Length > 0 ? output.ToString() : null,
            ConsoleError = error.Length > 0 ? error.ToString() : null,
            ExitCode = hasUnhealthy ? ExitCode_RecorderUnhealthy : 0,
        };
    }

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
    /// ViewModel がまだ利用できない（<see cref="ReadyWaitTimeout"/> 待っても生成されなかった）場合の失敗結果。
    /// </summary>
    /// <summary>
    /// 停止の結果を CLI の失敗（終了コード＋標準エラー）へ写す。使える成果物が残った場合は
    /// <see langword="null"/>。
    ///
    /// <para>
    /// <b>単体版と <c>-all</c> 版で共有する。</b> 片方だけ直すと、もう片方では
    /// 「終了コード 0 ＋ 使えないファイル」が返る
    /// ── 終了経路は2つあり、片方だけ塞いでも防げない。
    /// </para>
    /// </summary>
    private static CommandOutcome? StopFailure(RecordingStopOutcome outcome, string recorderName)
    {
        if (RecordingStopRules.IsUsableArtifact(outcome))
            return null;

        return CommandOutcome.Failure(exitCode: ExitCodeFor(outcome)) with
        {
            ConsoleError = Localization.GetString(MessageKeyFor(outcome), recorderName) + Environment.NewLine,
        };
    }

    /// <summary>結果 → 終了コード。<b>規則（どれが失敗か・どちらが強いか）は
    /// <see cref="RecordingStopRules"/> にあり、L1 が守っている</b>
    /// ── ここは番号の割り当てだけを持つ。</summary>
    private static int ExitCodeFor(RecordingStopOutcome outcome) => outcome switch
    {
        RecordingStopOutcome.NotFinalized => ExitCode_RecordingNotFinalized,
        RecordingStopOutcome.Empty => ExitCode_RecordingProducedNothing,
        _ => 0,
    };

    private static string MessageKeyFor(RecordingStopOutcome outcome) => outcome switch
    {
        RecordingStopOutcome.NotFinalized => "Resources/Cli_RecordingNotFinalized",
        _ => "Resources/Cli_RecordingProducedNothing",
    };

    private static CommandOutcome RecorderNotAvailableFailure() =>
        CommandOutcome.Failure(exitCode: ExitCode_RecorderNotAvailable) with
        {
            ConsoleError = Localization.GetString("Resources/Cli_RecorderNotAvailable") + Environment.NewLine,
        };

    /// <summary>
    /// 個別レコーダの録画開始／終了を実行し、その結果を <see cref="CommandOutcome"/> として返す。
    /// <paramref name="target"/> は数値ならインデックス（0始まり）、それ以外はレコーダ名として解決する。
    /// ViewModel 未生成の場合は準備完了を待ってから処理し、待っても未生成なら
    /// <see cref="ExitCode_RecorderNotAvailable"/>、対象が見つからない場合は <see cref="ExitCode_RecorderNotFound"/>、
    /// 現在の状態で実行できない場合は <see cref="ExitCode_RecordingNotExecutable"/> を返す。
    /// </summary>
    private static async Task<CommandOutcome> ExecuteRecorderCommandAsync(string target, bool start)
    {
        // 常駐ワーカー起動直後は ViewModel 生成待ち。準備完了まで待ってから実行する
        var controller = await WaitForControllerAsync();
        if (controller is null)
            return RecorderNotAvailableFailure();

        // 数値ならインデックス、それ以外は名前で対象レコーダを解決する
        // （規則は RecorderCliRules.ResolveTargetIndex。L1 が守っている）
        int index = RecorderCliRules.ResolveTargetIndex(
            controller.Recorders.Select(r => r.Name).ToArray(), target);
        GstEventRecorderViewModel? recorder = 0 <= index ? controller.Recorders[index] : null;

        if (recorder is null)
        {
            return CommandOutcome.Failure(exitCode: ExitCode_RecorderNotFound) with
            {
                ConsoleError = Localization.GetString("Resources/Cli_RecorderNotFound", target) + Environment.NewLine,
            };
        }

        // 実行前に実行可否を確認し、不可の場合は実行せず失敗として返す
        bool canExecute = start ? recorder.CanStartRecording : recorder.CanStopRecording;
        if (!canExecute)
        {
            return CommandOutcome.Failure(exitCode: ExitCode_RecordingNotExecutable) with
            {
                ConsoleError = Localization.GetString(
                    start ? "Resources/Cli_CannotStartInState" : "Resources/Cli_CannotStopInState",
                    recorder.Name) + Environment.NewLine,
            };
        }

        if (start)
        {
            recorder.StartRecording();
        }
        else
        {
            // 停止は完了まで待つ。`stop-recording X` の直後に `copy` するバッチが
            // 想定用途であり、コマンド復帰時に moov が
            // 確定している必要がある。HandleActivation は元々非同期なので
            // UI スレッドは塞がらない。
            await recorder.StopRecordingAsync();

            // **使えないファイルを成功として返さない。** ファイルのパスは出力したうえで、
            // 終了コードで理由を伝える（バッチはパスを使って後始末や修復ができるが、
            // 成果物として運んではいけない）。
            if (StopFailure(recorder.LastStopOutcome, recorder.Name) is CommandOutcome failure)
            {
                return failure with { ConsoleOutput = recorder.LastFilename + Environment.NewLine };
            }
        }

        // 出力は「実際に使われたファイルのパス」。-all 版と揃えるため、
        // 未展開の FilenameTemplate ではなく解決済みの LastFilename を返す。
        // 末尾の改行はランチャー側が Console.Out.Write（WriteLine ではない）で出力するため、ここで付ける。
        return CommandOutcome.Silent() with
        {
            ConsoleOutput = recorder.LastFilename + Environment.NewLine
        };
    }

    /// <summary>
    /// 引数をランチャー側でその場で処理すべきか判定し、処理した場合はその終了コードを返す。
    /// 処理しなかった場合（常駐ワーカーへ委譲すべき正常な引数の場合）は <see langword="null"/> を返す。
    ///
    /// ランチャー側で処理するのは以下の2種類：
    ///   1. ヘルプ表示要求（<c>--help</c>/<c>-h</c>/<c>-?</c> 等、サブコマンド付きも含む）または
    ///      バージョン表示要求（<c>--version</c>） → 表示して終了コード 0
    ///   2. 不明なサブコマンド・引数のパースエラー
    ///      → エラーメッセージを表示して終了コード <see cref="ExitCode_InvalidArguments"/>
    ///
    /// 常駐ワーカーは非表示のバックグラウンドプロセスであり、これらの表示を行っても
    /// それを呼び出したユーザーのコンソールには届かない（既に起動中の常駐ワーカーへ
    /// リダイレクトされた場合は特に、全く別のプロセス・別のコンソールでの表示になってしまう）。
    /// そのため、これらの表示はランチャー側で、常駐ワーカーへリダイレクトする前に直接処理する。
    /// </summary>
    public static int? TryHandleInLauncher(string[] args)
    {
        var rootCommand = BuildRootCommand(_ => { }, out _, out _, out _, out _);
        var parseResult = rootCommand.Parse(args);

        // --version のアクション（System.CommandLine.VersionOption.VersionOptionAction）は
        // internalなネスト型のため型名を直接参照できない。公開型である VersionOption を
        // DeclaringType経由で比較することで判定する。
        bool isHelp = parseResult.Action is HelpAction;
        bool isVersion = parseResult.Action?.GetType().DeclaringType == typeof(VersionOption);

        if (isHelp || isVersion)
        {
            parseResult.Invoke();
            return 0;
        }

        if (parseResult.Errors.Count > 0)
        {
            // パースエラー時の Invoke() はエラーメッセージと使用法の表示のみを行い、
            // どのコマンドの SetAction も実行しないため、ランチャープロセス内で
            // 実処理（録画開始・変数設定等）が走ることはない。
            parseResult.Invoke();
            return ExitCode_InvalidArguments;
        }

        return null;
    }

    /// <summary>
    /// コマンド体系（RootCommand/Subcommands）を構築する。
    /// <paramref name="setOutcome"/> は、マッチしたコマンドの処理結果を受け取るコールバック。
    /// <see cref="TryHandleInLauncher"/> からはヘルプ/バージョン/パースエラーの判定のためだけに
    /// 呼び出されるため、そちらでは実処理を伴わないよう何もしないコールバックを渡す
    /// （ヘルプ/バージョン要求時・パースエラー時はSetActionが実行されないため実際には無害だが、
    ///  意図を明確にするため）。
    /// </summary>
    private static RootCommand BuildRootCommand(Action<CommandOutcome> setOutcome,
        out Option<string[]> setOption, out Option<string[]> getOption,
        out Option<string[]> persistOption, out Option<string[]> noPersistOption)
    {
        var rootCommand = new RootCommand(Localization.GetString("Resources/Cli_RootDesc"));
        // サブコマンドなし（素の起動）はウィンドウを表示せず何もしない。
        rootCommand.SetAction(_ => setOutcome(CommandOutcome.Silent()));

        // --- --set Key=Value : テンプレート変数を設定する（繰り返し指定で複数設定可） ---
        // Recursive によりどのサブコマンドと併用しても効果がある（単独指定も可）。
        // 実際の設定処理は Parse() 側で、コマンド本体の実行前に行う。
        setOption = new Option<string[]>("--set")
        {
            Description = Localization.GetString("Resources/Cli_SetDesc"),
            Recursive = true,
        };
        setOption.Validators.Add(result =>
        {
            foreach (var pair in result.GetValueOrDefault<string[]>() ?? [])
            {
                if (pair.IndexOf('=') is var separatorIndex && separatorIndex <= 0)
                {
                    result.AddError(Localization.GetString("Resources/Cli_InvalidSet", pair));
                    return;
                }
            }
        });
        rootCommand.Options.Add(setOption);

        // --- --get [Key ...] : テンプレート変数を取得する（キー省略で全件、指定時は値のみ出力） ---
        // Recursive によりどのサブコマンドと併用しても効果がある（単独指定も可）。
        // 実際の取得処理は Parse() 側で、コマンド本体の実行後に行う。
        getOption = new Option<string[]>("--get")
        {
            Description = Localization.GetString("Resources/Cli_GetDesc"),
            Recursive = true,
            Arity = ArgumentArity.ZeroOrMore,
        };
        rootCommand.Options.Add(getOption);

        // --- --persist / --no-persist Key : そのテンプレート変数を settings.json に残すかどうか ---
        // **`--set` の意味は変えない。** `--set` はセッション限りで、
        // 永続化は明示的に指定されたキーだけが対象（意図的な仕様）。
        // `--get` と同じく繰り返し指定で複数キーを扱う
        // （AllowMultipleArgumentsPerToken は使わない ── Recursive なので
        //   `--persist A ping` の `ping` までキーとして飲み込まれる）。
        // 適用は `--set` の直後・コマンド本体より前（Parse() 側）。
        persistOption = new Option<string[]>("--persist")
        {
            Description = Localization.GetString("Resources/Cli_PersistDesc"),
            Recursive = true,
        };
        rootCommand.Options.Add(persistOption);

        noPersistOption = new Option<string[]>("--no-persist")
        {
            Description = Localization.GetString("Resources/Cli_NoPersistDesc"),
            Recursive = true,
        };
        rootCommand.Options.Add(noPersistOption);

        // --- --ping : 生存確認のためログに記録するだけ。ウィンドウ表示・通知いずれもしない ---
        // （＝「トースト通知しない引数」の例）
        var pingCommand = new Command("ping", Localization.GetString("Resources/Cli_PingDesc"));
        pingCommand.SetAction(_ =>
        {
            ActivityLog.Info("ping", "no window display or notification");
            setOutcome(CommandOutcome.Silent());
        });
        rootCommand.Subcommands.Add(pingCommand);

        // --- --activate : ウィンドウを表示する（通知無し） ---
        var activateCommand = new Command("activate", Localization.GetString("Resources/Cli_ActivateDesc"));
        activateCommand.SetAction(_ => setOutcome(CommandOutcome.Activate()));
        rootCommand.Subcommands.Add(activateCommand);

        // --- status : レコーダーごとの健全性を出力する ---
        // 観測のためのコマンドなので、ウィンドウ表示も通知もしない。
        var statusCommand = new Command("status", Localization.GetString("Resources/Cli_StatusDesc"));
        statusCommand.SetAction(async (_, _) =>
        {
            // 常駐ワーカー起動直後は ViewModel 生成待ち。準備完了まで待ってから読む
            // ── 待たないと「レコーダーが1件も無い」という嘘の健全な結果を返しうる。
            var controller = await WaitForControllerAsync();
            if (controller is null)
            {
                setOutcome(RecorderNotAvailableFailure());
                return;
            }
            setOutcome(BuildStatusOutcome(controller));
        });
        rootCommand.Subcommands.Add(statusCommand);

        // --- 録画制御コマンド群（ウィンドウ表示・通知はせず、失敗時のみ非0終了コードを返す） ---

        // start-recording-all : 全レコーダの録画を開始する
        var startRecordingAllCommand = new Command("start-recording-all", Localization.GetString("Resources/Cli_StartAllDesc"));
        startRecordingAllCommand.SetAction(async (_, _) =>
        {
            // 常駐ワーカー起動直後は ViewModel 生成待ち。準備完了まで待ってから実行する
            var controller = await WaitForControllerAsync();
            if (controller is null)
            {
                setOutcome(RecorderNotAvailableFailure());
                return;
            }
            // 開始前に実行可否を確認し、開始可能なレコーダが1つも無ければ失敗として返す
            if (!controller.CanStartRecordingAll)
            {
                setOutcome(CommandOutcome.Failure(exitCode: ExitCode_RecordingNotExecutable) with
                {
                    ConsoleError = Localization.GetString("Resources/Cli_NoRecorderCanStart") + Environment.NewLine,
                });
                return;
            }
            // **出すのは「今回開始した分」だけ**（stop-recording-all と同じ規則）。
            // 全件を出すと、開始しなかったレコーダーの行が前回録画の LastFilename を
            // 載せて出て、バッチがそれを今回の成果物として運んでしまう。
            //
            // 標準エラーへ出すのは**開始できるはずだったのに落ちた分**だけ
            // （既に録画中のレコーダーはトリガ運用では日常で、失敗ではない）。
            var startedRecorders = controller.StartRecordingAllAndReport(out var failedRecorders);
            string? startErrors = failedRecorders.Count == 0
                ? null
                : string.Concat(failedRecorders.Select(r =>
                    Localization.GetString("Resources/Cli_CannotStartInState", r.Name) + Environment.NewLine));

            if (startedRecorders.Count == 0)
            {
                // 1 台も開始できなかった。**終了コード 0 で返さない** ── 単体の
                // start-recording が同じ失敗で非 0 を返すのに -all だけ 0 を返すと、
                // バッチは「全部落ちた」を成功として通す。
                setOutcome(CommandOutcome.Failure(exitCode: ExitCode_RecordingNotExecutable) with
                {
                    ConsoleError = (startErrors ?? "")
                        + Localization.GetString("Resources/Cli_NoRecorderCanStart") + Environment.NewLine,
                });
                return;
            }

            setOutcome(CommandOutcome.Silent() with
            {
                // 末尾の改行は、ランチャー側が Console.Out.Write（WriteLine ではない）で出力するためここで付ける
                ConsoleOutput = string.Join(Environment.NewLine,
                    startedRecorders.Select(r => RecorderCliRules.FormatRecorderLine(r.Name, r.LastFilename)))
                    + Environment.NewLine,
                ConsoleError = startErrors,
            });
        });
        rootCommand.Subcommands.Add(startRecordingAllCommand);

        // stop-recording-all : 全レコーダの録画を終了する
        var stopRecordingAllCommand = new Command("stop-recording-all", Localization.GetString("Resources/Cli_StopAllDesc"));
        stopRecordingAllCommand.SetAction(async (_, _) =>
        {
            // 常駐ワーカー起動直後は ViewModel 生成待ち。準備完了まで待ってから実行する
            var controller = await WaitForControllerAsync();
            if (controller is null)
            {
                setOutcome(RecorderNotAvailableFailure());
                return;
            }
            // 終了前に実行可否を確認し、終了可能なレコーダが1つも無ければ失敗として返す
            if (!controller.CanStopRecordingAll)
            {
                setOutcome(CommandOutcome.Failure(exitCode: ExitCode_RecordingNotExecutable) with
                {
                    ConsoleError = Localization.GetString("Resources/Cli_NoRecorderCanStop") + Environment.NewLine,
                });
                return;
            }
            // 停止は完了まで待つ（stop-recording と同じ理由）。排出は並行に走る。
            //
            // **見るのは「今回止めたレコーダー」だけ。** 全件を走査してはいけない ──
            // 録画していなかったレコーダーの LastStopOutcome / LastFilename は
            // **前回の録画のもの**で（リセットは次の録画開始のときだけ）、
            // 前回失敗したレコーダーが1本あるだけで、今回正常に録れた分まで
            // 失敗として返してしまう（詳細は StopRecordingAllAsync の注記）。
            var stopped = await controller.StopRecordingAllAsync();

            // 末尾の改行は、ランチャー側が Console.Out.Write（WriteLine ではない）で出力するためここで付ける
            string files = string.Join(Environment.NewLine,
                stopped.Select(r => RecorderCliRules.FormatRecorderLine(r.Name, r.LastFilename)))
                + Environment.NewLine;

            // **1本でも使えなければ失敗として返す。** -all の成否は「全部ちゃんと録れたか」で、
            // 1本が壊れていても 0 を返すと、バッチはそれを検出する手段を持たない。
            // どのレコーダーがどう壊れたかは標準エラーへ名前つきで、**全件**出す
            // （最初の1件で止めると、残りが壊れているかどうか分からない）。
            var failed = stopped
                .Select(r => (Recorder: r, Failure: StopFailure(r.LastStopOutcome, r.Name)))
                .Where(x => x.Failure is not null)
                .ToList();

            if (0 < failed.Count)
            {
                // **終了コードは「強い方」を返す**（規則は RecordingStopRules。L1 が守っている）。
                // 混在した場合に弱い方を返すと、救済できたはずのデータを捨てさせる。
                var worst = failed
                    .Select(x => x.Recorder.LastStopOutcome)
                    .Aggregate(RecordingStopOutcome.Ok, RecordingStopRules.Stronger);

                setOutcome(CommandOutcome.Failure(exitCode: ExitCodeFor(worst)) with
                {
                    ConsoleOutput = files,
                    ConsoleError = string.Concat(failed.Select(x => x.Failure!.Value.ConsoleError)),
                });
                return;
            }

            setOutcome(CommandOutcome.Silent() with { ConsoleOutput = files });
        });
        rootCommand.Subcommands.Add(stopRecordingAllCommand);

        // start-recording <target> : 指定レコーダ（数値=インデックス／それ以外=名前）の録画を開始する
        var startTargetArgument = new Argument<string>("target")
        {
            Description = Localization.GetString("Resources/Cli_StartTargetArg"),
        };
        var startRecordingCommand = new Command("start-recording", Localization.GetString("Resources/Cli_StartDesc"))
        {
            Arguments = { startTargetArgument },
        };
        startRecordingCommand.SetAction(async (parseResult, _) =>
        {
            string target = parseResult.GetValue(startTargetArgument) ?? string.Empty;
            setOutcome(await ExecuteRecorderCommandAsync(target, start: true));
        });
        rootCommand.Subcommands.Add(startRecordingCommand);

        // stop-recording <target> : 指定レコーダ（数値=インデックス／それ以外=名前）の録画を終了する
        var stopTargetArgument = new Argument<string>("target")
        {
            Description = Localization.GetString("Resources/Cli_StopTargetArg"),
        };
        var stopRecordingCommand = new Command("stop-recording", Localization.GetString("Resources/Cli_StopDesc"))
        {
            Arguments = { stopTargetArgument },
        };
        stopRecordingCommand.SetAction(async (parseResult, _) =>
        {
            string target = parseResult.GetValue(stopTargetArgument) ?? string.Empty;
            setOutcome(await ExecuteRecorderCommandAsync(target, start: false));
        });
        rootCommand.Subcommands.Add(stopRecordingCommand);

        // ここに新しいコマンドを追加していく。例:
        //
        // var messageArgument = new Argument<string>("message");
        // var notifyCommand = new Command("notify", "任意のメッセージをトースト通知します")
        // {
        //     Arguments = { messageArgument },
        // };
        // notifyCommand.SetAction(parseResult =>
        // {
        //     string message = parseResult.GetValue(messageArgument) ?? string.Empty;
        //     setOutcome(CommandOutcome.SilentWithToast("通知", message));
        // });
        // rootCommand.Subcommands.Add(notifyCommand);

        return rootCommand;
    }
}
