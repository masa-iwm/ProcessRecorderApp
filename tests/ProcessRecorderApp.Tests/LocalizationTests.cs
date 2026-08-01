using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// L4（ローカライズ）の静的検証。
///
/// <para>
/// ローカライズは<b>壊れても静かに壊れる</b>。キーが欠けると
/// <c>Localization.GetString</c> は例外を投げ（＝実行時クラッシュ）、
/// <c>GetStringOrFallback</c> はキー名をそのまま画面に出す（＝英語ですらない表示）。
/// どちらもビルドは通るので、機械的に守らない限り気付けるのは実際に開いた人だけになる。
/// </para>
/// <para>
/// MRT ランタイムは起動せず、<c>.resw</c> を XML として読むだけなので高速で環境に依存しない。
/// </para>
/// </summary>
public class LocalizationTests
{
    /// <summary>
    /// en-US と ja-JP で値が同一でも「翻訳漏れ」ではないもの。
    /// <b>増やすときは必ず理由を書くこと</b> ── ここを緩めると未翻訳検出が形骸化する。
    /// </summary>
    private static readonly HashSet<string> IdenticalValueAllowList = new(StringComparer.Ordinal)
    {
        // ContentDialog の主ボタン。日本語 UI でも "OK" が慣例。
        "Resources.resw/PipelineDialog.PrimaryButtonText",
    };

    private static readonly Regex FormatPlaceholderRegex = new(@"\{(\d+)\}", RegexOptions.Compiled);

    public static TheoryData<string> ResourceSetNames() => [.. ResourceCatalog.All.Select(s => s.Name)];

    private static ResourceSet SetByName(string name) => ResourceCatalog.All.Single(s => s.Name == name);

