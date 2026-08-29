using System;
using System.IO;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <c>GET /api/recording-transcode/{*path}</c> の<b>検査の順序</b>をソーステキストで固定する。
///
/// <para>
/// <b>順序そのものが API の約束である。</b> 同じ要求が「綴りの誤り（400）」「ファイルが無い
/// （404）」「この PC ではできない（404 <c>transcode unavailable</c>）」「枠が無い（409）」の
/// どれで断られるかは、検査の並びだけで決まる ── 能力の判定を前へ出すと、
/// ハードウェア デコーダーの無い開発機では<b>クエリの誤りが一切表に出なくなり</b>、
/// E2E の 400 系の検査が「たまたま 404 が先に返っていただけ」で通ってしまう。
/// </para>
/// <para>
/// 照合は<b>この経路の登録から下だけ</b>を見る（同じファイルの他の経路にも
/// <c>OpenRequestedAsync</c> や <c>InProgress</c> が現れるため）。
/// 判定は L1 では実行できない ── ハンドラーは <c>WebApplication</c> と
/// 実際の録画ファイルを要求するので、動かせるのは E2E だけである。
/// </para>
/// </summary>
public sealed class RecordingTranscodeEndpointTests
{
    /// <summary>この経路の登録行（ここから下だけを見る）。</summary>
    private const string RouteMarker = "\"/api/recording-transcode/{*path}\"";

    /// <summary>
    /// 現れなければならない順序。<b>クエリ（相手の手元だけで直せる失敗）が先で、
    /// 実機の都合（能力・枠）が後</b>である。
    /// </summary>
    private static readonly string[] ExpectedOrder =
    [
        "\"invalid start\"",
        "\"unknown transcode quality\"",
        "\"invalid session\"",
        "OpenRequestedAsync(ctx, root)",
        "Snapshot(stream).InProgress",
        "GetCapabilitiesAsync",
        "OpenTranscodeAsync",
    ];

    private static string RouteSource()
    {
        string text = File.ReadAllText(
            RepositoryFiles.At("src", "RemoteControl", "Endpoints", "RecordingEndpoints.cs"));

        int start = text.IndexOf(RouteMarker, StringComparison.Ordinal);
        Assert.True(0 <= start,
            $"RecordingEndpoints.cs に {RouteMarker} が無い（経路を改名した？）。");

        return text[start..];
    }

    [Fact]
    public void TheTranscodeRouteChecksItsQueryBeforeTheFileAndTheCapability()
    {
        string source = RouteSource();
        int previous = -1;
        string previousToken = RouteMarker;

        foreach (string token in ExpectedOrder)
        {
            int at = source.IndexOf(token, StringComparison.Ordinal);

            Assert.True(0 <= at,
                $"{RouteMarker} の経路に `{token}` が見当たらない。"
                + "検査を消したか、書き方を変えた（順序の検査そのものが効かなくなる）。");

            Assert.True(previous < at,
                $"`{token}` が `{previousToken}` より前に出ている。"
                + "検査の順序は API の約束なので、並びを変えるなら src/README.md の"
                + "「録画トランスコード」節と E2E も対で直すこと。");

            previous = at;
            previousToken = token;
        }
    }

    /// <summary>
    /// <b>ブロッキングする読み取りをスレッドプールへ逃がしていること。</b>
    /// <c>TranscodeReader.TryRead</c> は呼び手のスレッドを最大 1 秒塞ぐので、
    /// 直に <c>await</c> の無いまま呼ぶと同時視聴者の数だけ HTTP のスレッドが止まる。
    /// </summary>
    [Fact]
    public void TheReaderIsPumpedFromTheThreadPool()
    {
        string text = File.ReadAllText(
            RepositoryFiles.At("src", "RemoteControl", "Endpoints", "RecordingEndpoints.cs"));

        Assert.Contains("Task.Run(() => reader.TryRead(", text, StringComparison.Ordinal);
    }
}
