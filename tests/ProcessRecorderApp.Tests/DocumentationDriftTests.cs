using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// ドキュメントと実装の齟齬を機械的に防ぐ。
///
/// <para>
/// 「書いた時点では正しかった」記述は、実装が動くと黙って腐る。
/// 表と定数を突き合わせるのは人力では続かないので、テストで固定する。
/// </para>
/// </summary>
public class DocumentationDriftTests
{
    /// <summary>
    /// <c>public const int ...ExitCode... = N;</c>。
    /// <c>ExitCode_*</c> だけでなく <c>DefaultFailureExitCode</c> /
    /// <c>UnexpectedErrorExitCode</c> も終了コード表に載っているため一緒に拾う。
    /// </summary>
    private static readonly Regex ExitCodeConstantRegex =
        new(@"public\s+const\s+int\s+(\w*ExitCode\w*)\s*=\s*(\d+)\s*;", RegexOptions.Compiled);

    /// <summary>
    /// 直前の型宣言（表の「定義箇所」列は <c>型名.定数名</c> で書かれている）。
    /// <c>record struct</c> / <c>record class</c> があるので、<c>record</c> の後ろの
    /// 種別キーワードを読み飛ばしてから名前を取る（さもないと型名が "struct" になる）。
    /// </summary>
    private static readonly Regex TypeDeclarationRegex =
        new(@"\b(?:record\s+(?:class|struct)\s+|class\s+|struct\s+|record\s+)(\w+)", RegexOptions.Compiled);

    /// <summary><c>new Command("name", ...)</c>（<c>RootCommand</c> は名前を持たないので対象外）。</summary>
    private static readonly Regex CommandRegistrationRegex =
        new(@"new\s+Command\s*\(\s*""([^""]+)""", RegexOptions.Compiled);

    private sealed record ExitCodeConstant(string TypeName, string Name, int Value)
    {
        public string QualifiedName => $"{TypeName}.{Name}";
    }

    private sealed record ExitCodeRow(int Value, string DefinitionCell);

