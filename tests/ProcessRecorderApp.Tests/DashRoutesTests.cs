using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 配信経路の末尾 1 セグメントの解釈（<see cref="DashRoutes.TryParse"/>）。
///
/// <para>
/// <b>MPD が書く URL とここが受ける名前は同じ正本から出ていること</b>を先に固定する
/// ── 片方だけ動かすと、クライアントが manifest どおりに要求したものが 404 になる
/// （<c>.claude/rules/doc-sync.md</c> の同期テスト一覧）。
/// </para>
/// </summary>
public sealed class DashRoutesTests
{
    /// <summary><c>SegmentTemplate</c> のテンプレートを展開したものは必ず受理される。</summary>
    [Fact]
    public void TheTemplatesFromTheManifestAreTheNamesThatAreAccepted()
    {
        Assert.True(DashRoutes.TryParse(DashManifest.InitializationTemplate, out var initKind, out _));
        Assert.Equal(DashRouteKind.Init, initKind);

        string media = DashManifest.MediaTemplate.Replace("$Time$", "9876543210");
        Assert.True(DashRoutes.TryParse(media, out var mediaKind, out ulong time));
        Assert.Equal(DashRouteKind.Media, mediaKind);
        Assert.Equal(9876543210UL, time);
    }

    [Theory]
    [InlineData("manifest.mpd", DashRouteKind.Manifest, 0UL)]
    [InlineData("init.mp4", DashRouteKind.Init, 0UL)]
    [InlineData("seg-0.m4s", DashRouteKind.Media, 0UL)]
    [InlineData("seg-1.m4s", DashRouteKind.Media, 1UL)]
    // ulong の上限そのものは受理する（溢れるのはその 1 つ上から）。
    [InlineData("seg-18446744073709551615.m4s", DashRouteKind.Media, ulong.MaxValue)]
    public void TheThreeShapesAreAccepted(string file, DashRouteKind expected, ulong expectedTime)
    {
        Assert.True(DashRoutes.TryParse(file, out var kind, out ulong time), file);
        Assert.Equal(expected, kind);
        Assert.Equal(expectedTime, time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("seg-.m4s")]                        // 桁が無い
    [InlineData("seg-+1.m4s")]                      // 符号（ulong.TryParse は既定で通す）
    [InlineData("seg--1.m4s")]
    [InlineData("seg- 1.m4s")]                      // 空白（同上）
    [InlineData("seg-1.m4s2")]                      // 拡張子が違う
    [InlineData("SEG-1.m4s")]                       // 序数比較（大文字小文字を区別する）
    [InlineData("seg-1.M4S")]
    [InlineData("seg-18446744073709551616.m4s")]    // ulong の溢れ
    [InlineData("seg-1234567890123456789012345.m4s")]
    [InlineData("manifest.mpd/")]
    [InlineData("manifest.mpd ")]
    [InlineData("init.mp4/")]
    [InlineData("other.txt")]
    [InlineData("../app.js")]
    public void EverythingElseIsRejected(string file)
    {
        Assert.False(DashRoutes.TryParse(file, out _, out ulong time), file);
        Assert.Equal(0UL, time);
    }
}
