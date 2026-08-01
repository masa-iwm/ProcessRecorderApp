using System.Diagnostics;
using System.Text;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>CLI を1回実行した結果。</summary>
public sealed record CliResult(string CommandLine, int ExitCode, string StdOut, string StdErr, TimeSpan Elapsed)
{
    /// <summary>stdout の最初の非空行（解決済みファイル名の取得に使う）。</summary>
    public string FirstOutputLine =>
        StdOut.Split('\n').Select(l => l.Trim('\r', ' ')).FirstOrDefault(l => l.Length > 0) ?? "";

    public override string ToString() =>
        $"`{CommandLine}` -> exit {ExitCode} ({Elapsed.TotalMilliseconds:F0}ms)" + Environment.NewLine +
        $"  stdout: {StdOut.Trim()}" + Environment.NewLine +
        $"  stderr: {StdErr.Trim()}";
}

/// <summary>
/// 完全に隔離した1つのアプリ実体（一時データディレクトリ＋固有のキー接頭辞＋常駐ワーカー）。
///
/// <para>
/// 隔離の2本立ては<b>どちらも必須</b>:
/// <c>PROCESSRECORDERAPP_DATA_DIR</c> が無いとテストが実ユーザーの
/// <c>%LOCALAPPDATA%\ProcessRecorderApp</c>（＝開発者の本物の設定）を書き換え、
/// <c>PROCESSRECORDERAPP_KEY_PREFIX</c> が無いとコマンドが開発者の常駐インスタンスへ
/// 転送される（逆にテストのワーカーが開発者の操作を奪う）。
/// 上書きが効いていないまま走ることが最悪なので、<see cref="StartWorkerAndWaitUntilReady"/> は
/// 一時ディレクトリに activity.log が生えたことを確認してからでないと成功と見なさない。
/// </para>
/// </summary>
public sealed class AppInstance : IDisposable
{
    // 環境変数名は製品側（Components.AppEnvironment）と一致していなければならない。
    // このプロジェクトは製品を参照しないので、外から見た契約としてここにも書く
    // （製品側の綴りは L1 の AppEnvironmentTests が固定している）。
    public const string DataDirVariable = "PROCESSRECORDERAPP_DATA_DIR";
    public const string KeyPrefixVariable = "PROCESSRECORDERAPP_KEY_PREFIX";
    public const string LanguageVariable = "PROCESSRECORDERAPP_LANG";

    /// <summary>
    /// 捕捉した出力を、差し替える前の標準エラーへも複写させる（製品側の
    /// <c>AppEnvironment.MirrorToOriginalStdErrVariable</c>）。
    /// <b>これを渡さないと <see cref="WorkerOutput"/> は構造的に常に空になる。</b>
    /// </summary>
    public const string MirrorStdErrVariable = "PROCESSRECORDERAPP_MIRROR_STDERR";

    /// <summary>
    /// これを設定して実行すると、テスト終了時に一時データディレクトリを消さない
    /// （settings.json / activity.log / debug.log / 生成 MP4 をそのまま調べられる）。
    /// 失敗の原因が製品側かテスト側かを切り分けるとき以外は使わない。
    /// </summary>
    public const string KeepDataDirVariable = "PROCESSRECORDERAPP_E2E_KEEP";

    private const string WorkerFlag = "--__resident-worker";

    /// <summary>
    /// CLI 1回あたりの既定の制限時間。
    ///
    /// <para>
    /// <b>ランチャー自身の待ちより長く取る。</b> 逆転すると、ランチャーが
    /// 終了コード 2（結果待ちタイムアウト）を返す前にハーネスが先に打ち切ってしまい、
    /// <b>製品が返したはずの終了コードを観測できなくなる</b>。
    /// ランチャー側の待ちは3つ:
    /// <list type="bullet">
    ///   <item><description>常駐ワーカーの準備完了待ち 120 秒（コールドスタート経路のみ）</description></item>
    ///   <item><description>受理可能待ち＋結果待ちで<b>合計 60 秒</b>（リダイレクト経路。
    ///     予算は共有で、直列に積むと Mutex 占有が倍増する）</description></item>
    /// </list>
    /// （いずれも `SingleInstanceManager.Launcher.cs`）。
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultCliTimeout = TimeSpan.FromSeconds(180);

