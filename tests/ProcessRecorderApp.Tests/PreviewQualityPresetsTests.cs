using System.Linq;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// ライブ画質プリセットの表と解決規則。
///
/// <para>
/// <b>プリセットは「ソースに対する相対」である。</b> 高さはソースを超えず、幅はソースの
/// 縦横比から導く ── 絶対の 4 値として持つと、16:9 でないソースや実行中に解像度が変わる
/// ウィンドウで縦横比が壊れる。ここで固定するのはその算術そのもので、
/// <b>ブラウザを開くまで気付けない</b>種類の誤りを閉じている。
/// </para>
/// </summary>
public sealed class PreviewQualityPresetsTests
{
    /// <summary>
    /// 表の並びと値。<b>並びはそのまま API とメニューの順になる</b>ので、
    /// 入れ替えると画面の並びが変わる。
    /// </summary>
    [Fact]
    public void TheTableListsTheFourPresetsFromTheLargestToTheSmallest()
    {
        Assert.Equal(
            new[] { "1080p", "720p", "480p", "360p" },
            PreviewQualityPresets.All.Select(p => p.Id));

        Assert.Equal(
            new[] { "1080p", "720p", "480p", "360p" },
            PreviewQualityPresets.All.Select(p => p.Label));

        Assert.Equal(new[] { 1080, 720, 480, 360 }, PreviewQualityPresets.All.Select(p => p.Height));
        Assert.Equal(new[] { 30, 30, 30, 15 }, PreviewQualityPresets.All.Select(p => p.Fps));
        Assert.Equal(new[] { 6000, 3000, 1500, 800 }, PreviewQualityPresets.All.Select(p => p.BitrateKbps));
    }

    /// <summary>
    /// 解決の固定値。<b>ソース未知は 16:9</b>、既知ならソースの縦横比・高さ・fps に従う。
    /// </summary>
    [Theory]
    // ソース未知（まだ 1 枚も届いていない）。16:9 で組む。
    [InlineData(0, 0, 0, "1080p", 1920, 1080, 30)]
    [InlineData(0, 0, 0, "720p", 1280, 720, 30)]
    [InlineData(0, 0, 0, "480p", 854, 480, 30)]
    [InlineData(0, 0, 0, "360p", 640, 360, 15)]
    // 16:10。高さはプリセットどおりで、幅だけがソースの比に従う。
    [InlineData(1920, 1200, 0, "720p", 1152, 720, 30)]
    // 4:3 でソースが小さい。プリセットの高さまで届かないものは全部ソースの大きさになる。
    [InlineData(640, 480, 0, "480p", 640, 480, 30)]
    [InlineData(640, 480, 0, "360p", 480, 360, 15)]
    [InlineData(640, 480, 0, "720p", 640, 480, 30)]
    [InlineData(640, 480, 0, "1080p", 640, 480, 30)]
    // 1366x768（16:9 ではない）。720p は 1280.625 → 最近接の偶数で 1280。
    [InlineData(1366, 768, 0, "720p", 1280, 720, 30)]
    [InlineData(1366, 768, 0, "480p", 854, 480, 30)]
    // 中点。480 × 816/512 = 765.0 ちょうどで 765/2 = 382.5 は大きい方へ ── 766 になる
    // （最近接の偶数へ倒すと 764 になり、幅が 2 px 縮む）。
    [InlineData(816, 512, 0, "480p", 766, 480, 30)]
    // 奇数の高さ。1081 は 1080 へ落としてから比を掛ける
    // ── 1080 × 1921/1081 = 1919.22… で、最近接の偶数は 1920。
    [InlineData(1921, 1081, 30, "1080p", 1920, 1080, 30)]
    // fps はソースを超えない（超える指定はフレームの複製にしかならない）。
    [InlineData(1920, 1080, 30, "360p", 640, 360, 15)]
    [InlineData(1920, 1080, 10, "720p", 1280, 720, 10)]
    // 上下限。ソースが下限より小さくてもエンコーダーが受ける形まで持ち上げる。
    [InlineData(100, 80, 0, "360p", 160, 120, 15)]
    public void ResolveFollowsTheSourceShape(
        int sourceWidth, int sourceHeight, int sourceFps,
        string presetId, int expectedWidth, int expectedHeight, int expectedFps)
    {
        Assert.True(PreviewQualityPresets.TryFind(presetId, out var preset));

        var source = sourceWidth == 0 && sourceHeight == 0 && sourceFps == 0
            ? (PreviewSourceInfo?)null
            : new PreviewSourceInfo(sourceWidth, sourceHeight, sourceFps);

        var quality = PreviewQualityPresets.Resolve(preset, source);

        Assert.Equal(expectedWidth, quality.Width);
        Assert.Equal(expectedHeight, quality.Height);
        Assert.Equal(expectedFps, quality.Fps);

        // ビットレートは縮めない（小さく符号化した方が余裕が出るだけ）。
        Assert.Equal(preset.BitrateKbps, quality.BitrateKbps);
    }

