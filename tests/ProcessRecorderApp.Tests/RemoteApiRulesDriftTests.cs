using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <c>RemoteApiRules.HttpStatusFor</c> ⇔ 終了コード定数の<b>双方向</b>照合。
///
/// <para>
/// 写像はリテラルの <c>switch</c> で書いてある ── 定数は WinUI アプリ側
/// （<c>ActivationCommands</c>）と <c>SingleInstance</c> にあり、<c>Components</c> からも
/// L1 テストからも型としては参照できない。<b>だから両側をソーステキストで読む。</b>
/// </para>
/// <para>
/// <b>収集件数を先に assert する。</b> 正規表現が空振りしたまま
/// 「全部の定数が switch に在る」を通すと、検査そのものが消える
/// （docs/test-harness.md「テストの有効性検証の原則」）。
/// </para>
/// </summary>
public sealed class RemoteApiRulesDriftTests
{
    /// <summary><c>public const int ...ExitCode... = N;</c>（<c>DocumentationDriftTests</c> と同じ形）。</summary>
    private static readonly Regex ExitCodeConstantRegex =
        new(@"public\s+const\s+int\s+(\w*ExitCode\w*)\s*=\s*(\d+)\s*;", RegexOptions.Compiled);

    /// <summary><c>switch</c> の 1 本のアーム（<c>13 =&gt; 404,</c>）。</summary>
    private static readonly Regex SwitchArmRegex =
        new(@"(\d+)\s*=>\s*(\d+)\s*,", RegexOptions.Compiled);

    /// <summary>
    /// API の応答に写る終了コードを定義しているファイル。
    /// <c>SingleInstanceManager.Launcher.cs</c> は<b>入れない</b> ── そこにある 1/2/3/6 は
    /// ランチャー（別プロセス）専用で、ワーカーの応答としては決して起きない。
    /// </summary>
    private static readonly string[][] SourceFiles =
    [
        ["src", "ProcessRecorderApp", "ActivationCommands.cs"],
        ["src", "SingleInstance", "CommandOutcome.cs"],
        ["src", "SingleInstance", "SingleInstanceManager.cs"],
    ];

    /// <summary>
    /// ワーカーが起動中に落ちたことだけを表すコードで、コマンドの結果ではない
    /// （HTTP へ写す対象は「コマンドの結果」だけ）。
    /// </summary>
    private const string LauncherOnlyConstant = "ExitCode_WorkerShuttingDown";

    /// <summary>
    /// 成功。名前付きの定数がどこにも無いので、逆方向の照合ではここだけ明示的に許す。
    /// </summary>
    private const int SuccessExitCode = 0;

    private static Dictionary<string, int> ExitCodeConstants()
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string[] segments in SourceFiles)
        {
            string text = File.ReadAllText(RepositoryFiles.At(segments));
            foreach (Match match in ExitCodeConstantRegex.Matches(text))
            {
                if (SourceReferences.IsCommentLine(text, match.Index))
                    continue;

                found[match.Groups[1].Value] = int.Parse(match.Groups[2].Value);
            }
        }

        return found;
    }

    private static Dictionary<int, int> SwitchArms()
    {
        string path = RepositoryFiles.At("src", "Components", "RemoteApiRules.cs");
        string text = File.ReadAllText(path);
        string body = SourceMethodBody.Extract(text, "public static int HttpStatusFor(int exitCode)");

        var arms = new Dictionary<int, int>();
        foreach (Match match in SwitchArmRegex.Matches(body))
        {
            if (SourceReferences.IsCommentLine(body, match.Index))
                continue;

            arms[int.Parse(match.Groups[1].Value)] = int.Parse(match.Groups[2].Value);
        }

        return arms;
    }

    [Fact]
    public void TheScanFindsEnoughOnBothSides()
    {
        var constants = ExitCodeConstants();
        var arms = SwitchArms();

        Assert.True(constants.Count >= 8,
            $"終了コード定数が {constants.Count} 件しか見つからない（8 件以上あるはず）。"
            + Environment.NewLine
            + "定数の書き方かファイルの置き場所が変わったのなら、"
            + "ExitCodeConstantRegex / SourceFiles も一緒に直すこと"
            + "（空振りのまま緑にすると、この照合が消える）。");

        Assert.True(arms.Count >= 8,
            $"HttpStatusFor の case が {arms.Count} 件しか読めない（8 件以上あるはず）。"
            + Environment.NewLine
            + "switch の書き方を変えたなら SwitchArmRegex も一緒に直すこと。");
    }

    [Fact]
    public void EveryExitCodeConstantHasACaseInTheStatusMap()
    {
        var arms = SwitchArms();

        foreach ((string name, int value) in ExitCodeConstants())
        {
            if (name == LauncherOnlyConstant)
                continue;

            Assert.True(arms.ContainsKey(value),
                $"{name} = {value} に対応する case が RemoteApiRules.HttpStatusFor に無い。"
                + Environment.NewLine
                + "終了コードを増やしたら、HTTP ステータスの写像も一緒に決めること"
                + Environment.NewLine
                + "（決めないと既定の 500 になり、404 や 409 で表現すべき失敗が"
                + "サーバー障害として見える）。");
        }
    }

    [Fact]
    public void EveryCaseInTheStatusMapNamesARealExitCode()
    {
        var values = ExitCodeConstants().Values.ToHashSet();

        foreach (int exitCode in SwitchArms().Keys)
        {
            if (exitCode == SuccessExitCode)
                continue;

            Assert.True(values.Contains(exitCode),
                $"HttpStatusFor に case {exitCode} があるが、その値の終了コード定数が無い。"
                + Environment.NewLine
                + "定数を消したのなら case も消すこと（消し忘れると、"
                + "起き得ない終了コードの写像が残って読み手を誤らせる）。");
        }
    }

    [Fact]
    public void TheCompiledMapMatchesTheSourceText()
    {
        // ソーステキストの照合だけだと「switch は在るが呼ばれていない」を見逃す。
        foreach ((int exitCode, int status) in SwitchArms())
            Assert.Equal(status, RemoteApiRules.HttpStatusFor(exitCode));

        Assert.Equal(200, RemoteApiRules.HttpStatusFor(0));
        Assert.Equal(503, RemoteApiRules.HttpStatusFor(12));
        Assert.Equal(404, RemoteApiRules.HttpStatusFor(13));
    }
}
