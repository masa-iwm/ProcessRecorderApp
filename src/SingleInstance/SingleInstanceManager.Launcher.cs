using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.SingleInstance;

/// <summary>
/// <see cref="SingleInstanceManager"/> のランチャー側（実処理を行わない、呼び出し用プロセス）の実装。
///
/// exeを実行すると、常に最初は「ランチャー」として <see cref="Run"/> が呼ばれることを想定する。
/// ランチャー自身が常駐インスタンスになることはない。
///
///   ・常駐ワーカーが既に起動済み → 引数をリダイレクトして自分は即終了
///   ・常駐ワーカーが未起動       → バックグラウンドで常駐ワーカーを起動し、
///                                  起動完了を待ってから引数をリダイレクトして
///                                  自分は終了する（常駐ワーカーは終了しない）
///
/// 複数のランチャープロセスがほぼ同時に起動された場合でも、常駐ワーカーが2つ以上
/// 起動されてしまわないよう、名前付き Mutex でランチャーの主要処理全体を排他制御している。
/// また、常駐ワーカーの起動完了待ちは、名前付き EventWaitHandle によるシグナル待ちで実装しており、
/// ポーリングは一切行わない。
/// </summary>
public sealed partial class SingleInstanceManager
{
    /// <summary>常駐ワーカーとして起動するための内部フラグ（ユーザー向けの引数ではない）</summary>
    private const string WorkerFlag = "--__resident-worker";

    /// <summary>常駐ワーカーの起動自体（プロセス起動や起動完了通知の待機）に失敗した場合の終了コード。</summary>
    public const int ExitCode_WorkerStartFailed = 1;

    /// <summary>
    /// 常駐ワーカーへ処理を委譲したものの、処理結果の通知がタイムアウトした場合の終了コード。
    /// 常駐ワーカー側の処理自体が成功したか失敗したかは、このランチャーからは判別できない。
    /// </summary>
    public const int ExitCode_WorkerResultTimeout = 2;

    /// <summary>
    /// 常駐ワーカーが既に起動済みの状態で、さらに常駐ワーカーとして起動された場合
    /// （内部フラグを付けて手動で直接起動された場合など）の終了コード。
    /// </summary>
    public const int ExitCode_WorkerAlreadyRunning = 3;

    /// <summary>
    /// 先行するランチャーが <c>launcherMutex</c> を握ったまま返らず、
    /// <see cref="LauncherMutexTimeoutMs"/> を超えて順番が回ってこなかった場合の終了コード。
    ///
    /// <para>
    /// <b>この経路は「何かが本当に詰まっている」ことだけを表す。</b>
    /// 正常な待ち行列でここへ来てはいけない ── 来たなら、それは調査すべき事象である。
    /// </para>
    /// </summary>
    public const int ExitCode_LauncherBusy = 6;

    /// <summary>
    /// Main() が受け取った生の引数配列が、常駐ワーカーとしての起動を表すかどうかを判定する。
    /// </summary>
    public static bool IsWorkerBootstrap(string[] args) =>
        args.Length == 1 && string.Equals(args[0], WorkerFlag, StringComparison.Ordinal);

