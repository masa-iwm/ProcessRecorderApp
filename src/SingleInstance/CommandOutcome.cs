namespace ProcessRecorderApp.SingleInstance;

/// <summary>
/// アクティブ化コマンドを実行した結果、常駐ワーカーが何をすべきかを表す。
/// コマンドごとに「ウィンドウを表示するか」「トースト通知するか」を独立に指定できる。
/// </summary>
public readonly record struct CommandOutcome
{
    /// <summary>ウィンドウを前面表示するかどうか</summary>
    public bool ShowWindow { get; init; }

    /// <summary>トースト通知のタイトル（null の場合は通知しない）</summary>
    public string? ToastTitle { get; init; }

    /// <summary>トースト通知の本文</summary>
    public string? ToastMessage { get; init; }

    /// <summary>トースト通知を行うかどうか</summary>
    public bool ShowsToast => ToastTitle is not null && ToastMessage is not null;

    /// <summary>ランチャー経由で呼び出し元の標準出力へ出力する文字列（null の場合は出力しない）</summary>
    public string? ConsoleOutput { get; init; }

    /// <summary>ランチャー経由で呼び出し元の標準エラー出力へ出力する文字列（null の場合は出力しない）</summary>
    public string? ConsoleError { get; init; }

    /// <summary>
    /// コマンド処理の終了コード。0は成功を表す。
    /// この値は常駐ワーカーから処理を委譲したランチャープロセスへそのまま通知され、
    /// ランチャー自身のプロセス終了コードとして返される（<see cref="SingleInstanceManager"/> 参照）。
    /// 0以外を指定する場合は、ランチャー側が予約している終了コード
    /// （<see cref="SingleInstanceManager.ExitCode_WorkerStartFailed"/> = 1、
    /// <see cref="SingleInstanceManager.ExitCode_WorkerResultTimeout"/> = 2）と衝突しないよう、
    /// <see cref="DefaultFailureExitCode"/>（10）以上の値を使うこと。
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>コマンド処理が失敗した場合の既定の終了コード</summary>
    public const int DefaultFailureExitCode = 10;

    /// <summary>通常のActivate要求：ウィンドウを表示するだけで通知はしない</summary>
    public static CommandOutcome Activate() => new() { ShowWindow = true };

    /// <summary>ウィンドウを表示せず、通知も出さずに完結する（トースト通知しない引数の例）</summary>
    public static CommandOutcome Silent() => new() { ShowWindow = false };

    /// <summary>ウィンドウを表示せず、処理結果をトースト通知する（トースト通知する引数の例）</summary>
    public static CommandOutcome SilentWithToast(string title, string message) =>
        new() { ShowWindow = false, ToastTitle = title, ToastMessage = message };

    /// <summary>ウィンドウを表示したうえで、あわせてトースト通知もする（両方必要な場合の例）</summary>
    public static CommandOutcome ActivateWithToast(string title, string message) =>
        new() { ShowWindow = true, ToastTitle = title, ToastMessage = message };

    /// <summary>
    /// コマンド処理が失敗したことを表す。ウィンドウは表示しない。
    /// title/messageを指定した場合はその内容でトースト通知する（失敗内容の通知にも使える）。
    /// この終了コードは呼び出し元ランチャープロセスの終了コードにそのまま反映されるため、
    /// 呼び出し側はプロセス終了コードを見るだけで常駐ワーカー側の処理失敗を検知できる。
    /// </summary>
    public static CommandOutcome Failure(
        string? toastTitle = null, string? toastMessage = null, int exitCode = DefaultFailureExitCode) =>
        new() { ShowWindow = false, ToastTitle = toastTitle, ToastMessage = toastMessage, ExitCode = exitCode };
}
