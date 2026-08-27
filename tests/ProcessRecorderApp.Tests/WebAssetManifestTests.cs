using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 埋め込み Web UI の台帳（<c>WebAssets.Manifest</c>）と <c>src/RemoteControl/wwwroot/</c> の
/// 実ファイルの一致。
///
/// <para>
/// <b>L1 は <c>RemoteControl</c> を参照できない</b>（参照した瞬間に ASP.NET Core の共有
/// フレームワークがテストホストへ降りる ── <c>RemoteControlIsolationTests</c> の③）ので、
/// ここはソースをテキストとして読む。<b>双方向で検査する</b>のが要点で、
/// 台帳に足してファイルを置き忘れれば起動時の型初期化で落ち、
/// ファイルを置いて台帳に足し忘れれば黙って 404 になる ── どちらも
/// 発行してブラウザを開くまで気付けない。
/// </para>
/// <para>
/// <b>第三者 JS がゼロであることもここで固定する。</b> LAN のブラウザへ配る資産で、
/// 外部から取ってくるものが 1 つでもあると、リポジトリの
/// <c>THIRD-PARTY-NOTICES.md</c> と実際の配布物が食い違う。
/// </para>
/// </summary>
public sealed class WebAssetManifestTests
{
    private static string WebRootDirectory => RepositoryFiles.At("src", "RemoteControl", "wwwroot");

    private static string WebAssetsSource
        => File.ReadAllText(RepositoryFiles.At("src", "RemoteControl", "WebAssets.cs"));

    private static string ProjectFile
        => File.ReadAllText(RepositoryFiles.At("src", "RemoteControl", "RemoteControl.csproj"));

    /// <summary><c>["index.html"] = "text/html; charset=utf-8",</c> の名前の側。</summary>
    private static readonly Regex ManifestEntryRegex =
        new(@"^\s*\[""(?<name>[^""]+)""\]\s*=\s*""", RegexOptions.Multiline | RegexOptions.Compiled);

    private static string[] ManifestNames()
        => [.. ManifestEntryRegex.Matches(WebAssetsSource)
                .Select(m => m.Groups["name"].Value)
                .OrderBy(n => n, StringComparer.Ordinal)];

