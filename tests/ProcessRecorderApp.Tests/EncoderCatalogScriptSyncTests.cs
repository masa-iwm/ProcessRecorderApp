using System.Text.RegularExpressions;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b><c>tools/Verify-GpuEncoders.ps1</c> のエンコーダー名を <see cref="EncoderCatalog"/> に固定する。</b>
///
/// <para>
/// <b>これは実際に起きた事故の再発防止。</b> カタログ側だけを直して
/// このスクリプトの一覧を直し忘れると、両者は黙って食い違う。
/// スクリプトは <b>自分の一覧に載っている名前しか <c>gst-inspect</c> に尋ねない</b>ので、
/// 一覧から漏れた要素は<b>実際に持っている実機で流しても、
/// 一度も問い合わされず、ケースも作られず、レポートにも出ない</b>
/// ── 「実機にも載っていない」と誤って記録される。
/// <b>エラーも警告も出ない。出力が「無い」ことと「訊いていない」ことの区別が付かない。</b>
/// </para>
/// <para>
/// <b>スクリプト側にも別方向の対策を入れてある</b>（<c>gst-inspect</c> の全要素を列挙して
/// カタログに無いものを警告する）。<b>両方要る</b> ── あちらは「その機械に在るのに
/// カタログが知らないもの」を、こちらは「カタログにあるのにスクリプトが訊かないもの」を
/// 捕まえる。片方だけではこの種の食い違いはどちらの向きからも漏れる。
/// </para>
/// <para>
/// PowerShell の一覧を型で突き合わせる方法は無いので、
/// <c>AppSettingsReloadTests</c> と同じくソースをテキストとして読む。
/// </para>
/// </summary>
public class EncoderCatalogScriptSyncTests
{
    private static readonly string ScriptPath =
        RepositoryFiles.At("tools", "Verify-GpuEncoders.ps1");

