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
        Assert.Equal("PROCESSRECORDERAPP_TEST_DEVICE_ARRIVAL", AppEnvironment.TestDeviceArrivalVariable);
    }

    // ---- デバイス到着の注入（テスト専用） ----

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void TestDeviceArrival_IsEnabledByTheAcceptedSpellings(string value)
        => Assert.True(AppEnvironment.ResolveTestDeviceArrivalEnabled(value));

    /// <summary>
    /// <b>既定は無効でなければならない。</b> 有効なままだと、通常起動のアプリが
    /// 名前付きイベントを作って待ち続ける ── 製品の挙動が「テストのための分岐」で変わる。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("yes")]
    [InlineData(" 1")]
    public void TestDeviceArrival_IsOffForAnythingElse(string? value)
        => Assert.False(AppEnvironment.ResolveTestDeviceArrivalEnabled(value));

    /// <summary>
    /// 注入に使う名前付きイベントは<b>キー接頭辞で隔離されている</b>こと。
    /// 隔離されていないと、並行して走る E2E のインスタンス同士が互いの復帰を起こし合い、
    /// 「早期に起きた」という表明が自分のシグナルによるものだと言えなくなる。
    /// </summary>
    [Fact]
    public void TheInjectionEventName_CarriesTheKeyPrefix()
    {
        Assert.Equal("Test-1234-DeviceArrival", AppEnvironment.ResolveTestDeviceArrivalEventName("Test-1234"));
        Assert.NotEqual(
            AppEnvironment.ResolveTestDeviceArrivalEventName("Test-1"),
            AppEnvironment.ResolveTestDeviceArrivalEventName("Test-2"));
    }
}
