using System.Reflection;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// L4（ローカライズ）とドキュメント整合の検証は、リポジトリ内のファイルを
/// <b>ファイルとして直接読む</b>。MRT ランタイムを起動しないので高速かつ安定し、
/// テストプロジェクトが参照していないプロジェクト（<c>ProcessRecorderApp</c> /
/// <c>Controls</c>）の中身も検査できる。
///
/// <para>
/// リポジトリルートの解決を <c>AppContext.BaseDirectory</c> からの相対パスで書くと、
/// 出力ディレクトリの深さ（構成・TFM・RID）が変わった瞬間に壊れる
/// ── ローカルで緑のまま CI だけ赤になる典型。そのため
/// <c>ProcessRecorderApp.Tests.csproj</c> がビルド時に絶対パスを
/// <see cref="AssemblyMetadataAttribute"/> として埋め込み、ここで読み出す。
/// リポジトリごと移動された場合に備えてセンチネル探索も持たせてある。
/// </para>
/// </summary>
internal static class RepositoryFiles
{
    /// <summary>リポジトリルートの絶対パス（末尾の区切りは含まない）。</summary>
    public static string Root { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        // 第一候補: csproj が埋め込んだ絶対パス（出力ディレクトリの深さに依存しない）
        string? embedded = typeof(RepositoryFiles).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryRoot")?.Value;
        if (IsRepositoryRoot(embedded))
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(embedded!));

        // 第二候補: 出力ディレクトリから上へセンチネルを探す（リポジトリごと移動された場合）
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (IsRepositoryRoot(dir.FullName))
                return Path.TrimEndingDirectorySeparator(dir.FullName);
        }

        throw new InvalidOperationException(
            "リポジトリルートを特定できなかった。"
            + "ProcessRecorderApp.Tests.csproj の AssemblyMetadata 'RepositoryRoot' を確認すること。");
    }

    private static bool IsRepositoryRoot(string? candidate)
        => !string.IsNullOrWhiteSpace(candidate)
           && File.Exists(Path.Combine(candidate, "src", "ProcessRecorderApp.slnx"));

    /// <summary>リポジトリルートからの相対パスを絶対パスにする。</summary>
    public static string At(params string[] segments)
        => Path.Combine([Root, .. segments]);

    /// <summary>表示用の相対パス（失敗メッセージにフルパスを出すと読みにくいため）。</summary>
    public static string Relative(string absolutePath)
        => Path.GetRelativePath(Root, absolutePath);

    /// <summary>
    /// <c>src/</c> 配下のソースファイル一覧。<c>obj/</c> と <c>bin/</c> は除外する
    /// ── XAML コンパイラ等が生成したコピーが含まれ、実ソースと二重に数えてしまうため。
    /// </summary>
    public static string[] SourceFiles(string searchPattern)
        => [.. Directory.EnumerateFiles(At("src"), searchPattern, SearchOption.AllDirectories)
                .Where(p => !IsBuildOutput(p))
                .OrderBy(p => p, StringComparer.Ordinal)];

    private static bool IsBuildOutput(string path)
        => Path.GetRelativePath(Root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                         || segment.Equals("bin", StringComparison.OrdinalIgnoreCase));
}
