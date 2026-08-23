using ProcessRecorderApp.Components;
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// リモート操作 API の純粋な規則。
///
/// <para>
/// <b>パス解決の検査が本体。</b> ここが緩いと LAN の誰でも配信 root の外の
/// ファイルを読める。判定を「文字列だけ」に閉じてあるので、
/// 実ファイルを作らずに拒否の網羅を固定できる。
/// </para>
/// </summary>
public sealed class RemoteApiRulesTests
{
    private const string Root = @"C:\rec";

    [Theory]
    [InlineData(0, 200)]
    [InlineData(4, 400)]
    [InlineData(10, 500)]
    [InlineData(11, 404)]
    [InlineData(12, 503)]
    [InlineData(13, 404)]
    [InlineData(14, 409)]
    [InlineData(15, 200)]
    [InlineData(16, 422)]
    [InlineData(17, 422)]
    [InlineData(99, 500)]
    public void EveryMappedExitCodeHasItsStatus(int exitCode, int status)
        => Assert.Equal(status, RemoteApiRules.HttpStatusFor(exitCode));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(-1)]
    [InlineData(12345)]
    public void UnknownExitCodesBecomeServerErrors(int exitCode)
        => Assert.Equal(500, RemoteApiRules.HttpStatusFor(exitCode));

    [Fact]
    public void TheRetryAfterHintIsPositive()
        => Assert.True(RemoteApiRules.RetryAfterSecondsWhenNotReady > 0);

    [Theory]
    [InlineData(null, "token", false)]
    [InlineData("token", null, false)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("", "token", false)]
    [InlineData("token", "", false)]
    [InlineData("token", "token", true)]
    [InlineData("token", "Token", false)]
    [InlineData("token", "token1", false)]
    [InlineData("token1", "token", false)]
    public void TokenEqualsRejectsEverythingButAnExactMatch(string? expected, string? presented, bool equal)
        => Assert.Equal(equal, RemoteApiRules.TokenEquals(expected, presented));

    [Fact]
    public void GeneratedTokensAre43UrlSafeCharacters()
    {
        string token = RemoteApiRules.GenerateAccessToken();

        // 256 ビットを Base64Url で表すと 43 文字（パディング無し）。
        // URL のクエリと Cookie にそのまま載せられる文字だけであること。
        Assert.Equal(43, token.Length);
        Assert.Matches(new Regex("^[A-Za-z0-9_-]{43}$"), token);
    }

    [Fact]
    public void GeneratedTokensDiffer()
        => Assert.NotEqual(RemoteApiRules.GenerateAccessToken(), RemoteApiRules.GenerateAccessToken());

    [Theory]
    [InlineData("a.mp4")]
    [InlineData("A.MP4")]
    [InlineData("sub/a.mp4")]
    [InlineData(@"sub\a.mp4")]
    [InlineData("2026/07/28/rec 01.mp4")]
    public void PathsUnderTheRootResolve(string relativePath)
    {
        Assert.True(RemoteApiRules.TryResolveUnderRoot(Root, relativePath, out string? full));
        Assert.StartsWith(Root + @"\", full, StringComparison.Ordinal);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Root, relativePath.Replace('/', '\\'))),
            full);
    }

    [Theory]
    [InlineData(null)]                          // 未指定
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..\\a.mp4")]                   // 親へ抜ける
    [InlineData("../a.mp4")]
    [InlineData("a/../b.mp4")]                  // 途中の ..
    [InlineData("./a.mp4")]
    [InlineData("sub//a.mp4")]                  // 空のセグメント
    [InlineData(@"C:\other\a.mp4")]             // 絶対パス
    [InlineData(@"\a.mp4")]                     // ルート相対
    [InlineData(@"\\server\share\a.mp4")]       // UNC
    [InlineData("C:a.mp4")]                     // ドライブ相対
    [InlineData("a.mp4v")]                      // 8.3 互換で紛れる拡張子
    [InlineData("a.txt")]
    [InlineData("a")]                           // 拡張子なし
    [InlineData("a.mp4.")]                      // 末尾ドット（Win32 が切り落とす）
    [InlineData("sub./a.mp4")]                  // 途中セグメントの末尾ドット
    [InlineData("sub /a.mp4")]                  // 途中セグメントの末尾空白
    [InlineData("aa:bb.mp4")]                   // NTFS の代替データストリーム
    [InlineData("a*.mp4")]                      // ワイルドカード
    [InlineData("a?.mp4")]
    [InlineData("a<b.mp4")]                     // Path.GetInvalidPathChars には無いが Win32 が拒む
    [InlineData("a>b.mp4")]
    [InlineData("a\"b.mp4")]
    [InlineData("a|b.mp4")]                     // Path.GetInvalidPathChars
    [InlineData("CON.mp4")]                     // DOS の予約名（拡張子が付いてもデバイスを開く）
    [InlineData("nul.mp4")]
    [InlineData("COM1.mp4")]
    [InlineData("LPT9/a.mp4")]                  // 途中セグメントの予約名
    public void PathsOutsideTheContractAreRejected(string? relativePath)
    {
        Assert.False(RemoteApiRules.TryResolveUnderRoot(Root, relativePath!, out string? full));
        Assert.Null(full);
    }

    [Theory]
    [InlineData(@"C:\rec")]
    [InlineData(@"C:\rec\")]
    [InlineData(@"C:\rec\\")]
    public void TheRootMayOrMayNotEndWithASeparator(string root)
    {
        Assert.True(RemoteApiRules.TryResolveUnderRoot(root, "sub/a.mp4", out string? full));
        Assert.Equal(@"C:\rec\sub\a.mp4", full);
    }

    [Fact]
    public void ASiblingDirectoryWithTheSamePrefixIsNotUnderTheRoot()
    {
        // "C:\rec" と "C:\recordings" の取り違え。区切りまで含めて比べていないと通る。
        Assert.False(RemoteApiRules.TryResolveUnderRoot(Root, @"..\recordings\a.mp4", out _));
    }

    [Fact]
    public void AnInvalidRootDoesNotThrow()
        => Assert.False(RemoteApiRules.TryResolveUnderRoot("", "a.mp4", out _));

    [Fact]
    public void TheEditableAndDeniedListsDoNotOverlap()
    {
        foreach (string name in RemoteApiRules.RemoteEditableAppSettings)
        {
            Assert.True(RemoteApiRules.IsRemoteEditable(name));
            Assert.DoesNotContain(name, RemoteApiRules.RemoteDeniedAppSettings);
        }

        foreach (string name in RemoteApiRules.RemoteDeniedAppSettings)
            Assert.False(RemoteApiRules.IsRemoteEditable(name));
    }

    [Fact]
    public void IsRemoteEditableIsOrdinal()
    {
        Assert.True(RemoteApiRules.IsRemoteEditable("GstDebug"));
        Assert.False(RemoteApiRules.IsRemoteEditable("gstdebug"));
        Assert.False(RemoteApiRules.IsRemoteEditable("OutputDirectory"));
        Assert.False(RemoteApiRules.IsRemoteEditable(""));
    }

    // ---- PATCH の重ね合わせ ----

    private static JsonObject Known() => new() { ["A"] = 1, ["B"] = "x" };

    private static bool IsAOrB(string key) => key is "A" or "B";

    [Fact]
    public void AKnownKeyOverwritesTheCurrentValue()
    {
        var target = Known();

        Assert.True(RemoteApiRules.TryMergeIntoNode(
            target, new JsonObject { ["A"] = 2 }, IsAOrB, out string? rejected));

        Assert.Null(rejected);
        Assert.Equal(2, (int)target["A"]!);
        // 触れていないキーは残る（PATCH であって PUT ではない）。
        Assert.Equal("x", (string)target["B"]!);
    }

    [Fact]
    public void AnUnknownKeyIsRejectedAndNamed()
    {
        Assert.False(RemoteApiRules.TryMergeIntoNode(
            Known(), new JsonObject { ["Nope"] = 1 }, IsAOrB, out string? rejected));

        // **どのキーが悪かったかを返すこと。** 「駄目だった」だけでは、
        // 呼び出し側は要求の何を直せばよいか分からない。
        Assert.Equal("Nope", rejected);
    }

    [Fact]
    public void AnEmptyPatchChangesNothing()
    {
        var target = Known();

        Assert.True(RemoteApiRules.TryMergeIntoNode(target, [], IsAOrB, out string? rejected));

        Assert.Null(rejected);
        Assert.Equal(1, (int)target["A"]!);
        Assert.Equal("x", (string)target["B"]!);
    }

    [Fact]
    public void ANullValueIsCopiedInsteadOfBeingSkipped()
    {
        var target = Known();

        // 「キーを書かない」と「null を書く」は別の意味 ── null 許容の設定を
        // 空へ戻す手段はこれしか無い。
        Assert.True(RemoteApiRules.TryMergeIntoNode(
            target, new JsonObject { ["B"] = null }, IsAOrB, out _));

        Assert.True(target.ContainsKey("B"));
        Assert.Null(target["B"]);
    }

    [Fact]
    public void TheCopiedValueIsDetachedFromTheRequestBody()
    {
        var target = Known();
        var patch = new JsonObject { ["A"] = 7 };

        // 複製せずに差すと、同じノードが 2 つの親を持って InvalidOperationException になる。
        Assert.True(RemoteApiRules.TryMergeIntoNode(target, patch, IsAOrB, out _));

        Assert.Equal(7, (int)target["A"]!);
        Assert.Equal(7, (int)patch["A"]!);
        Assert.NotSame(patch["A"], target["A"]);
    }
}