    /// <summary>
    /// <c>$allEncoders = @('a', 'b', ...)</c> / <c>@($present | Where-Object { $_ -in @('a', ...) })</c>
    /// のような PowerShell の文字列配列から要素を取り出す。
    /// </summary>
    private static string[] ArrayLiteralAfter(string text, string anchor)
    {
        // コメント行に書かれた例を実装と数えない（この repo で1度踏んでいる）。
        int at = -1;
        for (int i = text.IndexOf(anchor, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(anchor, i + 1, StringComparison.Ordinal))
        {
            if (!SourceReferences.IsCommentLine(text, i)) { at = i; break; }
        }

        Assert.True(at >= 0,
            $"{ScriptPath} に（コメントでない）'{anchor}' が見つからない。"
            + Environment.NewLine
            + "スクリプトの書き方を変えたなら、この検査も一緒に直すこと"
            + Environment.NewLine
            + "── 見つからないまま緑にすると、検査そのものが消える。");

        int open = text.IndexOf("@(", at, StringComparison.Ordinal);
        int close = text.IndexOf(')', open);
        Assert.True(open >= 0 && close > open, $"'{anchor}' の後に配列リテラルが無い。");

        return [.. Regex.Matches(text[open..close], @"'([^']+)'").Select(m => m.Groups[1].Value)];
    }

    /// <summary>
    /// スクリプトが問い合わせる名前が、カタログの候補名と<b>過不足なく</b>一致すること。
    /// </summary>
    [Fact]
    public void TheScriptProbesExactlyTheEncodersTheCatalogKnowsAbout()
    {
        string text = File.ReadAllText(ScriptPath);
        string[] inScript = ArrayLiteralAfter(text, "$allEncoders");

        string[] inCatalog = [.. EncoderCatalog.D3d12Candidates
            .Concat(EncoderCatalog.SystemCandidates)
            .Select(c => c.FactoryName)
            .Distinct()];

        Assert.Equal([.. inCatalog.Order()], [.. inScript.Order()]);
    }

    /// <summary>
    /// スクリプトが「GPU 側」として1件ずつケースを作る名前が、
    /// カタログの <c>D3d12</c> 専用候補（＝<c>D3d12Candidates</c> から
    /// <c>SystemCandidates</c> を引いたもの）と一致すること。
    ///
    /// <para>
    /// ここがずれると、<b>実在する GPU エンコーダーに専用ケースが作られない</b>
    /// ── 自動選択のケースだけは通るので、レポートは緑のまま「確認した」ように見える。
    /// </para>
    /// </summary>
    [Fact]
    public void TheScriptTreatsExactlyTheD3d12OnlyCandidatesAsGpuEncoders()
    {
        string text = File.ReadAllText(ScriptPath);
        string[] inScript = ArrayLiteralAfter(text, "$gpuEncoders");

        var systemNames = EncoderCatalog.SystemCandidates.Select(c => c.FactoryName).ToHashSet(StringComparer.Ordinal);
        string[] inCatalog = [.. EncoderCatalog.D3d12Candidates
            .Select(c => c.FactoryName)
            .Where(n => !systemNames.Contains(n))];

        Assert.Equal([.. inCatalog.Order()], [.. inScript.Order()]);
    }

    /// <summary>
    /// <b><c>d3d12download</c> のケースが選ぶ system-memory エンコーダーの一覧が、
    /// カタログの <see cref="EncoderCatalog.SystemCandidates"/> と
    /// 名前・プロパティ文字列・順序まで一致すること。</b>
    ///
    /// <para>
    /// <b>この検査が無かったために穴が開いた。</b> スクリプトの一覧には
    /// <c>mfh264enc</c> が入っておらず、<c>x264enc</c> か <c>openh264enc</c> の
    /// どちらかが在る機械では気付けなかった。同梱ランタイムから <c>openh264</c> を
    /// 外した瞬間に<b>どちらも無くなり、ケースは「スキップ」になった</b>
    /// ── <b>スキップは失敗ではないのでレポートは緑のまま</b>で、
    /// <c>d3d12download</c> が覆われていないことだけが静かに起きる。
    /// </para>
    /// <para>
    /// 順序も見る。スクリプトは<b>先頭から見て最初に実在したもの</b>を採るので、
    /// 並びが変わると「カタログが優先する方」と違うものでケースが作られる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheScriptsManualSystemMemoryCandidatesMatchTheCatalog()
    {
        string text = File.ReadAllText(ScriptPath);

        // 配列リテラルの中にコメントがあり、そこに ')' が現れるため
        // ArrayLiteralAfter は使えない。')' だけの行までを本体とする。
        int at = text.IndexOf("$manualCandidates = @(", StringComparison.Ordinal);
        Assert.True(at >= 0,
            $"{ScriptPath} に '$manualCandidates = @(' が見つからない。"
            + Environment.NewLine
            + "書き方を変えたなら、この検査も一緒に直すこと"
            + Environment.NewLine
            + "── 見つからないまま緑にすると、検査そのものが消える。");

        var end = Regex.Match(text[at..], @"(?m)^\)\s*$");
        Assert.True(end.Success, $"{ScriptPath} の $manualCandidates を閉じる ')' 行が見つからない。");
        string body = text.Substring(at, end.Index);

        (string Name, string Props)[] inScript = [.. Regex
            .Matches(body, @"Name\s*=\s*'([^']+)'\s*;\s*Props\s*=\s*'([^']+)'")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))];

        // 1件も取れないまま緑にしない（正規表現が壊れたときに検査が消える）。
        Assert.True(inScript.Length > 0,
            $"{ScriptPath} の $manualCandidates から候補を1つも読めなかった。書き方を変えたか、正規表現が壊れている。");

        (string Name, string Props)[] inCatalog = [.. EncoderCatalog.SystemCandidates
            .Select(c => (c.FactoryName, c.LaunchString))];

