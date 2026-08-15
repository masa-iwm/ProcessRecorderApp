using System.Security.Cryptography;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>同梱物のライセンス文が、上流の原文のまま・過不足なく揃っていることを固定する。</b>
///
/// <para>
/// 同梱配布は第三者バイナリを配るので、そのライセンス文も配らなければならない。
/// <b>公式インストーラにはライセンス文が1つも入っていない</b>（THIRD-PARTY-NOTICES.md 参照）ので、
/// 全文は上流から取得して <c>licenses/third-party/</c> に置いてある。
/// </para>
/// <para>
/// <b>ここが守れるのはリポジトリの中の整合だけである。</b>
/// 「配布 zip に実際に入っている」ことは、<c>release.yml</c> が
/// <c>COMPONENTS.tsv</c> と発行物を突き合わせる回が走るまで観測できない
/// ── その限界は THIRD-PARTY-NOTICES.md にも書いてある。
/// </para>
/// <para>
/// ハッシュの正本は <c>SOURCES.tsv</c> ただ1つ。
/// <c>tools/Fetch-ThirdPartyLicenses.ps1</c> もここも同じファイルを読むので、
/// 片方だけ更新して食い違う、という壊れ方をしない。
/// </para>
/// </summary>
public class ThirdPartyLicenseTests
{
    private static readonly string LicenseRoot = RepositoryFiles.At("licenses", "third-party");

    private sealed record SourceRow(string Project, string Version, string File, string Sha256, string Uri);

    private sealed record ComponentRow(string Path, string Project);

    /// <summary>
    /// タブ区切りの台帳を読む。<c>#</c> 始まりはコメント、最初の非コメント行がヘッダー。
    /// </summary>
    private static List<string[]> ReadTable(string path, int expectedColumns)
    {
        Assert.True(File.Exists(path), $"{RepositoryFiles.Relative(path)} が無い。");

        List<string[]> rows = [];
        bool headerSeen = false;
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            string[] fields = line.Split('\t');
            if (!headerSeen)
            {
                headerSeen = true;
                Assert.True(fields.Length >= expectedColumns,
                    $"{RepositoryFiles.Relative(path)} のヘッダーが {fields.Length} 列しか無い"
                    + $"（{expectedColumns} 列必要）。区切りがタブでなくなっていないか。");
                continue;
            }
            rows.Add(fields);
        }

