using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// PATH に載せる候補1件。
/// </summary>
/// <param name="Source">
/// どこから来た候補か。<b>activity.log とテストの照合に使う識別子</b>なので、
/// 表示用に言い換えたり訳したりしないこと。
/// </param>
/// <param name="BinDirectory">GStreamer 本体 DLL があるはずのディレクトリ。</param>
public sealed record GStreamerRuntimeCandidate(string Source, string BinDirectory);

/// <summary>
/// GStreamer のネイティブ一式をどこから読むかを決める。
///
/// <para>
/// <b>優先順位（意図的な契約）</b>:
/// <list type="number">
///   <item>元の <c>PATH</c> に既に入っているディレクトリ</item>
///   <item><c>%GSTREAMER_1_0_ROOT_MSVC_X86_64%\bin</c>（環境変数がある場合）</item>
///   <item>レジストリから見つけた GStreamer(MinGW) のインストール先の <c>bin</c></item>
///   <item>同梱物 <c>&lt;exe&gt;\gstreamer\&lt;RID&gt;\bin</c>
///     （GstSharpBundle.Windows.X64 の contentFiles が出力ディレクトリへ複製する）</item>
/// </list>
/// 3 の MinGW インストールは MSVC 命名の <see cref="CoreLibraryFileName"/> を持たないため
/// <see cref="Select"/> では選ばれない。候補として残すのは、<c>gst.runtime</c> のログに
/// 「MinGW 版しか無い環境で同梱物へ落ちた」ことを痕跡として出すため。
/// </para>
///
/// <para>
/// <b>候補を全部 PATH に繋いではいけない。</b> 依存 DLL（<c>glib-2.0-0.dll</c> 等）は
/// 「読み込み元 DLL のあるディレクトリ」ではなく <b>PATH の順</b>で解決されるため、
/// 繋ぐと「gstreamer は同梱物・glib は別インストール」のような<b>混成</b>が起こりうる。
/// 症状はプラグインが黙って blacklist されることで、原因が見えない。
/// そこで<b>最初に <see cref="CoreLibraryFileName"/> を持っていた候補だけ</b>を選び、
/// それを PATH の<b>先頭</b>へ置く。優先順位は上のとおりのまま保たれ、
/// かつ選んだ根の <c>bin</c> が最優先になるので依存 DLL も同じ根から取れる。
/// </para>
///
/// <para>
/// <b>MinGW 命名の GStreamer はこの経路からは見えない。</b>
/// <see cref="ImportResolver"/> と GstSharpBundle の DllImport が要求するのは
/// <c>gstreamer-1.0-0.dll</c>（MSVC 命名）で、MinGW 版は <c>libgstreamer-1.0-0.dll</c> と
/// 名前が違う。切り分けは <c>gst.runtime</c> のログで行う。
/// </para>
/// </summary>
public static class GStreamerRuntimeLocator
{
    /// <summary>この経路が探している GStreamer 本体（MSVC 命名）。</summary>
    public const string CoreLibraryFileName = "gstreamer-1.0-0.dll";

    /// <summary>混成の検出に使う依存 DLL。本体と同じディレクトリから来ていなければ混成。</summary>
    public const string GLibLibraryFileName = "glib-2.0-0.dll";

    /// <summary>GStreamer(MSVC) の公式インストーラが設定する環境変数。</summary>
    public const string MsvcRootVariable = "GSTREAMER_1_0_ROOT_MSVC_X86_64";

    // --- Source の識別子。ログとテストが照合するのでリテラルを変えないこと ---
    public const string SourcePath = "PATH";
    public const string SourceEnvironment = "env:" + MsvcRootVariable;
    public const string SourceRegistry = "registry:GStreamer-MinGW";
    public const string SourceBundled = "bundled";

