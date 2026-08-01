using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="AppEnvironment"/> の解決規則。
///
/// 実際の <see cref="AppEnvironment.DataDirectory"/> / <see cref="AppEnvironment.KeyPrefix"/> は
/// プロセスにつき1度だけ解決される（環境変数を後から変えても反映されない）ため、
/// 規則そのものは純粋関数として分離してある。ここではその純粋関数を検証する。
///
/// この上書きが効かなくなると、E2E テストが実ユーザーの
/// <c>%LOCALAPPDATA%\ProcessRecorderApp\settings.json</c> を書き換え、
/// かつ開発者の常駐インスタンスとコマンドを奪い合うようになる。
/// </summary>
public class AppEnvironmentTests
{
    private const string LocalAppData = @"C:\Users\tester\AppData\Local";

    // ---- データディレクトリ ----

    [Fact]
    public void DataDirectory_WithoutOverride_IsUnderLocalAppData()
        => Assert.Equal(
            Path.Combine(LocalAppData, "ProcessRecorderApp"),
            AppEnvironment.ResolveDataDirectory(null, LocalAppData));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void DataDirectory_BlankOverride_IsTreatedAsUnset(string overrideValue)
        => Assert.Equal(
            Path.Combine(LocalAppData, "ProcessRecorderApp"),
            AppEnvironment.ResolveDataDirectory(overrideValue, LocalAppData));

    [Fact]
    public void DataDirectory_AbsoluteOverride_IsUsedAsIs()
        => Assert.Equal(
            @"C:\temp\prapp-test",
            AppEnvironment.ResolveDataDirectory(@"C:\temp\prapp-test", LocalAppData));

    [Fact]
    public void DataDirectory_OverrideIsTrimmed()
        => Assert.Equal(
            @"C:\temp\prapp-test",
            AppEnvironment.ResolveDataDirectory("  C:\\temp\\prapp-test  ", LocalAppData));

    [Fact]
    public void DataDirectory_RelativeOverride_IsMadeAbsolute()
    {
        string resolved = AppEnvironment.ResolveDataDirectory("relative-dir", LocalAppData);

        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith("relative-dir", resolved);
    }

    [Fact]
    public void GetDataFilePath_IsUnderTheDataDirectory()
        => Assert.Equal(
            Path.Combine(AppEnvironment.DataDirectory, "settings.json"),
            AppEnvironment.GetDataFilePath("settings.json"));

    // ---- 単一インスタンスキーの接頭辞 ----

    [Fact]
    public void KeyPrefix_WithoutOverride_IsTheApplicationName()
        => Assert.Equal("ProcessRecorderApp", AppEnvironment.ResolveKeyPrefix(null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void KeyPrefix_BlankOverride_IsTreatedAsUnset(string overrideValue)
        => Assert.Equal(AppEnvironment.DefaultKeyPrefix, AppEnvironment.ResolveKeyPrefix(overrideValue));

    [Fact]
    public void KeyPrefix_Override_IsUsed()
        => Assert.Equal("Test-1234", AppEnvironment.ResolveKeyPrefix("Test-1234"));

    [Fact]
    public void KeyPrefix_OverrideIsTrimmed()
        => Assert.Equal("Test-1234", AppEnvironment.ResolveKeyPrefix("  Test-1234  "));

    /// <summary>
    /// 既定値が変わると、既存の常駐ワーカーと新しいランチャーが別のインスタンスとして
    /// 共存してしまう（＝単一インスタンス制御が無言で壊れる）。
    /// </summary>
    [Fact]
    public void DefaultKeyPrefix_IsTheApplicationName()
        => Assert.Equal("ProcessRecorderApp", AppEnvironment.DefaultKeyPrefix);

    // ---- 環境変数名（テストと製品コードで綴りが一致していること） ----

    [Fact]
    public void VariableNames_AreStable()
    {
        Assert.Equal("PROCESSRECORDERAPP_DATA_DIR", AppEnvironment.DataDirectoryVariable);
        Assert.Equal("PROCESSRECORDERAPP_KEY_PREFIX", AppEnvironment.KeyPrefixVariable);
        Assert.Equal("PROCESSRECORDERAPP_LANG", AppEnvironment.LanguageVariable);
        Assert.Equal("PROCESSRECORDERAPP_MIRROR_STDERR", AppEnvironment.MirrorToOriginalStdErrVariable);
    }
}