    /// <summary>
    /// <b>0 の source は未知と同じ。</b> caps が読めなかった経路が
    /// <c>default</c> を渡してくるので、null と同じ扱いにしないと 0 除算になる。
    /// </summary>
    [Fact]
    public void AZeroSizedSourceMeansUnknown()
    {
        Assert.True(PreviewQualityPresets.TryFind("720p", out var preset));

        Assert.Equal(
            PreviewQualityPresets.Resolve(preset, null),
            PreviewQualityPresets.Resolve(preset, default(PreviewSourceInfo)));
    }

    /// <summary>ソースが未知なら 4 つとも出す（何が届くか分からない）。</summary>
    [Fact]
    public void AnUnknownSourceOffersEveryPreset()
    {
        Assert.Equal(
            new[] { "1080p", "720p", "480p", "360p" },
            PreviewQualityPresets.Offered(null).Select(p => p.Id));
    }

    /// <summary>ソースより高いプリセットは出さない（拡大しても情報は増えない）。</summary>
    [Fact]
    public void ASourceCapsTheOfferedPresets()
    {
        Assert.Equal(
            new[] { "720p", "480p", "360p" },
            PreviewQualityPresets.Offered(new PreviewSourceInfo(1366, 768, 60)).Select(p => p.Id));
    }

    /// <summary>
    /// <b>境界はソースの高さを偶数へ落としてから見る。</b> 1081 は 1080 になるので
    /// 1080p が残る（残らないと、ちょうど収まるソースで最上位が消える）。
    /// </summary>
    [Fact]
    public void TheOddHeightOfASourceIsRoundedDownBeforeTheComparison()
    {
        Assert.Contains(
            PreviewQualityPresets.Offered(new PreviewSourceInfo(1921, 1081, 30)),
            p => p.Id == "1080p");
    }

    /// <summary>
    /// どのプリセットにも届かないソースでも<b>最小の 1 つは出す</b>
    /// ── 選択肢が空のメニューを出さない。
    /// </summary>
    [Fact]
    public void ATinySourceStillOffersTheSmallestPreset()
    {
        Assert.Equal(
            new[] { "360p" },
            PreviewQualityPresets.Offered(new PreviewSourceInfo(100, 80, 0)).Select(p => p.Id));
    }

    /// <summary>
    /// override が無ければ <c>custom</c>。<b>末尾は必ず <c>custom</c></b> で、
    /// その 4 値はレコーダー設定そのまま（クランプしない）。
    /// </summary>
    [Fact]
    public void BuildStateFallsBackToCustomAndAlwaysOffersIt()
    {
        var custom = new PreviewQuality(1280, 720, 15, 2000);
        var state = PreviewQualityPresets.BuildState(null, null, custom, null, null);

        Assert.Equal(PreviewQualityPresets.Custom, state.Current);
        Assert.Null(state.Source);
        Assert.Null(state.EffectiveId);
        Assert.Null(state.Effective);

        var last = state.Qualities[^1];
        Assert.Equal(PreviewQualityPresets.Custom, last.Id);
        Assert.Equal("Custom", last.Label);
        Assert.Equal(custom, last.Quality);

        Assert.Equal(
            new[] { "1080p", "720p", "480p", "360p", "custom" },
            state.Qualities.Select(q => q.Id));
    }

    /// <summary>
    /// 指示・ソース・実効値はそれぞれ別物。<b>指示を変えても mux を組み直すまでは
    /// 実効値が古いまま</b>なので、姿は 3 つを同時に運ぶ。
    /// </summary>
    [Fact]
    public void BuildStateCarriesTheSourceAndTheRunningQualitySeparately()
    {
        var source = new PreviewSourceInfo(1366, 768, 30);
        var effective = new PreviewQuality(1280, 720, 15, 2000);
        var state = PreviewQualityPresets.BuildState(
            "360p", source, new PreviewQuality(1280, 720, 15, 2000),
            PreviewQualityPresets.Custom, effective);

        Assert.Equal("360p", state.Current);
        Assert.Equal(source, state.Source);
        Assert.Equal(PreviewQualityPresets.Custom, state.EffectiveId);
        Assert.Equal(effective, state.Effective);

        // 選択肢はソースで絞られる（1080p は出ない）。
        Assert.Equal(
            new[] { "720p", "480p", "360p", "custom" },
            state.Qualities.Select(q => q.Id));
    }

    /// <summary>
    /// 経路が受け付ける id は<b>表の 4 つと <c>custom</c> だけ</b>
    /// （供給側は検査済みの値しか受け取らない）。
    /// </summary>
    [Theory]
    [InlineData("1080p", true)]
    [InlineData("720p", true)]
    [InlineData("480p", true)]
    [InlineData("360p", true)]
    [InlineData("custom", true)]
    [InlineData("Custom", false)]
    [InlineData("1080P", false)]
    [InlineData("bogus", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidIdAcceptsOnlyTheTableAndCustom(string? id, bool expected)
    {
        Assert.Equal(expected, PreviewQualityPresets.IsValidId(id));
    }
}
