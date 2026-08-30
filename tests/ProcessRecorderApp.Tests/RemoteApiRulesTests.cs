using ProcessRecorderApp.Components;
using System;
using System.IO;
using System.Reflection;
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

    /// <summary>
    /// UI スレッドが塞がっているときの <c>Retry-After</c> は正で、
    /// <b>「エンジンがまだ使えない」より短い</b>こと ── 同じ終了コード 12 でも
    /// 待つ相手が違う（初期化ではなく実行中の 1 操作）。
    /// </summary>
    [Fact]
    public void TheUiThreadBusyHintIsShorterThanTheNotReadyHint()
    {
        Assert.True(RemoteApiRules.RetryAfterSecondsWhenUiThreadBusy > 0);
        Assert.True(
            RemoteApiRules.RetryAfterSecondsWhenUiThreadBusy
                < RemoteApiRules.RetryAfterSecondsWhenNotReady);
    }

    /// <summary>
    /// <c>RemoteControlBackend.RunOnUiAsync</c> が<b>UI スレッドへ乗るまで</b>に
    /// 期限を持ち、超過を 12 ＋ <c>ui thread busy</c> ＋ 短い <c>Retry-After</c> で
    /// 断ること。
    ///
    /// <para>
    /// <b>ソーステキストで固定するしかない。</b> <c>RemoteControlBackend</c> は
    /// WinUI アプリ側の型で、L1 からは参照できない（<c>DispatcherQueue</c> を持つ）。
    /// </para>
    /// <para>
    /// <b>期限は「終わるまで」に掛けてはいけない。</b> 停止は
    /// <c>EventRecorder.MaxAdvisedStopFinalizeTimeoutMs</c>（50 秒）まで正当に掛かるので、
    /// 全体へ掛けると正常に遅い停止を「塞がっている」と偽って断る。
    /// ここでは待つ対象が「乗ったこと」であることまで見る。
    /// </para>
    /// </summary>
    [Fact]
    public void TheBackendGivesUpWhenItCannotGetOntoTheUiThread()
    {
        string source = File.ReadAllText(
            RepositoryFiles.At("src", "ProcessRecorderApp", "Services", "RemoteControlBackend.cs"));

        Assert.Contains("UiThreadEntryDeadline = TimeSpan.FromSeconds(30)", source, StringComparison.Ordinal);
        Assert.Contains("\"ui thread busy\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "RetryAfterSeconds = RemoteApiRules.RetryAfterSecondsWhenUiThreadBusy",
            source, StringComparison.Ordinal);

        // 待っているのは「乗ったこと」であって「終わったこと」ではない。
        Assert.Contains("Task.WhenAny(entered.Task, delay)", source, StringComparison.Ordinal);
        Assert.Contains("entered.TrySetResult();", source, StringComparison.Ordinal);

        // 降りたあとに立つ例外を誰も見ないままにしない。
        Assert.Contains("TaskContinuationOptions.OnlyOnFaulted", source, StringComparison.Ordinal);
    }

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

    /// <summary>
    /// <b>拒否リストの名前が実在するレコーダー設定であること。</b> 文字列なので
    /// 改名や削除にコンパイラは追随しない ── 綴りが外れると、拒否しているつもりの
    /// キーが黙って書けるようになる。
    /// </summary>
    [Fact]
    public void EveryDeniedRecorderSettingIsARealProperty()
    {
        var properties = typeof(ProcessRecorderApp.GStreamer.EventRecorderSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        foreach (string name in RemoteApiRules.RemoteDeniedRecorderSettings)
        {
            Assert.Contains(properties, p => string.Equals(p.Name, name, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("SrcPipeline")]
    [InlineData("EncodingProperties")]
    [InlineData("ContinuousEncodingProperties")]
    [InlineData("FilenameTemplate")]
    [InlineData("ContinuousFilenameTemplate")]
    public void TheDeniedRecorderSettingsAreNotEditable(string name)
        => Assert.False(RemoteApiRules.IsRemoteEditableRecorderSetting(name));

    /// <summary>
    /// <b>パイプライン記述へ生で入る設定は、増えたら必ず拒否リストへ載せること。</b>
    /// 「載っている名前が実在するか」を見るだけでは<b>取りこぼしは見つからない</b> ──
    /// <c>SrcPipeline</c> と同じ実行能力を持つ設定（エンコーダー指定）が許可のまま
    /// 残ると、トークン所持者が任意の GStreamer 要素を注入できる。
    /// 名前で拾えるのはここに書いた形だけなので、別の形の設定を足すときは
    /// 拒否リストの xml-doc の基準で判断すること。
    /// </summary>
    [Fact]
    public void EveryPipelineTextRecorderSettingIsDenied()
    {
        var properties = typeof(ProcessRecorderApp.GStreamer.EventRecorderSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        foreach (var property in properties)
        {
            bool isPipelineText =
                property.Name.EndsWith("SrcPipeline", StringComparison.Ordinal) ||
                property.Name.EndsWith("EncodingProperties", StringComparison.Ordinal) ||
                property.Name.EndsWith("FilenameTemplate", StringComparison.Ordinal);
            if (isPipelineText)
                Assert.False(RemoteApiRules.IsRemoteEditableRecorderSetting(property.Name));
        }
    }

    [Fact]
    public void IsRemoteEditableRecorderSettingIsOrdinal()
    {
        // 拒否リストなので、綴りが 1 文字でも違えば「許可」に落ちる。
        Assert.False(RemoteApiRules.IsRemoteEditableRecorderSetting("SrcPipeline"));
        Assert.True(RemoteApiRules.IsRemoteEditableRecorderSetting("srcpipeline"));
        Assert.True(RemoteApiRules.IsRemoteEditableRecorderSetting("BufferDuration"));
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

    // ---- 録画配信の ETag と経路表記 ----

    [Fact]
    public void TheRecordingETagIsQuoted()
    {
        string etag = RemoteApiRules.RecordingETag(1234, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        // 引用符を付けるのは値を作る側の責務 ── HTTP の entity-tag は
        // 引用符まで含めて 1 つの値である。
        Assert.StartsWith("\"", etag, StringComparison.Ordinal);
        Assert.EndsWith("\"", etag, StringComparison.Ordinal);
        Assert.DoesNotContain("W/", etag, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameFileGivesTheSameRecordingETag()
    {
        var written = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        Assert.Equal(RemoteApiRules.RecordingETag(1234, written), RemoteApiRules.RecordingETag(1234, written));
    }

    [Fact]
    public void ADifferentLengthOrTimeGivesADifferentRecordingETag()
    {
        var written = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        string baseline = RemoteApiRules.RecordingETag(1234, written);

        // 録画中のファイルは書かれるたびに両方が動く ── どちらか片方でも
        // 値が変わらないと、伸びたファイルが古い内容のまま配られる。
        Assert.NotEqual(baseline, RemoteApiRules.RecordingETag(1235, written));
        Assert.NotEqual(baseline, RemoteApiRules.RecordingETag(1234, written.AddTicks(1)));
    }

    [Theory]
    [InlineData(@"a.mp4", "a.mp4")]
    [InlineData(@"2026\06\a.mp4", "2026/06/a.mp4")]
    [InlineData("", "")]
    public void TheUrlPathUsesForwardSlashes(string relativePath, string urlPath)
    {
        Assert.Equal(urlPath, RemoteApiRules.ToUrlPath(relativePath));
        Assert.Equal(relativePath, RemoteApiRules.FromUrlPath(urlPath));
    }

    [Theory]
    [InlineData(@"2026\06\a.mp4")]
    [InlineData(@"a b\c.mp4")]
    [InlineData("a.mp4")]
    public void TheUrlPathConversionRoundTrips(string relativePath)
        => Assert.Equal(relativePath, RemoteApiRules.FromUrlPath(RemoteApiRules.ToUrlPath(relativePath)));
}
