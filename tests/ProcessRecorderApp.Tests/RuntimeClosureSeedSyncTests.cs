using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b><c>tools/Get-GStreamerImportClosure.ps1</c> の「名前で読むライブラリ」の一覧を、
/// 製品側の実際の名前に固定する。</b>
///
/// <para>
/// 同梱物は<b>この種から辿った PE インポート閉包そのもの</b>である（46 ファイル。docs/runtime-update.md）。
/// 種が製品より狭ければ、閉包から落ちたライブラリが同梱物から消える
/// ── <b>PE のインポートではなくマネージド側が名前で読むものは、
/// ビルドでもテストでも落ちず、同梱配布の実行時にだけ壊れる。</b>
/// </para>
/// <para>
/// <see cref="EncoderCatalogScriptSyncTests"/> と同じ性格の検査で、狙う事故も同じ
/// ── 片方を直してもう片方を直し忘れたときに、<b>何のエラーも出ないまま</b>
/// 検証や配布物の側だけが古くなる。PowerShell を型で突き合わせる方法は無いので
/// ソースをテキストとして読む。
/// </para>
/// </summary>
public class RuntimeClosureSeedSyncTests
{
    private static readonly string ScriptPath =
        RepositoryFiles.At("tools", "Get-GStreamerImportClosure.ps1");

    /// <summary>実機検証スクリプトが同梱ツリーから実行するもの（閉包の種に入れてある）。</summary>
    private const string InspectTool = "gst-inspect-1.0.exe";

    /// <summary>
    /// 利用者の機械でパイプラインを単体再現するために意図的に同梱するツール
    /// （閉包の種に入れてある。使い方の例は docs/environment-facts.md の再現手順）。
    /// </summary>
    private const string LaunchTool = "gst-launch-1.0.exe";

    /// <summary>製品が名前で読むライブラリではない、意図的に同梱するツール。</summary>
    private static readonly string[] BundledTools = [InspectTool, LaunchTool];

    /// <summary><c>$namedSeeds = @('bin/x.dll', ...)</c> から相対パスを取り出す。</summary>
    private static string[] NamedSeeds()
    {
        string text = File.ReadAllText(ScriptPath);
        int at = text.IndexOf("$namedSeeds = @(", StringComparison.Ordinal);
        Assert.True(at >= 0,
            $"{RepositoryFiles.Relative(ScriptPath)} に '$namedSeeds = @(' が見つからない。"
            + Environment.NewLine
            + "書き方を変えたなら、この検査も一緒に直すこと"
            + Environment.NewLine
            + "── 見つからないまま緑にすると、検査そのものが消える。");

        Match end = Regex.Match(text[at..], @"(?m)^\)\s*$");
        Assert.True(end.Success, $"{RepositoryFiles.Relative(ScriptPath)} の $namedSeeds を閉じる ')' 行が無い。");

        string body = text.Substring(at, end.Index);
        string[] seeds = [.. Regex.Matches(body, @"'([^']+)'").Select(m => m.Groups[1].Value)];
        Assert.NotEmpty(seeds);
        return seeds;
    }

    /// <summary>
    /// 製品側（GstSharp.Net バインディング）が**名前で読む**ライブラリの一覧（MinGW 命名）。
    ///
    /// <para>
    /// 正本は GstSharp.Net リポジトリの <c>Core/Interop/NativeNames.cs</c> と、
    /// このアプリが参照するバインディングアセンブリが登録するモジュール集合
    /// （GLib / GObject / GModule / Gst / GstBase / GstApp。Video / Audio / Pbutils は
    /// 参照していないので要求されない）。名前空間はアプリ側に無いので、
    /// <b>パッケージの版を上げたら NativeNames.cs と突き合わせてここを更新する</b>こと
    /// ── ずれてもビルドもテストも落ちず、同梱配布の実行時にだけ壊れる
    /// （このファイルの存在理由そのもの）。<b>GStreamer を名前で読むのはバインディングだけ</b>
    /// である ── アプリに GStreamer の生 P/Invoke は残っていないので、
    /// この一覧に足すものはアプリ側には無い。
    /// </para>
    /// </summary>
    private static readonly string[] FileNamesNamedByProduct =
    [
        "libglib-2.0-0.dll",
        "libgobject-2.0-0.dll",
        "libgmodule-2.0-0.dll",
        "libgstreamer-1.0-0.dll",
        "libgstbase-1.0-0.dll",
        "libgstapp-1.0-0.dll",
    ];

    /// <summary>
    /// <b>種の一覧が、製品が名前で読むライブラリと過不足なく一致すること。</b>
    /// 足りなければ同梱物から消え、余っていれば「使わないものを配る」に戻る。
    /// </summary>
    [Fact]
    public void TheClosureSeedsAreExactlyTheLibrariesTheProductNamesItself()
    {
        string[] seedFiles = [.. NamedSeeds()
            .Select(s => s[(s.LastIndexOf('/') + 1)..])
            .Where(s => !BundledTools.Contains(s, StringComparer.OrdinalIgnoreCase))];

        Assert.Equal(
            [.. FileNamesNamedByProduct.Order(StringComparer.Ordinal)],
            [.. seedFiles.Distinct().Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// 実機検証スクリプトが同梱ツリーから実行する <c>gst-inspect-1.0.exe</c> も種であること
    /// ── 落とすと、<c>tools/Verify-GpuEncoders.ps1</c> が同梱構成で
    /// <b>要素を1つも問い合わせられなくなる</b>（アプリ自身は壊れないので気付きにくい）。
    /// </summary>
    [Fact]
    public void TheInspectToolTheVerificationScriptRunsIsAlsoASeed()
    {
        Assert.Contains(NamedSeeds(), s => s.EndsWith(InspectTool, StringComparison.OrdinalIgnoreCase));

        string verify = File.ReadAllText(RepositoryFiles.At("tools", "Verify-GpuEncoders.ps1"));
        Assert.Contains(InspectTool, verify, StringComparison.Ordinal);
    }

    /// <summary>
    /// 現場診断用の <c>gst-launch-1.0.exe</c> も種であること ── 落とすと、利用者の機械で
    /// パイプラインを単体再現する手段（アプリ抜きの切り分け）が同梱配布から消える。
    /// </summary>
    [Fact]
    public void TheLaunchToolShipsForFieldDiagnostics()
    {
        Assert.Contains(NamedSeeds(), s => s.EndsWith(LaunchTool, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>種が同梱物に実在すること。</b> 名前で読むライブラリを増やしたのに
    /// 同梱物へ入れ忘れる、が一番効く事故 ── 非同梱版（利用者の GStreamer を使う）では
    /// 起きず、<b>同梱配布の実行時にだけ</b>壊れる。
    /// </summary>
    [Fact]
    public void EverySeedIsActuallyShipped()
    {
        HashSet<string> shipped = [.. File.ReadAllLines(
                RepositoryFiles.At("licenses", "third-party", "COMPONENTS.tsv"))
            .Where(l => !l.StartsWith('#') && l.Trim().Length > 0)
            .Skip(1)
            .Select(l => l.Split('\t')[0])];
        Assert.NotEmpty(shipped);

        string[] missing = [.. NamedSeeds().Where(s => !shipped.Contains(s))];

        Assert.True(missing.Length == 0,
            "閉包の種なのに COMPONENTS.tsv に無い（＝同梱物に入らない）: "
            + string.Join(", ", missing));
    }
}