    /// <summary>ウォームアップ済みの状態で常駐ワーカーが応答するまでの既定の締め切り。</summary>
    public static readonly TimeSpan DefaultReadyBudget = TimeSpan.FromSeconds(180);

    /// <summary>
    /// 準備完了ポーリングの <c>ping</c> 1回あたりの打ち切り。
    /// <b>短く保つこと</b> ── ここは回数で拾う仕組みで、1回を長くすると回数が減って
    /// かえって弱くなる（<see cref="StartWorkerAndWaitUntilReady"/> のコメント）。
    /// </summary>
    public static readonly TimeSpan ReadyPingTimeout = TimeSpan.FromSeconds(15);

    private readonly PublishedApp _app;
    private readonly List<Process> _startedProcesses = [];
    private readonly StringBuilder _workerOutput = new();
    private readonly object _outputGate = new();

    public string DataDir { get; }
    public string KeyPrefix { get; }
    public string RecordingsDir { get; }
    public SettingsFile Settings { get; }

    /// <summary>強制する表示言語（null なら OS の表示言語のまま）。</summary>
    public string? Language { get; init; }

    /// <summary>
    /// ランチャー・常駐ワーカーの双方に渡す追加の環境変数。
    /// <c>{ENV.x}</c> の展開を、実行環境に依存しない自前の変数で検証するために使う
    /// （<c>USERNAME</c> は機械によって非 ASCII になりうる）。
    /// </summary>
    public Dictionary<string, string> ExtraEnvironment { get; } = [];

    public string SettingsPath => Path.Combine(DataDir, "settings.json");
    public string ActivityLogPath => Path.Combine(DataDir, "activity.log");
    public string RotatedActivityLogPath => Path.Combine(DataDir, "activity.log.1");

    private AppInstance(PublishedApp app, SettingsFile settings)
    {
        _app = app;
        Settings = settings;

        // ASCII だけのパスにする。CLI の出力（解決済みファイル名）を読むとき、
        // バイト列はコンソールのコードページになるため、非 ASCII を混ぜると
        // 「アプリの出力が壊れている」ように見える（実際に一度誤読しかけた）。
        string stamp = Guid.NewGuid().ToString("N")[..8];
        DataDir = Path.Combine(Path.GetTempPath(), "pra-e2e-" + stamp);
        KeyPrefix = "PraE2E" + stamp;
        RecordingsDir = Path.Combine(DataDir, "rec");

        Directory.CreateDirectory(RecordingsDir);
    }

    /// <summary>
    /// 一時ディレクトリと settings.json を用意した実体を作る。
    /// <paramref name="startWorker"/> が false の場合は常駐ワーカーを起動しない
    /// （コールドスタートの検証で必要）。
    /// </summary>
    public static AppInstance Create(
        PublishedApp app,
        SettingsFile settings,
        bool startWorker = true,
        string? language = null,
        Action<AppInstance>? configure = null)
    {
        var instance = new AppInstance(app, settings) { Language = language };
        configure?.Invoke(instance);
        instance.WriteSettings();
        if (startWorker)
            instance.StartWorkerAndWaitUntilReady(DefaultReadyBudget);
        return instance;
    }

    /// <summary>現在の <see cref="Settings"/> を settings.json として書き出す。</summary>
    public void WriteSettings() => Settings.WriteTo(SettingsPath, RecordingsDir);

