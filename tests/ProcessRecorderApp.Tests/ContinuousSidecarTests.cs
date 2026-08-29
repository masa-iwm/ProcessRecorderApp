using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 常時録画のセグメントに置く sidecar の<b>値の出所</b>。
///
/// <para>
/// 常時枝は <c>ContinuousFramerate</c> / <c>ContinuousResolution</c> で本線とは別の
/// レート・別の解像度で回る。本線の観測値（<c>EventRecorder._capsWidth</c> 等）は
/// <b>イベント録画の枝でしか確定しない</b>ので、流用すると
/// (1) イベント録画を 1 度もしていないプロセスでは空になり、
/// (2) していても<b>そのファイルには当たらない値</b>が載る。
/// 変換（<c>PreviewQualityPresets.Resolve</c>）はこの値でプリセットを解決するので、
/// 空や別物だとセグメントが変換できない。
/// </para>
/// <para>
/// <b>ソーステキストとして見る。</b> 値を実際に読むには本物の GStreamer と
/// 走行中の常時枝が要り、L1 からは到達できない。実体の検査は E2E
/// （<c>TranscodeTests.AContinuousSegment_CarriesTheBranchShapeAndTranscodes</c>）が行う。
/// </para>
/// </summary>
public sealed class ContinuousSidecarTests
{
    private static string RecorderSource
        => File.ReadAllText(RepositoryFiles.At("src", "GStreamer.GstSharpNet", "EventRecorder.cs"));

    private static string EngineSource
        => File.ReadAllText(RepositoryFiles.At("src", "GStreamer.GstSharpNet", "ContinuousRecorder.cs"));

    /// <summary>
    /// <b>形はセグメントを開いた時点の negotiate 済み caps から採る。</b>
    /// そこが常時枝の実体（別レート・別解像度）を読める唯一の場所である。
    /// </summary>
    [Fact]
    public void TheSegmentShapeComesFromTheNegotiatedCaps()
    {
        Assert.Contains(
            "writer.Shape = ContinuousSegmentShape.From(negotiated);",
            EngineSource,
            StringComparison.Ordinal);

        Assert.Contains(
            "_host.OnContinuousSegmentFinalized(path, result == \"ok\", writer.Shape);",
            EngineSource,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>形はセグメントごとに持つ。</b> 確定は非同期で次のセグメントと重なるので、
    /// エンジン側の 1 つのフィールドに置くと取り違える。
    /// </summary>
    [Fact]
    public void TheShapeIsHeldPerSegment()
        => Assert.Contains("public ContinuousSegmentShape Shape { get; set; }", EngineSource, StringComparison.Ordinal);

    /// <summary>
    /// <b>宿主は枝が報せた形をそのまま sidecar へ渡す</b>（本線の観測値を流用しない）。
    /// </summary>
    [Fact]
    public void TheHostWritesTheBranchShapeAndNotTheMainlineCaps()
    {
        Assert.Contains(
            "shape.Width, shape.Height, shape.Fps);",
            RecorderSource,
            StringComparison.Ordinal);

        int finalized = RecorderSource.IndexOf(
            "public void OnContinuousSegmentFinalized(", StringComparison.Ordinal);
        Assert.True(0 <= finalized, "OnContinuousSegmentFinalized が見つかりません。");

        int end = RecorderSource.IndexOf(
            "public void OnContinuousError(", finalized, StringComparison.Ordinal);
        Assert.True(0 <= end, "OnContinuousError が見つかりません。");

        // コメント行は落とす（説明文の `_caps*` で検査が落ちてしまう）。
        string body = Regex.Replace(
            RecorderSource[finalized..end], @"^\s*//.*$", "", RegexOptions.Multiline);

        Assert.DoesNotContain("_caps", body, StringComparison.Ordinal);
    }
}
