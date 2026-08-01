using System.Reflection;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// ビルド時に埋め込んだリポジトリルート（<c>AssemblyMetadata 'RepositoryRoot'</c>）。
///
/// <para>
/// <b><see cref="AppContext.BaseDirectory"/> からの相対パスでは書かない</b>
/// ── 出力ディレクトリの深さ（構成・TFM・RID）が変わった瞬間に壊れ、
/// ローカルで緑のまま CI だけ赤になる。
/// </para>
/// </summary>
internal static class RepositoryLayout
{
    /// <summary>リポジトリルートの絶対パス。</summary>
    public static string Root { get; } = Resolve();

    private static string Resolve()
    {
        string root = typeof(RepositoryLayout).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryRoot")?.Value
            ?? throw new InvalidOperationException(
                "AssemblyMetadata 'RepositoryRoot' が埋め込まれていません（csproj を確認してください）。");

        return Path.GetFullPath(root);
    }
}
