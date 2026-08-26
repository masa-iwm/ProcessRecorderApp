using ProcessRecorderApp.GStreamer;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
/// <b>false 側の文字列は 1 文字も動かさない。</b> <c>FragmentedOutput</c> を切った録画物の
/// バイト列がこれで決まっている。
/// </para>
/// </summary>
public sealed class RecordingSrcPipelineTests
{
    /// <summary>非 fragmented（<c>FragmentedOutput=false</c>）の文字列そのもの。</summary>
    private const string LegacySrcPipeline =
        "appsrc format=time name=src ! h264parse ! mp4mux faststart=true name=mux ! filesink name=file";

    [Fact]
    public void TheNonFragmentedPipelineIsUnchanged()
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
    /// <b>fragmented 側の <c>filesink</c> は受け取ったバッファを溜めない。</b>
    ///
    /// <para>
    /// 既定の <c>filesink</c> は <c>buffer-size</c>（既定 65536）に届いてから 1 度に書くので、
    /// mux が 1 秒ごとに fragment を出しても
    /// <b>他のプロセスから見えるファイル長は 64 KiB 溜まるまで伸びない</b> ──
    /// ブラウザの追いかけ再生はそのぶんデータ切れになり、強制終了では末尾が失われる。
    /// <b>この違いは録画物のバイト列には現れない</b>ので、
    /// 完成したファイルを検分するどのテストでも検出できない。
    /// </para>
    /// <para>
    /// 非 fragmented 側には付けない ── 書き込み中に読む相手が居らず
    /// （<c>faststart=true</c> なので途中では 0 バイト）、
    /// 1 バイトも文字列を動かさないという約束の方が優先する
    /// （<see cref="TheNonFragmentedPipelineIsUnchanged"/> が完全一致で縛っている）。
    /// </para>
    /// </summary>
    [Fact]
    public void OnlyTheFragmentedFormWritesWithoutTheFilesinkBuffer()
    {
        Assert.Contains(
            "filesink name=file buffer-mode=unbuffered",
            EventRecorder.BuildSrcPipeline(fragmented: true),
            StringComparison.Ordinal);
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
    /// <b>既定は true</b>（録画中・強制終了後でも読めるファイルを既定にする）。
    /// アプリ全体の設定 <c>AppSettings.FragmentedOutput</c> の static ミラーで、
    /// 何も読み込まれていない状態がこの値である ──
    /// <b><c>AppSettings.FragmentedOutput</c> の初期化子と揃っていること</b>。
    /// </summary>
    [Fact]
    public void TheStaticMirrorDefaultsToOn()
    {
        Assert.True(EventRecorder.FragmentedOutput);
    }

    /// <summary>
    /// アプリ設定側の既定も <c>true</c> であること。
    ///
    /// <para>
    /// <c>AppSettings</c> は WinUI アプリのプロジェクトにあり L1 からは参照できないので、
    /// <b>ソースをテキストとして</b>突き合わせる。ここが食い違うと、settings.json に
    /// キーが無い利用者と、何も読み込んでいない状態とで書き方が変わる。
    /// </para>
    /// <para>
    /// <b>読むのは宣言の初期化子そのもの</b>（<c>= true</c> / <c>= false</c> を捕捉して
    /// <see cref="EventRecorder.FragmentedOutput"/> と突き合わせる）で、
    /// <b>コメント行は除く</b> ── 素の部分一致だと、その宣言を説明したコメントに
    /// 同じ文字列を書くだけで緑になる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheAppSettingDefaultMatchesTheStaticMirror()
    {
        string source = File.ReadAllText(
            RepositoryFiles.At("src", "ProcessRecorderApp", "Settings", "AppSettings.cs"));

        var declarations = FragmentedOutputDeclarationRegex.Matches(source)
            .Where(m => !SourceReferences.IsCommentLine(source, m.Index))
            .ToArray();

        // 走査が壊れると 0 件になり「見つからないので通る」に倒れる。件数そのものを縛る。
        Assert.True(declarations.Length == 1,
            $"AppSettings.FragmentedOutput の宣言が {declarations.Length} 件見つかりました"
            + "（走査が壊れているか、宣言の書き方が変わっています）。");

        Assert.Equal(
            EventRecorder.FragmentedOutput ? "true" : "false",
            declarations[0].Groups[1].Value);
    }

    /// <summary>
    /// <c>AppSettings.FragmentedOutput</c> の自動プロパティ宣言と、その初期化子の値。
    /// <b>行頭に錨を打たない</b> ── 打つと一致位置が必ず行頭になり、
    /// コメント行の除外が「常に偽」へ倒れて何も守らなくなる。
    /// </summary>
    private static readonly Regex FragmentedOutputDeclarationRegex = new(
        @"public\s+(?:partial\s+)?bool\s+FragmentedOutput\s*\{\s*get;\s*set;\s*\}\s*=\s*(true|false)\s*;",
        RegexOptions.Compiled);

    /// <summary>
    /// <c>FragmentedOutput</c> は<b>レコーダーごとの設定ではない</b> ──
    /// 再初期化の助言の一覧に載せる対象でもない（アプリ全体の設定なので、
    /// レコーダー設定の PATCH では現れない）。
    /// </summary>
    [Fact]
    public void FragmentedOutputIsNotARecorderSetting()
    {
        Assert.DoesNotContain(
            "FragmentedOutput",
            EventRecorderSettings.PropertiesRequiringReinitialize,
            StringComparer.Ordinal);
    }
}