    private static string[] DiskNames()
        => [.. Directory.EnumerateFiles(WebRootDirectory)
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)!];

    /// <summary>
    /// <c>wwwroot</c> の JavaScript を<b>全部</b>つないだテキスト。
    ///
    /// <para>
    /// 資産は 5 本に分かれているので、1 本だけを読む検査は「他の 4 本は無検査」になる
    /// ── 定数や文字列が別のファイルへ移った日に、違反ではなく<b>検査の消失</b>が起こる。
    /// 行の構造は保つ（コメント行の除外が行単位で効く）。
    /// </para>
    /// </summary>
    private static string AllScripts()
        => string.Join('\n', Directory.EnumerateFiles(WebRootDirectory, "*.js")
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(File.ReadAllText));

    [Fact]
    public void TheManifestAndTheFilesOnDiskAgree()
    {
        string[] manifest = ManifestNames();

        // 空振りで緑にしない ── 正規表現が合わなくなったら、
        // 「違反 0 件」ではなく「検査が消えた」ことに気付けるようにする。
        Assert.True(7 <= manifest.Length,
            $"WebAssets.cs から取り出せた名前が {manifest.Length} 件しかない"
            + "（index.html + app.css + JavaScript 5 本）。"
            + "台帳の書き方を変えたなら ManifestEntryRegex も一緒に直すこと。");

        Assert.Equal(DiskNames(), manifest);
    }

    [Fact]
    public void TheWebRootHasNoSubdirectories()
    {
        // 論理名は wwwroot 直下を前提にしている（LogicalName の RecursiveDir が空）。
        // 階層を作ると、名前 1 セグメントで引く WebAssets.TryGet からは引けない。
        Assert.Empty(Directory.EnumerateDirectories(WebRootDirectory));
    }

    [Fact]
    public void TheProjectEmbedsTheWebRootWithFixedLogicalNames()
    {
        Assert.Contains(@"EmbeddedResource Include=""wwwroot\**\*""", ProjectFile, StringComparison.Ordinal);

        // LogicalName を `/` 区切りで固定していないと、RootNamespace を変えた日に
        // WebAssets の探し方が黙って壊れる（起動時の型初期化で落ちる）。
        Assert.Contains(@"LogicalName=""wwwroot/", ProjectFile, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptHasNoThirdPartyDependency()
    {
        string[] files = [.. Directory.EnumerateFiles(WebRootDirectory, "*.js")];

        // 空振りで緑にしない ── 走査が 0 件になっても foreach は静かに終わるので、
        // 「第三者 JS なし」ではなく「検査が消えた」ことに気付けるようにする。
        Assert.Equal(5, files.Length);

        foreach (string path in files)
        {
            string script = File.ReadAllText(path);

            Assert.DoesNotContain("<script src=\"http", script, StringComparison.Ordinal);
            Assert.DoesNotContain("import ", script, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>「まだ始まっていない」の綴りが 2 か所にある。</b> DASH の配信は 503 の本文の
    /// <c>error</c> をそのまま返し（HTTP 層は特別扱いしない）、<c>app-player.js</c> は
    /// その文字列と<b>完全一致</b>で「待てば直る」を判定する ── 正本は
    /// <see cref="DashPreviewReasons.Starting"/> なので、そこを書き換えたら
    /// ブラウザは黙って「待つ」のをやめ、開始直後の 503 で停止するようになる。
    ///
    /// <para>
    /// <b>参照を共有できないからテキストで縛る</b>（<c>EncoderCatalogTests</c> と
    /// <c>tools/</c> のスクリプトの関係と同じ）── JavaScript から C# の定数は引けない。
    /// </para>
    /// </summary>
    [Fact]
    public void TheScriptSpellsTheStartingReasonExactlyAsTheServerDoes()
    {
        string script = AllScripts();

        Assert.Contains(
            "'" + DashPreviewReasons.Starting + "'",
            script,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>wwwroot</c> の JavaScript にある <c>FOLLOW_TRIM_SAFETY_SECONDS</c> の宣言。
    /// <b>行頭に錨を打たない</b> ── 打つと一致位置が必ず行頭になり、
    /// コメント行の除外が「常に偽」へ倒れて何も守らなくなる。
    /// </summary>
    private static readonly Regex FollowTrimSafetyRegex =
        new(@"\bvar\s+FOLLOW_TRIM_SAFETY_SECONDS\s*=\s*(\d+(?:\.\d+)?)\s*;", RegexOptions.Compiled);

    /// <summary>
    /// <b>追いかけ再生のトリムの安全域は、キーフレーム間隔 2 本ぶんより広い。</b>
    ///
    /// <para>
    /// <c>SourceBuffer.remove(a, b)</c> は <c>b</c> で止まらず、<c>b</c> 以降の最初の
    /// ランダムアクセス点まで削る ── 再生位置から数えて安全域がキーフレーム間隔
    /// 1 本ぶんしか無いと、削る要求が<b>再生中の GOP を巻き添えにしうる</b>。
    /// 録画物のキーフレーム間隔は <see cref="EncoderCatalog.TargetKeyframeIntervalSeconds"/>
    /// なので、その 2 倍を超えていることを縛る（片方だけ動かすと成立しなくなる）。
    /// </para>
    /// <para>
    /// <b>コメント行は除く</b> ── 素の走査は、その定数を説明しているコメント自身に一致しうる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheFollowTrimKeepsMoreThanTwoKeyframeIntervalsBehindPlayback()
    {
        string script = AllScripts();

        var declarations = FollowTrimSafetyRegex.Matches(script)
            .Where(m => !SourceReferences.IsCommentLine(script, m.Index))
            .ToArray();

        Assert.True(declarations.Length == 1,
            $"wwwroot の JavaScript に FOLLOW_TRIM_SAFETY_SECONDS が {declarations.Length} 件見つかりました"
            + "（走査が壊れているか、宣言の書き方が変わっています）。");

        double safety = double.Parse(
            declarations[0].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(2 * EncoderCatalog.TargetKeyframeIntervalSeconds < safety,
            $"トリムの安全域が狭すぎます（{safety} 秒 ≦ キーフレーム間隔 "
            + $"{EncoderCatalog.TargetKeyframeIntervalSeconds} 秒 × 2）── "
            + "remove() が再生中の GOP を巻き添えにしえます。");
    }

    [Fact]
    public void TheDocumentOnlyReferencesManifestedAssets()
    {
        string html = File.ReadAllText(Path.Combine(WebRootDirectory, "index.html"));
        var manifest = new HashSet<string>(ManifestNames(), StringComparer.Ordinal);

        var referenced = Regex.Matches(html, @"(?:<script[^>]*\ssrc|<link[^>]*\shref)=""(?<url>[^""]*)""")
            .Select(m => m.Groups["url"].Value)
            .ToArray();

        Assert.NotEmpty(referenced);

        foreach (string url in referenced)
        {
            Assert.True(manifest.Contains(url),
                $"index.html が台帳に無い '{url}' を参照している。"
                + "配れるのは WebAssets.Manifest に載っている名前だけで、それ以外は 404 になる。");
        }
    }
}
