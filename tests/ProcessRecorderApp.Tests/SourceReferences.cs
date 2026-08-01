using System.Text.RegularExpressions;

namespace ProcessRecorderApp.Tests;

/// <summary>ソース上のリソースキー参照1件。</summary>
/// <param name="Key">参照しているキー（<see cref="ResourceSet.KeyPrefix"/> を除いた部分）。</param>
/// <param name="Set">解決先の resw。</param>
/// <param name="Location">失敗メッセージ用の「相対パス:行番号」。</param>
internal sealed record KeyReference(string Key, ResourceSet Set, string Location)
{
    public override string ToString() => $"{Set.KeyPrefix}{Key} ({Location})";
}

/// <summary>
/// <c>src/</c> のソースを走査して「実行時に解決されるリソースキー」を集める。
///
/// <para>
/// <c>Localization.GetString</c> は<b>キー欠落で例外を投げる</b>設計なので、
/// ここでの照合は「起動直後のクラッシュの静的検出」に相当する。
/// <c>GetStringOrFallback</c>（PropertyGrid の Category/Description）は例外を投げず
/// <b>キー名がそのまま画面に出る</b>ため、こちらは「静かに壊れる」経路の検出になる
/// ── <c>PropCat_Encoding</c> のように後から増えたカテゴリキーの描画確認に UIA が要るのはこのため。
/// </para>
/// </summary>
internal static class SourceReferences
{
    /// <summary>
    /// <c>Localization.GetString("...")</c> / <c>GetStringOrFallback("...")</c> の
    /// <b>文字列リテラル</b>。補間文字列（<c>$"Resources/{key}"</c>）は静的に解決できないので拾わない
    /// ── そちらは <see cref="PropertyGridAttributeKeys"/> が別経路で受け持つ。
    /// </summary>
    private static readonly Regex LocalizationCallRegex =
        new(@"Localization\.GetString(?:OrFallback)?\s*\(\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>
    /// 完全修飾キーの形をした文字列リテラルを、呼び出し形に関係なく拾う。
    ///
    /// <para>
    /// 呼び出しの直後にリテラルが来ない書き方が実在する（<c>ActivationCommands</c> の
    /// <c>GetString(start ? "Resources/Cli_CannotStartInState" : "Resources/Cli_CannotStopInState", ...)</c>）。
    /// <see cref="LocalizationCallRegex"/> だけではこの2キーを取りこぼし、
    /// <b>削除しても検出できない</b>（実行時は <c>GetString</c> が例外を投げる経路）。
    /// 接頭辞そのものを手掛かりにすれば呼び出し形に依存しない。
    /// </para>
    /// </summary>
    private static readonly Regex QualifiedKeyLiteralRegex = new(
        "\"((?:" + string.Join('|', ResourceCatalog.All.Select(s => Regex.Escape(s.KeyPrefix)))
            + ")[^\"]*)\"",
        RegexOptions.Compiled);

    /// <summary><c>[Description("...")]</c> / <c>[Category("...")]</c>（完全修飾形も含む）。</summary>
    private static readonly Regex PropertyAttributeRegex =
        new(@"\[\s*(?:System\.ComponentModel\.)?(?:Description|Category)\s*\(\s*""([^""]*)""\s*\)\s*\]", RegexOptions.Compiled);

    /// <summary>
    /// 属性以外から PropertyGrid へ渡されるキー
    /// （<c>PropertyGridItem.DefaultCategoryKey = "PropCat_General"</c> のような定数）。
    /// </summary>
    private static readonly Regex PropertyGridKeyLiteralRegex =
        new(@"""((?:PropCat|PropDesc)_[A-Za-z0-9_]+)""", RegexOptions.Compiled);

    /// <summary><c>x:Uid="..."</c>。</summary>
    private static readonly Regex XamlUidRegex =
        new(@"x:Uid\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>C# から完全修飾キーで引かれるキー。</summary>
    public static KeyReference[] LocalizationCalls { get; } =
    [
        .. ScanCSharp(LocalizationCallRegex, qualified: true),
        .. ScanCSharp(QualifiedKeyLiteralRegex, qualified: true),
    ];

    /// <summary>
    /// PropertyGrid が実行時に <c>Resources/{属性値}</c> として解決するキー。
    /// 「属性値はリソースキーである」という規約に依っている（現状 100% そうなっている）。
    /// </summary>
    public static KeyReference[] PropertyGridAttributeKeys { get; } =
    [
        .. ScanCSharp(PropertyAttributeRegex, qualified: false),
        .. ScanCSharp(PropertyGridKeyLiteralRegex, qualified: false),
    ];

    /// <summary>
    /// XAML の <c>x:Uid</c>。resw 側は <c>{Uid}.{プロパティ}</c> 形式のキーを持つため、
    /// 参照の単位は「キー」ではなく「接頭辞」になる。
    /// </summary>
    public static KeyReference[] XamlUids { get; } = [.. ScanXaml()];

    private static List<KeyReference> ScanCSharp(Regex regex, bool qualified)
    {
        var found = new List<KeyReference>();

        foreach (string file in RepositoryFiles.SourceFiles("*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in regex.Matches(text))
            {
                string captured = match.Groups[1].Value;
                string location = Location(file, text, match.Index);

                // 補間文字列（$"Resources/{categoryKey}"）は静的に解決できない。
                // 穴の残った文字列を「実在しないキー」として報告しても意味が無いので落とす。
                if (captured.Contains('{'))
                    continue;

                // コメント行に書かれた例（Localization.cs の doc コメントに
                // "Resources/Foo_Bar" が例示されている）は参照ではない。
                // 行頭がコメントかどうかだけを見る簡易判定 ── 行内に紛れたブロックコメントは
                // 拾ってしまうが、その場合は「実在しないキー」として**失敗する**ので黙って消えることはない。
                if (IsCommentLine(text, match.Index))
                    continue;

                if (!qualified)
                {
                    // 属性値は接頭辞を持たない（PropertyGridView が "Resources/" を付けて解決する）
                    found.Add(new(captured, ResourceCatalog.App, location));
                }
                else if (ResourceCatalog.TryResolve(captured, out var set, out string key))
                {
                    found.Add(new(key, set, location));
                }
                else
                {
                    // どの resw の接頭辞にも一致しない＝キーの綴り自体が誤り。
                    // 存在検査で落とすため、完全修飾キーのまま積む。
                    found.Add(new(captured, ResourceCatalog.App, location));
                }
            }
        }

        return found;
    }

    private static List<KeyReference> ScanXaml()
    {
        var found = new List<KeyReference>();
        string controlsPrefix = Path.Combine("src", "Controls");

        foreach (string file in RepositoryFiles.SourceFiles("*.xaml"))
        {
            // XAML が属するプロジェクトで解決先の resw が決まる
            var set = RepositoryFiles.Relative(file).StartsWith(controlsPrefix, StringComparison.OrdinalIgnoreCase)
                ? ResourceCatalog.Controls
                : ResourceCatalog.App;

            string text = File.ReadAllText(file);
            foreach (Match match in XamlUidRegex.Matches(text))
                found.Add(new(match.Groups[1].Value, set, Location(file, text, match.Index)));
        }

        return found;
    }

    private static string Location(string file, string text, int index)
        => $"{RepositoryFiles.Relative(file)}:{text.AsSpan(0, index).Count('\n') + 1}";

    /// <summary>
    /// 指定位置を含む行が、行頭からコメントで始まっているか。
    /// コメントアウトされたコード（<c>// var notifyCommand = new Command("notify", ...)</c>）を
    /// 実装として数えないために使う。
    /// </summary>
    internal static bool IsCommentLine(string text, int index)
    {
        int lineStart = text.LastIndexOf('\n', Math.Min(index, text.Length - 1)) + 1;
        var line = text.AsSpan(lineStart, index - lineStart).TrimStart();
        return line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith("*");
    }
}