    /// <summary>
    /// 実際に登録されているサブコマンド名。
    /// コメントアウトされた登録（<c>// var notifyCommand = new Command("notify", ...)</c>）は
    /// 実装ではないので除く。
    /// </summary>
    private static HashSet<string> RegisteredSubcommands()
    {
        string source = File.ReadAllText(RepositoryFiles.At("src", "ProcessRecorderApp", "ActivationCommands.cs"));
        return CommandRegistrationRegex.Matches(source)
            .Where(m => !SourceReferences.IsCommentLine(source, m.Index))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static List<ExitCodeConstant> ExitCodeConstants()
    {
        var found = new List<ExitCodeConstant>();

        foreach (string file in RepositoryFiles.SourceFiles("*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in ExitCodeConstantRegex.Matches(text))
            {
                // 定数を囲む型は「直前に現れた型宣言」。partial class でも
                // ファイル内の宣言から取れる（SingleInstanceManager.Launcher.cs がこの形）。
                var typeMatches = TypeDeclarationRegex.Matches(text, 0);
                string typeName = typeMatches.LastOrDefault(m => m.Index < match.Index)?.Groups[1].Value
                    ?? Path.GetFileNameWithoutExtension(file);

                found.Add(new(typeName, match.Groups[1].Value, int.Parse(match.Groups[2].Value)));
            }
        }

        return found;
    }

    /// <summary>src/README.md の「終了コードの一覧」表を読む。</summary>
    private static List<ExitCodeRow> ExitCodeTableRows()
    {
        var rows = new List<ExitCodeRow>();

        foreach (string[] cells in MarkdownTable.RowsAfter(
                     RepositoryFiles.At("src", "README.md"), "### 終了コードの一覧"))
        {
            // 先頭と末尾は空（行が | で始まり | で終わるため）: ["", 終了コード, 意味, 定義箇所, ""]
            if (cells.Length < 5 || cells[1] == "終了コード")
                continue;

            var value = Regex.Match(cells[1], @"\d+");
            if (value.Success)
                rows.Add(new(int.Parse(value.Value), cells[3]));
        }

        return rows;
    }

    /// <summary>
    /// 定数側 → 表。終了コードを追加／変更したのに表を直し忘れると落ちる。
    /// </summary>
    [Fact]
    public void EveryExitCodeConstant_AppearsInTheReadmeTable()
    {
        var constants = ExitCodeConstants();
        var rows = ExitCodeTableRows();

        Assert.True(constants.Count > 0, "終了コード定数が1件も見つからない。走査側を確認すること。");
        Assert.True(rows.Count > 0, "src/README.md の終了コード表を読めていない。見出しか表の形が変わった可能性がある。");

        var missing = constants
            .Where(c => !rows.Any(r => r.Value == c.Value && r.DefinitionCell.Contains(c.QualifiedName, StringComparison.Ordinal)))
            .Select(c => $"{c.QualifiedName} = {c.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0,
            $"src/README.md の終了コード表に無い（または値が食い違う）定数:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// 表 → 定数側。定数を消した／改名したのに表に残っていると落ちる。
    /// 定数を名指ししていない行（`0` や `10`以上 の説明行）は対象外。
    /// </summary>
    [Fact]
    public void EveryExitCodeTableRow_NamesAnExistingConstant()
    {
        var constants = ExitCodeConstants();

        var stale = new List<string>();
        foreach (var row in ExitCodeTableRows())
        {
            var named = Regex.Match(row.DefinitionCell, @"`(\w+)\.(\w+)`");
            if (!named.Success)
                continue; // 定数を名指ししていない行

            string qualified = $"{named.Groups[1].Value}.{named.Groups[2].Value}";
            if (!constants.Any(c => c.QualifiedName == qualified && c.Value == row.Value))
                stale.Add($"{qualified}（表では {row.Value}）");
        }

        Assert.True(stale.Count == 0,
            $"src/README.md の終了コード表が実装と一致しない（定数が無い／値が違う）:{Environment.NewLine}"
            + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    /// ルート README の言語対。<b>両方を検査する</b> ── 片方だけを見ていると、
    /// もう一方が古くなっても誰も気づかない。
    /// </summary>
    private static readonly string[] RootReadmes = ["README.md", "README.ja.md"];

    /// <summary>
    /// ルート README のコマンド表に、実装済みのサブコマンドがすべて載っていること。
    /// <b>言語対の両方</b>（<c>README.md</c> / <c>README.ja.md</c>）を見る。
    /// </summary>
    [Theory]
    [InlineData("README.md")]
    [InlineData("README.ja.md")]
    public void EverySubcommand_AppearsInTheReadmeCommandTable(string readmeName)
    {
        var subcommands = RegisteredSubcommands().Order(StringComparer.Ordinal).ToArray();

        Assert.True(subcommands.Length > 0, "サブコマンドが1件も見つからない。走査側を確認すること。");

        string readme = File.ReadAllText(RepositoryFiles.At(readmeName));
        var missing = subcommands
            .Where(name => !readme.Contains($"ProcessRecorderApp.exe {name}", StringComparison.Ordinal))
            .ToArray();

        Assert.True(missing.Length == 0,
            $"{readmeName} のコマンド表に無いサブコマンド: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// 逆方向 ── ルート README のコマンド表に、実装されていないコマンドが残っていないこと。
    /// オプション（<c>--set</c> / <c>--get</c> / <c>--help</c>）は Command ではないので対象外。
    /// </summary>
    [Theory]
    [InlineData("README.md")]
    [InlineData("README.ja.md")]
    public void EveryReadmeCommandTableEntry_IsImplemented(string readmeName)
    {
        var subcommands = RegisteredSubcommands();

        var documented = Regex.Matches(File.ReadAllText(RepositoryFiles.At(readmeName)), @"ProcessRecorderApp\.exe ([^\s`]+)")
            .Select(m => m.Groups[1].Value)
            .Where(name => !name.StartsWith('-'))   // オプションはサブコマンドではない
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var unknown = documented.Where(name => !subcommands.Contains(name)).ToArray();

        Assert.True(unknown.Length == 0,
            $"{readmeName} に載っているが実装されていないコマンド: {string.Join(", ", unknown)}");
    }

    /// <summary>
    /// 言語対の見出し構造が一致すること。
    ///
    /// <para>
    /// <b>照合するのは見出しの「深さの並び」であって文言ではない。</b>
    /// 片方の言語にだけ節を足す／消すのが、この対でいちばん起きやすい壊れ方で、
    /// 文言は言語ごとに違うのだから比べようがない。
    /// </para>
    /// <para>
    /// コードブロックの中の <c>#</c> を見出しと誤認しないよう、
    /// フェンス（``` ）の内側は読み飛ばす。
    /// </para>
    /// </summary>
    [Fact]
    public void TheRootReadmePair_HasTheSameHeadingStructure()
    {
        var structures = RootReadmes.ToDictionary(name => name, name => HeadingLevels(name));

        // 走査が空振りしたら失敗させる（L4 の原則）── 見出しを1つも拾えていないのに
        // 「両方とも空だから一致」で緑になるのが最悪。
        foreach (var (name, levels) in structures)
            Assert.True(levels.Count > 0, $"{name} から見出しを1つも読み取れていない。走査側を確認すること。");

        Assert.True(
            structures["README.md"].SequenceEqual(structures["README.ja.md"]),
            "ルート README の言語対で見出し構造が食い違っている（片方にだけ節がある可能性）。" +
            Environment.NewLine +
            $"  README.md    : {string.Join(" ", structures["README.md"].Select(l => new string('#', l)))}" +
            Environment.NewLine +
            $"  README.ja.md : {string.Join(" ", structures["README.ja.md"].Select(l => new string('#', l)))}");
    }

    /// <summary>言語対が互いにリンクしていること（どちらから来ても他方へ行ける）。</summary>
    [Fact]
    public void TheRootReadmePair_LinksToEachOther()
    {
        Assert.Contains("(README.ja.md)", File.ReadAllText(RepositoryFiles.At("README.md")), StringComparison.Ordinal);
        Assert.Contains("(README.md)", File.ReadAllText(RepositoryFiles.At("README.ja.md")), StringComparison.Ordinal);
    }

    /// <summary>Markdown の見出しの深さを出現順に返す（コードブロックの中は数えない）。</summary>
    private static List<int> HeadingLevels(string readmeName)
    {
        var levels = new List<int>();
        bool inFence = false;

        foreach (string line in File.ReadAllLines(RepositoryFiles.At(readmeName)))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || !trimmed.StartsWith('#'))
                continue;

            int level = trimmed.TakeWhile(c => c == '#').Count();
            // `#`の直後に空白が要る（`#!/...` のようなものを拾わない）。
            if (level <= 6 && trimmed.Length > level && trimmed[level] == ' ')
                levels.Add(level);
        }

        return levels;
    }
}
