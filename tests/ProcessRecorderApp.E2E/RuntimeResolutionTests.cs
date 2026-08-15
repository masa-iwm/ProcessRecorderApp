using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// GStreamer のネイティブ一式が<b>どこから読まれたか</b>（<c>activity.log</c> の <c>gst.runtime</c>）。
///
/// <para>
/// 本体は「元の PATH → <c>GSTREAMER_1_0_ROOT_MSVC_X86_64</c> → レジストリの
/// GStreamer(MSVC) → 同梱物（発行物の <c>gstreamer\win-x64\bin</c>）」の
/// いずれかから来る。**正解は環境ごとに違う** ── MSVC 版のインストールがあれば
/// それが勝ち、無ければ同梱物へ落ちる（MinGW 版は MSVC 命名の本体を持たないため
/// 候補にしない）。そのため<b>特定のディレクトリを焼き込まない</b>。
/// 焼き込むとどこかで必ず偽の赤になるか、逆に何も検証しない緑になる。
/// </para>
///
/// <para>
/// ここで固定するのは環境に依らない不変条件:
/// <list type="number">
///   <item>解決先が1つに決まっていること（<c>selected</c> が <c>(none)</c> でない）</item>
///   <item><b>選んだディレクトリから実際にロードされていること</b>
///     ── 「選んだつもり」と「Windows がロードしたもの」は一致するとは限らない</item>
///   <item><b><c>gstreamer-1.0-0.dll</c> と <c>glib-2.0-0.dll</c> が同じ根から来ていること</b>
///     （<c>mixed=False</c>）。候補を全部 PATH に繋ぐ実装に戻すとここが落ちる。
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
    /// L2 は製品プロジェクトを参照しないので、ここにも書いてある
    /// ── 片方だけ変えると <c>GStreamerRuntimeLocatorTests</c> かここが必ず落ちる。
    /// </summary>
    private static readonly string[] KnownSources =
    [
        "PATH",
        "env:GSTREAMER_1_0_ROOT_MSVC_X86_64",
        "registry:GStreamer-MSVC",
        "bundled",
    ];

    private static string Field(string detail, string name)
    {
        // 値にはディレクトリが入るので空白までを1つの値とする（パスに空白があると
        // 切れるが、この4項目はいずれもフィールド名で始まる次の項目が続くため、
        // 「次のフィールド名の直前まで」で切る方が安全）。
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
        //    どの候補にも本体が無い（＝録画できない）。
        string selected = Field(detail, "selected");
        Assert.NotEqual("(none)", selected);
        Assert.Contains(selected, KnownSources);

        // 2. 選んだディレクトリから実際にロードされていること。
        string directory = Field(detail, "dir");
        string core = Field(detail, "core");
        Assert.NotEqual("(not loaded)", core);
        Assert.Equal(
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Path.GetDirectoryName(core)!).TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: true);

        // 3. 本体と glib が同じ根から来ていること（混成していない）。
        Assert.Equal("False", Field(detail, "mixed"));

        // レコーダーが実際に初期化できていること（1〜3 が成立していても
        // プラグインが壊れていれば録画はできない ── 「読めた」で終わらせない）。
        Assert.NotEmpty(ActivityLogFile.Events(log, "recorder.init ok"));
        Assert.Empty(ActivityLogFile.Events(log, "app.error"));
    }
}