    /// <summary>
    /// キー名の集合が両ロケールで完全一致すること。
    /// <b>件数ではなく集合で見る</b> ── キーは今後も増えるので、数を固定すると
    /// 「両方に足し忘れた」ではなく「数が変わった」で落ちるだけの弱いテストになる。
    /// </summary>
    [Theory]
    [MemberData(nameof(ResourceSetNames))]
    public void KeySets_AreIdenticalAcrossLocales(string setName)
    {
        var set = SetByName(setName);

        var missingInJa = set.EnUs.Entries.Keys.Except(set.JaJp.Entries.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var missingInEn = set.JaJp.Entries.Keys.Except(set.EnUs.Entries.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var message = new StringBuilder();
        if (missingInJa.Length > 0)
            message.AppendLine($"{set.JaJp.DisplayPath} に無い: {string.Join(", ", missingInJa)}");
        if (missingInEn.Length > 0)
            message.AppendLine($"{set.EnUs.DisplayPath} に無い: {string.Join(", ", missingInEn)}");

        Assert.True(message.Length == 0, $"{set.Name} のキー集合がロケール間で一致していない。{Environment.NewLine}{message}");
    }

    /// <summary>同じキーが1つの resw に2回現れると後勝ちで黙って消える。</summary>
    [Theory]
    [MemberData(nameof(ResourceSetNames))]
    public void Keys_AreNotDuplicatedWithinAFile(string setName)
    {
        foreach (var locale in SetByName(setName).Locales)
        {
            Assert.True(locale.DuplicateKeys.Length == 0,
                $"{locale.DisplayPath} に重複したキーがある（後勝ちで消える）: {string.Join(", ", locale.DuplicateKeys)}");
        }
    }

    /// <summary>空値は「キーはあるのに何も表示されない」になる。欠落より気付きにくい。</summary>
    [Theory]
    [MemberData(nameof(ResourceSetNames))]
    public void Values_AreNeverEmpty(string setName)
    {
        foreach (var locale in SetByName(setName).Locales)
        {
            var empty = locale.Entries.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key).Order(StringComparer.Ordinal).ToArray();
            Assert.True(empty.Length == 0, $"{locale.DisplayPath} に空値のキーがある: {string.Join(", ", empty)}");
        }
    }

    /// <summary>
    /// ja-JP の値が en-US と完全一致＝コピペしたまま翻訳し忘れ。
    /// 正当な同一（固有名詞・"OK" 等）は <see cref="IdenticalValueAllowList"/> で除外する。
    /// </summary>
    [Theory]
    [MemberData(nameof(ResourceSetNames))]
    public void Values_AreActuallyTranslated(string setName)
    {
        var set = SetByName(setName);

        var untranslated = set.EnUs.Entries
            .Where(e => set.JaJp.Entries.TryGetValue(e.Key, out var ja) && string.Equals(ja, e.Value, StringComparison.Ordinal))
            .Select(e => e.Key)
            .Where(key => !IdenticalValueAllowList.Contains($"{set.Name}/{key}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(untranslated.Length == 0,
            $"{set.Name}: ja-JP の値が en-US と同一（未翻訳の疑い）: {string.Join(", ", untranslated)}"
            + $"{Environment.NewLine}正当な同一なら LocalizationTests.IdenticalValueAllowList に理由付きで追加すること。");
    }

    /// <summary>
    /// <c>{0}</c> 等の書式プレースホルダの出現集合がロケール間で一致すること。
    /// 不一致は実行時の <c>FormatException</c> か情報欠落に直結する
    /// （例: <c>Cli_RecorderNotFound</c> はレコーダー名 <c>{0}</c> を取る）。
    /// </summary>
    [Theory]
    [MemberData(nameof(ResourceSetNames))]
    public void FormatPlaceholders_MatchAcrossLocales(string setName)
    {
        var set = SetByName(setName);
        var mismatched = new List<string>();

        foreach (var (key, enValue) in set.EnUs.Entries)
        {
            if (!set.JaJp.Entries.TryGetValue(key, out var jaValue))
                continue; // キー集合の不一致は KeySets_AreIdenticalAcrossLocales の担当

            var enPlaceholders = Placeholders(enValue);
            var jaPlaceholders = Placeholders(jaValue);
            if (!enPlaceholders.SetEquals(jaPlaceholders))
                mismatched.Add($"{key} (en-US: {Format(enPlaceholders)} / ja-JP: {Format(jaPlaceholders)})");
        }

        Assert.True(mismatched.Count == 0,
            $"{set.Name}: 書式プレースホルダがロケール間で一致していない: {string.Join(", ", mismatched)}");

        static HashSet<string> Placeholders(string value)
            => [.. FormatPlaceholderRegex.Matches(value).Select(m => m.Groups[1].Value)];

        static string Format(HashSet<string> placeholders)
            => placeholders.Count == 0 ? "なし" : string.Join("+", placeholders.Order(StringComparer.Ordinal).Select(p => $"{{{p}}}"));
    }

    /// <summary>
    /// C# が完全修飾キーで引くキーが両ロケールに実在すること。
    /// <c>GetString</c> はキー欠落で例外を投げるので、これは実行時クラッシュの静的検出になる。
    /// </summary>
    [Fact]
    public void KeysReferencedFromCSharp_ExistInEveryLocale()
    {
        AssertReferencesResolve(SourceReferences.LocalizationCalls, "C# の Localization.GetString");
    }

    /// <summary>
    /// <c>[Description]</c> / <c>[Category]</c> の属性値が両ロケールに実在すること。
    /// PropertyGrid は <c>GetStringOrFallback</c> で解決するため、
    /// <b>欠けていても例外にならずキー名がそのまま画面に出る</b>（実際に確認した経路）。
    /// </summary>
    [Fact]
    public void KeysReferencedFromPropertyAttributes_ExistInEveryLocale()
    {
        AssertReferencesResolve(SourceReferences.PropertyGridAttributeKeys, "[Description]/[Category] の属性値");
    }

    /// <summary>
    /// XAML の <c>x:Uid</c> に対応するキー（<c>{Uid}.{プロパティ}</c>）が
    /// 両ロケールに1件以上あること。x:Uid だけあってキーが無いと、
    /// その要素は文言が空のまま表示される。
    /// </summary>
    [Fact]
    public void XamlUids_HaveAtLeastOneKeyInEveryLocale()
    {
        var unresolved = new List<string>();

        foreach (var reference in SourceReferences.XamlUids)
        {
            string prefix = reference.Key + ".";
            foreach (var locale in reference.Set.Locales)
            {
                if (!locale.Entries.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                    unresolved.Add($"{reference} → {locale.Locale} に {prefix}* が無い");
            }
        }

        Assert.True(unresolved.Count == 0,
            $"x:Uid に対応するリソースキーが無い:{Environment.NewLine}{string.Join(Environment.NewLine, unresolved.Distinct().Order(StringComparer.Ordinal))}");
    }

    /// <summary>
    /// C# から参照するキーはドットを含まないフラット名にする
    /// （src/README.md「ローカライズ（en-US / ja-JP）」の規約）。
    /// ドット付きは <c>x:Uid</c> 由来の <c>Uid.Property</c> 形式のためだけに使う ──
    /// 混ざると「どちらの経路で使われているキーか」が読めなくなる。
    /// </summary>
    [Fact]
    public void KeysReferencedFromCSharp_AreFlatNames()
    {
        var dotted = SourceReferences.LocalizationCalls
            .Concat(SourceReferences.PropertyGridAttributeKeys)
            .Where(r => r.Key.Contains('.'))
            .Select(r => r.ToString())
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(dotted.Length == 0,
            $"C# から参照するキーにドットが含まれている（x:Uid 由来のみ許可）:{Environment.NewLine}{string.Join(Environment.NewLine, dotted)}");
    }

    /// <summary>
    /// どこからも参照されていないキーの報告。<b>失敗ではなく警告</b>。
    /// 削除候補を可視化するのが目的で、参照経路の見落とし（動的な組み立て等）で
    /// 誤検出しうるため、ゲートにはしない。xunit にログ専用の窓口が無いので
    /// スキップの理由として出す ── 実行結果の要約に残る。
    /// </summary>
    [Fact]
    public void UnreferencedKeys_AreReportedAsAWarning()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in SourceReferences.LocalizationCalls.Concat(SourceReferences.PropertyGridAttributeKeys))
            referenced.Add($"{reference.Set.Name}/{reference.Key}");

        var uidPrefixes = SourceReferences.XamlUids
            .Select(r => (r.Set.Name, Prefix: r.Key + "."))
            .ToArray();

        var unreferenced = new List<string>();
        foreach (var set in ResourceCatalog.All)
        {
            foreach (var key in set.AnyLocaleKeys.Order(StringComparer.Ordinal))
            {
                if (referenced.Contains($"{set.Name}/{key}"))
                    continue;
                if (uidPrefixes.Any(u => u.Name == set.Name && key.StartsWith(u.Prefix, StringComparison.Ordinal)))
                    continue;
                unreferenced.Add($"{set.Name}/{key}");
            }
        }

        Assert.SkipWhen(unreferenced.Count > 0,
            $"どこからも参照されていないキー（削除候補・失敗ではない）: {string.Join(", ", unreferenced)}");
    }

    private static void AssertReferencesResolve(KeyReference[] references, string what)
    {
        var missing = new List<string>();

        foreach (var reference in references)
        {
            foreach (var locale in reference.Set.Locales)
            {
                if (!locale.Entries.ContainsKey(reference.Key))
                    missing.Add($"{reference} → {locale.DisplayPath} に無い");
            }
        }

        Assert.True(missing.Count == 0,
            $"{what} が参照するキーが resw に無い:{Environment.NewLine}{string.Join(Environment.NewLine, missing.Distinct().Order(StringComparer.Ordinal))}");

        // 参照が1件も集まらなければ、走査そのものが壊れている（＝何も守らない緑のテスト）
        Assert.True(references.Length > 0, $"{what} の参照が1件も見つからない。走査側（SourceReferences）を確認すること。");
    }
}
