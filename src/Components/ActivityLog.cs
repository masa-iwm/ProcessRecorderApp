using System;
using System.Globalization;
using System.IO;

namespace ProcessRecorderApp.Components;

/// <summary>
/// ユーザー向けの活動ログ（<c>activity.log</c>）。
///
/// 「いつ録れて、いつ失敗したか」がプロセス終了後も残ることを目的とする。
/// 既存の <c>DebugLogEx.Log</c> は <c>gst_debug_log</c> 経由で、<c>GST_DEBUG</c> が未設定だと
/// 何も出力しない ── つまり既定の起動ではユーザーから完全に見えない。
/// GStreamer 相関トレースは引き続きそちらへ、ユーザー向けの事実はすべてここへ出す。
///
/// <para>
/// <b>書き手はプロセス1つだけ</b>。常駐ワーカーのみが書き込み、ランチャープロセスは書かない。
/// <see cref="File.AppendAllText(string, string?)"/> は <c>FileShare.Read</c> で開くため、
/// 2プロセスが同時に追記すると片方が <see cref="IOException"/> で失敗する。
/// このクラスは例外を絶対に投げない設計なので、その失敗は**黙って行が消える**形で現れる
/// ── まさにこのアプリが繰り返し踏んできた種類の不具合になる。
/// そのため CLI コマンドの記録（<c>cli</c>）も、ランチャーではなく常駐ワーカー側
/// （<c>ActivationCommands.Parse</c> の末尾）で行う。
/// </para>
///
/// <para>
/// 出力書式は <c>yyyy-MM-dd HH:mm:ss.fff LEVEL event detail</c> の固定形式・インバリアント。
/// L2 E2E が正規表現で読むため、ローカライズはしない（＝ resw キーを増やさない）。
/// </para>
/// </summary>
public static class ActivityLog
{
    /// <summary>ログファイル名。</summary>
    public const string FileName = "activity.log";

    /// <summary>ローテーション先のファイル名（世代は1つだけ保持する）。</summary>
    public const string RotatedFileName = FileName + ".1";

    /// <summary>ローテーションの既定閾値（1MB）。</summary>
    public const long DefaultRotationThresholdBytes = 1024 * 1024;

    private static readonly object _gate = new();

    /// <summary>
    /// ローテーション閾値。テストから小さな値に差し替えて、実際にローテーションが
    /// 起きることを検証するために可変にしてある（通常は <see cref="DefaultRotationThresholdBytes"/>）。
    /// </summary>
    internal static long RotationThresholdBytes { get; set; } = DefaultRotationThresholdBytes;

    private static string? _path;
    private static bool _mirrorToConsole;

    /// <summary>
    /// 出力先ディレクトリと標準エラーへの複写を設定する。
    ///
    /// <paramref name="mirrorToConsole"/> は <c>Console.Error</c> へ複写して
    /// アプリ内 Log 画面（<c>StandardStreamRedirector</c>）にも出すためのもの。
    /// <b>ランチャープロセスでは必ず false</b> ── ランチャーの標準エラーはユーザーの
    /// コンソールそのもので、CLI 出力を汚染する。
    /// </summary>
    public static void Initialize(string directory, bool mirrorToConsole)
    {
        lock (_gate)
        {
            _path = Path.Combine(directory, FileName);
            _mirrorToConsole = mirrorToConsole;
        }
    }

    /// <summary>通常の事実を記録する。</summary>
    public static void Info(string eventName, string? detail = null) => Write("INFO", eventName, detail);

    /// <summary>異常ではないが注意を要する事実を記録する。</summary>
    public static void Warn(string eventName, string? detail = null) => Write("WARN", eventName, detail);

    /// <summary>失敗を記録する。</summary>
    public static void Error(string eventName, string? detail = null) => Write("ERROR", eventName, detail);

    /// <summary>
    /// 1行の書式（テストのために純粋関数として分離）。改行は含まない。
    /// 詳細の中の改行はタブへ潰す ── 1イベント＝1行を崩さないため
    /// （例外の <c>ToString()</c> をそのまま渡しても行指向の読み取りが壊れない）。
    /// </summary>
    internal static string FormatLine(DateTimeOffset timestamp, string level, string eventName, string? detail)
    {
        string head = string.Create(CultureInfo.InvariantCulture,
            $"{timestamp:yyyy-MM-dd HH:mm:ss.fff} {level} {eventName}");
        if (string.IsNullOrEmpty(detail))
            return head;
        return head + " " + detail.ReplaceLineEndings("\t");
    }

    private static void Write(string level, string eventName, string? detail)
    {
        string line = FormatLine(DateTimeOffset.Now, level, eventName, detail);
        try
        {
            lock (_gate)
            {
                // Initialize を呼び忘れても捨てない（既定の保存先へ、複写なしで出す）。
                // 呼び忘れが「ログが1行も残らない」形で現れるのは、このクラスの目的に反する。
                string path = _path ??= AppEnvironment.GetDataFilePath(FileName);

                if (_mirrorToConsole)
                    Console.Error.WriteLine(line);

                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                RotateIfNeeded(path, line.Length + Environment.NewLine.Length);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // ログの失敗でアプリを止めない（ディスク満杯・権限なし・排他違反）。
        }
    }

    /// <summary>
    /// 閾値を超える場合に <see cref="RotatedFileName"/> へ退避する。世代は1つだけ。
    ///
    /// <para>
    /// <b>失敗しても呼び出し側へ投げない。</b> 投げると <see cref="Write"/> の catch が
    /// その行を捨てるが、ファイルは閾値を超えたままなので<b>次の行も、その次の行も
    /// 同じところで落ちる</b> ── 1回の失敗（誰かが <c>activity.log.1</c> を開いている等）で
    /// <b>ログが恒久的に止まる</b>。ローテーションだけ諦めて追記は続ける方が、
    /// 「いつ録れて、いつ失敗したかが残る」というこのクラスの目的に合う。
    /// 肥大化は次の書き込みで再試行される。
    /// </para>
    /// </summary>
    private static void RotateIfNeeded(string path, int incomingBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length + incomingBytes <= RotationThresholdBytes)
                return;

            string rotated = Path.Combine(Path.GetDirectoryName(path) ?? ".", RotatedFileName);
            File.Delete(rotated);   // 存在しなくても例外にならない
            File.Move(path, rotated);
        }
        catch
        {
        }
    }
}
