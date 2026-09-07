using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>E2E のシャード分割（<c>tools/Run-E2E.ps1</c>）が拠って立つ前提を固定する。</b>
///
/// <para>
/// シャードは「フィルタが実際に何を選ぶか」でしか定義できない。フィルタが
/// <b>1 件も選ばなくても <c>dotnet test</c> は成功で終わる</b>ので、前提が崩れた形は
/// <b>「速くなった」ようにしか見えない</b> ── 走らなくなったテストは
/// 誰にも報告されない。ここで捕まえるのはその 3 通り:
/// </para>
/// <list type="number">
/// <item><b>トレイトの付け忘れ・付け過ぎ</b>。<c>AppUi</c> / <c>TrayUi</c>（UIA）を使う
/// テストは実デスクトップを持つ runner でしか通らない。使っているのに
/// <c>Category=Gui</c> が無ければ、そのテストは他のシャードへ紛れて落ちる。
/// 逆に使っていないのに付いていると、gui シャードだけが際限なく太る。</item>
/// <item><b>web シャードのクラス名の腐り</b>。名前は部分一致で当てるので、改名すると
/// web は空振り（＝スクリプトが落とす）、core はそのクラスを二重に走らせる。</item>
/// <item><b>CI の matrix とスクリプトの食い違い</b>。<c>build.yml</c> にしか無いシャード名は
/// <c>ValidateSet</c> で弾かれ、スクリプトにしか無いシャードは誰も流さない。</item>
/// </list>
/// <para>
/// 規則は<b>ファイル単位</b>である ── 「ファイルが <c>AppUi</c> / <c>TrayUi</c> を参照する
/// ⇔ そのファイルの全テストクラスがクラスレベルの <c>Gui</c> トレイトを持つ」。
/// メソッドレベルのトレイトは使わない（<c>vstest</c> のトレイトフィルタは
/// クラスとメソッドの両方を見るので、混ぜると「どちらで付いたのか」が読めなくなる）。
/// </para>
/// </summary>
public class E2EShardSyncTests
{
    private static readonly string E2EDirectory = RepositoryFiles.At("tests", "ProcessRecorderApp.E2E");

    private static readonly string ScriptPath = RepositoryFiles.At("tools", "Run-E2E.ps1");

    private static readonly string WorkflowPath = RepositoryFiles.At(".github", "workflows", "build.yml");

    /// <summary>クラスに付いた Gui トレイト。</summary>
    private const string GuiTrait = "[Trait(\"Category\", \"Gui\")]";

    /// <summary>
    /// E2E のテストファイル（<c>*Tests.cs</c>）。<c>obj/</c> と <c>bin/</c> は見ない
    /// ── 生成された <c>.cs</c> が混ざる。
    /// </summary>
    private static string[] TestFiles()
    {
        string[] files = Directory.GetFiles(E2EDirectory, "*Tests.cs", SearchOption.TopDirectoryOnly);
        Assert.True(files.Length > 0, $"{RepositoryFiles.Relative(E2EDirectory)} に *Tests.cs が 1 つも無い。");
        return files;
    }