    /// <summary>
    /// 常駐ワーカーを直接起動し（<c>--__resident-worker</c>）、<c>ping</c> が 0 を返すまで待つ。
    /// ランチャー経由で暗黙に起動させないのは、初回の GStreamer レジストリ構築が長く、
    /// ランチャーの待ちを超えうるため ── ここでは自前の締め切りで待ち直す。
    ///
    /// <para>
    /// <b>1回あたりの ping は短く打ち切って、回数を稼ぐ。</b> ここは「起きているか」の
    /// ポーリングであって、1回の結果に意味は無い ── 効いているのは**繰り返すこと**。
    /// ここに <see cref="DefaultCliTimeout"/> を使うと、ランチャーの結果待ち（60 秒）に
    /// 引きずられて**1回の ping が 60 秒**かかるようになり、
    /// 180 秒の締め切りに <b>3 回しか入らなくなる</b>（15 秒の打ち切りなら 12 回入る）。
    /// そうなると、リトライで拾えていたケースが拾えなくなって失敗が増え、
    /// スイートの所要時間も大幅に伸びる。**待ちを長くしても強くはならない。**
    /// </para>
    /// </summary>
    /// <returns>ping を試した回数。</returns>
    public int StartWorkerAndWaitUntilReady(TimeSpan budget)
    {
        StartWorkerProcess();

        var deadline = Stopwatch.StartNew();
        int attempts = 0;
        CliResult? last = null;

        while (deadline.Elapsed < budget)
        {
            attempts++;
            last = Run(ReadyPingTimeout, "ping");
            if (last.ExitCode == 0)
            {
                // 隔離が実際に効いていることの確認。ここが空なら、テストは
                // 開発者の本物のデータディレクトリを相手に走っている。
                if (!File.Exists(ActivityLogPath))
                {
                    throw new InvalidOperationException(
                        $"ping は成功したのに '{ActivityLogPath}' が存在しません。" +
                        $"{DataDirVariable} / {KeyPrefixVariable} の上書きが効いていない可能性があります。" +
                        Environment.NewLine + DiagnosticDump());
                }
                return attempts;
            }

            Thread.Sleep(500);
        }

        throw new InvalidOperationException(
            $"常駐ワーカーが {budget.TotalSeconds:F0} 秒以内に応答しませんでした（ping {attempts} 回）。" +
            Environment.NewLine + last + Environment.NewLine + DiagnosticDump());
    }

    private void StartWorkerProcess()
    {
        var psi = CreateStartInfo(WorkerFlag);

        // **ここでだけ有効にする（CreateStartInfo に入れてはいけない）。**
        // ワーカーは StandardStreamRedirector で標準出力/標準エラーを3層とも
        // プロセス内パイプへ差し替えるので、既定のままでは下の BeginErrorReadLine が
        // 1行も受け取れない（＝失敗時の診断がゼロ）。
        //
        // CreateStartInfo に入れるとランチャー起動にも乗り、ランチャーが
        // 暗黙起動する常駐ワーカーへ環境ごと継承される。そちらのワーカーの複写先は
        // **既に終了したランチャー呼び出しのパイプ**で、誰も吸い出さない
        // ── 埋まった時点で書き込みがブロックし、ワーカーの読み取りスレッドごと止まる。
        // 直接起動したワーカーだけは、このハーネスが最後まで吸い続ける。
        psi.Environment[MirrorStdErrVariable] = "1";

        // .NET ランタイム自身のミニダンプ。**WER と違って昇格も HKLM も要らない**ので、
        // 開発機でも実際にダンプが出ることを確かめられる（WER は HKLM に書けず未検証のまま）。
        // ただし**これは CoreCLR の機能なので AOT 発行物では効かない** ── AOT 側は
        // WER の LocalDumps が唯一の手段。両方入れてあるのはそのため（片方を消さないこと）。
        psi.Environment["DOTNET_DbgEnableMiniDump"] = "1";
        psi.Environment["DOTNET_DbgMiniDumpType"] = "1"; // 1 = ミニ（フルは数百MB）
        psi.Environment["DOTNET_DbgMiniDumpName"] = Path.Combine(DataDir, "worker.%p.dmp");

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("常駐ワーカーを起動できませんでした。");

        // 常駐ワーカーは activity.log を stderr へ複写する。読み捨てないとパイプが詰まって
        // ワーカーが止まるので、必ず吸い続ける（内容は失敗時の診断に使う）。
        process.OutputDataReceived += (_, e) => AppendWorkerOutput(e.Data);
        process.ErrorDataReceived += (_, e) => AppendWorkerOutput(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _startedProcesses.Add(process);
    }

    private void AppendWorkerOutput(string? line)
    {
        if (line is null)
            return;
        lock (_outputGate)
        {
            // 障害注入のケースでは数千行出ることがあるので上限を設ける。
            if (_workerOutput.Length < 512 * 1024)
                _workerOutput.AppendLine(line);
        }
    }

    /// <summary>常駐ワーカーの標準出力/標準エラー（失敗時の診断用）。</summary>
    public string WorkerOutput
    {
        get { lock (_outputGate) return _workerOutput.ToString(); }
    }

    /// <summary>CLI を1回実行する（ランチャープロセスの終了を待つ）。</summary>
    public CliResult Run(params string[] args) => Run(DefaultCliTimeout, args);

    /// <summary>CLI を1回実行する。</summary>
    public CliResult Run(TimeSpan timeout, params string[] args)
    {
        var psi = CreateStartInfo(args);
        string commandLine = string.Join(' ', args);

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"起動できませんでした: {commandLine}");

        // 先に読み切ってから待つ（待ってから読むとパイプが詰まって相互待ちになる）。
        var stdout = Task.Run(() => process.StandardOutput.ReadToEnd());
        var stderr = Task.Run(() => process.StandardError.ReadToEnd());

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            TryKill(process);
            sw.Stop();
            var timedOut = new CliResult(commandLine, -1, "", $"(timeout after {timeout.TotalSeconds:F0}s)", sw.Elapsed);
            AppendHarnessLog($"{timedOut}{Environment.NewLine}{WorkerProcessStates()}");
            return timedOut;
        }
        sw.Stop();