    /// <summary>
    /// 常駐ワーカー本体（WinUI3アプリ本体）を起動する。このメソッドは通常、アプリ終了までブロックする。
    /// 常駐ワーカーが既に起動済みの場合は、2つ目の常駐ワーカーを起動せず
    /// <see cref="ExitCode_WorkerAlreadyRunning"/> を返して即座に終了する。
    /// </summary>
    /// <param name="keyPrefix">単一インスタンス判定キーや各種名前付きカーネルオブジェクトの共通プレフィックス。</param>
    /// <param name="appFactory">アプリ固有の <see cref="Application"/> 派生クラスのインスタンスを生成するコールバック。</param>
    public static int StartResidentWorker(string keyPrefix, Func<Application> appFactory, Action? initializing = null)
    {
        // 既に常駐ワーカーが存在する場合は、2つ目の常駐ワーカーとして起動しない。
        // （通常はランチャーがLauncherMutexで多重起動を防いでいるが、内部フラグを付けて
        //   手動で直接起動された場合はこのチェックが唯一の防衛線となる）
        // 自分がキーを登録できた場合、後続の SingleInstanceManager コンストラクターでの
        // FindOrRegisterForKey は同一プロセスのため同じ現在インスタンスを返し、
        // WorkerReadyEvent の通知を含む従来の起動シーケンスには影響しない。
        var keyInstance = AppInstance.FindOrRegisterForKey(Names.InstanceKey(keyPrefix));
        if (!keyInstance.IsCurrent)
        {
            return ExitCode_WorkerAlreadyRunning;
        }

        // **キー登録に勝った直後に「まだ受け取れない」を宣言する。**
        // ここから Activated の購読（SingleInstanceManager のコンストラクター）までの間、
        // ランチャーからは「ワーカーが居る」と見えるのにリダイレクトは捨てられる
        // ── その窓をランチャー側に待たせるための旗。
        //
        // **リセットはキー登録の「後」でなければならない。** 前に置くと、
        // 登録に負けた（＝既に別のワーカーが動いている）場合に
        // **そのワーカーのシグナルを消してしまう**。
        //
        // このハンドルはプロセスの生存中ずっと保持する（static フィールド）。
        // using で閉じると、待ち手が居ない瞬間にカーネルオブジェクトごと消える。
        //
        // **握り潰すのはランチャー側（WaitUntilWorkerAcceptsCommands）と対称にするため。**
        // ここはまだ initializing?.Invoke() の前で ActivityLog が初期化されていないので、
        // 投げると app.start も app.exit も残さずワーカーが死ぬ ──
        // 「最悪でも従来の挙動に戻るだけ」という設計意図に反する経路になってしまう。
        // 失敗しても null のままで良い（SignalWorkerAcceptsCommands は ?. で null 安全）。
        try
        {
            _workerAcceptingEvent = new EventWaitHandle(
                initialState: false, EventResetMode.ManualReset, Names.WorkerAcceptingEvent(keyPrefix));
            _workerAcceptingEvent.Reset();
        }
        catch (Exception)
        {
            _workerAcceptingEvent = null;
        }

        initializing?.Invoke();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            appFactory();
        });

        return 0;
    }

    /// <summary>
    /// 呼び出し用ランチャーとして動作する。常駐インスタンスにはならず、処理完了後は必ず終了する。
    /// </summary>
    /// <param name="keyPrefix">単一インスタンス判定キーや各種名前付きカーネルオブジェクトの共通プレフィックス。</param>
    /// <param name="rawArgs">
    /// Main() が受け取った生の引数配列（exeパスを含まない）。
    /// AppActivationArguments から復元される文字列はexeパスを含む場合があり
    /// 「引数の有無」の判定には使えないため、こちらを正とする。
    /// </param>
    /// <param name="tryHandleInLauncher">
    /// 引数をランチャー側でその場で処理すべきか判定し、処理した場合はその終了コードを、
    /// 処理しなかった場合（常駐ワーカーへ委譲すべき場合）は null を返すコールバック。
    /// ヘルプ/バージョン表示や不明な引数のエラー表示など、アプリ固有のコマンド体系に依存するため、
    /// 呼び出し側（アプリ）が実装する。常駐ワーカーは非表示のバックグラウンドプロセスであり、
    /// そちらで表示しても呼び出し元のコンソールには届かないため、この判定・表示はランチャー側で行う。
    /// </param>
    public static int Run(string keyPrefix, string[] rawArgs, Func<string[], int?>? tryHandleInLauncher = null)
    {
        // アプリ本体は OutputType=Exe（コンソールサブシステム）でビルドされている想定。
        // コンソールから起動された場合は標準入出力を自然に継承するため、
        // ヘルプ/エラーメッセージの表示に AttachConsole 等の特別な処理は不要。
        if (tryHandleInLauncher is not null && tryHandleInLauncher(rawArgs) is int handledExitCode)
        {
            return handledExitCode;
        }

        // 自分自身がどう起動されたか（コマンドライン引数含む）を取得しておく。
        // これをそのまま常駐ワーカーへ転送する。
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        // 「常駐ワーカーの有無を確認し、必要なら起動する」処理全体を、
        // 名前付きMutexで排他制御する。
        using var launcherMutex = new Mutex(initiallyOwned: false, Names.LauncherMutex(keyPrefix));

        // **所有できたかを明示的に持つ。** 打ち切った場合に finally で
        // ReleaseMutex() を呼ぶと ApplicationException（「同期されていないコード
        // ブロックから呼ばれた」）になり、**本来の失敗が別の例外にすり替わる**。
        bool ownsMutex;
        try
        {
            ownsMutex = launcherMutex.WaitOne(LauncherMutexTimeoutMs);
        }
        catch (AbandonedMutexException)
        {
            // 直前にこのMutexを保持していたプロセスが、解放せずに異常終了した場合に
            // スローされる。この時点で現在のスレッドはMutexの所有権を取得済みなので、
            // 特別な後始末は不要でそのまま処理を継続してよい。
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            Console.Error.WriteLine(Localization.GetString("Resources/Cli_LauncherBusy"));
            return ExitCode_LauncherBusy;
        }

        // 常駐ワーカーを新たに起こしてから引数を届ける経路。
        //
        // **引数の `owned` は「このプロセスが今キーを登録できている」インスタンスに限る。**
        // 非所有のハンドルに `UnregisterKey()` を呼ぶと何が起きるかは WinAppSDK の
        // 仕様に明記が無く、**他人のキーを解除しうる**（＝無関係なワーカーを
        // 単一インスタンス制御から締め出す）。**そこへ賭けない設計にしてある** ──
        // 呼び出し側は必ず `IsCurrent` を確かめた直後のハンドルを渡すこと。
        int ColdStart(AppInstance owned)
        {
            owned.UnregisterKey();

            var workerInstance = StartResidentWorkerAndWaitForRegistration(keyPrefix);
            if (workerInstance is null)
            {
                Console.Error.WriteLine(Localization.GetString("Resources/Cli_WorkerStartTimeout"));
                return ExitCode_WorkerStartFailed;
            }

            // 常駐ワーカーが存在しなかった＝これは単なる「アプリの起動」であり、
            // 明示的なActivate要求ではない。ユーザー指定の引数（rawArgs）が無い場合は
            // ウィンドウを表示させず、常駐ワーカーをタスクトレイに常駐させたまま待機させる。
            if (rawArgs.Length > 0)
            {
                return RedirectActivationAndGetExitCode(keyPrefix, activationArgs, workerInstance);
            }

            return 0;
        }

        try
        {
            var keyInstance = AppInstance.FindOrRegisterForKey(Names.InstanceKey(keyPrefix));

            if (!keyInstance.IsCurrent)
            {
                // 既に常駐ワーカーが起動済み → そのままリダイレクトして終了。
                //
                // **ただし「キーが登録済み」＝「コマンドを受け取れる」ではない。**
                // ワーカーはキーを StartResidentWorker の冒頭で登録するが、
                // リダイレクトの受信ハンドラ（Activated）が張られるのは
                // その後の Application.Start → App ctor（SingleInstanceManager の
                // コンストラクター）である。間には app.start のログと
                // GStreamer の StaticInitialize()（初回はレジストリ構築で 10 秒超）が挟まる。
                // **この窓に届いたリダイレクトは購読者が居ないので痕跡ゼロで捨てられ**、
                // ランチャーは結果通知を 60 秒待ってから ExitCode_WorkerResultTimeout を返す。
                //
                // これは実測で確認した実バグ:
                //   常駐ワーカーを直接起動した直後に ping を撃つと
                //   **4/4 が 60.5 秒・終了コード 2**、activity.log には
                //   その ping の `cli` 行が**一行も残らない**（＝処理されていない）。
                //   起動から 5 秒待つだけで 4/4 が 0.3 秒・終了コード 0 になる。
                //   利用者から見れば「アプリ起動直後のコマンドが黙って失われる」不具合。
                //
                // **コールドスタート経路には元々この待ちがある**
                // （StartResidentWorkerAndWaitForRegistration が WorkerReadyEvent を待つ）。
                // 実際、ランチャーにワーカーを起こさせた場合の ping は 4/4 が約3秒・
                // 終了コード 0 で通る。**欠けていたのはこちらの経路だけ。**
                //
                // **受理待ちと結果待ちは予算を共有する。** 直列に積むと、ワーカーが
                // 購読前に固まったとき（＝この待ちが想定している当のケース）に
                // 1回の CLI が launcherMutex を握る時間が 60 秒から **120 秒へ倍増**し、
                // その間、このメソッド冒頭の launcherMutex.WaitOne()（上限無し）で
                // **他の CLI が全部止まる**。
                // 「打ち切っても従来の挙動に戻るだけ」は**終了コードについては真だが
                // 経過時間については偽**である。
                // 使った分を差し引いて、CLI 1回あたりの合計を 60 秒に固定する。
                var accepting = System.Diagnostics.Stopwatch.StartNew();
                bool accepted = WaitUntilWorkerAcceptsCommands(keyPrefix);

                var target = keyInstance;
                if (!accepted)
                {
                    // **受理の合図が来なかった。ワーカーが死んでいる可能性がある。**
                    //
                    // ここまでの `keyInstance` は待ちに入る「前」に見たキーの持ち主で、
                    // 最大 60 秒ぶん古い。そのままリダイレクトすると、相手が既に
                    // 消えている場合に**さらに結果待ちの上限まで待たされて exit 2**
                    // ── 利用者から見れば「2分近く固まって成否不明」になる。
                    //
                    // **再検証は「合図が来なかったとき」だけ行う。** 常態では
                    // p90 337ms で合図が来るので、ここは踏まれない
                    // ── **毎回踏むと、全 CLI の hot path にプロセス間 COM 呼び出しが
                    // 1回増える**（E2E 全件に乗る）。
                    var recheck = AppInstance.FindOrRegisterForKey(Names.InstanceKey(keyPrefix));
                    if (recheck.IsCurrent)
                    {
                        // キーが空いていた＝ワーカーは消えている。**今キーを持っているのは自分**。
                        // コールドスタートへ倒す（自分の登録を解除してから起動する）。
                        return ColdStart(recheck);
                    }

                    // 別のワーカーが居る。**`keyInstance` ではなく `recheck` へ送る**
                    // ── 待っている間に世代交代していれば、こちらが新しい方を指している。
                    target = recheck;
                }

                int resultBudgetMs = Math.Max(
                    MinimumResultTimeoutMs, WorkerAcceptTimeoutMs - (int)accepting.ElapsedMilliseconds);
                return RedirectActivationAndGetExitCode(keyPrefix, activationArgs, target, resultBudgetMs);
            }

            // 常駐ワーカーがまだ存在しない。このランチャープロセス自身は常駐インスタンスには
            // ならないため、一旦キーの登録を解除し、別プロセスとして常駐ワーカーを起動する。
            return ColdStart(keyInstance);
        }
        finally
        {
            launcherMutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// AppInstance.RedirectActivationToAsync を同期的に待ち合わせる。
    /// STAスレッド上でメッセージポンプが存在しない状態のまま直接awaitすると
    /// ハングする場合があるため、公式サンプルに倣いバックグラウンドスレッドで実行する。
    ///
    /// <para>
    /// <b>待ちは必ず有界にすること。</b> ここが無制限だと、転送が失敗（例外）または
    /// 停止（返らない）した瞬間に呼び出し元が<b>永久にブロック</b>する。しかも
    /// 呼び出し元（<see cref="Run"/>）は <c>launcherMutex</c> を握ったままなので、
    /// <b>以後 <i>同じ keyPrefix・同じセッション</i>で起動される CLI が全部、
    /// 無言で永久に止まる</b> ── 実利用では keyPrefix は1つなので、
    /// 利用者から見れば<b>アプリの操作手段が丸ごと失われる</b>。
    /// （<see cref="Names.LauncherMutex"/> は修飾無しの名前なので
    /// スコープはセッションローカル。E2E が keyPrefix を上書きして
    /// テストごとに隔離しているのはこのため。）
    /// 例外は fire-and-forget の <c>Task</c> の中で握り潰され、
    /// .NET では未観測例外がプロセスを落とさないため<b>痕跡も残らない</b>。
    /// </para>
    /// <para>
    /// <b><c>AutoResetEvent</c> ではなく <c>Task.Wait(ミリ秒)</c> を使う。</b>
    /// イベント＋<c>Set()</c> の形に上限を足すと、<c>using</c> による破棄と
    /// 遅れて走る <c>Set()</c> が競合する（破棄済みハンドルへの <c>Set()</c>）。
    /// <c>Task</c> にすれば待ち合わせの器そのものが無くなるので、その競合が
    /// <b>回避ではなく消滅</b>する。失敗は <c>Wait</c> が
    /// <see cref="AggregateException"/> として観測するので、
    /// 例外経路も同時に閉じる。
    /// </para>
    /// <para>
    /// <b>STA のメッセージポンプは壊れない（実測済み）。</b>
    /// <c>WaitHandle.WaitOne()</c> から <c>Task.Wait()</c> へ替えると
    /// COM のポンプが止まって <c>RedirectActivationToAsync</c> が
    /// デッドロックしうる、という懸念は<b>否定された</b>
    /// ── 注入なしの <c>ping</c> が 311〜582ms で終了コード 0。
    /// <b>ここを触るときは必ず「注入なしの対照」を一緒に測ること</b>
    /// （壊れ方が「正常系のハング」なので、注入側だけ見ていると気付けない）。
    /// </para>
    /// </summary>
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance target)
    {
        var redirect = Task.Run(() => target.RedirectActivationToAsync(args).AsTask().Wait());

        try
        {
            redirect.Wait(RedirectTimeoutMs);
        }
        catch (AggregateException)
        {
            // 転送自体が失敗した。呼び出し元は結果待ちのタイムアウトへ落ちる
            // （＝従来からある「成否不明」の経路）。永久ブロックより厳密に良い。
        }
    }

    /// <summary>
    /// 常駐ワーカーへ起動引数をリダイレクトし、常駐ワーカー側でのコマンド処理が完了して
    /// 結果が通知されるまで待機する。通知されたコンソール出力文字列を呼び出し元の
    /// 標準出力／標準エラー出力へ出力したうえで、終了コードを返す。
    /// </summary>
    /// <param name="resultTimeoutMsOverride">
    /// 結果通知を待つ上限（ミリ秒）。null なら <see cref="WorkerAcceptTimeoutMs"/>。
    /// <b>リダイレクト経路は受理待ちに使った分を差し引いて渡す</b> ── CLI 1回あたりの
    /// <c>launcherMutex</c> 占有を 60 秒に固定するため（呼び出し側のコメントを参照）。
    /// </param>
    private static int RedirectActivationAndGetExitCode(
        string keyPrefix, AppActivationArguments args, AppInstance target, int? resultTimeoutMsOverride = null)
    {
        // **チャネルはリダイレクトより前に張る。** 相関 ID もここで確定し、
        // ワーカーは届いた瞬間にそれを claim する（CommandResultChannel の doc を参照）。
        using var channel = CommandResultChannel.CreateForRequest(keyPrefix, Guid.NewGuid());

        RedirectActivationTo(args, target);

        // 常駐ワーカーからの結果通知を待つ上限。
        //
        // **10 秒では短い。**
        // 録画コマンドはワーカー側で最大 8 秒の準備完了待ちを行うので、10 秒では
        // **その上わずか 2 秒**しか余裕が無い。CI ランナー（2 vCPU で 2 本の
        // x264enc が回る）では実際に `activate` が 12.6 秒かかって終了コード 2 になった。
        //
        // ここは「異常が起きた」ことを検出するための上限であって、正常系の待ち時間ではない
        // ── 通常のコマンドはミリ秒で返る。長くしても正常系の体感は変わらず、
        // 変わるのは「ワーカーが詰まっているときに CLI が何秒で諦めるか」だけ。
        // この値が `StopFinalizeTimeoutMs` の使える上限（目安 50000 以下）を決めている。
        int resultTimeoutMs = resultTimeoutMsOverride ?? WorkerAcceptTimeoutMs;
        if (!channel.TryWaitForResult(resultTimeoutMs, out var result))
        {
            // 常駐ワーカー側からの結果通知がタイムアウト：処理が成功したか失敗したかは
            // このランチャーからは判別できないため、専用の終了コードで区別する。
            //
            // **「他人の結果を捨てた」場合もここへ来る。** 相関 ID が一致しない通知は
            // 配らないので、取り残された古いコマンドが遅れて答えても
            // **このコマンドの答えにはならない**（相関 ID を照合しないと、
            // それを自分の結果として読んでしまう）。
            return ExitCode_WorkerResultTimeout;
        }

        if (result.ConsoleOutput.Length > 0)
            Console.Out.Write(result.ConsoleOutput);

        if (result.ConsoleError.Length > 0)
            Console.Error.Write(result.ConsoleError);

        return result.ExitCode;
    }

    /// <summary>
    /// 自分自身のexeを「常駐ワーカーモード」で別プロセスとして起動し、
    /// そのプロセスがインスタンスキーの登録を完了する（＝ WorkerReadyEvent をSetする）まで待機する。
    /// 呼び出し元は既にランチャーMutexを保持しているため、ポーリングによるキーの登録/解除を行う必要はない。
    /// </summary>
    private static AppInstance? StartResidentWorkerAndWaitForRegistration(string keyPrefix)
    {
        string? exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            return null;
        }

        // 常駐ワーカーの起動完了通知を待つためのイベント。
        // 万一、以前の異常終了などでシグナル状態のまま残っていた場合に備え、
        // 明示的に非シグナル状態へリセットしてから使用する。
        using var workerReadyEvent = new EventWaitHandle(
            initialState: false, EventResetMode.AutoReset, Names.WorkerReadyEvent(keyPrefix));
        workerReadyEvent.Reset();

        // UseShellExecute = true（ShellExecuteEx経由）で起動することで、ランチャーのハンドルを
        // 常駐ワーカーに一切継承させない。
        // UseShellExecute = false（CreateProcess直接）だと、.NETは常に bInheritHandles=TRUE で
        // 子プロセスを生成するため、呼び出し元シェルがパイプやリダイレクトを使っていた場合に
        // そのハンドルが常駐ワーカーへ漏れ、ランチャー終了後もシェルが常駐ワーカーの終了まで
        // 待ち続けてしまう（RedirectStandard* で標準ハンドルを差し替えても、継承可能ハンドル
        // 自体の漏れは防げない）。
        // WindowStyle = Hidden により、常駐ワーカーのコンソールウィンドウは非表示で生成される。
        var psi = new ProcessStartInfo(exePath, WorkerFlag)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        // 常駐ワーカーが「インスタンスキーの登録を終えた」ことを待つ上限。
        //
        // 常駐ワーカーの**初回**起動は GStreamer のプラグインレジストリ構築で
        // 10〜30 秒かかるため 120 秒にしてある。10 秒程度に縮めると
        // **利用者が最初に叩いたコマンドが失敗する**
        // （E2E は `PublishedApp` が1回ウォームアップして回避しているが、
        //   実利用者にはそんな回避策は無い）。
        const int timeoutMs = 120_000;
        if (!workerReadyEvent.WaitOne(timeoutMs))
        {
            // タイムアウト：常駐ワーカーが起動に失敗した、またはハングしている可能性がある。
            return null;
        }

        // ここに到達した時点で、常駐ワーカーはインスタンスキーの登録を完了している
        // （ランチャーMutexにより、この間に割り込む他のランチャーは存在しない）。
        var candidate = AppInstance.FindOrRegisterForKey(Names.InstanceKey(keyPrefix));
        if (candidate.IsCurrent)
        {
            // 想定外の状態（常駐ワーカーが登録に失敗した等）。念のため解放しておく。
            candidate.UnregisterKey();
            return null;
        }

        return candidate;
    }

    /// <summary>
    /// 常駐ワーカー自身が保持する「受け取れる状態になった」旗。
    /// <b>プロセスの生存中ずっと開いたままにする</b> ── 閉じると、待ち手が居ない瞬間に
    /// 名前付きカーネルオブジェクトごと消えてしまい、次のランチャーが
    /// 「作られたばかりの非シグナル状態のイベント」を掴んで無駄に待つ。
    /// </summary>
    private static EventWaitHandle? _workerAcceptingEvent;

    /// <summary>
    /// <c>Activated</c> の購読が済んだことを宣言する
    /// （<see cref="SingleInstanceManager"/> のコンストラクターから呼ぶ）。
    /// これ以降、ランチャーのリダイレクトは確実に受信ハンドラへ届く。
    /// </summary>
    private static void SignalWorkerAcceptsCommands() => _workerAcceptingEvent?.Set();

    /// <summary>
    /// 常駐ワーカーが<b>リダイレクトを受け取れる状態になるまで</b>待つ上限。
    ///
    /// <para>
    /// <b>これは健全性の検査ではなく、起動の窓を跨ぐための待ち。</b>
    /// 待つ対象は「キー登録」ではなく「<c>Activated</c> の購読が済んだこと」で、
    /// その間に走るのは GStreamer の <c>StaticInitialize()</c>（初回はプラグイン
    /// レジストリ構築で 10 秒を超えうる）である。
    /// </para>
    /// <para>
    /// <b>これは結果通知の待ちと共有する予算である</b>（＝CLI 1回で合計 60 秒）。
    /// 受理待ちと結果待ちを別々の予算として直列に積むと、
    /// ワーカーが購読前に固まったときに <c>launcherMutex</c> の占有が
    /// <b>60 秒から 120 秒へ倍増</b>し、その間に来た CLI が全部止まる
    /// ── 「失敗する経路は増えない」は<b>終了コードについては真だが経過時間については偽</b>になる。
    /// </para>
    /// <para>
    /// <b>この 60 秒を短くしてはいけない。</b> 理由は2つあり、<b>どちらも独立している</b>。
    /// </para>
    /// <para>
    /// (1) <b>受理待ちとして</b>: 本当に効くのは
    /// <b>初回起動（GStreamer のプラグインレジストリ構築で 10 秒超）</b>の場合で、
    /// 数秒に切り詰めると元のバグがそこで再発する。
    /// 常態では実測 p90 337ms で抜けるので、短くして得るものは無い。
    /// </para>
    /// <para>
    /// (2) <b>結果待ちとして</b>: これが
    /// <c>EventRecorder.StopFinalizeTimeoutMs</c> の<b>使える上限を決めている</b>。
    /// 停止は <c>StopFinalizeTimeoutMs + StopFinalizeSlackMs</c> まで専有しうる公開ノブで、
    /// <b>設定画面と README が「50000 以下」と案内している</b>
    /// （<c>EventRecorder.MaxAdvisedStopFinalizeTimeoutMs</c>）。
    /// ここを縮めると<b>案内どおりに設定した利用者の <c>stop-recording</c> が
    /// 終了コード 2 を返し始める</b>。
    /// <b>縮めるなら先に (2)（<c>StopFinalizeTimeoutMs</c> の上限）を動かすこと。</b>
    /// 関係は <c>StopFinalizeBudgetTests</c>（L1）が機械的に守っている。
    /// </para>
    /// </summary>
    public const int WorkerAcceptTimeoutMs = 60_000;

    /// <summary>
    /// 受理待ちで予算を使い切っても、結果通知にはこれだけは残す
    /// （0 にすると「リダイレクトした瞬間に諦める」形になり、
    /// 間に合っていたはずの応答まで取りこぼす）。
    /// </summary>
    private const int MinimumResultTimeoutMs = 5_000;

    /// <summary>
    /// リダイレクトの<b>転送そのもの</b>を待つ上限。
    ///
    /// <para>
    /// <b>受理待ち／結果待ちの予算とは独立にしてある。</b> 転送は本来ミリ秒で終わる
    /// プロセス間 COM 呼び出しで、ここが秒単位かかること自体が異常である。
    /// 共有予算へ畳み込むと<b>コールドスタート経路の算術に触ることになり</b>、
    /// 初回起動（GStreamer のレジストリ構築）で正当に長い待ちを縮めてしまう。
    /// </para>
    /// <para>
    /// <b><c>launcherMutex</c> の占有上限には、この項も加わる。</b>
    /// リダイレクト経路は 受理(≤60) + 転送(≤30) + 結果(≥5) になり、
    /// 「CLI 1回あたり合計 60 秒」という不変条件はこの転送分だけ超えうる。
    /// 畳み込むなら <c>launcherMutex.WaitOne()</c> の上限（<c>LauncherMutexTimeoutMs</c>）と
    /// <b>同じパスで一緒に決めること</b> ── 片方だけ縮めても意味が無い。
    /// </para>
    /// </summary>
    private const int RedirectTimeoutMs = 30_000;

    /// <summary>
    /// <c>launcherMutex</c> の順番待ちの上限。**デッドロック破りであって UX の調整値ではない。**
    ///
    /// <para>
    /// <b>この値は「正当な保持時間の最悪値」より十分大きくなければならない。</b>
    /// 低く見積もると、<b>正当に待っている CLI が失敗する</b>という退行になる
    /// ── しかも待ち行列は<b>加算的</b>で、N 本並べば N 倍待つ。
    /// 現在の正当な最悪値は<b>コールドスタート経路の約 210 秒</b>
    /// （<see cref="StartResidentWorkerAndWaitForRegistration"/> の 120 秒
    /// ＋ 転送 30 秒 ＋ 結果待ち 60 秒）。
    /// リダイレクト経路は最悪 約 95 秒（受理+結果で ≤65 ＋ 転送 30）。
    /// <b>通常の保持は数秒</b>で、210 秒は「起動に失敗しつつ上限まで粘った」病的な場合。
    /// </para>
    /// <para>
    /// 10 分にしてあるのは、病的な保持が<b>3本積んでも</b>まだ届かない値だから。
    /// ここへ到達するのは事実上「本当に楔が刺さっている」ときだけで、
    /// そのとき返るのは <see cref="ExitCode_LauncherBusy"/>。
    /// <b>短くしたくなったら、先にコールドスタートの 120 秒と結果待ちの 60 秒を
    /// 見直すこと</b> ── この値だけ縮めても、縮むのは「正当に待てる時間」の方である。
    /// </para>
    /// </summary>
    private const int LauncherMutexTimeoutMs = 600_000;

    /// <summary>
    /// 常駐ワーカーが <c>Activated</c> の購読を終える（＝リダイレクトを実際に受け取れる）
    /// まで待つ。<see cref="WorkerAcceptTimeoutMs"/> を超えたら待つのをやめ、
    /// <b>従来どおりリダイレクトへ進む</b>。
    ///
    /// <para>
    /// <b>残る窓（既知・許容）:</b> このイベントはワーカーがキー登録に勝った<b>直後</b>に
    /// リセットされる。前のワーカーが死んだ瞬間に別のランチャーがハンドルを保持していると、
    /// カーネルオブジェクトが生き残ってシグナル状態のまま次のワーカーへ引き継がれうる。
    /// その場合このメソッドは素通りするが、<b>結果は従来の挙動（60 秒待って
    /// <see cref="ExitCode_WorkerResultTimeout"/>）に戻るだけ</b>で、退行にはならない。
    /// キー登録の<b>前</b>にリセットする案は採れない ── 登録に負けた場合に
    /// <b>生きている別のワーカーのシグナルを消してしまう</b>。
    /// </para>
    /// </summary>
    /// <returns>
    /// <b>合図を受け取れたか。</b> <c>false</c> は「上限まで待っても受理の合図が来なかった」
    /// または「名前付きオブジェクトを開けなかった」。
    /// <b>呼び出し側はこれを見てキーの持ち主を再検証する</b> ── 合図が来ないケースは、
    /// ワーカーが死んでいるケースと同じ集合だからである
    /// （常態では p90 337ms で <c>true</c> が返るので、再検証は hot path に載らない）。
    /// </returns>
    private static bool WaitUntilWorkerAcceptsCommands(string keyPrefix)
    {
        try
        {
            using var accepting = new EventWaitHandle(
                initialState: false, EventResetMode.ManualReset, Names.WorkerAcceptingEvent(keyPrefix));
            return accepting.WaitOne(WorkerAcceptTimeoutMs);
        }
        catch (Exception)
        {
            // 名前付きオブジェクトを開けない環境（権限・名前衝突）でも、
            // ここは最適化にすぎないので黙って従来どおりリダイレクトへ進む。
            return false;
        }
    }

    /// <summary>単一インスタンス判定キーや各種名前付きカーネルオブジェクトの名前を、共通プレフィックスから導出する。</summary>
    private static class Names
    {
        public static string InstanceKey(string prefix) => $"{prefix}.SingleInstanceKey";
        public static string LauncherMutex(string prefix) => $"{prefix}.LauncherMutex";
        public static string WorkerReadyEvent(string prefix) => $"{prefix}.WorkerReadyEvent";

        /// <summary>
        /// 常駐ワーカーが <c>Activated</c> の購読を終えたことを表す<b>手動リセット</b>イベント。
        /// <see cref="WorkerReadyEvent"/>（自動リセット・コールドスタートの1回きり）とは別物で、
        /// <b>ワーカーの生存中ずっとシグナル状態を保つ</b>必要があるため分けてある。
        /// </summary>
        public static string WorkerAcceptingEvent(string prefix) => $"{prefix}.WorkerAcceptingEvent";
        public static string CommandResultReadyEvent(string prefix) => $"{prefix}.CommandResultReadyEvent";
        public static string CommandResultMap(string prefix) => $"{prefix}.CommandResultMap";
    }
}
