using System;
using System.IO;
using System.Text.RegularExpressions;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 録画トランスコードのパイプライン文字列と、失敗を読める形にするための 2 点
/// （位置指定の早期脱出・バスの debug 保持）。
///
/// <para>
/// <b>診断はソーステキストとして見る。</b> 実行で確かめるには本物の GStreamer と
/// 壊れた入力が要り、L1 からは到達できない ── ここが縛るのは
/// 「<c>SeekToStart</c> がループ内で破綻を見る」と
/// 「<c>OnBusMessage</c> が <c>ParseError</c> の debug を捨てない」の 2 点である。
/// </para>
/// </summary>
public sealed class TranscodeSessionTests
{
    private static string SessionSource
        => File.ReadAllText(RepositoryFiles.At("src", "GStreamer.GstSharpNet", "TranscodeSession.cs"));

    private static string Pipeline
        => TranscodeSession.BuildPipeline(
            @"C:\rec\a b.mp4", 1280, 720, 30, "openh264dec", "x264enc bitrate=3000 key-int-max=30");

    [Theory]
    // 位置指定を直接送る相手（パイプラインへ送ると mp4mux まで遡って 0 バイトで固まる）。
    [InlineData("qtdemux name=demux")]
    // 間引きだけ（複製はしない）。
    [InlineData("videorate drop-only=true")]
    [InlineData("videoscale")]
    [InlineData("videoconvert")]
    // セグメントは途中から取得される。SPS/PPS を各 IDR へ付けないと復帰できない。
    [InlineData("h264parse config-interval=-1")]
    // fragment が出る形（faststart を足すと EOS まで 1 バイトも出ない）。
    [InlineData("mp4mux name=mux fragment-duration=1000 fragment-mode=dash-or-mss")]
    // 捨てない appsink（引き手が遅ければ上流を止める）。
    [InlineData("appsink name=sink sync=false async=false max-buffers=16 drop=false")]
    public void ThePipelineKeepsItsFrozenTokens(string token)
        => Assert.Contains(token, Pipeline, StringComparison.Ordinal);

    /// <summary>
    /// <b>出力の fps は上限で書く。</b> <c>videorate drop-only=true</c> は落とすことしか
    /// できないので、<c>framerate=30/1</c> と固定すると実 fps がそれ未満のファイル
    /// （sidecar の無い本・実 fps が <c>89/3</c> のカメラ）が変換できない ──
    /// しかも失敗は capsfilter ではなく <c>qtdemux</c> の
    /// <c>Internal data stream error.</c>（debug <c>reason not-linked (-1)</c>）に出る。
    /// </summary>
    [Fact]
    public void TheOutputFramerateIsAnUpperBound()
    {
        Assert.Contains(
            "video/x-raw,width=1280,height=720,framerate=[1/1,30/1],pixel-aspect-ratio=1/1",
            Pipeline,
            StringComparison.Ordinal);

        Assert.DoesNotContain("framerate=30/1", Pipeline, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>録画パスは二重引用符で囲み、区切りを <c>/</c> へ直す</b>（録画パスに空白が入りうる）。
    /// </summary>
    [Fact]
    public void TheSourcePathIsQuotedAndUsesForwardSlashes()
        => Assert.Contains("filesrc location=\"C:/rec/a b.mp4\"", Pipeline, StringComparison.Ordinal);

    /// <summary>
    /// <b><c>SeekToStart</c> は破綻を見て降りる。</b> preroll しないまま壊れたパイプラインは
    /// seek を永久に受理しないので、見ないと <c>SeekTimeoutMs</c>（5 秒）を回り切ったうえ、
    /// バスが既に報せている真因の代わりに「位置指定が受理されなかった」だけが残る。
    /// </summary>
    [Fact]
    public void TheSeekLoopLeavesOnAFailure()
    {
        string body = MethodBody(SessionSource, "private bool SeekToStart(Element demux)");

        Assert.Contains("_error is { } failure", body, StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b><c>OnBusMessage</c> は <c>ParseError</c> の debug を捨てない。</b>
    /// <c>qtdemux</c> の <c>Internal data stream error.</c> は message だけ読んでも
    /// 原因が分からず、debug の <c>streaming stopped, reason not-linked (-1)</c> で
    /// 初めてリンクの失敗と分かる。<b>発信元は <c>src=</c> で出し、<c>encoder=</c> は
    /// 出さない</b> ── 失敗の発信元はエンコーダーとは限らない（実測では <c>qtdemux</c>）。
    /// </summary>
    [Fact]
    public void TheBusHandlerKeepsTheDebugString()
    {
        string body = MethodBody(SessionSource, "private void OnBusMessage(Message message)");

        Assert.Contains("var (gerror, debug) = message.ParseError();", body, StringComparison.Ordinal);
        Assert.Contains("$\"{gerror.Message}; {debug}\"", body, StringComparison.Ordinal);
        Assert.Contains("src='{source}' detail='{detail}'", body, StringComparison.Ordinal);
        Assert.DoesNotContain("encoder='", body, StringComparison.Ordinal);

        // 破綻の内容には detail を丸ごと入れる（start-failed の detail= がそのまま真因になる）。
        Assert.Contains("_error ??= detail;", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="signature"/> から次の <c>private</c> / <c>internal</c> / <c>public</c>
    /// メンバーの手前までを切り出す。<b>コメント行は落とす</b>
    /// ── 説明文に書いた語で検査が通ってしまうのを防ぐ。
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(0 <= start, $"'{signature}' が見つかりません。");

        int end = source.IndexOf("\n    /// <summary>", start + signature.Length, StringComparison.Ordinal);
        string body = end < 0 ? source[start..] : source[start..end];

        return Regex.Replace(body, @"^\s*//.*$", "", RegexOptions.Multiline);
    }
}
