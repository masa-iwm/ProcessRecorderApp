using ProcessRecorderApp.GStreamer;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// GStreamer のネイティブ一式をどこから読むかの決め方
/// （<see cref="GStreamerRuntimeLocator"/> の純粋関数部分）。
///
/// <para>
/// ここが守るのは<b>優先順位</b>（元の PATH → 環境変数 → レジストリ →
/// MSYS2 → 同梱物）と、<b>1件だけを選ぶこと</b>。選んだ根は
/// <c>GstSharpOptions.NativeSearchPath</c> として GstSharp.Net へ渡り、
/// バインディングが全モジュールを固定ディレクトリから絶対パスでロードする
/// ── 「gstreamer と glib が別の根から来る」混成の防止はバインディング側の
/// 固定（pin）が担う（PATH の組み立てはアプリの仕事ではなくなった）。
/// </para>
///
/// <para>
/// 実環境に触る部分（レジストリ・<c>Directory.Exists</c>・ロード済みモジュール）は
/// 開発機と CI で答えが違うので L1 では検証しない。**実際にどこからロードされたか**は
/// L2 が <c>activity.log</c> の <c>gst.runtime</c> で見る。
/// </para>
/// </summary>
public class GStreamerRuntimeLocatorTests
{
    private const string AppDir = @"C:\app";
    private const string Rid = "win-x64";

    private static IReadOnlyList<GStreamerRuntimeCandidate> AllCandidates()
        => GStreamerRuntimeLocator.BuildCandidates(
            mingwRoot: @"C:\gst-env",
            installedGStreamerRoot: @"C:\gst-reg",
            msys2Root: @"C:\msys64",
            appDirectory: AppDir,
            runtimeIdentifier: Rid);

    [Fact]
    public void Candidates_AreOrderedFromTheMostSpecificInstallToTheBundledCopy()
    {
        var candidates = AllCandidates();

        Assert.Equal(
            (string[])
            [
                GStreamerRuntimeLocator.SourceEnvironment,
                GStreamerRuntimeLocator.SourceRegistry,
                GStreamerRuntimeLocator.SourceMsys2,
                GStreamerRuntimeLocator.SourceBundled,
            ],
            candidates.Select(c => c.Source));

        Assert.Equal(
            (string[])
            [
                @"C:\gst-env\bin",
                @"C:\gst-reg\bin",
                @"C:\msys64\ucrt64\bin",
                @"C:\app\runtimes\win-x64\bin",
            ],
            candidates.Select(c => c.BinDirectory));
    }

    [Fact]
    public void Candidates_SkipRootsThatWereNotDiscovered()
    {
        var candidates = GStreamerRuntimeLocator.BuildCandidates(
            mingwRoot: null, installedGStreamerRoot: "  ", msys2Root: null,
            appDirectory: AppDir, runtimeIdentifier: Rid);

        var only = Assert.Single(candidates);
        Assert.Equal(GStreamerRuntimeLocator.SourceBundled, only.Source);
    }

    [Fact]
    public void Candidates_TolerateATrailingSeparatorOnTheRoot()
    {
        // GStreamer(MinGW) のインストーラはレジストリの InstallLocation を `\` 付きで書く。
        var candidates = GStreamerRuntimeLocator.BuildCandidates(
            mingwRoot: null, installedGStreamerRoot: @"C:\gst-reg\", msys2Root: null,
            appDirectory: null, runtimeIdentifier: Rid);

        Assert.Equal(@"C:\gst-reg\bin", Assert.Single(candidates).BinDirectory);
    }

    [Fact]
    public void SplitPath_DropsEmptyEntriesAndQuotes()
    {
        Assert.Equal(
            (string[])[@"C:\a", @"C:\b c"],
            GStreamerRuntimeLocator.SplitPath($@"C:\a;;""C:\b c"";  "));
    }

    [Fact]
    public void SplitPath_OfNothingIsEmpty()
    {
        Assert.Empty(GStreamerRuntimeLocator.SplitPath(null));
        Assert.Empty(GStreamerRuntimeLocator.SplitPath(""));
    }

    [Fact]
    public void Select_PrefersADirectoryThatIsAlreadyOnPath()
    {
        var chosen = GStreamerRuntimeLocator.Select(
            (string[])[@"C:\windows", @"C:\other\bin"],
            AllCandidates(),
            directory => directory is @"C:\other\bin" or @"C:\gst-env\bin");

        Assert.NotNull(chosen);
        Assert.Equal(GStreamerRuntimeLocator.SourcePath, chosen.Source);
        Assert.Equal(@"C:\other\bin", chosen.BinDirectory);
    }

    [Fact]
    public void Select_FallsThroughTheCandidatesInOrder()
    {
        // 環境変数の根には本体が無く、レジストリの根にはある、という状況。
        var chosen = GStreamerRuntimeLocator.Select(
            (string[])[@"C:\windows"],
            AllCandidates(),
            directory => directory is @"C:\gst-reg\bin" or @"C:\msys64\ucrt64\bin");

        Assert.NotNull(chosen);
        Assert.Equal(GStreamerRuntimeLocator.SourceRegistry, chosen.Source);
    }

    [Fact]
    public void Select_ReachesTheBundledCopyWhenNothingElseHasIt()
    {
        var chosen = GStreamerRuntimeLocator.Select(
            (string[])[@"C:\windows"],
            AllCandidates(),
            directory => directory == @"C:\app\runtimes\win-x64\bin");

        Assert.NotNull(chosen);
        Assert.Equal(GStreamerRuntimeLocator.SourceBundled, chosen.Source);
    }

    [Fact]
    public void Select_IsNullWhenNoCandidateHasTheCoreLibrary()
    {
        Assert.Null(GStreamerRuntimeLocator.Select(
            (string[])[@"C:\windows"], AllCandidates(), _ => false));
    }

    [Fact]
    public void DescribeRuntime_NamesTheSourceAndSurvivesAnUnloadedRuntime()
    {
        // L1 では GStreamer をロードしていないので core/glib は取れない。
        // それでも1行は出す（診断が空になるのが一番困る）。
        var candidates = AllCandidates();
        string line = GStreamerRuntimeLocator.DescribeRuntime(candidates, candidates[0]);

        Assert.Contains($"selected={GStreamerRuntimeLocator.SourceEnvironment}", line, StringComparison.Ordinal);
        Assert.Contains(@"dir=C:\gst-env\bin", line, StringComparison.Ordinal);
        Assert.Contains("mixed=", line, StringComparison.Ordinal);
        Assert.Contains(@"candidates=[", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeRuntime_SaysSoWhenNothingWasChosen()
    {
        string line = GStreamerRuntimeLocator.DescribeRuntime(
            (GStreamerRuntimeCandidate[])[], chosen: null);

        Assert.Contains("selected=(none)", line, StringComparison.Ordinal);
        Assert.Contains("candidates=[(none)]", line, StringComparison.Ordinal);
    }
}
