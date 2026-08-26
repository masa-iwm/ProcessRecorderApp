using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <c>gst-launch-1.0</c> で<b>入力を作る</b>ための道具。製品を通さずに、答えの分かった
/// メディアファイルが要るときだけ使う（追いかけ再生の長いクリップ・尺の分かった fMP4）。
///
/// <para>
/// <b>特定のディレクトリを焼き込まない。</b> 開発機・CI・同梱で GStreamer の場所が違うので、
/// <c>activity.log</c> の <c>gst.runtime</c> が書いた「実際にロードした bin」から辿る。
/// <b>launcher が在ることは x264 が在ることを意味しない</b> ── 同梱ランタイムが解決に勝つ
/// 機械では GPL のプラグインだけが無いので、呼び出し側は
/// <see cref="HasX264Plugin"/> で確かめてスキップすること。
/// </para>
/// </summary>
public static class GstLaunchTool
{
    /// <summary>
    /// <c>activity.log</c> の <c>gst.runtime</c> が書いた <c>dir=</c> から
    /// <c>gst-launch-1.0.exe</c> を探す。ベアネームで解決した段
    /// （<c>dir=(search-path)</c>）には固定の場所が無いので null。
    /// </summary>
    public static string? FindLauncher(AppInstance instance)
    {
        foreach (string line in ActivityLogFile.Events(instance.ReadActivityLog(), "gst.runtime"))
        {
            // 値にはディレクトリ（空白を含みうる）が入るので、空白では切れない
            // ── 次のフィールド名の直前までで切る（RuntimeResolutionTests と同じ規則）。
            var match = Regex.Match(ActivityLogFile.DetailOf(line), @"\bdir=(.*?)(?=\s+\w[\w:.]*=|$)");
            if (!match.Success)
                continue;

            string directory = match.Groups[1].Value.Trim();
            if (directory == "(search-path)")
                continue;

            string launcher = Path.Combine(directory, "gst-launch-1.0.exe");
            if (File.Exists(launcher))
                return launcher;
        }

        return null;
    }

    /// <summary>launcher の隣のプラグイン ディレクトリ（<c>..\lib\gstreamer-1.0</c>）。</summary>
    public static string PluginDirectoryOf(string launcher)
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(launcher)!, "..", "lib", "gstreamer-1.0"));

    /// <summary>
    /// <c>x264enc</c> のプラグインが launcher の隣にあるか。
    /// 名前は形態で変わる（MinGW は <c>lib</c> 接頭辞つき、MSVC は無し）。
    /// </summary>
    public static bool HasX264Plugin(string launcher)
    {
        string plugins = PluginDirectoryOf(launcher);
        return File.Exists(Path.Combine(plugins, "libgstx264.dll"))
            || File.Exists(Path.Combine(plugins, "gstx264.dll"));
    }

    /// <summary>
    /// launcher を走らせ、終わるまで待つ。終了コードが 0 でなければ、あるいは
    /// <paramref name="budget"/> を超えたら落とす。返すのは stdout＋stderr。
    ///
    /// <para>
    /// <b>プラグインとレジストリは launcher の隣へ隔離する。</b> 開発機には複数の
    /// GStreamer が同居しうるので、名指ししないと別の実装が混ざる
    /// ── レジストリのキャッシュもシステム側のものを書き換えない。
    /// </para>
    /// </summary>
    public static async Task<string> RunAsync(
        string launcher,
        IEnumerable<string> arguments,
        AppInstance instance,
        string registryFileName,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(launcher)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(launcher)!,
        };

        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        string pluginDir = PluginDirectoryOf(launcher);
        if (Directory.Exists(pluginDir))
        {
            start.Environment["GST_PLUGIN_PATH"] = pluginDir;
            start.Environment["GST_PLUGIN_SYSTEM_PATH"] = pluginDir;
            start.Environment["GST_PLUGIN_PATH_1_0"] = pluginDir;
            start.Environment["GST_PLUGIN_SYSTEM_PATH_1_0"] = pluginDir;
        }
        start.Environment["GST_REGISTRY"] = Path.Combine(instance.DataDir, registryFileName);

        using var process = Process.Start(start)!;

        // 両方を同時に汲む（片方だけを待つと、もう片方のパイプが埋まって止まる）。
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var kill = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        kill.CancelAfter(budget);
        try
        {
            await process.WaitForExitAsync(kill.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"gst-launch-1.0 が {budget.TotalSeconds:F0} 秒で終わりませんでした。");
        }

        string tail = await stdout + Environment.NewLine + await stderr;
        Assert.True(process.ExitCode == 0,
            $"gst-launch-1.0 が {process.ExitCode} で終わりました:{Environment.NewLine}{tail}");
        return tail;
    }
}