        var result = new CliResult(commandLine, process.ExitCode, stdout.Result, stderr.Result, sw.Elapsed);

        // **0 以外はその場でワーカーの生死を残す。** 調査中の失敗（`activate` の exit 2、
        // `start-recording-all` の exit 2）は「ランチャーが 0 以外を返した瞬間」にしか
        // 手掛かりが無く、テストが `Assert.Equal(0, ...ExitCode)` で落ちると
        // TRX には `Expected: 0 / Actual: 2` しか出ない ── ここで書いておけば、
        // 呼び出し側が何を表明していようと harness.log には必ず残る。
        // 0 以外を**期待している**ケース（CliContractTests の終了コード表）でも
        // 1行増えるだけなので、条件は単純に保つ。
        if (result.ExitCode != 0)
            AppendHarnessLog($"{result}{Environment.NewLine}{WorkerProcessStates()}");

        return result;
    }

    /// <summary>
    /// CLI の終了コードを表明する。<b>失敗したときだけ</b>診断を組み立てて添える。
    ///
    /// <para>
    /// <c>Assert.Equal(0, result.ExitCode)</c> と書いてはいけない ── xUnit の
    /// <c>Assert.Equal</c> はメッセージを取らないので、TRX に残るのは
    /// <c>Expected: 0 / Actual: 2</c> の1行だけになり、**ワーカーの生死が消える**。
    /// 調査中の失敗はまさにこの形（<c>activate</c> / <c>start-recording-all</c> の exit 2）。
    /// </para>
    /// <para>
    /// <b><c>Assert.True(cond, DiagnosticDump())</c> でも駄目。</b> メッセージ引数は
    /// 成否によらず**先に評価される**ので、緑のときも毎回 activity.log を読みに行く
    /// ことになる（このスイートは CLI を数百回叩く）。
    /// </para>
    /// </summary>
    public CliResult AssertExit(int expected, CliResult result)
    {
        if (result.ExitCode != expected)
            Assert.Fail($"{result}{Environment.NewLine}{DiagnosticDump()}");
        return result;
    }

    /// <summary>CLI を1回実行し、終了コードを表明する（<see cref="AssertExit"/> の短縮形）。</summary>
    public CliResult RunExpecting(int expected, params string[] args) => AssertExit(expected, Run(args));

    /// <summary>
    /// 常駐ワーカーの起動だけを行い、待たずに返す（多重起動の検証用）。
    /// 呼び出し側が終了コードを見る。
    /// </summary>
    public CliResult RunWorkerBootstrap(TimeSpan timeout) => Run(timeout, WorkerFlag);

    private ProcessStartInfo CreateStartInfo(params string[] args)
    {
        var psi = new ProcessStartInfo(_app.ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _app.PublishDir,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        psi.Environment[DataDirVariable] = DataDir;
        psi.Environment[KeyPrefixVariable] = KeyPrefix;
        if (Language is { Length: > 0 })
            psi.Environment[LanguageVariable] = Language;
        else
            psi.Environment.Remove(LanguageVariable);

        foreach (var (key, value) in ExtraEnvironment)
            psi.Environment[key] = value;

        return psi;
    }

    /// <summary>activity.log の全行（無ければ空）。</summary>
    public IReadOnlyList<string> ReadActivityLog() => ActivityLogFile.ReadLines(ActivityLogPath);

    /// <summary>
    /// <c>activity.log</c> に指定イベントが現れるまで待つ。
    ///
    /// <para>
    /// <b>固定の <c>Sleep</c> で代用しないこと。</b> 待ちたいのは経過時間ではなく
    /// 「ワーカー側で事象が起きたこと」で、ランナーの速度は開発機の何倍もばらつく
    /// ── 短ければ落ち、長ければスイート全体が伸びる。
    /// ここは <b>1イベント＝1行</b>という activity.log の形をそのまま使える場所である。
    /// </para>
    /// </summary>
    /// <returns>現れたか（false は予算切れ）。</returns>
    public bool WaitForActivityLogEvent(string eventName, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (0 < ActivityLogFile.Events(ReadActivityLog(), eventName).Count)
                return true;
            Thread.Sleep(100);
        }

        return 0 < ActivityLogFile.Events(ReadActivityLog(), eventName).Count;
    }

    /// <summary>録画先ディレクトリの MP4 を作成順に返す。</summary>
    public IReadOnlyList<string> ListRecordings() =>
        Directory.Exists(RecordingsDir)
            ? [.. Directory.GetFiles(RecordingsDir, "*.mp4").OrderBy(File.GetCreationTimeUtc)]
            : [];

    /// <summary>
    /// 常駐ワーカーのアプリウィンドウのキャプション（<c>GetWindowText</c>）を全て返す。
    ///
    /// <para>
    /// <b>ウィンドウを表示せずに読む。</b> Launch-to-Tray なのでウィンドウは
    /// 生成直後に隠されており、UIA のデスクトップ直下には出てこない
    /// ── <c>EnumWindows</c> で pid から辿る。
    /// </para>
    /// <para>
    /// <b>ここを「表示してから読む」形にしてはいけない。</b> <c>MainWindow_Activated</c> の
    /// <c>SetTitleBar(AppTitleBar)</c> は<b>アクティブ化の時点で</b>キャプションを
    /// 製品名に直してしまうので、表示してから見るとキャプションは常に正しく見える。
    /// 一方 WinUIEx がトレイアイコンのツールチップに使う <c>AppWindow.Title</c> は
    /// <b>アイコン登録時の1回きり</b>＝アクティブ化より前の値なので、
    /// 表示後に見る表明では**「トレイのツールチップが既定値のまま」という不具合を
    /// 原理的に検出できない**（実測で確認済み）。
    /// </para>
    /// </summary>
    /// <summary>
    /// WinUI 3 のアプリウィンドウのクラス名。
    ///
    /// <para>
    /// <b>このクラスのウィンドウは1つとは限らない。</b> 実測では2つあり、片方は
    /// WinUI が自分で作る補助ウィンドウで <c>"WinUI Desktop"</c>（WinUI 3 の既定の
    /// タイトル）のまま残る。もう片方が <c>MainWindow</c>。
    /// 「テキストのある最初のトップレベルウィンドウ」でも
    /// 「このクラスの最初のウィンドウ」でも取り違える ── どちらも実際に踏んだ。
    /// </para>
    /// </summary>
    public const string MainWindowClassName = "WinUIDesktopWin32WindowClass";

    /// <summary>常駐ワーカーのトップレベルウィンドウ（診断にも使う）。</summary>
    public IReadOnlyList<(nint Handle, string ClassName, string Caption)> ListWorkerWindows()
    {
        var windows = new List<(nint, string, string)>();
        var pids = ActivityLogFile.WorkerPids(ReadActivityLog()).Select(p => (uint)p).ToHashSet();
        if (pids.Count == 0)
            return windows;

        // ラムダの第2引数を `_` にしない ── discard ではなく nint のパラメータとして
        // 束縛され、中の `_ = GetWindowThreadProcessId(...)` がそれへの代入になる。
        EnumWindows((handle, unusedLParam) =>
        {
            _ = GetWindowThreadProcessId(handle, out uint owner);
            if (!pids.Contains(owner))
                return true;

            var className = new StringBuilder(256);
            _ = GetClassName(handle, className, className.Capacity);
            var caption = new StringBuilder(512);
            _ = GetWindowText(handle, caption, caption.Capacity);
            windows.Add((handle, className.ToString(), caption.ToString()));
            return true;
        }, nint.Zero);

        return windows;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>
    /// このハーネスが直接起動した常駐ワーカーの生死（1行1プロセス）。
    ///
    /// <para>
    /// <b>「ワーカーの無言死」を観測する唯一の手段。</b> ワーカーが <c>app.exit</c> も
    /// <c>app.error</c> も残さずに消える現象では activity.log に手掛かりが1つも無いので、
    /// <b>プロセスの終了コード</b>だけが仮説を切り分ける。
    /// <c>0</c> ならメッセージループが <c>Program.cs</c> の <c>app.exit</c> 記録に
    /// 到達せずに畳まれたということで、<c>0xC0000005</c> のような NTSTATUS ならクラッシュ
    /// （＝WER のダンプを見に行く）。
    /// </para>
    /// <para>
    /// <b>観測済みの無言死はすべてネイティブクラッシュだった。</b>
    /// <c>0xC0000005</c>（アクセス違反）が大半で、ほかに <c>0xC0000374</c>（ヒープ破壊）・
    /// <c>0xC0000409</c>（スタック破壊の <c>__fastfail</c>）・<c>0xC0000002</c>。
    /// <b>＝メモリ破壊</b>で、終了経路の論理の問題ではない。
    /// </para>
    /// <para>
    /// <b><c>Exited</c> イベントの配線は要らない。</b> <see cref="Process"/> オブジェクトを
    /// 握ったままにしてあるので、プロセスが終了したあとでも OS は終了記録を保持し続け、
    /// <c>ExitCode</c> / <c>ExitTime</c> はいつでも読める。
    /// </para>
    /// <para>
    /// <b>「死亡時のワーカー年齢」（<c>ExitTime - StartTime</c>）を必ず出すこと。</b>
    /// これまでに観測された無言死はすべて年齢 0.7〜5.2 秒に集中しており、
    /// 同じ帯に入るかどうかが「同じ署名の再発か」の判定そのものになる。
    /// </para>
    /// </summary>
    public string WorkerProcessStates()
    {
        if (_startedProcesses.Count == 0)
            return "(このハーネスが直接起動したワーカーは無い)";

        var sb = new StringBuilder();
        foreach (var process in _startedProcesses)
        {
            sb.Append("  ").Append(DescribeWorkerProcess(process)).AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string DescribeWorkerProcess(Process process)
    {
        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return "pid=? (プロセスオブジェクトが無効)";
        }

        try
        {
            if (!process.HasExited)
                return $"pid={pid} 生存中 age={AgeOf(process, DateTime.Now)}";

            // 終了コードは10進と16進の両方で出す。クラッシュの NTSTATUS
            // （0xC0000005 等）は16進でないと見分けが付かない。
            int exitCode = process.ExitCode;
            return $"pid={pid} **終了済み** exitCode={exitCode} (0x{exitCode:X8}) " +
                   $"age={AgeOf(process, ExitTimeOrNow(process))}";
        }
        catch (SystemException ex)
        {
            return $"pid={pid} 状態を読めない: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static DateTime ExitTimeOrNow(Process process)
    {
        try { return process.ExitTime; }
        catch (SystemException) { return DateTime.Now; }
    }

    /// <summary>
    /// 死亡時のワーカー年齢。
    ///
    /// <para>
    /// <b>負の値や桁外れの値をそのまま出さないこと。</b> <c>ExitTime</c> は取れないときに
    /// <b>FILETIME の原点（1601-01-01）</b>を返すことがあり、そのまま引くと
    /// <c>age=-13429765046.24s</c>（＝約 -425 年）のような値になる
    /// ── 初回の CI 実行で実際に出た。**診断が診断として読めなくなる**ので、
    /// 妥当でない場合は数字を出さずに理由を書く。
    /// </para>
    /// </summary>
    private static string AgeOf(Process process, DateTime at)
    {
        try
        {
            var age = at - process.StartTime;
            if (age < TimeSpan.Zero || age > TimeSpan.FromDays(1))
                return $"?(ExitTime={at:yyyy-MM-dd HH:mm:ss} StartTime={process.StartTime:yyyy-MM-dd HH:mm:ss})";
            return $"{age.TotalSeconds:F2}s";
        }
        catch (SystemException) { return "?"; }
    }

    /// <summary>失敗メッセージに添える診断（activity.log の末尾とワーカーの出力）。</summary>
    public string DiagnosticDump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- data dir: {DataDir}");
        var log = ReadActivityLog();
        sb.AppendLine($"--- activity.log ({log.Count} 行, 末尾 40 行) ---");
        foreach (string line in log.TakeLast(40))
            sb.AppendLine(line);

        // **ここは activity.log より後ろに置かない。** 無言死ではログ側が空なので、
        // 先に読まれる位置に生死を置いておかないと、TRX を上から読んだ人が
        // 「何も分からない」と結論して終わる。
        sb.AppendLine("--- 起動したワーカーのプロセス状態 ---");
        sb.AppendLine(WorkerProcessStates());

        // キー登録に負けたワーカーは app.start を書かないまま app.exit だけを残す。
        // WorkerPids には出てこないので、ここで別に数える。
        var orphans = ActivityLogFile.WorkerExits(log)
            .Where(e => !ActivityLogFile.WorkerPids(log).Contains(e.Pid))
            .ToList();
        if (orphans.Count > 0)
        {
            sb.AppendLine($"--- app.start を持たない app.exit（{orphans.Count} 件） ---");
            foreach (var (pid, exitCode) in orphans)
                sb.AppendLine($"  pid={pid} exitCode={exitCode}");
        }

        string worker = WorkerOutput;
        if (worker.Length > 0)
        {
            var lines = worker.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            sb.AppendLine($"--- worker stdout/stderr ({lines.Length} 行, 末尾 20 行) ---");
            foreach (string line in lines.TakeLast(20))
                sb.AppendLine(line);
        }
        return sb.ToString();
    }

    /// <summary>
    /// このインスタンスに属する常駐ワーカーを全て落とす。
    /// 自分が起動した子プロセスに加え、<b>ランチャーが暗黙に起動したワーカー</b>
    /// （コールドスタート経路。子ではない）も activity.log の <c>app.start pid=</c> から拾う。
    /// <b>残すと次の dotnet publish が MSB3027（発行先の DLL がロック）で落ちる。</b>
    /// </summary>
    public void KillWorkers()
    {
        // **殺す前に読むこと。** 自分で Kill した後だと ExitCode は自分が与えた値になり、
        // 「勝手に死んでいたのか」「生きていたのを落としたのか」の区別が消える
        // ── その区別こそがワーカーの無言死の観測そのもの。
        //
        // アサーションが立たずに終わったケース（緑だが裏でワーカーが入れ替わっていた等）は
        // DiagnosticDump() が呼ばれないので、ここでファイルにも残す。
        // 拡張子を .log にするのは build.yml の成果物収集が `pra-e2e-*/*.log` で
        // 拾うため（.txt にすると黙ってアップロードされない）。
        AppendHarnessLog("KillWorkers 直前のワーカーの状態:" + Environment.NewLine + WorkerProcessStates());

        foreach (var process in _startedProcesses)
            TryKill(process);
        _startedProcesses.Clear();

        foreach (int pid in ActivityLogFile.WorkerPids(ReadActivityLog()))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.ProcessName.Equals("ProcessRecorderApp", StringComparison.OrdinalIgnoreCase))
                    TryKill(process);
            }
            catch (ArgumentException)
            {
                // 既に終了している（pid が存在しない）。
            }
        }
    }

    /// <summary>
    /// ハーネス自身の観測を <c>&lt;DataDir&gt;\harness.log</c> へ追記する。
    /// <b>製品が書く activity.log とは別のファイルにする</b> ── 混ぜると
    /// <see cref="ActivityLogFile"/> の行の解析が壊れ、L2 の表明が誤って動く。
    /// </summary>
    private void AppendHarnessLog(string text)
    {
        try
        {
            // **BOM 付きで書く。** このファイルは失敗の直後に手元で開かれるものなので、
            // Windows PowerShell 5.1 の `Get-Content` が BOM 無しを ANSI（この機械では
            // CP932）として読んで日本語を文字化けさせる罠を踏ませない
            // ── 実際に踏んで、診断のはずのファイルが読めなかった。
            File.AppendAllText(
                Path.Combine(DataDir, "harness.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (IOException) { } // DirectoryNotFoundException もこれに含まれる
        catch (UnauthorizedAccessException) { }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException) { }
        catch (SystemException) { }
    }

    public void Dispose()
    {
        KillWorkers();

        if (Environment.GetEnvironmentVariable(KeepDataDirVariable) is { Length: > 0 })
        {
            Console.WriteLine($"[{KeepDataDirVariable}] データディレクトリを残しました: {DataDir}");
            return;
        }

        // ワーカーが debug.log 等を掴んだまま終了直後だと削除に失敗することがある。
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (Directory.Exists(DataDir))
                    Directory.Delete(DataDir, recursive: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(200);
        }
        // 消せなくてもテストは落とさない（%TEMP% に残るだけ）。
    }
}