        Assert.Equal(inCatalog, inScript);
    }

    /// <summary>
    /// <b>スクリプトの列挙用正規表現が、カタログの全候補名に当たること。</b>
    ///
    /// <para>
    /// スクリプトは <c>gst-inspect-1.0</c> の全出力から H.264 エンコーダーの行を拾って
    /// 「カタログが知らない要素」を警告する。その正規表現が名前を取りこぼすと、
    /// <b>取りこぼした要素は「この機械に無い」ように見える</b>
    /// ── 一覧のずれと同じ事故が、一段下で再発する。
    /// </para>
    /// <para>
    /// <b>実際に踏んだ:</b> 素直に <c>h264enc</c> で絞ると <c>x264enc</c> が落ちる
    /// （<c>x264enc</c> に <c>h</c> は無い）。この開発機で列挙が
    /// <c>mfh264enc, openh264enc</c> の2件しか返さず、実際には在る <c>x264enc</c> が
    /// 消えていた。<b>警告も出ない。</b>
    /// </para>
    /// </summary>
    [Fact]
    public void TheScriptsElementScanPattern_MatchesEveryCatalogName()
    {
        string text = File.ReadAllText(ScriptPath);

        // スクリプト中の '...264enc...' を含むシングルクォートの正規表現リテラルを取る。
        var literal = Regex.Matches(text, @"'(\^[^']*264enc[^']*)'")
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault();

        Assert.True(literal is not null,
            $"{ScriptPath} に要素名を拾う正規表現リテラルが見つからない。"
            + Environment.NewLine
            + "書き方を変えたなら、この検査も一緒に直すこと。");

        // PowerShell の -match と .NET の Regex は同じエンジンなので、そのまま試せる。
        var pattern = new Regex(literal!);

        foreach (string name in EncoderCatalog.D3d12Candidates
                     .Concat(EncoderCatalog.SystemCandidates)
                     .Select(c => c.FactoryName)
                     .Distinct())
        {
            // gst-inspect-1.0 の一覧行の形（"plugin:  element: Description"）を再現する。
            string line = $"someplugin:  {name}: Some H.264 Encoder";
            var m = pattern.Match(line);

            Assert.True(m.Success,
                $"列挙用の正規表現が '{name}' に当たらない。"
                + Environment.NewLine
                + $"パターン: {literal}"
                + Environment.NewLine
                + $"行: {line}"
                + Environment.NewLine
                + "当たらない要素は「この機械に無い」ように見え、警告も出ない。");

            Assert.Equal(name, m.Groups[2].Value);
        }
    }

    /// <summary>
    /// <b><c>Verify-HighResolution.ps1</c> のエンコーダー行がカタログと一致すること。</b>
    ///
    /// <para>
    /// あのスクリプトは<b>実機の報告に出ていた起動文字列をそのまま</b>持っており、
    /// 解像度スイープにも同じ文字列を使う（行の間で解像度だけが違うようにするため）。
    /// カタログ側の <c>qsvh264enc</c> の起動文字列が変わったのにここが変わらないと、
    /// <b>スクリプトは製品がもう作らない構成を検証し続け、しかも緑を返す。</b>
    /// </para>
    /// <para>
    /// <b>「報告どおりに凍結する」判断もあり得る</b>ので、ここは自動追随ではなく
    /// <b>失敗させて判断を迫る</b>形にしてある ── 凍結すると決めたなら、
    /// この表明を「凍結した文字列」に書き換えて理由を残すこと。
    /// <b>黙ってずれるのだけは駄目。</b>
    /// </para>
    /// </summary>
    [Fact]
    public void TheHighResolutionScriptsEncoderLine_StillMatchesTheCatalog()
    {
        string text = File.ReadAllText(RepositoryFiles.At("tools", "Verify-HighResolution.ps1"));

        int at = -1;
        for (int i = text.IndexOf("$reportedEnc", StringComparison.Ordinal); i >= 0;
             i = text.IndexOf("$reportedEnc", i + 1, StringComparison.Ordinal))
        {
            if (!SourceReferences.IsCommentLine(text, i)) { at = i; break; }
        }
        Assert.True(at >= 0, "Verify-HighResolution.ps1 に（コメントでない）$reportedEnc が無い。");

        var literal = Regex.Match(text[at..], @"'([^']+)'");
        Assert.True(literal.Success, "$reportedEnc に文字列リテラルが無い。");

        string inCatalog = EncoderCatalog.D3d12Candidates
            .First(c => c.FactoryName == "qsvh264enc").LaunchString;

        Assert.True(literal.Groups[1].Value == inCatalog,
            "Verify-HighResolution.ps1 のエンコーダー行がカタログとずれている。"
            + Environment.NewLine
            + $"  スクリプト: {literal.Groups[1].Value}"
            + Environment.NewLine
            + $"  カタログ  : {inCatalog}"
            + Environment.NewLine
            + "このままだと、実機検証は製品がもう作らない構成を検証して緑を返す。"
            + Environment.NewLine
            + "カタログを意図的に変えたなら、スクリプトも合わせるか、"
            + Environment.NewLine
            + "「報告どおりに凍結する」と決めてこの表明を書き換えること。");
    }

    /// <summary>
    /// <b><c>Verify-GpuEncoders.ps1</c> の手動上書きケースの起動文字列がカタログと一致すること。</b>
    ///
    /// <para>
    /// あのケースは <c>EncodingProperties</c> を<b>手で書いて渡す</b>ので、
    /// 文字列が間違っていると実機で <c>FAILED</c> になる ── しかも
    /// <b>製品ではなくスクリプトが悪いのに、製品の欠陥に見える。</b>
    /// 実機検証は往復に時間がかかるため、<b>偽の赤は「製品に欠陥が1件ある」という
    /// 誤った記録を残す</b>（`x264enc` 決め打ちで実際に起きた）。
    /// </para>
    /// <para>
    /// <b>これは繰り返し踏んでいる同じ罠の変種である</b> ── 存在しない <c>nvd3d12</c> の綴り・
    /// 列挙を <c>h264enc</c> で絞って <c>x264enc</c> を落とす・
    /// 手動ケースの要素名の決め打ち・その<b>プロパティ文字列</b>の決め打ち。
    /// いずれも原因は「要素が在ると仮定した」こと
    /// ── 目視ではなく機械に持たせる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheGpuScriptsManualOverrideStrings_StillMatchTheCatalog()
    {
        string text = File.ReadAllText(RepositoryFiles.At("tools", "Verify-GpuEncoders.ps1"));

        // $manualCandidates の各行: Name = 'x' ; Props = 'y'
        var rows = Regex.Matches(text, @"Name\s*=\s*'([^']+)'\s*;\s*Props\s*=\s*'([^']+)'")
            .Where(m => !SourceReferences.IsCommentLine(text, m.Index))
            .ToList();

        Assert.True(0 < rows.Count,
            "Verify-GpuEncoders.ps1 に手動上書きケースの候補（Name/Props の対）が見つからない。"
            + Environment.NewLine
            + "書き方を変えたなら、この検査も一緒に直すこと"
            + "（見つからないまま緑にすると、検査そのものが消える）。");

        foreach (var row in rows)
        {
            string name = row.Groups[1].Value;
            string props = row.Groups[2].Value;

            var inCatalog = EncoderCatalog.SystemCandidates.FirstOrDefault(c => c.FactoryName == name);
            Assert.True(inCatalog is not null,
                $"スクリプトの手動上書き候補 '{name}' がカタログの SystemCandidates に無い。"
                + Environment.NewLine
                + "このケースは「システムメモリのエンコーダーを D3D12 経路へ手で指定する」"
                + "（＝d3d12download が入ることの確認）ものなので、"
                + Environment.NewLine
                + "候補はシステムメモリ側から選ぶこと。");

            Assert.True(props == inCatalog!.LaunchString,
                $"手動上書きケース '{name}' の起動文字列がカタログとずれている。"
                + Environment.NewLine
                + $"  スクリプト: {props}"
                + Environment.NewLine
                + $"  カタログ  : {inCatalog.LaunchString}"
                + Environment.NewLine
                + "このままだと実機で FAILED になり、**製品の欠陥に見える**"
                + "（往復に時間がかかるので、偽の赤の害は大きい）。");
        }
    }

    /// <summary>
    /// <b>訂正した名前が二度と戻らないこと。</b> <c>nvd3d12h264enc</c> は実在しない
    /// （<c>nvcodec</c> の H.264 は <c>nvh264enc</c> / <c>nvd3d11h264enc</c> /
    /// <c>nvautogpuh264enc</c>）。上の検査でも捕まるが、<b>失敗メッセージが
    /// 「なぜ間違いなのか」を語らない</b>ので、この名前だけは名指しで止める。
    /// </summary>
    [Theory]
    [InlineData("tools", "Verify-GpuEncoders.ps1")]
    [InlineData("tools", "Verify-HighResolution.ps1")]
    public void TheNonExistentNvidiaElementNameIsGone(params string[] pathParts)
    {
        string text = File.ReadAllText(RepositoryFiles.At(pathParts));

        Assert.DoesNotContain("nvd3d12h264enc", text, StringComparison.Ordinal);
    }
}
