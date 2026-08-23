using System;
using System.Collections.Generic;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// CLI の録画コマンドがレコーダー集合へ適用する<b>選択と整形の規則</b>（純粋関数）。
///
/// <para>
/// 規則の本体は WinUI アプリプロジェクト（<c>RecorderControlService</c> / <c>ActivationCommands</c>）
/// から呼ばれるが、あちらは L1 テストプロジェクトから参照できない。終了コード契約の中核
/// （どのレコーダーが対象になるか・出力の列の並び）が発行物 E2E でしか検証できない
/// 死角になっていたので、<c>RecordingStopRules</c> / <c>RecordingCommandState</c> と
/// 同じ形でここへ置き、L1 で固定する。
/// </para>
/// </summary>
public static class RecorderCliRules
{
    /// <summary>
    /// <c>start-recording &lt;target&gt;</c> / <c>stop-recording &lt;target&gt;</c> の対象解決。
    /// 見つからなければ -1。
    ///
    /// <para>
    /// <b>数値はインデックス（0 始まり）として解釈し、名前へはフォールバックしない。</b>
    /// 「0」という名前のレコーダーには名前では到達できない ── 両様に解釈すると、
    /// 同じ引数が構成しだいで別のレコーダーを指す。
    /// 名前は<b>序数での完全一致・先勝ち</b>（大文字小文字を区別する。
    /// 一意性は追加・改名時の <see cref="RecorderNaming.MakeUnique"/> が守る）。
    /// </para>
    /// </summary>
    public static int ResolveTargetIndex(IReadOnlyList<string> recorderNames, string target)
    {
        if (int.TryParse(target, out int index))
            return 0 <= index && index < recorderNames.Count ? index : -1;

        for (int i = 0; i < recorderNames.Count; i++)
        {
            if (string.Equals(recorderNames[i], target, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// <c>start-recording-all</c> / <c>stop-recording-all</c> の標準出力 1 行
    /// （<c>名前\tファイル名</c>）。バッチが TAB で切って読む機械可読な形。
    /// </summary>
    public static string FormatRecorderLine(string name, string? lastFilename)
        => $"{name}\t{lastFilename}";

    /// <summary>
    /// <c>status</c> の標準出力 1 行（TAB 区切りの 8 列:
    /// <c>名前 / 初期化済み / 録画中 / 復帰待ち / 直近のファイル / 常時録画 /
    /// 常時録画のファイル / 直近の障害</c>）。
    ///
    /// <para>
    /// <b>自由記述である「直近の障害」は必ず最後の列。</b> 途中に置くと、理由文に TAB が
    /// 混ざったときに後続の列の意味がずれる。改行と TAB は <see cref="FlattenToOneLine"/> で
    /// 空白へ潰し、1 レコーダー＝1 行を崩さない（<c>ActivityLog</c> と同じ規約）。
    /// 真偽値は <see cref="bool.ToString()"/>（<c>True</c>/<c>False</c>）でカルチャに依存しない。
    /// </para>
    /// </summary>
    public static string FormatStatusLine(
        string name, bool isInitialized, bool isRecording, bool isAwaitingRecoveryResume,
        string? lastFilename,
        string continuousState, string? continuousFilename, string? lastError)
        => string.Join('\t',
            name,
            isInitialized.ToString(),
            isRecording.ToString(),
            isAwaitingRecoveryResume.ToString(),
            lastFilename ?? string.Empty,
            continuousState,
            continuousFilename ?? string.Empty,
            FlattenToOneLine(lastError));

    /// <summary>常時録画が設定されていない。</summary>
    public const string ContinuousOff = "off";

    /// <summary>常時録画が回っていてセグメントを書いている。</summary>
    public const string ContinuousOn = "on";

    /// <summary>有効だが、まだ最初のフレームが来ていない（起動直後の窓）。</summary>
    public const string ContinuousPending = "pending";

    /// <summary>有効だが障害が出ている。詳細は activity.log の <c>continuous.*</c>。</summary>
    public const string ContinuousError = "error";

    /// <summary>
    /// <c>status</c> の常時録画の列（1 語）。
    ///
    /// <para>
    /// <b>1 語に畳むのは、自由記述の列を 2 つ持てないため。</b> 「直近の障害」は
    /// 必ず最後の列でなければならず、常時録画の障害文をもう 1 列足すとその規約が崩れる。
    /// 詳細は PropertyGrid の <c>ContinuousLastError</c> と <c>activity.log</c> にある。
    /// </para>
    /// <para>
    /// <b>この列は終了コードには効かない。</b> 常時録画の失敗がイベント録画の健全性を
    /// 汚さないのが隔離契約であり（<see cref="IsHealthy"/> は見ていない）、
    /// 既存のスクリプトの挙動も変えない。
    /// </para>
    /// </summary>
    public static string ContinuousState(bool enabled, bool running, string? lastError)
        => !enabled ? ContinuousOff
            : 0 < FlattenToOneLine(lastError).Length ? ContinuousError
            : running ? ContinuousOn
            : ContinuousPending;

    /// <summary>
    /// <c>status</c> の健全判定。不健全（初期化されていない、または直近の障害が残っている）
    /// なら終了コード 15 の根拠になる。
    /// <b>「直近の障害」は今も壊れているという意味ではない</b>
    /// ── <c>EventRecorder.LastError</c> は次の録画開始で消える。
    /// </summary>
    public static bool IsHealthy(bool isInitialized, string? lastError)
        => isInitialized && FlattenToOneLine(lastError).Length == 0;

    /// <summary>改行と TAB を空白へ潰し、1 レコーダー＝1 行・1 列＝1 値を崩さないようにする。</summary>
    public static string FlattenToOneLine(string? text)
        => text is null ? string.Empty : text.ReplaceLineEndings(" ").Replace('\t', ' ').Trim();
}
