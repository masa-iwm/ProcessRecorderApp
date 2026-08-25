using System;
using System.Globalization;
using System.IO;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// DASH プレビューの第 2 パイプライン文字列と、<c>EventRecorder</c> への配線。
///
/// <para>
/// <b>文字列は同梱ランタイム（1.28.6）で通したものを凍結してある。</b>
/// <c>fragment-mode=dash-or-mss</c> を落とすと fragment が出ず、<c>faststart</c> を足すと
/// EOS まで 1 バイトも出ない ── どちらも<b>ブラウザを開くまで気付けない</b>。
/// </para>
/// <para>
/// <b>配線はソーステキストとして見る。</b> 実行で確かめるには本物の GStreamer と
/// 録画スレッドが要り、L1 からは到達できない。ここが縛るのは
/// 「枝A のドレインで 1 回だけ・<c>OnPreview</c> の直前に呼ぶ」と
/// 「<c>CloseCore</c> が枝を静止させてから閉じる」の 2 点である。
/// </para>
/// </summary>
public sealed class DashPreviewPipelineTests
{
    private static string RecorderSource
        => File.ReadAllText(RepositoryFiles.At("src", "GStreamer.GstSharpNet", "EventRecorder.cs"));

    private static string StreamSource
        => File.ReadAllText(RepositoryFiles.At("src", "GStreamer.GstSharpNet", "DashPreviewStream.cs"));

    private static string Pipeline
        => DashPreviewStream.BuildPipeline(1280, 720, 15, "mfh264enc bitrate=2000 gop-size=15 low-latency=true");

    [Theory]
    // 生フレームは 1 枚が MB 級。上限は枚数で持つ（バイト上限では解像度で枚数が変わる）。
    [InlineData("appsrc name=src")]
    [InlineData("format=time")]
    [InlineData("block=false")]
    [InlineData("max-buffers=2")]
    [InlineData("max-bytes=0")]
    [InlineData("leaky-type=downstream")]
    // fps を落とすのは間引きだけ（増やして複製すると、エンコーダーに無駄な仕事をさせる）。
    [InlineData("videorate drop-only=true")]
    [InlineData("videoscale")]
    [InlineData("videoconvert")]
    // 縮小後の形は capsfilter で確定させる（ここを外すと元の解像度のまま符号化される）。
    [InlineData("video/x-raw,width=1280,height=720,framerate=15/1,pixel-aspect-ratio=1/1")]
    // セグメントは途中から取得される。SPS/PPS を各 IDR へ付けないと復帰できない。
    [InlineData("h264parse config-interval=-1")]
    // fragment ごとに moof+mdat を吐かせる（これが無いと DASH のセグメントにできない）。
    [InlineData("mp4mux name=mux")]
    [InlineData("fragment-duration=1000")]
    [InlineData("fragment-mode=dash-or-mss")]
    // appsink はクロックに同期させない（枝 1 本の待ちで配信を止めない）。
    [InlineData("appsink name=sink")]
    [InlineData("sync=false")]
    [InlineData("async=false")]
    public void ThePipelinePinsTheSettingsThatMakeItStreamable(string fragment)
    {
        Assert.Contains(fragment, Pipeline, StringComparison.Ordinal);
    }

