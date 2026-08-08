using ProcessRecorderApp.Components;
using ProcessRecorderApp.SingleInstance;
using WinRT;

namespace ProcessRecorderApp;

/// <summary>
/// アプリのエントリポイント。単一インスタンス制御・タスクトレイ常駐・起動引数のディスパッチは
/// <see cref="SingleInstanceManager"/>（<c>SingleInstance</c>プロジェクト）に委譲し、
/// このクラスはアプリ固有の値（<see cref="KeyPrefix"/>、常駐ワーカーの生成方法、
/// ランチャー側で直接処理する引数の判定）を結び付けるだけの薄いエントリポイントとする。
/// </summary>
public static class Program
{
    /// <summary>
    /// 単一インスタンス判定キーや名前付きMutex/EventWaitHandle/MemoryMappedFile等に
    /// 共通で使うプレフィックス。既定値は <see cref="AppEnvironment.DefaultKeyPrefix"/> で、
    /// 環境変数 <c>PROCESSRECORDERAPP_KEY_PREFIX</c> があればそちらを使う
    /// （E2E テストを開発者の常駐インスタンスから隔離するため）。
    /// </summary>
    internal static string KeyPrefix => AppEnvironment.KeyPrefix;

    // ネイティブDLL・マネージドDLL の標準出力/標準エラーを捕捉する（アプリ寿命のため Dispose しない）
    private static StandardStreamRedirector? _stdRedirector;

    [STAThread]
    private static int Main(string[] args)
    {
        // 単一ファイル発行(win-x64-singlefileプロファイル)では、生成されたマニフェストが
        // WinAppSDKネイティブDLLのロード先をこの環境変数で解決するため、
        // WinRT APIを呼び出す前(Main先頭)に設定が必要。
        // 通常発行(多ファイル)ではマニフェストにリダイレクトが無く、設定しても無害。
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);

        ComWrappersSupport.InitializeComWrappers();

        ApplyLanguageOverride();

        if (SingleInstanceManager.IsWorkerBootstrap(args))
        {
            var result = SingleInstanceManager.StartResidentWorker(KeyPrefix, () => new App(), () =>
            {
                // 捕捉先はプロセス寿命の有界リング。受け口をコンストラクターで渡すのは、
                // ここから購読するまでの間の出力（直後の app.start を含む）を落とさないため
                _stdRedirector = new StandardStreamRedirector(LogBuffer.Shared.Append);

                // 破棄マーカーの文言をローカライズする。呼ばれるのは実際に破棄が起きたときだけなので、
                // リソースの解決もそのときまで遅らせる
                LogBuffer.DropMarkerFormatter = dropped => string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Localization.GetString("Resources/Log_LinesDropped"),
                    dropped);

                // activity.log の初期化は StandardStreamRedirector の生成後（複写した行が
                // アプリ内 Log 画面と DebugLogFile へ届くようにするため）、かつ
                // Controller.StaticInitialize() より前（gst.encoders の行を取りこぼさないため）。
                // mirrorToConsole=true は常駐ワーカーのみ。ランチャープロセスの標準エラーは
                // ユーザーのコンソールそのものなので、そちらでは初期化しない。
                ActivityLog.Initialize(AppEnvironment.DataDirectory, mirrorToConsole: true);
                ActivityLog.Info("app.start", $"pid={Environment.ProcessId} data='{AppEnvironment.DataDirectory}'");

                // デバッグログファイルの反映（設定変更時に即時反映するため PropertyChanged を購読する）
                void ApplyLogFile()
                {
                    var path = Settings.AppSettings.Default.DebugLogFile;
#if DEBUG
                    // 設定が空欄の場合、デバッグビルドでは従来通り debug.txt に保存する
                    if (string.IsNullOrEmpty(path))
                        path = "debug.txt";
#endif
                    // 開けない・解決できないパスは保存を諦めて記録だけ残す。
                    // ここは App の未処理例外ハンドラ購読より前に走るので、例外が漏れると
                    // 保存済みの不正なパス1つで常駐ワーカーが毎回起動途中で死ぬ。
                    try
                    {
                        // 相対パスは実行ファイル基準（AppDirectories 規約）。常駐ワーカーは最初に
                        // 起動したシェルのカレントディレクトリをプロセス寿命ぶん引きずるため、
                        // CWD 基準だと同じ設定でも起動のたびに別の場所へ出る。
                        path = AppDirectories.ResolveOptional(path);

                        if (_stdRedirector!.SetLogFile(path) is { } logFileError)
                            ActivityLog.Error("log.file error", $"path='{path}' {logFileError.Message}");
                    }
                    catch (Exception ex)
                    {
                        // ResolveOptional（GetFullPath）が不正なパスで投げる経路への保険。
                        ActivityLog.Error("log.file error", $"path='{path}' {ex.Message}");
                    }
                }
                ApplyLogFile();
                Settings.AppSettings.Default.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(Settings.AppSettings.DebugLogFile))
                        ApplyLogFile();
                };

