using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>常時録画が使う GStreamer 要素と、同梱ランタイムの内容を突き合わせる。</b>
///
/// <para>
/// パイプライン文字列に書いた要素が同梱ランタイムから消えても、初期化を実行しない
/// 検査は緑のまま ── 壊れるのは同梱配布の実行時だけ。
/// <see cref="EncoderCatalogScriptSyncTests"/> と同じ性格の検査で、狙う事故も同じである。
/// </para>
/// <para>
/// 検査対象は<b>実物の同梱ツリー</b> ── GstSharpBundle.Windows.X64 由来の
/// <c>gstreamer\win-x64\lib\gstreamer-1.0</c> は、参照経由でこのテストの出力
/// ディレクトリにも複製される。台帳ではなく実ファイルを見るので、パッケージの
/// 版を替えて中身が変わればここが追従して落ちる。
/// </para>
/// <para>
/// <c>videorate</c> は同梱に入っている（別 fps の常時録画がそれに依存する）。
/// 同時に「<c>ContinuousFramerate</c> が空なら <c>videorate</c> を出さない」ガードも固定する
/// ── 利用者が別途入れた GStreamer にその要素が無いことがあるため。
/// </para>
/// </summary>
public class ContinuousRuntimeDependencyTests
{
    private static string[] BundledPlugins()
    {
        string dir = Path.Combine(
            AppContext.BaseDirectory, "gstreamer", "win-x64", "lib", "gstreamer-1.0");

        Assert.True(Directory.Exists(dir),
            $"同梱プラグインのディレクトリが無い: {dir}（GstSharpBundle.Windows.X64 の複製が"
            + "テスト出力へ流れていない）");

        string[] paths = [.. Directory.EnumerateFiles(dir, "*.dll").Select(Path.GetFileName)!];
        Assert.NotEmpty(paths);
        return paths;
    }

    private static bool IsBundled(string pluginFileName)
        => BundledPlugins().Any(p => string.Equals(p, pluginFileName, StringComparison.OrdinalIgnoreCase));

    private static string Build(
        EventRecordingType type, string encoder, bool needsSystemMemory,
        string? framerate, string? resolution, bool sourceSizeIsPinned = true, bool sourceFramerateIsPinned = true)
        => ContinuousBranch.Plan(type, encoder, needsSystemMemory, framerate, resolution, sourceSizeIsPinned, sourceFramerateIsPinned).Branch;

    /// <summary>
    /// 常時録画が<b>無条件に</b>使う要素は、すべて同梱ランタイムに在ること。
    /// ここが崩れると、常時録画は同梱配布で一切動かない。
    /// </summary>
    [Theory]
    [InlineData("gstcoreelements.dll")]   // queue / tee / filesink
    [InlineData("gstapp.dll")]            // appsink / appsrc
    [InlineData("gstisomp4.dll")]         // mp4mux
    [InlineData("gstvideoparsersbad.dll")]// h264parse
    [InlineData("gstvideoconvertscale.dll")] // videoconvert / videoscale（解像度の上書き）
    [InlineData("gstd3d12.dll")]          // d3d12convert / d3d12download
    public void EveryElementTheContinuousBranchAlwaysUses_IsBundled(string plugin)
    {
        Assert.True(IsBundled(plugin),
            $"{plugin} が同梱ランタイムに無い。常時録画は同梱配布で動かなくなる。");
    }

    /// <summary>
    /// <b><c>videorate</c> が同梱に在ること。</b> 常時録画を別のフレームレートで回す機能は
    /// この 1 ファイル（<c>gstvideorate.dll</c>）に依存している。
    /// 同梱ランタイムの供給元（GstSharpBundle.Windows.X64）を絞った構成へ替えるときに
    /// これを落とすと、同梱配布でだけ機能が消える。落とすなら製品側の機能も一緒に畳むこと。
    /// </summary>
    [Fact]
    public void Videorate_IsBundled_SoADifferentFramerateWorksInTheShippedRuntime()
    {
        Assert.True(IsBundled("gstvideorate.dll"),
            "gstvideorate.dll が同梱ランタイムに無い。"
            + "ContinuousFramerate（別 fps の常時録画）が同梱配布でだけ使えなくなる。");
    }

    /// <summary>
    /// <b>要求されていないときは <c>videorate</c> を枝に出さない。</b>
    ///
    /// <para>
    /// 同梱物には入れてあるが、<b>利用者が別途入れた GStreamer には無いことがある</b>
    /// ── 無条件に書くと、フレームレートを変えていない構成まで巻き添えで初期化に失敗する。
    /// 要求されたのに要素が無い場合は、<c>ParseLaunch</c> の <c>no element</c> ではなく
    /// 「<c>videorate</c> が無い」と名指しで失敗させる（<c>ResolveContinuousEncoder</c>）。
    /// </para>
    /// </summary>
    [Fact]
    public void VideorateIsEmittedOnlyWhenADifferentFramerateIsAskedFor()
    {
        // (1) 上書きが空なら枝に出さない
        Assert.False(ContinuousBranch.RequiresVideorate(null));
        Assert.False(ContinuousBranch.RequiresVideorate(""));
        Assert.DoesNotContain(
            ContinuousBranch.VideorateFactory,
            Build(EventRecordingType.D3d12, "x264enc", false, "", "1280x720"));
        Assert.DoesNotContain(
            ContinuousBranch.VideorateFactory,
            Build(EventRecordingType.System, "x264enc", false, "", ""));

        // (2) 要求されたときだけ出す
        Assert.Contains(
            ContinuousBranch.VideorateFactory,
            Build(EventRecordingType.D3d12, "x264enc", false, "5/1", ""));

        // (3) 無いときは名指しで失敗させる
        string recorder = File.ReadAllText(
            RepositoryFiles.At("src", "GStreamer.GstSharpBundle", "EventRecorder.cs"));
        string guard = SourceMethodBody.Extract(recorder, "private H264EncoderDef ResolveContinuousEncoder");

        Assert.Contains("RequiresVideorate", guard, StringComparison.Ordinal);
        Assert.Contains("ProbeWithGStreamer", guard, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>splitmuxsink</c>（<c>libgstmultifile.dll</c>）に依存していないこと。
    /// 分割は C# 側の書き出しパイプラインの作り直しで行う設計で、
    /// うっかり <c>splitmuxsink</c> を使うと同梱配布でだけ壊れる。
    /// </summary>
    [Fact]
    public void TheSplitterIsNotAGStreamerElement()
    {
        Assert.DoesNotContain("splitmuxsink", ContinuousBranch.SegmentWriterPipeline, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "splitmuxsink",
            Build(EventRecordingType.D3d12, "x264enc", false, "5/1", "640x360"),
            StringComparison.Ordinal);
    }
}
