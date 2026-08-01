using System.Xml.Linq;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// 言語強制のマトリクス（L3）で使う<b>期待値の出どころ</b>。
/// リポジトリの <c>.resw</c> をそのまま読む。
///
/// <para>
/// <b>訳文をテストソースに焼き込まない。</b> 焼き込むと、翻訳を直しただけで
/// テストが赤になり、しかも「画面に出ている文字列が resw の値と一致しているか」
/// という本来の主張からずれる。ここが読むのは製品が表示すべき値そのもの。
/// </para>
///
/// <para>
/// <b>キーが無ければ失敗させる。</b> 既定値へフォールバックすると、キーの綴りを
/// 間違えたテストが「期待値も実際も同じ既定値」で緑になる ── L4 が
/// 「走査が空振りしたら失敗させる」としているのと同じ理由。
/// </para>
/// </summary>
public static class UiResources
{
    /// <summary>ニュートラル言語。en-US でも ja-JP でもない表示言語はここへフォールバックする。</summary>
    public const string NeutralLocale = "en-US";

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>指定ロケールの <c>Resources.resw</c> から値を取る。</summary>
    public static string Get(string locale, string name)
    {
        var values = Load(locale);
        if (!values.TryGetValue(name, out string? value))
        {
            throw new InvalidOperationException(
                $"{locale}/Resources.resw にキー '{name}' がありません。" +
                $" 実在するキーの例: [{string.Join(", ", values.Keys.Order(StringComparer.Ordinal).Take(5))}, ...]");
        }
        return value;
    }

    private static IReadOnlyDictionary<string, string> Load(string locale)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(locale, out var cached))
                return cached;

            string path = Path.Combine(
                RepositoryLayout.Root, "src", "ProcessRecorderApp", "Strings", locale, "Resources.resw");
            if (!File.Exists(path))
                throw new InvalidOperationException($"resw が見つかりません: '{path}'");

            var doc = XDocument.Load(path);
            var values = doc.Root?.Elements("data")
                .Where(d => d.Attribute("name") is not null)
                .ToDictionary(
                    d => d.Attribute("name")!.Value,
                    d => d.Element("value")?.Value ?? "",
                    StringComparer.Ordinal)
                ?? throw new InvalidOperationException($"resw を解析できません: '{path}'");

            if (values.Count == 0)
                throw new InvalidOperationException($"resw にキーが1件もありません: '{path}'");

            Cache[locale] = values;
            return values;
        }
    }
}
