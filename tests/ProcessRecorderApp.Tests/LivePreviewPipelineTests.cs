using System;
using System.Globalization;
using System.IO;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// ライブプレビューの mux パイプライン文字列と、<c>EventRecorder</c> への配線。
///
/// <para>
/// <b>文字列は実測で凍結したもの。</b> <c>fragment-mode=dash-or-mss</c> を落とすと
/// MSE が受け取れない形（1 本の <c>moov</c> で終わる通常の MP4）になり、
/// <c>faststart</c> を足すと EOS まで 1 バイトも出なくなる ── どちらも
/// <b>ブラウザを開くまで気付けない</b>ので、ここで固定する。
/// </para>
/// <para>
/// <b>配線はソーステキストとして見る。</b> 実行で確かめるには本物の GStreamer と
/// 録画スレッドが要り、L1 からは到達できない。ここが縛るのは
/// 「<c>ProcessRecordSample</c> の退避の後に 1 回だけ呼ぶ」と
/// 「<c>CloseCore</c> が閉じる」の 2 点で、どちらも順序を崩すと
/// <b>配信していないときの録画経路の費用</b>や<b>停止時の安全</b>が壊れる。
/// </para>
/// </summary>
public sealed class LivePreviewPipelineTests
{
    private static string RecorderSource
        => File.ReadAllText(RepositoryFiles.At("src", "GStreamer.GstSharpNet", "EventRecorder.cs"));

    private static string StreamSource
        => File.ReadAllText(RepositoryFiles.At("src", "GStreamer.GstSharpNet", "LivePreviewStream.cs"));

    [Theory]
    // fragment ごとに moof+mdat を吐かせる（これが無いと MSE へ渡せる形にならない）。
    [InlineData("fragment-mode=dash-or-mss")]
    [InlineData("fragment-duration=1000")]
    // appsrc は詰まっても録画側を止めない。
    [InlineData("block=false")]
    [InlineData("leaky-type=downstream")]
    [InlineData("format=time")]
    // appsink はクロックに同期させない（枝 1 本の待ちで配信を止めない）。
    [InlineData("sync=false")]
    [InlineData("async=false")]
    // 詰まりの上限。無いと appsrc は無制限にバッファを抱える。
    [InlineData("max-bytes=4194304")]
    // 名前は GetByName で掴む契約そのもの。消すと実行時に NullReference になる。
    [InlineData("name=src")]
    [InlineData("name=sink")]
    [InlineData("name=mux")]
    public void ThePreviewMuxPipelinePinsTheSettingsThatMakeItStreamable(string fragment)
    {
        Assert.Contains(fragment, LivePreviewStream.PreviewMuxPipeline, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>faststart</c> は付けない。<b>付けると EOS まで 1 バイトも出ない</b>
    /// （mp4mux が全体を一時ファイルへ溜めてから書き直す）── ライブ配信では
    /// 「繋がっているのに何も来ない」という無音の失敗になる。
    /// </summary>
    [Fact]
    public void ThePreviewMuxDoesNotUseFaststart()
    {
        Assert.DoesNotContain("faststart", LivePreviewStream.PreviewMuxPipeline, StringComparison.Ordinal);
    }

    /// <summary>
    /// 定数とパイプライン文字列の値が一致すること（<b>どちらを動かしても落ちる</b>）。
    /// </summary>
    [Fact]
    public void TheFragmentDurationConstantMatchesThePipelineString()
    {
        Assert.Contains(
            "fragment-duration=" + LivePreviewStream.FragmentDurationMs.ToString(CultureInfo.InvariantCulture),
            LivePreviewStream.PreviewMuxPipeline,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>録画のサンプル処理から呼ぶのはちょうど 1 回で、退避の後。</b>
    ///
    /// <para>
    /// 2 回呼べば同じフレームが 2 度 mux へ入り、退避より前に呼べば
    /// 「mux 起動時に流し込むリング」と「これから押し込む窓」がずれる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheRecordingPathCallsThePreviewOnceAfterTheEviction()
    {
        string body = SourceMethodBody.Extract(RecorderSource, "private void ProcessRecordSample");

        Assert.Equal(1, CountCode(body, "_live?.OnEncodedSample("));

        int evict = SourceMethodBody.IndexOfCode(body, "_ringBuffer.Evict(");
        int preview = SourceMethodBody.IndexOfCode(body, "_live?.OnEncodedSample(");

        Assert.True(0 <= evict, "ProcessRecordSample に _ringBuffer.Evict( が見つからない。");
        Assert.True(evict < preview,
            "ライブプレビューへの受け渡しがリングの退避より前にある。"
            + "mux 起動時に流し込む窓が、これから押し込む窓とずれる。");
    }

    /// <summary>
    /// <b>停止経路が、枝を静止させてから配信エンジンを閉じること。</b>
    ///
    /// <para>
    /// 閉じないと、宿主のパイプラインを解放した後も自前の mux が残り、購読者は
    /// 永久に待たされる。<b>順序も同じくらい効いている</b> ── quiesce
    /// （sink パイプラインの <c>Null</c> 化＝実行中のコールバックの復帰待ち）より前に
    /// 閉じると、録画スレッドがまだ mux を触っている最中に <c>SetState(Null)</c> と
    /// <c>Dispose</c> を掛けることになり、使用中のネイティブオブジェクトを壊す。
    /// </para>
    /// </summary>
    [Fact]
    public void TheCloseSequenceClosesThePreviewAfterTheBranchIsQuiesced()
    {
        string body = SourceMethodBody.Extract(RecorderSource, "private void CloseCore");

        Assert.True(SourceMethodBody.ContainsCode(body, "Volatile.Write(ref _live, null);"),
            "CloseCore が _live を手放していない。");

        int quiesce = SourceMethodBody.IndexOfCode(body, "quiescing.SetState(State.Null)");
        int waited = SourceMethodBody.IndexOfCode(body, ".Wait(SinkQuiesceTimeoutMs)");
        int closed = SourceMethodBody.IndexOfCode(body, "live?.Close();");

        Assert.True(0 <= quiesce, "CloseCore に sink パイプラインの quiesce が見つからない。");
        Assert.True(0 <= waited, "CloseCore に quiesce の有界待ちが見つからない。");
        Assert.True(0 <= closed, "CloseCore が配信エンジンを閉じていない。");

        Assert.True(quiesce < closed && waited < closed,
            "ライブプレビューを閉じるのが枝の quiesce より前にある。"
            + "録画スレッドがまだ mux を触っている最中に解放すると、"
            + "使用中のネイティブオブジェクトを壊す。");
    }

    /// <summary>
    /// <b>配信エンジンは宿主の状態ロックを知らない。</b> 呼ばれるのは宿主の
    /// ストリーミングスレッドで、宿主の停止経路はそのロックを保持したまま
    /// コールバックの復帰を待つ ── 触った瞬間にデッドロックが成立する。
    /// <c>ContinuousRecorder</c> と同じ規律で、<b>コメントに書くのも不可</b>
    /// （参照が要らない設計であることが読み手に伝わらなくなる）。
    /// </summary>
    [Fact]
    public void ThePreviewEngineNeverNamesTheHostStateLock()
    {
        Assert.DoesNotContain("_stateLock", StreamSource, StringComparison.Ordinal);
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
