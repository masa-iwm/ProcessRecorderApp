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
/// ここが守るのは2つ。<b>優先順位</b>（元の PATH → 環境変数 → レジストリ →
/// MSYS2 → 同梱物）と、<b>選んだ1件だけを PATH の先頭に置くこと</b>。
/// 後者は「候補を全部繋ぐと gstreamer と glib が別の根から来る」混成を防ぐためで、
/// 繋ぐ実装に戻すとここが落ちる。
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
    public void ComposePath_PutsOnlyTheChosenDirectoryInFront()
    {
        // **この1件が混成を防いでいる。** 候補を全部繋ぐ実装に戻すとここが落ちる
        // ── 依存 DLL（libglib-2.0-0.dll）は PATH の順で解決されるので、
        // 選ばなかった候補が前に残っていると別の根から取られる。
        var candidates = AllCandidates();
        var chosen = candidates.Single(c => c.Source == GStreamerRuntimeLocator.SourceBundled);

        string composed = GStreamerRuntimeLocator.ComposePath(@"C:\windows;C:\msys64\ucrt64\bin", candidates, chosen);

        Assert.Equal(@"C:\app\runtimes\win-x64\bin;C:\windows;C:\msys64\ucrt64\bin", composed);
        Assert.DoesNotContain(@"C:\gst-env\bin", composed, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\gst-reg\bin", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposePath_KeepsTheOriginalPathAheadOfEverythingWhenNothingWasChosen()
    {
        // 選べなかったときだけ全候補を末尾に繋ぐ。元の PATH を壊さず、
        // 失敗の出方を「本体が見つからない」に保つため。
        string composed = GStreamerRuntimeLocator.ComposePath(@"C:\windows", AllCandidates(), chosen: null);

        Assert.Equal(
            @"C:\windows;C:\gst-env\bin;C:\gst-reg\bin;C:\msys64\ucrt64\bin;C:\app\runtimes\win-x64\bin",
            composed);
    }

    [Fact]
    public void ComposePath_DoesNotEmitAStraySeparatorWhenThereIsNoOriginalPath()
    {
        var candidates = AllCandidates();
        var chosen = candidates[0];

        Assert.Equal(@"C:\gst-env\bin", GStreamerRuntimeLocator.ComposePath(null, candidates, chosen));
        Assert.Equal(@"C:\gst-env\bin", GStreamerRuntimeLocator.ComposePath("", candidates, chosen));
        Assert.Equal(
            @"C:\gst-env\bin;C:\gst-reg\bin;C:\msys64\ucrt64\bin;C:\app\runtimes\win-x64\bin",
            GStreamerRuntimeLocator.ComposePath(null, candidates, chosen: null));
    }

    [Fact]
    public void ComposePath_OfNothingAtAllIsTheOriginalPath()
    {
        Assert.Equal(@"C:\windows",
            GStreamerRuntimeLocator.ComposePath(
                @"C:\windows", (GStreamerRuntimeCandidate[])[], chosen: null));
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
