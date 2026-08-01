using System.Xml.Linq;

namespace ProcessRecorderApp.Tests;

/// <summary>1ロケール分の <c>.resw</c> の中身。</summary>
/// <param name="Locale">ロケール名（<c>en-US</c> / <c>ja-JP</c>）。</param>
/// <param name="Path">ファイルの絶対パス。</param>
/// <param name="Entries">キー → 値。</param>
/// <param name="DuplicateKeys">同じ <c>name</c> が複数回現れたキー（後勝ちで黙って消えるため別途検査する）。</param>
internal sealed record LocalizedResources(
    string Locale,
    string Path,
    IReadOnlyDictionary<string, string> Entries,
    string[] DuplicateKeys)
{
    public string DisplayPath => RepositoryFiles.Relative(Path);
}

/// <summary>
/// 1つの <c>.resw</c> を en-US / ja-JP の対で扱う単位。
/// </summary>
/// <param name="Name">表示名（失敗メッセージ用）。</param>
/// <param name="KeyPrefix">
/// <c>Localization.GetString</c> に渡す完全修飾キーの接頭辞
/// （アプリ本体は <c>Resources/</c>、Controls は <c>Controls/ControlsResources/</c>）。
/// </param>
internal sealed record ResourceSet(string Name, string KeyPrefix, LocalizedResources EnUs, LocalizedResources JaJp)
{
    public LocalizedResources[] Locales => [EnUs, JaJp];

    /// <summary>どちらか一方のロケールにでも存在するキー（存在検査は両ロケールで別途行う）。</summary>
    public IEnumerable<string> AnyLocaleKeys => EnUs.Entries.Keys.Union(JaJp.Entries.Keys, StringComparer.Ordinal);
}

/// <summary>
/// リポジトリ内の <c>.resw</c> をすべて読み込んだカタログ。
/// <b>件数はハードコードしない</b> ── キーは今後も増えるので、
/// 検査するのは常に「集合が一致すること」であって「93件であること」ではない。
/// </summary>
internal static class ResourceCatalog
{
    /// <summary>アプリ本体（<c>src/ProcessRecorderApp/Strings/{locale}/Resources.resw</c>）。</summary>
    public static ResourceSet App { get; } = Load(
        name: "Resources.resw",
        keyPrefix: "Resources/",
        directory: RepositoryFiles.At("src", "ProcessRecorderApp", "Strings"),
        fileName: "Resources.resw");

    /// <summary>Controls ライブラリ（<c>src/Controls/Strings/{locale}/ControlsResources.resw</c>）。</summary>
    public static ResourceSet Controls { get; } = Load(
        name: "ControlsResources.resw",
        keyPrefix: "Controls/ControlsResources/",
        directory: RepositoryFiles.At("src", "Controls", "Strings"),
        fileName: "ControlsResources.resw");

    public static ResourceSet[] All { get; } = [App, Controls];

    /// <summary>
    /// 完全修飾キー（例 <c>"Resources/Cli_NoVariables"</c>）を「どの resw の・どのキーか」へ分解する。
    /// 接頭辞が長いものから順に見る。
    /// </summary>
    public static bool TryResolve(string qualifiedKey, out ResourceSet set, out string key)
    {
        foreach (var candidate in All.OrderByDescending(s => s.KeyPrefix.Length))
        {
            if (qualifiedKey.StartsWith(candidate.KeyPrefix, StringComparison.Ordinal))
            {
                set = candidate;
                key = qualifiedKey[candidate.KeyPrefix.Length..];
                return true;
            }
        }

        set = null!;
        key = string.Empty;
        return false;
    }

    private static ResourceSet Load(string name, string keyPrefix, string directory, string fileName)
        => new(name, keyPrefix,
            LoadLocale(Path.Combine(directory, "en-US", fileName), "en-US"),
            LoadLocale(Path.Combine(directory, "ja-JP", fileName), "ja-JP"));

    private static LocalizedResources LoadLocale(string path, string locale)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (var data in XDocument.Load(path).Root!.Elements("data"))
        {
            string key = (string?)data.Attribute("name")
                ?? throw new InvalidOperationException($"{RepositoryFiles.Relative(path)}: name 属性の無い <data> がある");
            // xml:space="preserve" があるため、値は加工せずそのまま保持する
            string value = (string?)data.Element("value") ?? string.Empty;

            if (!entries.TryAdd(key, value))
                duplicates.Add(key);
        }

        return new(locale, path, entries, [.. duplicates]);
    }
}
