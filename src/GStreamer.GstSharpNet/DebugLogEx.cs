using Gst;
using GObject = Gst.GObject; // 既存の GObject.Object 修飾名をそのまま生かす
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace ProcessRecorderApp.GStreamer;

public static class DebugLogEx
{
    // カテゴリの生成とログ出力は GstSharp.Net の DebugCategory.New / Log を使う
    // （preview2 で入ったので、生の P/Invoke 対は不要になった）。
    // カスタムカテゴリは初回呼び出しで作って使い回す（GStreamer 側が同名カテゴリを
    // 重複させないので、競合して 2 回作っても実害なし）。
    private static DebugCategory? _debugCategory;
    public static void Log(DebugLevel level, string? message,
        GObject.Object? @object = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0,
        [CallerMemberName] string function = "")
    {
        // 自クラスの不変条件（IsGstInitialized を見ずにネイティブへ触らない）をここでも守る。
        // catch 節から呼ばれやすい API なので、初期化前の呼び出しで診断のためのログが
        // ローダーに勝手な根をピンさせて Controller.StaticInitialize を巻き添えにする形に
        // しない（TrySetThreshold と同じ扱い。カテゴリ生成自体もネイティブ呼び出し）。
        if (!IsGstInitialized)
            return;

        _debugCategory ??= DebugCategory.New("myapp", 0, "My application");
        _debugCategory.Log(level, message ?? "", @object, file, function, line);
    }

    /// <summary>
    /// GstSharp.Net の初期化（<c>Initialize</c> ＝ ネイティブのロードと <c>gst_init</c>）が
    /// 済んだか。<see cref="Controller.StaticInitialize"/> だけが立てる。
    ///
    /// <para>
    /// <b>これを見ずに GStreamer のネイティブ（<c>Gst.*</c> 全般）を呼んではいけない。</b>
    /// <c>AppSettings</c> は初期化より前に読み込まれ
    /// （<c>Program.cs</c> で <c>AppSettings.Default</c> → <c>Controller.StaticInitialize()</c> の順）、
    /// その逆シリアル化の setter からここへ来る。初期化前にバインディングへ触ると
    /// 例外にはならず、**ローダーがその場でネイティブを解決してピンしてしまう**
    /// ── <c>gst_init</c> より前に GStreamer を呼ぶことになり、
    /// <c>gst.runtime</c> に残る解決結果も本来の初期化のものではなくなる。
    /// 「もう DllNotFoundException にならないから」とこのゲートを外してはいけない。
    /// </para>
    /// </summary>
    internal static volatile bool IsGstInitialized;

    /// <summary>
    /// <c>GST_DEBUG</c> 相当のしきい値を<b>今すぐ</b>適用する（<c>AppSettings.GstDebug</c> のミラー）。
    /// 適用したら true、まだ初期化前で何もしなかったら false。
    ///
    /// <para>
    /// <b>起動時の反映はここではなく環境変数が担当する</b>
    /// （<c>AppSettings.ApplyStartupEnvironmentVariables</c>）。そちらは外部で
    /// <c>GST_DEBUG</c> が設定済みなら上書きしないので、シェルや
    /// <c>launchSettings.json</c> の指定が勝つ。この非対称は意図であって、
    /// 初期化後にここから再適用して揃えたりしない。
    /// </para>
    /// </summary>
    public static bool TrySetThreshold(string? value)
    {
        if (!IsGstInitialized)
            return false;

        // 既定でデバッグが有効かどうかに寄りかからず、明示的に有効化する。
        Gst.Global.DebugSetActive(true);

        // reset: true は「既定へ戻してから list を適用する」。空文字は
        // parse_debug_list が何もしないので、**空欄＝既定へ戻す**が自然に成り立つ。
        Gst.Global.DebugSetThresholdFromString(value ?? "", true);

        Components.ActivityLog.Info("gst.debug", $"threshold='{value}'");
        return true;
    }

    /// <summary>
    /// パイプラインのグラフを <c>.dot</c> として書き出し、書いた絶対パスを返す。
    ///
    /// <para>
    /// <b><c>gst_debug_bin_to_dot_file</c> 系は使わない。</b> あちらは <c>gst_init</c> の時点で
    /// 控えた <c>priv_gst_dump_dot_dir</c> しか見ず（<c>gst.c</c> の <c>init_pre</c> が
    /// <c>g_getenv</c> を1回読むだけで、以降は読み直さない）、しかも未設定なら
    /// <b>無言で何も書かずに返る</b>。起動後に <c>GST_DEBUG_DUMP_DOT_DIR</c> を
    /// 書き換えても効かないので、保存先をアプリが持つ経路にしてある。
    /// </para>
    /// </summary>
    public static string WriteDotFile(Bin bin, string directory, string name, System.DateTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(bin);

        string dot = Gst.Global.DebugBinToDotData(bin, DebugGraphDetails.All);
        string path = Path.Combine(directory, BuildDotFileName(timestamp, name));
        Directory.CreateDirectory(directory);

        // **BOM を付けない**（引数なしの WriteAllText は BOM 無し UTF-8）。
        // Graphviz は先頭の BOM を解釈できず、`dot` が構文エラーで止まる。
        File.WriteAllText(path, dot);
        return path;
    }

    /// <summary>
    /// <c>.dot</c> のファイル名を組み立てる。<b>純粋関数</b>なので単体テストから直接検証できる。
    /// 先頭を時刻にするのは、同じパイプラインを何度も保存したときに並び順が経過順になるため。
    /// </summary>
    public static string BuildDotFileName(System.DateTime timestamp, string name)
    {
        string safe = string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');

        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{timestamp:yyyyMMdd-HHmmss.fff}.{safe}.dot");
    }
}
