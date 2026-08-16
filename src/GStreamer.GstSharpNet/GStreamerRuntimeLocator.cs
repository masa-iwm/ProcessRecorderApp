using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// GStreamer の根の候補1件。
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
/// <b>優先順位（意図的な契約。<c>tools/Verify-GpuEncoders.ps1</c> が同じ順序を模倣する）</b>:
/// <list type="number">
///   <item>元の <c>PATH</c> に既に入っているディレクトリ</item>
///   <item><c>%GSTREAMER_1_0_ROOT_MINGW_X86_64%\bin</c>（環境変数がある場合）</item>
///   <item>レジストリから見つけた GStreamer(MinGW) のインストール先の <c>bin</c></item>
///   <item>MSYS2 の <c>ucrt64\bin</c></item>
///   <item>同梱物 <c>&lt;exe&gt;\runtimes\&lt;RID&gt;\bin</c></item>
/// </list>
/// 3 が要るのは実測による ── GStreamer(MinGW) を**ユーザー単位で**
/// インストールすると、インストーラは <c>GSTREAMER_1_0_ROOT_MINGW_X86_64</c> も
/// <c>PATH</c> も設定しない（実測: 1.28.6 を
/// <c>%LOCALAPPDATA%\Programs\gstreamer\1.0\mingw_x86_64</c> へ導入した状態で、
/// <c>HKLM\...\Session Manager\Environment</c> と <c>HKCU\Environment</c> の
/// どちらにも存在しなかった）。2 だけに頼ると「入れたのに見つからない」になる。
/// </para>
///
/// <para>
/// <b>候補は1件だけを選ぶ（全部渡さない）。</b> 依存 DLL（<c>libglib-2.0-0.dll</c> 等）が
/// 別の根から解決されると「gstreamer は同梱物・glib は MSYS2」のような<b>混成</b>が
/// 起こりうる。症状はプラグインが黙って blacklist されることで、原因が見えない。
/// GitHub ランナーには <b>GStreamer 抜きの MSYS2 が <c>C:\msys64</c> にプリインストール</b>
/// されているので、これは机上の心配ではない。
/// そこで<b>最初に <see cref="CoreLibraryFileName"/> を持っていた候補だけ</b>を選ぶ
/// （この選定契約は従来のまま）。選んだ根は <c>GstSharpOptions.NativeSearchPath</c> として
/// GstSharp.Net へ渡り、バインディングが各モジュールを<b>その根から絶対パスで</b>ロードし、
/// プラグインの依存解決のために <c>bin</c> を自分で PATH の先頭へ足す。
/// 最初にロードした根への固定（ピン）もバインディング側にあるので、
/// アプリはもう PATH を組み立てない。
/// </para>
///
/// <para>
/// <b>公式 MSVC 版の GStreamer はこの候補探索からは選ばれない。</b>
/// 候補の確認に使うのは <c>libgstreamer-1.0-0.dll</c>（MinGW 命名）で、
/// MSVC 版は <c>gstreamer-1.0-0.dll</c> と名前が違う。ただしここで何も選べなかった
/// 場合（<c>NativeSearchPath</c> が null）、GstSharp.Net 自身のプローブ
/// （レジストリ／既定ディレクトリ）は MSVC 版も見つけられる。
/// どちらが勝ったかは <c>gst.runtime</c> のログ（<c>loaderFlavor</c>）で切り分ける。
/// </para>
/// </summary>
public static class GStreamerRuntimeLocator
{
    /// <summary>この経路が探している GStreamer 本体（MinGW 命名）。</summary>
    public const string CoreLibraryFileName = "libgstreamer-1.0-0.dll";

    /// <summary>混成の検出に使う依存 DLL。本体と同じディレクトリから来ていなければ混成。</summary>
    public const string GLibLibraryFileName = "libglib-2.0-0.dll";

    // MSVC 命名（lib 接頭辞なし）。DescribeRuntime のロード済み判定にだけ使う
    // ── バインディング自身のプローブは MSVC 版も選びうるので、診断はどちらの命名でも
    // 本体を見つけられる必要がある。候補の探索（選定契約）と同梱の種は MinGW のままなので、
    // 意図的に *LibraryFileName の命名から外してある（RuntimeClosureSeedSyncTests が
    // その接尾辞で種を収集する）。
    private const string MsvcCoreFileName = "gstreamer-1.0-0.dll";
    private const string MsvcGLibFileName = "glib-2.0-0.dll";

    /// <summary>GStreamer(MinGW) の公式インストーラが設定する環境変数。</summary>
    public const string MinGwRootVariable = "GSTREAMER_1_0_ROOT_MINGW_X86_64";