    /// <summary>
    /// <c>PATH</c> を候補ディレクトリの列へ分解する。空要素と引用符を落とす。
    /// </summary>
    public static IReadOnlyList<string> SplitPath(string? pathVariable)
    {
        if (string.IsNullOrEmpty(pathVariable))
            return [];

        return pathVariable
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim('"'))
            .Where(entry => entry.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// <c>PATH</c> 以外の候補を優先順に組み立てる（純粋関数。実在確認はしない）。
    /// 根が未指定（null / 空白）のものは飛ばす。
    /// </summary>
    public static IReadOnlyList<GStreamerRuntimeCandidate> BuildCandidates(
        string? msvcRoot,
        string? installedGStreamerRoot,
        string? appDirectory,
        string runtimeIdentifier)
    {
        var candidates = new List<GStreamerRuntimeCandidate>(3);

        Add(SourceEnvironment, msvcRoot, "bin");
        Add(SourceRegistry, installedGStreamerRoot, "bin");
        Add(SourceBundled, appDirectory, "gstreamer", runtimeIdentifier, "bin");

        return candidates;

        void Add(string source, string? root, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;
            candidates.Add(new GStreamerRuntimeCandidate(
                source, Path.Combine([root.Trim().TrimEnd('\\', '/'), .. parts])));
        }
    }

    /// <summary>
    /// 実際に使う1件を決める（純粋関数）。<c>PATH</c> にあるディレクトリを先に見て、
    /// 次に <paramref name="candidates"/> を順に見る。どれも本体を持っていなければ null。
    /// </summary>
    public static GStreamerRuntimeCandidate? Select(
        IReadOnlyList<string> pathDirectories,
        IReadOnlyList<GStreamerRuntimeCandidate> candidates,
        Func<string, bool> hasCoreLibrary)
    {
        foreach (string directory in pathDirectories)
        {
            if (hasCoreLibrary(directory))
                return new GStreamerRuntimeCandidate(SourcePath, directory);
        }

        foreach (var candidate in candidates)
        {
            if (hasCoreLibrary(candidate.BinDirectory))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// 新しい <c>PATH</c> を組み立てる（純粋関数）。
    ///
    /// <para>
    /// 選べたときは<b>その1件だけを先頭へ</b>置く（依存 DLL を同じ根から取るため）。
    /// 選べなかったときは候補を末尾に全部繋ぐ ── 元の PATH を壊さず、
    /// 失敗の出方を「本体が見つからない」のまま保つ。
    /// </para>
    /// </summary>
    public static string ComposePath(
        string? originalPath,
        IReadOnlyList<GStreamerRuntimeCandidate> candidates,
        GStreamerRuntimeCandidate? chosen)
    {
        string original = originalPath ?? string.Empty;

        if (chosen is not null)
            return string.IsNullOrEmpty(original)
                ? chosen.BinDirectory
                : chosen.BinDirectory + ";" + original;

        var tail = candidates.Select(c => c.BinDirectory).ToArray();
        if (tail.Length == 0)
            return original;

        return string.IsNullOrEmpty(original)
            ? string.Join(';', tail)
            : original + ";" + string.Join(';', tail);
    }

    /// <summary>
    /// 実在する候補だけを優先順に集める。レジストリ読みは失敗しても無視する
    /// （<c>Gst.Application.Init</c> より前に走るので、ここで例外を漏らすと起動が丸ごと死ぬ）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<GStreamerRuntimeCandidate> Discover(
        string? appDirectory, string runtimeIdentifier)
    {
        string? msvcRoot = TryGet(() => Environment.GetEnvironmentVariable(MsvcRootVariable));
        string? installedRoot = TryGet(FindInstalledGStreamerRoot);

        return BuildCandidates(msvcRoot, installedRoot, appDirectory, runtimeIdentifier)
            .Where(c => Directory.Exists(c.BinDirectory))
            .ToArray();

        static string? TryGet(Func<string?> get)
        {
            try
            {
                return get();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// レジストリのアンインストール情報から GStreamer(MinGW) のインストール先を探す。
    /// MinGW インストールは MSVC 命名のセンチネルを満たさないため選ばれることは無いが、
    /// 候補として <c>gst.runtime</c> のログに痕跡を残す（クラスの doc コメント参照）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? FindInstalledGStreamerRoot()
        => FindInstallLocation(name =>
            name.StartsWith("GStreamer", StringComparison.OrdinalIgnoreCase)
            && name.Contains("MinGW", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] UninstallKeyPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    [SupportedOSPlatform("windows")]
    private static string? FindInstallLocation(Func<string, bool> displayNameMatches)
    {
        foreach (var hive in (RegistryKey[])[Registry.CurrentUser, Registry.LocalMachine])
        {
            foreach (string keyPath in UninstallKeyPaths)
            {
                using var key = hive.OpenSubKey(keyPath);
                if (key is null)
                    continue;

                foreach (string subKeyName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey?.GetValue("DisplayName") is not string displayName
                        || !displayNameMatches(displayName))
                    {
                        continue;
                    }

                    if (subKey.GetValue("InstallLocation") is string location
                        && !string.IsNullOrWhiteSpace(location))
                    {
                        return location.Trim().TrimEnd('\\', '/');
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 既にこのプロセスへロード済みのモジュールの実パスを返す（未ロードなら null）。
    /// </summary>
    public static string? FindLoadedModulePath(string moduleFileName)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(module.ModuleName, moduleFileName, StringComparison.OrdinalIgnoreCase))
                    return module.FileName;
            }
        }
        catch
        {
            // 診断のための情報であって、取れなくても録画には影響しない。
        }

        return null;
    }

    /// <summary>
    /// <c>activity.log</c> の <c>gst.runtime</c> に出す1行を組み立てる。
    ///
    /// <para>
    /// <b>「自分が選んだ候補」だけを出しても意味が無い。</b> それは自前の計算の写経であって、
    /// 防ごうとしている混成そのものを見逃す。<b><c>Gst.Application.Init</c> の後に呼び</b>、
    /// 実際にロードされた本体と glib のパスを見て <c>mixed</c> を判定すること。
    /// </para>
    /// </summary>
    public static string DescribeRuntime(
        IReadOnlyList<GStreamerRuntimeCandidate> candidates,
        GStreamerRuntimeCandidate? chosen)
    {
        string? corePath = FindLoadedModulePath(CoreLibraryFileName);
        string? glibPath = FindLoadedModulePath(GLibLibraryFileName);

        // 同じディレクトリから来ていれば混成ではない。どちらかが取れないときは判定しない。
        string mixed = corePath is null || glibPath is null
            ? "unknown"
            : (string.Equals(Path.GetDirectoryName(corePath), Path.GetDirectoryName(glibPath),
                             StringComparison.OrdinalIgnoreCase) ? "False" : "True");

        string candidateList = candidates.Count == 0
            ? "(none)"
            : string.Join(", ", candidates.Select(c => $"{c.Source}={c.BinDirectory}"));

        return $"selected={chosen?.Source ?? "(none)"} dir={chosen?.BinDirectory ?? "(none)"}"
             + $" core={corePath ?? "(not loaded)"} glib={glibPath ?? "(not loaded)"}"
             + $" mixed={mixed} candidates=[{candidateList}]";
    }
}