        // 空の結果を緑にしない。パースが壊れたときに「全件 OK」で通るのが一番危ない。
        Assert.True(rows.Count > 0, $"{RepositoryFiles.Relative(path)} からデータ行を1つも読めなかった。");
        return rows;
    }

    private static List<SourceRow> Sources()
        => [.. ReadTable(Path.Combine(LicenseRoot, "SOURCES.tsv"), 5)
                .Select(f => new SourceRow(f[0], f[1], f[2], f[3], f[4]))];

    private static List<ComponentRow> Components()
        => [.. ReadTable(Path.Combine(LicenseRoot, "COMPONENTS.tsv"), 2)
                .Select(f => new ComponentRow(f[0], f[1]))];

    private static string[] LicenseFilesOnDisk()
        => [.. Directory.EnumerateFiles(LicenseRoot, "*", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetRelativePath(LicenseRoot, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)];

    /// <summary>
    /// 全文が上流の原文のままであること。<b>1バイトでも変われば落ちる。</b>
    /// 生成・要約したものに差し替わっていないことを保証しているのはこの検査だけ。
    /// </summary>
    [Fact]
    public void EveryLicenseTextMatchesTheHashPinnedUpstream()
    {
        List<string> problems = [];
        foreach (SourceRow row in Sources())
        {
            string path = Path.Combine(LicenseRoot, row.Project, row.File);
            if (!File.Exists(path))
            {
                problems.Add($"{row.Project}/{row.File}: 無い");
                continue;
            }
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!actual.Equals(row.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{row.Project}/{row.File}: 期待 {row.Sha256} / 実際 {actual}");
        }

        Assert.True(problems.Count == 0,
            "ライセンス全文が SOURCES.tsv のハッシュと一致しない:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems)
            + Environment.NewLine
            + "取り直すなら tools/Fetch-ThirdPartyLicenses.ps1、確認だけなら -Verify。");
    }

    /// <summary>
    /// 台帳とディスクが両方向で一致すること
    /// ── 載っていないファイルも、ファイルの無い行も、どちらも通さない。
    /// </summary>
    [Fact]
    public void TheLedgerAndTheFilesOnDiskAgreeBothWays()
    {
        string[] inLedger = [.. Sources()
            .Select(r => $"{r.Project}/{r.File}")
            .OrderBy(p => p, StringComparer.Ordinal)];
        string[] onDisk = LicenseFilesOnDisk();

        Assert.Equal(inLedger, onDisk);
    }

    /// <summary>
    /// <b>同梱する全ファイルが、ライセンス文のあるプロジェクトに紐付いていること。</b>
    /// 同梱物に何かを足してライセンス文を足し忘れたら、ここで落ちる。
    /// </summary>
    [Fact]
    public void EveryBundledFileIsCoveredByALicenseText()
    {
        HashSet<string> licensed = [.. Sources().Select(r => r.Project)];
        string[] uncovered = [.. Components()
            .Where(c => !licensed.Contains(c.Project))
            .Select(c => $"{c.Path} -> {c.Project}")
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)];

        Assert.True(uncovered.Length == 0,
            "同梱するのにライセンス文が無いプロジェクトがある:"
            + Environment.NewLine + string.Join(Environment.NewLine, uncovered));
    }

    /// <summary>
    /// 逆向き ── 同梱しなくなったものの文だけが残る状態を作らない。
    /// <b>openh264 を外したときに実際に効いた向きの検査。</b>
    /// </summary>
    [Fact]
    public void NoLicenseTextIsKeptForSomethingNoLongerBundled()
    {
        HashSet<string> bundled = [.. Components().Select(c => c.Project)];
        string[] stale = [.. Sources()
            .Select(r => r.Project)
            .Distinct()
            .Where(p => !bundled.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal)];

        Assert.True(stale.Length == 0,
            "同梱していないのにライセンス文だけ残っている: " + string.Join(", ", stale));
    }

    /// <summary>
    /// 表記（THIRD-PARTY-NOTICES.md）が台帳から外れていないこと。
    /// <b>版まで見る</b> ── コンポーネントを上げて表記を直し忘れる方が、
    /// 行を消し忘れるよりずっと起きやすい。
    /// </summary>
    [Fact]
    public void TheNoticesFileNamesEveryBundledProjectAndItsVersion()
    {
        string notices = File.ReadAllText(RepositoryFiles.At("THIRD-PARTY-NOTICES.md"));
        List<string> missing = [];
        foreach (SourceRow row in Sources().DistinctBy(r => r.Project))
        {
            if (!notices.Contains(row.Project, StringComparison.Ordinal))
                missing.Add($"プロジェクト名 '{row.Project}'");
            if (!notices.Contains(row.Version, StringComparison.Ordinal))
                missing.Add($"'{row.Project}' の版 '{row.Version}'");
        }

        Assert.True(missing.Count == 0,
            "THIRD-PARTY-NOTICES.md に載っていないものがある:"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// 表記に書いた件数が実際の同梱物と合っていること。
    /// <b>数字は本文に散らばるので、台帳から数え直して突き合わせる。</b>
    /// </summary>
    [Fact]
    public void TheCountsInTheNoticesFileMatchTheComponentList()
    {
        string notices = File.ReadAllText(RepositoryFiles.At("THIRD-PARTY-NOTICES.md"));
        List<ComponentRow> components = Components();

        int plugins = components.Count(c => c.Path.StartsWith("lib/gstreamer-1.0/", StringComparison.Ordinal));
        int gstLibs = components.Count(c => c.Path.StartsWith("bin/libgst", StringComparison.Ordinal)
                                            && c.Path.EndsWith("-1.0-0.dll", StringComparison.Ordinal));
        // GStreamer 以外（gst-inspect-1.0.exe を除く）。ここを数えていなかったため、
        // 「その他の第三者ライブラリ（12）」だけが古くなっても気付けなかった。
        int otherLibs = components.Count - plugins - gstLibs
                        - components.Count(c => c.Path.EndsWith(".exe", StringComparison.Ordinal));
        int total = components.Count;

        (string Label, string Expected)[] expectations =
        [
            ("プラグイン", $"GStreamer プラグイン（{plugins}）"),
            ("ライブラリ", $"GStreamer ライブラリ（{gstLibs}）"),
            ("その他のライブラリ", $"その他の第三者ライブラリ（{otherLibs}）"),
            ("同梱ファイル総数", $"同梱 {total} ファイル全件"),
        ];

        List<string> wrong = [.. expectations
            .Where(e => !notices.Contains(e.Expected, StringComparison.Ordinal))
            .Select(e => $"{e.Label}: 見出し '{e.Expected}' が無い")];

        Assert.True(wrong.Count == 0,
            "THIRD-PARTY-NOTICES.md の件数が COMPONENTS.tsv と合っていない:"
            + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// <b>ライセンス文を発行物へ運ぶ配線が外れていないこと。</b>
    ///
    /// <para>
    /// これを書くまで発行物には**本体の MIT すら入っていなかった**。
    /// 配線が消えても他のテストは全部緑のままなので、ソースを直接見るしかない
    /// ── <c>ShutdownRedirectHandlingTests</c> と同じ性格の検査で、
    /// <b>「そう書いてある」ことの確認であって、zip に入ったことの確認ではない。</b>
    /// </para>
    /// </summary>
    [Fact]
    public void ThePublishOutputIsWiredToCarryTheLicenseTexts()
    {
        string appProj = File.ReadAllText(
            RepositoryFiles.At("src", "ProcessRecorderApp", "ProcessRecorderApp.csproj"));
        string gstProj = File.ReadAllText(
            RepositoryFiles.At("src", "GStreamer.GstSharpBundle", "GStreamer.GstSharpBundle.csproj"));
        string release = File.ReadAllText(
            RepositoryFiles.At(".github", "workflows", "release.yml"));

        List<string> missing = [];

        // 本体の分は配布形態によらず入る。
        foreach (string file in (string[])["license.txt", "THIRD-PARTY-NOTICES.md"])
        {
            if (!appProj.Contains(file, StringComparison.Ordinal))
                missing.Add($"ProcessRecorderApp.csproj が {file} をコピーしていない");
        }

        // 同梱物の分。GStreamer ランタイムは GstSharpBundle.Windows.X64 の contentFiles が
        // 常に同梱するため、ライセンス文の複製も無条件の ItemGroup に居ること。
        if (!gstProj.Contains(@"licenses\third-party", StringComparison.Ordinal))
        {
            missing.Add(@"GStreamer.GstSharpBundle.csproj が licenses\third-party を複製していない");
        }
        else if (!gstProj.Contains("CopyToPublishDirectory", StringComparison.Ordinal))
        {
            missing.Add("GStreamer.GstSharpBundle.csproj のライセンス文複製が publish へ届かない（CopyToPublishDirectory が無い）");
        }

        // 発行物と台帳の突き合わせ（「300 ファイル以上」の当て推量に戻さない）。
        if (!release.Contains("COMPONENTS.tsv", StringComparison.Ordinal))
            missing.Add("release.yml が COMPONENTS.tsv と発行物を突き合わせていない");

        Assert.True(missing.Count == 0,
            "ライセンス文が配布物へ入る配線が外れている:"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }
}
