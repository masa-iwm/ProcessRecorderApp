using ProcessRecorderApp.GStreamer;
using System;
using System.Globalization;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 録画（src 側）パイプライン文字列（<see cref="EventRecorder.BuildSrcPipeline"/>）。
///
/// <para>
/// <b><c>faststart</c> と <c>fragment-mode</c> は排他である。</b> <c>faststart=true</c> は
/// EOS のあとにファイル全体を書き直すので、書き込み中の <c>filesink</c> の出力先は
/// 0 バイトのまま ── fragment を出させておいて faststart を付けると、
/// 「録画中でも読める」という目的そのものが消える。どちらの取り違えも
/// <b>録って再生してみるまで気付けない</b>ので、ここで固定する。
/// </para>
/// <para>
/// <b>false 側の文字列は 1 文字も動かさない。</b> 既定の録画物のバイト列がこれで決まっている。
/// </para>
/// </summary>
public sealed class RecordingSrcPipelineTests
{
    /// <summary>既定（<c>FragmentedOutput=false</c>）の文字列そのもの。</summary>
    private const string LegacySrcPipeline =
        "appsrc format=time name=src ! h264parse ! mp4mux faststart=true name=mux ! filesink name=file";

    [Fact]
    public void TheDefaultPipelineIsUnchanged()
    {
        Assert.Equal(LegacySrcPipeline, EventRecorder.BuildSrcPipeline(fragmented: false));
    }

    [Fact]
    public void FaststartAndFragmentModeAreExclusive()
    {
        string plain = EventRecorder.BuildSrcPipeline(fragmented: false);
        string fragmented = EventRecorder.BuildSrcPipeline(fragmented: true);

        Assert.Contains("faststart=true", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment-mode", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment-duration", plain, StringComparison.Ordinal);

        Assert.Contains("fragment-mode=dash-or-mss", fragmented, StringComparison.Ordinal);
        Assert.DoesNotContain("faststart", fragmented, StringComparison.Ordinal);
    }

    /// <summary>
    /// 定数と文字列の値が一致すること（<b>どちらを動かしても落ちる</b>）。
    /// </summary>
    [Fact]
    public void TheFragmentDurationConstantMatchesThePipelineString()
    {
        Assert.Contains(
            "fragment-duration=" + EventRecorder.FragmentDurationMs.ToString(CultureInfo.InvariantCulture),
            EventRecorder.BuildSrcPipeline(fragmented: true),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 名前は <c>GetByName</c> で掴む契約そのもの（消すと実行時に NullReference になる）。
    /// 前段も両方で同じでなければならない ── 変えてよいのは mux の書き方だけである。
    /// </summary>
    [Theory]
    [InlineData("appsrc format=time name=src")]
    [InlineData("h264parse")]
    [InlineData("name=mux")]
    [InlineData("filesink name=file")]
    public void BothFormsKeepTheSameElements(string fragment)
    {
        Assert.Contains(fragment, EventRecorder.BuildSrcPipeline(fragmented: false), StringComparison.Ordinal);
        Assert.Contains(fragment, EventRecorder.BuildSrcPipeline(fragmented: true), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>FragmentedOutput</c> は src パイプラインの文字列そのものなので、
    /// 動いているパイプラインへ後から効かせる道が無い。
    /// </summary>
    [Fact]
    public void FragmentedOutputRequiresReinitialize()
    {
        Assert.Contains(
            nameof(EventRecorderSettings.FragmentedOutput),
            EventRecorderSettings.PropertiesRequiringReinitialize,
            StringComparer.Ordinal);
    }

    /// <summary>既定は false（既存の録画物と挙動を変えない）。</summary>
    [Fact]
    public void TheDefaultIsOff()
    {
        Assert.False(new EventRecorderSettings().FragmentedOutput);
    }
}
