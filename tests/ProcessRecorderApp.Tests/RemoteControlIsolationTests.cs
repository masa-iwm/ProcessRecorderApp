using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <c>src/RemoteControl/</c> の<b>隔離</b>。ASP.NET Core を持ち込んだ代償として
/// 守らなければならない 3 つの境界を、ソーステキストとして固定する。
///
/// <para>
/// <b>① UI の型が出てこないこと。</b> <c>Components.csproj</c> しか参照していなくても、
/// <c>Components</c> が <c>Microsoft.WindowsAppSDK</c> を推移的に持ち込むので、
/// <c>DispatcherQueue</c> は<b>書けばコンパイルが通ってしまう</b>
/// ── 「UI スレッド越境は呼び出し側の責務」を守っているのはこのテキスト検査だけである。
/// 参照関係で止まるのは <c>GstControllerViewModel</c> と
/// <c>RecorderControlService</c>（あの 2 つのプロジェクトは参照していない）。
/// </para>
/// <para>
/// <b>② ハンドラが <c>(HttpContext)</c> 単一引数であること。</b> 最小 API は
/// <c>RequestDelegate</c> と <c>Delegate</c> の 2 つのオーバーロードを持ち、
/// 後者へ落ちた瞬間に Native AOT の警告（IL2026 / IL3050）が出る。
/// <b>これはビルドでは分からない</b> ── 気付けるのは 4 分かかる AOT 発行だけなので、
/// L1 で先に落とす。
/// </para>
/// <para>
/// <b>③ L1 のテストホストへ ASP.NET Core を降ろさないこと。</b> このテスト
/// プロジェクトが <c>RemoteControl.csproj</c> を参照した瞬間、共有フレームワークが
/// テスト実行に載る。参照しないからこそ、ここはソースをテキストとして読んでいる。
/// </para>
/// </summary>
public sealed class RemoteControlIsolationTests
{
    /// <summary>
    /// <c>RemoteControl</c> に現れてはいけない名前。どれも「UI スレッドを持っている側」の
    /// 型で、参照できてしまうと越境の規律がコードレビュー任せになる。
    /// </summary>
    private static readonly string[] ForbiddenNames =
    [
        "DispatcherQueue",
        "GstControllerViewModel",
        "RecorderControlService",
        "Microsoft.UI",
    ];

    /// <summary>
    /// 経路の登録。<c>MapFallback</c> も含めるのは、あちらも同じ
    /// <c>RequestDelegate</c> / <c>Delegate</c> の二択を持つため。
    /// </summary>
    private static readonly Regex MapCallRegex =
        new(@"Map(?:Get|Post|Patch|Put|Delete|Fallback)\(", RegexOptions.Compiled);

    private static string RemoteControlDir => RepositoryFiles.At("src", "RemoteControl");

    private static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(RemoteControlDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar)
                .Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase)
                       || s.Equals("bin", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.Ordinal);

    [Fact]
    public void TheScanFindsTheRemoteControlSources()
    {
        // 走査が空振りすると、以下の検査は全部「違反 0 件」で緑になる。
        Assert.True(5 <= SourceFiles().Count(),
            $"src/RemoteControl の .cs が {SourceFiles().Count()} 件しか見つからない。"
            + "プロジェクトを移動・改名したならこのテストも一緒に直すこと。");
    }

    [Fact]
    public void NoUiThreadTypesAppearInRemoteControl()
    {
        var violations = new List<string>();

        foreach (string file in SourceFiles())
        {
            string text = File.ReadAllText(file);
            foreach (string name in ForbiddenNames)
            {
                int index = text.IndexOf(name, StringComparison.Ordinal);
                if (0 <= index)
                    violations.Add($"{RepositoryFiles.Relative(file)}: '{name}'");
            }
        }

        Assert.True(violations.Count == 0,
            "src/RemoteControl に UI スレッド側の型が現れている:" + Environment.NewLine
            + string.Join(Environment.NewLine, violations) + Environment.NewLine
            + "UI スレッドへの乗り換えはアプリ側（Services/RemoteControlBackend.cs）の責務。"
            + "コメントに書くのも不可 ── 参照が要らない設計であることが読み手に伝わらなくなる。");
    }

    [Fact]
    public void EveryMappedEndpointTakesASingleHttpContext()
    {
        var matches = new List<(string File, int Index, string Text)>();

        foreach (string file in SourceFiles())
        {
            string text = File.ReadAllText(file);
            foreach (Match match in MapCallRegex.Matches(text))
            {
                if (SourceReferences.IsCommentLine(text, match.Index))
                    continue;
                matches.Add((file, match.Index, text));
            }
        }

        Assert.True(5 <= matches.Count,
            $"経路の登録が {matches.Count} 件しか見つからない（5 件以上あるはず）。"
            + "登録の書き方が変わったなら MapCallRegex も一緒に直すこと"
            + "（空振りのまま緑にすると、この検査そのものが消える）。");

        var violations = matches
            .Where(m => !ArgumentsOf(m.Text, m.Index).Contains("(HttpContext ", StringComparison.Ordinal))
            .Select(m => $"{RepositoryFiles.Relative(m.File)}: {ArgumentsOf(m.Text, m.Index).Trim()}")
            .ToArray();

        Assert.True(violations.Length == 0,
            "ハンドラが (HttpContext) 単一引数になっていない:" + Environment.NewLine
            + string.Join(Environment.NewLine, violations) + Environment.NewLine
            + "Delegate 版のオーバーロードへ束縛されると AOT 発行で IL2026 / IL3050 が出る。");
    }

    /// <summary>登録呼び出しの引数（<c>=&gt;</c> まで、無ければ 200 文字まで）。</summary>
    private static string ArgumentsOf(string text, int matchIndex)
    {
        int start = text.IndexOf('(', matchIndex);
        int end = text.IndexOf("=>", start, StringComparison.Ordinal);
        if (end < 0 || start + 200 < end)
            end = Math.Min(start + 200, text.Length);
        return text[start..end];
    }

    [Fact]
    public void TheL1TestProjectDoesNotReferenceRemoteControl()
    {
        string project = File.ReadAllText(
            RepositoryFiles.At("tests", "ProcessRecorderApp.Tests", "ProcessRecorderApp.Tests.csproj"));

        Assert.DoesNotContain("RemoteControl.csproj", project, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppProjectReferencesRemoteControl()
    {
        string project = File.ReadAllText(
            RepositoryFiles.At("src", "ProcessRecorderApp", "ProcessRecorderApp.csproj"));

        // 参照が消えると、サーバーは 1 行も動かないまま「設定はあるのに何も起きない」になる。
        Assert.Contains("RemoteControl.csproj", project, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteControlOnlyReferencesComponents()
    {
        string project = File.ReadAllText(RepositoryFiles.At("src", "RemoteControl", "RemoteControl.csproj"));

        Assert.Contains("Components.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("GStreamer.GstSharpNet.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessRecorderApp.csproj", project, StringComparison.Ordinal);
        // PackageReference を足すと中央パッケージ管理（Directory.Packages.props）へ
        // 版を書く必要が生じる。ASP.NET Core は共有フレームワーク参照で足りている。
        Assert.DoesNotContain("<PackageReference", project, StringComparison.Ordinal);
    }
}