    /// <summary>コメント行を除いて <paramref name="needle"/> を含むか。</summary>
    private static bool ContainsOutsideComments(string text, string needle)
    {
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            if (!SourceReferences.IsCommentLine(text, i))
                return true;
        }
        return false;
    }

    /// <summary>
    /// <b>UIA を使うファイル ⇔ Gui トレイトを持つファイル（双方向）。</b>
    ///
    /// <para>
    /// 片方向だけだと穴が開く: 「使っているのに付いていない」は gui 以外のシャードでの
    /// 失敗として<b>いずれ気付ける</b>が、「使っていないのに付いている」は
    /// <b>永久に気付けない</b> ── 実デスクトップの runner では通ってしまうので、
    /// gui シャードが黙って重くなるだけになる。
    /// </para>
    /// <para>
    /// <b>E2E からの UIA アクセスは <c>AppUi.</c> / <c>TrayUi.</c> 経由に限ること。</b>
    /// この検査はその 2 つの名前の出現を見るだけの<b>テキスト検査</b>なので、
    /// テストが <c>UIA3Automation</c> などを直に触ると、UIA を使っているのに
    /// トレイトが無い形が素通りする。
    /// </para>
    /// </summary>
    [Fact]
    public void GuiTraitMatchesUiaUsage()
    {
        List<string> missingTrait = [];
        List<string> unexpectedTrait = [];

        foreach (string file in TestFiles())
        {
            string text = File.ReadAllText(file);
            bool usesUia = ContainsOutsideComments(text, "AppUi.") || ContainsOutsideComments(text, "TrayUi.");
            bool hasTrait = ContainsOutsideComments(text, GuiTrait);

            if (usesUia && !hasTrait)
                missingTrait.Add(RepositoryFiles.Relative(file));
            else if (!usesUia && hasTrait)
                unexpectedTrait.Add(RepositoryFiles.Relative(file));
        }

        Assert.True(
            missingTrait.Count == 0 && unexpectedTrait.Count == 0,
            "E2E の Gui トレイトが AppUi / TrayUi の使用と食い違っている。"
            + Environment.NewLine
            + $"AppUi/TrayUi を使うのに {GuiTrait} が無い: "
            + (missingTrait.Count == 0 ? "（無し）" : string.Join(", ", missingTrait))
            + Environment.NewLine
            + $"使っていないのに {GuiTrait} が付いている: "
            + (unexpectedTrait.Count == 0 ? "（無し）" : string.Join(", ", unexpectedTrait))
            + Environment.NewLine
            + "規則はファイル単位（そのファイルの全テストクラスにクラスレベルで付ける）。"
            + Environment.NewLine
            + "UIA を使うテストと使わないテストが同居するなら、ファイルごと分けること。");
    }

    /// <summary>
    /// <c>tools/Run-E2E.ps1</c> の <c>$WebShardClasses = @('A', 'B', ...)</c> を読む。
    /// </summary>
    private static string[] WebShardClasses()
    {
        string text = File.ReadAllText(ScriptPath);

        // 行頭に錨を下ろす（`^` + Multiline）── スクリプトのヘルプ本文に出てくる
        // `$WebShardClasses` の言及はインデントされているので、代入行だけに当たる。
        Match assignment = Regex.Match(text, @"^\$WebShardClasses\s*=\s*@\(", RegexOptions.Multiline);
        int at = assignment.Success ? assignment.Index : -1;

        Assert.True(at >= 0,
            $"{RepositoryFiles.Relative(ScriptPath)} に（コメントでない）'$WebShardClasses = @(...)' が無い。"
            + Environment.NewLine
            + "スクリプトの書き方を変えたなら、この検査も一緒に直すこと"
            + Environment.NewLine
            + "── 見つからないまま緑にすると、web シャードの定義が無検査になる。");

        int open = text.IndexOf("@(", at, StringComparison.Ordinal);
        int close = text.IndexOf(')', open);
        Assert.True(open >= 0 && close > open, "'$WebShardClasses' の後に配列リテラルが無い。");

        string[] names = [.. Regex.Matches(text[open..close], @"'([^']+)'").Select(m => m.Groups[1].Value)];
        Assert.True(names.Length > 0, "'$WebShardClasses' が空。web シャードが何も選ばなくなる。");
        return names;
    }

    /// <summary>E2E の <c>public sealed class</c>（クラス名 → そのファイル）。</summary>
    private static Dictionary<string, string> E2EClasses()
    {
        Dictionary<string, string> map = [];
        foreach (string file in TestFiles())
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"^public sealed class (\w+)",
                         RegexOptions.Multiline))
            {
                map[match.Groups[1].Value] = file;
            }
        }
        return map;
    }

    /// <summary>
    /// <b>web シャードの 4 クラスが実在し、部分一致で事故を起こさないこと。</b>
    ///
    /// <para>
    /// フィルタは <c>E2E.&lt;Class&gt;.</c> と<b>前後を付けて</b>書くので、
    /// <c>E2E.RecordingTests.</c> は <c>E2E.ContinuousRecordingTests.Method</c> の
    /// 部分文字列には<b>ならない</b> ── 現在の書き方なら末尾一致の事故は起きない。
    /// 下の EndsWith 検査が守るのは<b>裸のクラス名を書いてしまった場合</b>である
    /// （<c>FullyQualifiedName~RecordingTests</c> は
    /// <c>ContinuousRecordingTests</c> まで巻き込む）。前後を落としても壊れない名前だけを
    /// 使う、という余裕を保つための検査。
    /// </para>
    /// <para>
    /// Gui トレイトを持たないことも見る。web は <c>Category!=Gui</c> と AND を取るので、
    /// Gui が付いた瞬間にそのクラスは web からも core からも漏れる。
    /// </para>
    /// </summary>
    [Fact]
    public void WebShardClassesExist()
    {
        var classes = E2EClasses();

        foreach (string name in WebShardClasses())
        {
            Assert.True(classes.TryGetValue(name, out string? file),
                $"{RepositoryFiles.Relative(ScriptPath)} の $WebShardClasses にある '{name}' が"
                + " E2E の 'public sealed class' として見つからない（改名した？）。"
                + Environment.NewLine
                + "web シャードは空振りし、そのクラスは core 側で二重に走る。");

            string text = File.ReadAllText(file!);

            Assert.True(ContainsOutsideComments(text, "[Collection(E2ECollection.Name)]"),
                $"'{name}'（{RepositoryFiles.Relative(file!)}）に [Collection(E2ECollection.Name)] が無い。");

            Assert.False(ContainsOutsideComments(text, GuiTrait),
                $"'{name}'（{RepositoryFiles.Relative(file!)}）に {GuiTrait} が付いている。"
                + Environment.NewLine
                + "web シャードは Category!=Gui と AND を取るので、このクラスはどのシャードでも走らなくなる。");

            string[] shadowed = [.. classes.Keys
                .Where(other => other != name && other.EndsWith(name, StringComparison.Ordinal))
                .Order()];

            Assert.True(shadowed.Length == 0,
                $"'{name}' は他のクラス名の末尾に一致する: {string.Join(", ", shadowed)}。"
                + Environment.NewLine
                + "現在のフィルタは E2E.<Class>. と前後を付けて書くのでこの形でも当たらないが、"
                + "誰かが裸のクラス名（FullyQualifiedName~RecordingTests）を書いた瞬間に、"
                + "web がそれらを巻き込み core がそれらを落とす。"
                + Environment.NewLine
                + "どちらかを改名すること。");
        }
    }

    /// <summary>
    /// <c>tools/Run-E2E.ps1</c> の <c>-Shard</c> の <c>ValidateSet</c> を読む。
    /// </summary>
    private static string[] ScriptShardNames()
    {
        var match = Regex.Match(File.ReadAllText(ScriptPath), @"\[ValidateSet\(([^)]*)\)\]");
        Assert.True(match.Success,
            $"{RepositoryFiles.Relative(ScriptPath)} に -Shard の [ValidateSet(...)] が無い。");

        return [.. Regex.Matches(match.Groups[1].Value, @"'([^']+)'").Select(m => m.Groups[1].Value)];
    }

    /// <summary>
    /// <b><c>build.yml</c> の <c>shard:</c> matrix ⇔ スクリプトの <c>ValidateSet</c>。</b>
    ///
    /// <para>
    /// <c>all</c> はスクリプトだけの値（手元で全部流すため）なので matrix からは除く。
    /// それ以外が食い違うと、matrix 側にしか無いシャードは <c>ValidateSet</c> で即座に落ち、
    /// スクリプト側にしか無いシャードは<b>誰にも流されないまま静かに残る</b>。
    /// </para>
    /// </summary>
    [Fact]
    public void BuildYmlMatrixMatchesTheScript()
    {
        string[] lines = File.ReadAllLines(WorkflowPath);
        string? matrixLine = lines.FirstOrDefault(l =>
            l.TrimStart().StartsWith("shard:", StringComparison.Ordinal) && l.Contains('['));

        Assert.NotNull(matrixLine);

        int open = matrixLine!.IndexOf('[');
        int close = matrixLine.IndexOf(']', open);
        Assert.True(close > open, $"{RepositoryFiles.Relative(WorkflowPath)} の shard: 行が配列になっていない。");

        string[] inWorkflow = [.. matrixLine[(open + 1)..close]
            .Split(',')
            .Select(s => s.Trim().Trim('\'', '"'))
            .Where(s => s.Length > 0)
            .Order()];

        string[] inScript = [.. ScriptShardNames()
            .Where(s => !string.Equals(s, "all", StringComparison.Ordinal))
            .Order()];

        Assert.Equal(inScript, inWorkflow);

        // シャードの数そのものも固定する。増やすときは build.yml・スクリプト・ここを対で直す
        // ── ValidateSet とだけ突き合わせると、両方に同じ間違いを書いた形が素通りする。
        Assert.Equal(["core", "gui", "web"], inWorkflow);
    }
}