    /// <summary>エンコーダーの定義はそのまま鎖の中へ入る（加工しない）。</summary>
    [Fact]
    public void TheEncoderLaunchStringIsInterpolatedVerbatim()
    {
        Assert.Contains(
            "videoconvert ! x264enc tune=zerolatency bitrate=1234 ! h264parse",
            DashPreviewStream.BuildPipeline(640, 360, 10, "x264enc tune=zerolatency bitrate=1234"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>faststart</c> は付けない。<b>付けると EOS まで 1 バイトも出ない</b>
    /// （mp4mux が全体を一時ファイルへ溜めてから書き直す）── ライブ配信では
    /// 「つながっているのに何も来ない」という無音の失敗になる。
    /// </summary>
    [Fact]
    public void ThePipelineDoesNotUseFaststart()
    {
        Assert.DoesNotContain("faststart", Pipeline, StringComparison.Ordinal);
    }

    /// <summary>
    /// 定数とパイプライン文字列の値が一致すること（<b>どちらを動かしても落ちる</b>）。
    /// </summary>
    [Fact]
    public void TheFragmentDurationConstantMatchesThePipelineString()
    {
        Assert.Contains(
            "fragment-duration=" + DashPreviewStream.FragmentDurationMs.ToString(CultureInfo.InvariantCulture),
            Pipeline,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>枝A のドレインから呼ぶのはちょうど 1 回で、<c>OnPreview</c> の直前。</b>
    ///
    /// <para>
    /// 2 回呼べば同じフレームが 2 度押し込まれ、<c>OnPreview</c> より後に置くと
    /// 画面表示側の購読が何をしたかに配信が左右される（サンプルは
    /// <c>using</c> を抜けた時点で破棄されるので、外へ出すと解放済みを触る）。
    /// </para>
    /// </summary>
    [Fact]
    public void ThePreviewBranchFeedsTheDashEngineOnceBeforeTheUiPreview()
    {
        string body = SourceMethodBody.Extract(RecorderSource, "private void InitializeWith");

        Assert.Equal(1, CountCode(body, "_dash?.OnRawSample(sample);"));

        int dash = SourceMethodBody.IndexOfCode(body, "_dash?.OnRawSample(sample);");
        int preview = SourceMethodBody.IndexOfCode(body, "OnPreview(sample);");

        Assert.True(0 <= dash, "枝A のドレインに _dash?.OnRawSample( が見つからない。");
        Assert.True(0 <= preview, "枝A のドレインに OnPreview( が見つからない。");
        Assert.True(dash < preview, "DASH への受け渡しが OnPreview より後にある。");
    }

    /// <summary>
    /// <b>停止経路が、枝を静止させてから配信エンジンを閉じること。</b>
    ///
    /// <para>
    /// quiesce（sink パイプラインの <c>Null</c> 化＝実行中のコールバックの復帰待ち）より前に
    /// 閉じると、枝A のスレッドがまだ第 2 パイプラインを触っている最中に
    /// <c>SetState(Null)</c> と <c>Dispose</c> を掛けることになる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheCloseSequenceClosesTheDashEngineAfterTheBranchIsQuiesced()
    {
        string body = SourceMethodBody.Extract(RecorderSource, "private void CloseCore");

        Assert.True(SourceMethodBody.ContainsCode(body, "Volatile.Write(ref _dash, null);"),
            "CloseCore が _dash を手放していない。");

        int quiesce = SourceMethodBody.IndexOfCode(body, "quiescing.SetState(State.Null)");
        int live = SourceMethodBody.IndexOfCode(body, "live?.Close();");
        int dash = SourceMethodBody.IndexOfCode(body, "dash?.Close();");

        Assert.True(0 <= quiesce, "CloseCore に sink パイプラインの quiesce が見つからない。");
        Assert.True(0 <= live, "CloseCore がライブプレビューを閉じていない。");
        Assert.True(0 <= dash, "CloseCore が DASH の配信エンジンを閉じていない。");

        Assert.True(quiesce < dash,
            "DASH の配信エンジンを閉じるのが枝の quiesce より前にある。"
            + "枝A のスレッドがまだ第 2 パイプラインを触っている最中に解放すると、"
            + "使用中のネイティブオブジェクトを壊す。");
        Assert.True(live < dash, "配信エンジンを閉じる順序が live → dash になっていない。");
    }

    /// <summary>
    /// <b>配信エンジンは宿主の状態ロックを知らない。</b> 呼ばれるのは宿主の
    /// ストリーミングスレッドで、宿主の停止経路はそのロックを保持したまま
    /// コールバックの復帰を待つ ── 触った瞬間にデッドロックが成立する。
    /// <b>コメントに書くのも不可</b>（参照が要らない設計であることが伝わらなくなる）。
    /// </summary>
    [Fact]
    public void TheDashEngineNeverNamesTheHostStateLock()
    {
        Assert.DoesNotContain("_stateLock", StreamSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b><c>_muxLock</c> は待たずに取る。</b> 取るのは録画と同じストリーミングスレッドなので、
    /// <c>lock</c> で待つと <b>1 枚の遅れが録画そのものの遅れになる</b>
    /// ── 取れないときはサンプルを落として降りるのが正しい。
    /// </summary>
    [Fact]
    public void TheMuxLockIsOnlyEverTakenWithTryEnter()
    {
        Assert.DoesNotContain("lock (_muxLock)", StreamSource, StringComparison.Ordinal);
        Assert.Contains("Monitor.TryEnter(_muxLock, ref entered)", StreamSource, StringComparison.Ordinal);
        Assert.Contains("Monitor.TryEnter(_muxLock, CallbackExitTimeoutMs, ref entered)",
            StreamSource, StringComparison.Ordinal);
    }

    /// <summary>コメント行を除いた出現回数。</summary>
    private static int CountCode(string body, string needle)
    {
        int count = 0;
        for (int i = body.IndexOf(needle, StringComparison.Ordinal); 0 <= i;
             i = body.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            if (!SourceReferences.IsCommentLine(body, i))
                count++;
        }
        return count;
    }
}
