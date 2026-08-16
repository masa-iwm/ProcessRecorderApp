using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>GitHub Packages から復元するための配線が、<c>build.yml</c> と <c>release.yml</c> の
/// 両方に在ること。</b>
///
/// <para>
/// <c>GstSharp.Net*</c> は private な GitHub Packages から来るので、restore には
/// <c>NuGetPackageSourceCredentials_github</c>（nuget.config のソース key <c>github</c> と
/// 対になる NuGet の規約）と <c>packages: read</c> の両方が要る。
/// </para>
/// <para>
/// <b>これを書いた理由。</b> 取得元を GitHub Packages へ替えたとき <c>build.yml</c> だけが
/// 直り、<c>release.yml</c> は認証を持たないまま残った。<c>build.yml</c> は push のたびに
/// 走るので緑に見え続け、<b>release.yml はタグを打つまで一度も流れない</b> ──
/// 気付いたのは配布物を作ろうとした回で、restore が 401（NU1301）で落ちたときである。
/// <b>「片方だけ直した」が次のリリースまで見えない</b>のがこの事故の本体なので、
/// ここで両方を突き合わせる。
/// </para>
/// <para>
/// <b>値までは見ない。</b> 見るのは「その名前がその workflow に在るか」だけで、
/// 秘密の中身も式の書き方も縛らない ── 縛ると、片方の書き換えのたびに
/// 意味の無い赤が出る。<see cref="ThirdPartyLicenseTests"/> の
/// <c>ThePublishOutputIsWiredToCarryTheLicenseTexts</c> と同じ性格の検査である。
/// </para>
/// </summary>
public class WorkflowPackageAuthTests
{
    /// <summary>復元に要る資格情報の env 名（NuGet の規約で、末尾はソース key）。</summary>
    private const string CredentialsVariable = "NuGetPackageSourceCredentials_github";

    /// <summary>GITHUB_TOKEN に要るスコープ。</summary>
    private const string PackagesReadScope = "packages: read";

    private static string Workflow(string name)
        => File.ReadAllText(RepositoryFiles.At(".github", "workflows", name));

    [Theory]
    [InlineData("build.yml")]
    [InlineData("release.yml")]
    public void EveryWorkflowThatBuildsCanAuthenticateToGitHubPackages(string workflow)
    {
        string text = Workflow(workflow);

        Assert.True(text.Contains(CredentialsVariable, StringComparison.Ordinal),
            $"{workflow} に {CredentialsVariable} が無い。"
            + Environment.NewLine
            + "GstSharp.Net* の restore が 401（NU1301）で落ちる ── "
            + "build.yml は push のたびに走るので気付けるが、release.yml は"
            + "タグを打つまで流れないので、次のリリースまで見えない。");

        Assert.True(text.Contains(PackagesReadScope, StringComparison.Ordinal),
            $"{workflow} に '{PackagesReadScope}' が無い。"
            + Environment.NewLine
            + "permissions を1つでも書くと未記載のスコープは none になるので、"
            + "GITHUB_TOKEN ではパッケージを読めない。");
    }

    /// <summary>
    /// <b>nuget.config 側のソース key と env 名の末尾が一致していること。</b>
    /// NuGet はこの対応で資格情報を引くので、どちらかを改名すると
    /// <b>設定は残ったまま黙って無視され</b>、401 だけが出る。
    /// </summary>
    [Fact]
    public void TheCredentialsVariableMatchesTheSourceKeyInNuGetConfig()
    {
        string sourceKey = CredentialsVariable["NuGetPackageSourceCredentials_".Length..];
        string nugetConfig = File.ReadAllText(RepositoryFiles.At("nuget.config"));

        Assert.True(nugetConfig.Contains($"key=\"{sourceKey}\"", StringComparison.Ordinal),
            $"nuget.config に key=\"{sourceKey}\" のパッケージソースが無い。"
            + Environment.NewLine
            + $"env 名 {CredentialsVariable} の末尾はソース key でなければ、"
            + "NuGet は資格情報を引けない（エラーにはならず 401 になるだけ）。");
    }
}
