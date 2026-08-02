using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// Log 画面のターミナルが同梱している xterm.js（<c>Assets/Terminal/vendor/</c>）が、
/// <c>Assets/Terminal/SOURCES.md</c> に書いた版・SHA256 と一致していること。
///
/// <para>
/// <b>これが無いと `.gitattributes` の <c>-text</c> 規則が「効いているつもり」になる。</b>
/// この機の <c>core.autocrlf</c> は true なので、規則を落とすとチェックアウトのたびに
/// 改行が書き換えられ、**上流の配布物と 1 バイトも違わない**という前提が黙って崩れる。
/// 同梱ライセンス文が <c>ThirdPartyLicenseTests</c> でハッシュ固定されているのと同じ理由で、
/// ここにも同じ守りを置く（あちらの台帳には載せられない ── 理由は SOURCES.md）。
/// </para>
/// <para>
/// 突き合わせは<b>双方向</b>にする。表から行を消してもファイルを消しても落ちる。
/// </para>
/// </summary>
public class TerminalAssetPinTests
{
    private static string VendorDir => RepositoryFiles.At(
        "src", "ProcessRecorderApp", "Assets", "Terminal", "vendor");

    private static string SourcesPath => RepositoryFiles.At(
        "src", "ProcessRecorderApp", "Assets", "Terminal", "SOURCES.md");

    /// <summary>`| `ファイル名` | ... | `SHA256` |` の形の行から (ファイル名, ハッシュ) を取る。</summary>
    private static readonly Regex RowPattern = new(
        @"^\|\s*`(?<file>[^`]+)`\s*\|.*\|\s*`(?<sha>[0-9a-f]{64})`\s*\|\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static Dictionary<string, string> PinnedHashes()
    {
        var text = File.ReadAllText(SourcesPath);
        var pinned = RowPattern.Matches(text)
            .ToDictionary(m => m.Groups["file"].Value, m => m.Groups["sha"].Value, StringComparer.Ordinal);

        // 空の結果を緑にしない（表の書式を変えて 1 行も拾えなくなったら落とす）
        Assert.True(pinned.Count > 0,
            $"{RepositoryFiles.Relative(SourcesPath)} からファイルと SHA256 の対を1件も読めなかった。表の書式を変えていないか。");
        return pinned;
    }

    private static string Sha256Of(string path)
        => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void EveryVendoredFileMatchesTheHashPinnedInSources()
    {
        foreach (var (file, expected) in PinnedHashes())
        {
            var path = Path.Combine(VendorDir, file);
            Assert.True(File.Exists(path), $"同梱ファイルが無い: {RepositoryFiles.Relative(path)}");
            Assert.Equal(expected, Sha256Of(path));
        }
    }

    [Fact]
    public void TheSourcesTableAndTheFilesOnDiskAgreeBothWays()
    {
        var pinned = PinnedHashes().Keys.OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var onDisk = Directory.EnumerateFiles(VendorDir)
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(pinned, onDisk);
    }

    [Fact]
    public void TheNoticesFileNamesTheSameVersionsAsSources()
    {
        var sources = File.ReadAllText(SourcesPath);
        var notices = File.ReadAllText(RepositoryFiles.At("THIRD-PARTY-NOTICES.md"));

        // SOURCES.md の表に出てくる「パッケージ 版」の対が NOTICES にも出ていること
        var pairs = Regex.Matches(sources, @"`(?<pkg>@xterm/[a-z-]+)`\s*\|\s*(?<ver>\d+\.\d+\.\d+)")
            .Select(m => (Package: m.Groups["pkg"].Value, Version: m.Groups["ver"].Value))
            .Distinct()
            .ToArray();
        Assert.True(pairs.Length > 0, "SOURCES.md からパッケージと版の対を1件も読めなかった。");

        foreach (var (package, version) in pairs)
        {
            Assert.Contains(package, notices, StringComparison.Ordinal);
            Assert.Contains(version, notices, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheLineEndingRuleThatKeepsTheHashesValid_IsStillDeclared()
    {
        // この規則を消すと core.autocrlf=true の環境でチェックアウト時に改行が書き換わり、
        // 上のハッシュ照合が「手元だけ緑・CI だけ赤」になる
        var attributes = File.ReadAllText(RepositoryFiles.At(".gitattributes"));

        Assert.Contains("src/ProcessRecorderApp/Assets/Terminal/vendor/** -text", attributes, StringComparison.Ordinal);
    }
}
