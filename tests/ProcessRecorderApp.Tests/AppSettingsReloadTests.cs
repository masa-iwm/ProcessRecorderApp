using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <c>AppSettings.Reload()</c> は全プロパティを<b>手書きでコピー</b>している。
/// プロパティを増やしたときに追記を忘れると、<c>Reload()</c> がその値だけ黙って
/// 既定値へ戻す ── コンパイルもテストも通ったまま、その設定だけが再読込で失われる。
///
/// <para>
/// <b>L1 からは実行できない。</b> <c>AppSettings</c> は WinUI アプリプロジェクトにあり、
/// このテストプロジェクトは参照していない。<b>L3 からは実行できる</b>
/// （Settings 画面の「再読み込み」ボタンが <c>Reload()</c> を呼ぶ）が、
/// 外から見えるのは画面に出ている値だけで、<b>どのプロパティが写ったかは見えない</b>。
/// そこでソースを<b>テキストとして</b>突き合わせる。実行はしないが、
/// 「宣言したのにコピーしていない」という唯一の失敗モードは確実に捕まえられる。
/// </para>
/// </summary>
public class AppSettingsReloadTests
{
    /// <summary>
    /// 自動プロパティの宣言（<c>public [partial] T Name { get; [internal] set; }</c>）。
    /// 式形式（<c>=> _default.Value</c>）や <c>const</c> は対象外。
    /// </summary>
    private static readonly Regex PropertyDeclarationRegex = new(
        @"^\s*public\s+(?:partial\s+)?[^\r\n]*?\s(\w+)\s*\{\s*get;\s*(?:(?:internal|private|protected)\s+)?set;\s*\}",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static string SourcePath => RepositoryFiles.At("src", "ProcessRecorderApp", "Settings", "AppSettings.cs");

    [Fact]
    public void EveryPersistedProperty_IsCopiedByReload()
    {
        string source = File.ReadAllText(SourcePath);

        var properties = PropertyDeclarationRegex.Matches(source)
            .Where(m => !SourceReferences.IsCommentLine(source, m.Index))
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // 走査が壊れると 0 件になり「1件も無いので全部通る」になる。件数の下限で守る
        // （増える分には問題ない）。
        Assert.True(properties.Length >= 15,
            $"AppSettings のプロパティ走査が壊れている（{properties.Length} 件しか見つからない）: {string.Join(", ", properties)}");

        string reloadBody = ExtractMethodBody(source, "public void Reload()");
        var missing = properties
            .Where(name => !Regex.IsMatch(reloadBody, $@"\b{Regex.Escape(name)}\b"))
            .ToArray();

        Assert.True(missing.Length == 0,
            $"AppSettings.Reload() がコピーしていないプロパティ: {string.Join(", ", missing)}"
            + $"{Environment.NewLine}追記を忘れると Reload() でそのプロパティだけ既定値に戻る。");
    }

    /// <summary>シグネチャ直後の <c>{</c> から対応する <c>}</c> までを取り出す。</summary>
    private static string ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"AppSettings.cs に '{signature}' が見つからない（改名された可能性がある）。");

        int open = source.IndexOf('{', start + signature.Length);
        Assert.True(open >= 0, $"'{signature}' の本体が見つからない。");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"'{signature}' の本体が閉じていない。");
    }
}
