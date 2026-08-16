using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// GStreamer のネイティブ一式が<b>どこから読まれたか</b>（<c>activity.log</c> の <c>gst.runtime</c>）。
///
/// <para>
/// 探索はすべて GstSharp.Net のローダーに委ねてある（アプリ側のロケーターは無い）。
/// 段は「元の <c>PATH</c> の走査 → <c>GSTREAMER_1_0_ROOT_*</c> → レジストリのアンインストール情報
/// → 公式インストーラの既定ディレクトリ → MSYS2 → 同梱の <c>runtimes\{RID}\bin</c>
/// → ベアネーム（OS のローダー任せ）」で、**正解は環境ごとに違う**
/// ── 開発機は MinGW インストール、CI は MSYS2、同梱リリースは <c>runtimes/</c>。
/// そのため<b>特定のディレクトリを焼き込まない</b>。焼き込むとどこかで必ず
/// 偽の赤になるか、逆に何も検証しない緑になる。
/// </para>
///
/// <para>
/// ここで固定するのは環境に依らない不変条件:
/// <list type="number">
///   <item>解決先が1つに決まっていること（<c>selected</c> が <c>(none)</c> でない）</item>
///   <item><b>選んだディレクトリから実際にロードされていること</b>
///     ── 「選んだつもり」と「Windows がロードしたもの」は一致するとは限らない。
///     ただしベアネームで解決した段（<c>dir=(search-path)</c>）だけは
///     固定ディレクトリが存在しないので、この照合は行わない</item>
///   <item><b>本体と GLib が同じ根から来ていること</b>（<c>mixed=False</c>）。
///     候補を全部 PATH に繋ぐ実装に戻すとここが落ちる。
///     混成は「プラグインが黙って blacklist される」形で出るので、
///     この表明が無いと原因に辿り着けない</item>
/// </list>
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class RuntimeResolutionTests(PublishedApp app)
{
    /// <summary>
    /// 製品側が <c>gst.runtime</c> に出す識別子（外から見た契約）。
    /// 正本はバインディングの <c>Gst.Interop.GstInstallOrigin</c>（列挙子の名前をそのまま出す）。
    /// L2 は製品プロジェクトもバインディングも参照しないので、ここにも書いてある
    /// ── 段を増減させたらここと GstSharp.Net 側の <c>NativeInstallPlannerTests</c> が対になる。
    /// </summary>
    private static readonly string[] KnownSources =
    [
        "ConfiguredSearchPath",
        "PathDirectory",
        "EnvironmentVariable",
        "Registry",
        "DefaultInstallDirectory",
        "Msys2",
        "BundledRuntime",
        "ProcessSearchPath",
    ];

    private static string Field(string detail, string name)
    {
        // 値にはディレクトリ（空白を含みうる）や `(not loaded)` が入るので、空白では切れない。
        // 「次のフィールド名の直前まで」で切る。行末の `source=` は人間向けの自由文
        // （空白も `=` も含みうる）なので、**この関数では読まない** ── 読むと
        // 次のフィールド名を探す規則が成り立たない。
        var match = Regex.Match(detail, $@"\b{Regex.Escape(name)}=(.*?)(?=\s+\w[\w:.]*=|$)");
        Assert.True(match.Success, $"gst.runtime に {name}= が無い: {detail}");
        return match.Groups[1].Value.Trim();
    }

    [Fact]
    public void TheResidentWorker_LogsWhichGStreamerItActuallyLoaded()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        Assert.Equal(0, instance.Run("ping").ExitCode);

        var log = instance.ReadActivityLog();
        var lines = ActivityLogFile.Events(log, "gst.runtime");
        Assert.True(lines.Count >= 1,
            $"gst.runtime が記録されていない。{instance.DiagnosticDump()}");

        string detail = ActivityLogFile.DetailOf(lines[0]);

        // 1. 解決先が決まっていること。ここが (none) なら、この実機には
        //    どの段にも本体が無い（＝録画できない）。
        string selected = Field(detail, "selected");
        Assert.NotEqual("(none)", selected);
        Assert.Contains(selected, KnownSources);

        // 2. 実際にロードされていること。ファイル名は MinGW 版（`libgstreamer-1.0-0.dll`）と
        //    MSVC 版（`gstreamer-1.0-0.dll`）で違い、同梱物もどちらでもありうるので、
        //    **名前は見ない**（ロードできた事実と場所だけを見る）。
        string core = Field(detail, "core");
        Assert.NotEqual("(not loaded)", core);

        //    選んだディレクトリから来ていること。ベアネームで解決した段だけは
        //    固定したディレクトリが無い（OS のローダーが決める）ので免除する。
        string directory = Field(detail, "dir");
        if (directory != "(search-path)")
        {
            Assert.Equal(
                Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(Path.GetDirectoryName(core)!).TrimEnd(Path.DirectorySeparatorChar),
                ignoreCase: true);
        }

        // 3. 本体と glib が同じ根から来ていること（混成していない）。
        Assert.Equal("False", Field(detail, "mixed"));

        // レコーダーが実際に初期化できていること（1〜3 が成立していても
        // プラグインが壊れていれば録画はできない ── 「読めた」で終わらせない）。
        Assert.NotEmpty(ActivityLogFile.Events(log, "recorder.init ok"));
        Assert.Empty(ActivityLogFile.Events(log, "app.error"));
    }
}