    // --- Source の識別子。ログとテストが照合するのでリテラルを変えないこと ---
    public const string SourcePath = "PATH";
    public const string SourceEnvironment = "env:" + MinGwRootVariable;
    public const string SourceRegistry = "registry:GStreamer-MinGW";
    public const string SourceMsys2 = "msys2:ucrt64";
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
        string? mingwRoot,
        string? installedGStreamerRoot,
        string? msys2Root,
        string? appDirectory,
        string runtimeIdentifier)
    {
        var candidates = new List<GStreamerRuntimeCandidate>(4);

        Add(SourceEnvironment, mingwRoot, "bin");
        Add(SourceRegistry, installedGStreamerRoot, "bin");
        Add(SourceMsys2, msys2Root, "ucrt64", "bin");
        Add(SourceBundled, appDirectory, "runtimes", runtimeIdentifier, "bin");

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
    /// 実在する候補だけを優先順に集める。レジストリ読みは失敗しても無視する
    /// （GstSharp.Net の初期化より前に走るので、ここで例外を漏らすと起動が丸ごと死ぬ）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<GStreamerRuntimeCandidate> Discover(
        string? appDirectory, string runtimeIdentifier)
    {
        string? mingwRoot = TryGet(() => Environment.GetEnvironmentVariable(MinGwRootVariable));
        string? installedRoot = TryGet(FindInstalledGStreamerRoot);
        string? msys2Root = TryGet(FindMsys2Root);

        return BuildCandidates(mingwRoot, installedRoot, msys2Root, appDirectory, runtimeIdentifier)
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
    /// <b>MSVC 版は拾わない</b>（DLL 名が違うので拾っても使えず、混乱を増やすだけ）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? FindInstalledGStreamerRoot()
        => FindInstallLocation(name =>
            name.StartsWith("GStreamer", StringComparison.OrdinalIgnoreCase)
            && name.Contains("MinGW", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// MSYS2 のインストール先を探す。レジストリ → 既定の場所の順。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? FindMsys2Root()
    {
        if (FindInstallLocation(name => name.StartsWith("MSYS2", StringComparison.OrdinalIgnoreCase))
            is { } fromRegistry && Directory.Exists(fromRegistry))
        {
            return fromRegistry;
        }

        string?[] defaults =
        [
            Combine(Environment.GetEnvironmentVariable("SystemDrive"), "msys64"),
            Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "msys64"),
        ];

        return defaults.FirstOrDefault(d => d is not null && Directory.Exists(d));

        static string? Combine(string? root, params string[] parts)
            => string.IsNullOrWhiteSpace(root) ? null : Path.Combine([root, .. parts]);
    }

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
    /// 防ごうとしている混成そのものを見逃す。<b>GstSharp.Net の初期化の後に呼び</b>、
    /// 実際にロードされた本体と glib のパスを見て <c>mixed</c> を判定すること。
    /// バインディングのローダーが最終的に選んだ根と系統（MinGW / MSVC）も
    /// <c>loaderDir</c> / <c>loaderFlavor</c> として末尾に出す
    /// （末尾に足すのは、<c>tools/Verify-GpuEncoders.ps1</c> が <c>dir=</c> を
    /// 位置で読むため）。
    /// </para>
    /// </summary>
    public static string DescribeRuntime(
        IReadOnlyList<GStreamerRuntimeCandidate> candidates,
        GStreamerRuntimeCandidate? chosen)
    {
        // ローダーは MSVC 版を選ぶこともあるので、両方の命名で探す（見つかった方を採る）。
        string? corePath = FindLoadedModulePath(CoreLibraryFileName)
            ?? FindLoadedModulePath(MsvcCoreFileName);
        string? glibPath = FindLoadedModulePath(GLibLibraryFileName)
            ?? FindLoadedModulePath(MsvcGLibFileName);

        // 同じディレクトリから来ていれば混成ではない。どちらかが取れないときは判定しない。
        string mixed = corePath is null || glibPath is null
            ? "unknown"
            : (string.Equals(Path.GetDirectoryName(corePath), Path.GetDirectoryName(glibPath),
                             StringComparison.OrdinalIgnoreCase) ? "False" : "True");

        string candidateList = candidates.Count == 0
            ? "(none)"
            : string.Join(", ", candidates.Select(c => $"{c.Source}={c.BinDirectory}"));

        // loaderDir が null なのは「まだ何もロードしていない」か「プロセスの探索パスで
        // 見つかった（ピンにディレクトリが無い）」とき。
        return $"selected={chosen?.Source ?? "(none)"} dir={chosen?.BinDirectory ?? "(none)"}"
             + $" core={corePath ?? "(not loaded)"} glib={glibPath ?? "(not loaded)"}"
             + $" mixed={mixed} candidates=[{candidateList}]"
             + $" loaderDir={Gst.Interop.NativeLoader.ResolvedDirectory ?? "(search-path)"}"
             + $" loaderFlavor={Gst.Interop.NativeLoader.ResolvedFlavor?.ToString() ?? "(none)"}";
    }
}