                // GStreamer 初期化前にデバッグ用環境変数を反映する（既に指定済みの場合は上書きしない）
                Settings.AppSettings.Default.ApplyStartupEnvironmentVariables();

                GStreamer.Controller.StaticInitialize();
            });

            // app.exit は AppWindow.Destroying ではなくここで記録する。トレイ格納は Closing を
            // キャンセルするだけなので Destroying は本当の終了時にしか発火せず、
            // かつ StartResidentWorker が返る経路（＝メッセージループの終了）は
            // ウィンドウを作らずに終了した場合も通るため、こちらの方が取りこぼしが少ない。
            // なお強制終了（Stop-Process -Force）ではどちらの経路も通らない。
            ActivityLog.Info("app.exit", $"pid={Environment.ProcessId} exitCode={result}");

            _stdRedirector?.Dispose();

            return result;
        }

        return SingleInstanceManager.Run(KeyPrefix, args, ActivationCommands.TryHandleInLauncher);
    }

    /// <summary>
    /// 環境変数 <c>PROCESSRECORDERAPP_LANG</c> があれば表示言語を強制する（テスト用フック）。
    /// 未設定なら何もしないので通常起動の挙動は変わらない。
    /// ランチャー・常駐ワーカーの双方で有効にする必要がある ── <c>--help</c> の出力は
    /// ランチャープロセスが行い、画面の文言は常駐ワーカーが行うため。
    /// リソースの解決前（<see cref="Localization"/> の初回呼び出し前）に設定しなければ効かない。
    ///
    /// <para>
    /// <b><c>Windows.Globalization</c> ではなく <c>Microsoft.Windows.Globalization</c> を使う。</b>
    /// 前者（OS 側の WinRT API）は<b>パッケージ ID を要求する</b>ため、アンパッケージ配布の
    /// 本アプリでは必ず <c>0x80073D54</c>（「プロセスにパッケージ ID がありません」）で失敗し、
    /// <b>この機能全体が無言で効かない</b>状態になる。WinAppSDK 側の同名 API は
    /// アンパッケージ前提で用意されたもので、MRT Core の解決（<c>x:Uid</c> を含む）に効く。
    /// </para>
    /// </summary>
    private static void ApplyLanguageOverride()
    {
        string? language = Environment.GetEnvironmentVariable(AppEnvironment.LanguageVariable);
        if (string.IsNullOrWhiteSpace(language))
            return;

        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language.Trim();
        }
        catch (Exception ex)
        {
            // 不正な言語タグでもアプリは起動させる（OS の表示言語のまま続行する）。
            // ランチャーの stderr はユーザーのコンソールそのものなので普段は絶対に汚さないが、
            // ここは「環境変数を明示的に設定したときだけ」出る ── 黙って効かないより、
            // 効かなかったことが見える方がよい（実際にこの1行が上記の不具合を露出させた）。
            Console.Error.WriteLine($"Failed to apply {AppEnvironment.LanguageVariable}='{language}': {ex.Message}");
        }
    }
}
