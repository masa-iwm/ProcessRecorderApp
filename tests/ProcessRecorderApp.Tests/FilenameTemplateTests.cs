using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="FilenameTemplate"/> の展開規則。
///
/// このクラスの <c>{ENV.x}</c> 系・エスケープ系のテストは、実際にあった
/// 2件の不具合に対する回帰テストである:
///   - プレースホルダのキーが <c>\w+</c> だと <c>{ENV.USERNAME}</c> が一切マッチしない
///   - <c>{{</c>/<c>}}</c> のエスケープを展開後に後掛け Replace すると結果が壊れる
/// </summary>
public class FilenameTemplateTests
{
    private static readonly DateTime FixedNow = new(2026, 7, 25, 13, 45, 09, DateTimeKind.Local);

    /// <summary>変数・環境変数を一切持たない既定の展開。</summary>
    private static string Format(
        string template,
        string recorderName = "Recorder #1",
        IReadOnlyDictionary<string, string>? variables = null,
        Func<string, string?>? env = null)
        => FilenameTemplate.Format(
            template,
            recorderName,
            FixedNow,
            variables ?? new Dictionary<string, string>(),
            env ?? (_ => null));

    // ---- {Now} ----

    [Fact]
    public void Now_WithFormatSpecifier_UsesInvariantCulture()
        => Assert.Equal("20260725_134509", Format("{Now:yyyyMMdd_HHmmss}"));

    [Fact]
    public void Now_WithoutFormatSpecifier_UsesDefaultToString()
        => Assert.Equal(FilenameTemplate.SanitizeValue(FixedNow.ToString()), Format("{Now}"));

    [Fact]
    public void Now_DefaultTemplate_ProducesExpectedName()
        => Assert.Equal("20260725_134509_Recorder #1.mp4", Format("{Now:yyyyMMdd_HHmmss}_{Name}.mp4"));

    [Fact]
    public void Now_AppearingTwice_UsesTheSameInstant()
    {
        // 呼び出し側が1回だけ取得した時刻を渡す設計であることの確認
        var result = Format("{Now:HHmmss}-{Now:HHmmss}");
        Assert.Equal("134509-134509", result);
    }

    [Fact]
    public void Now_FormatSpecifierContainingColon_IsSanitizedInResult()
        => Assert.Equal("13：45：09", Format("{Now:HH:mm:ss}"));

    // ---- {Name} ----

    [Fact]
    public void Name_IsSubstituted()
        => Assert.Equal("cam.mp4", Format("{Name}.mp4", recorderName: "cam"));

    [Fact]
    public void Name_ContainingPathSeparator_IsSanitizedNotTreatedAsDirectory()
        => Assert.Equal("a／b.mp4", Format("{Name}.mp4", recorderName: "a/b"));

    // ---- {ENV.x}: 回帰テスト（キーの正規表現が \w+ だと一切マッチしない） ----

    [Fact]
    public void Env_IsSubstituted()
        => Assert.Equal(
            "masa.mp4",
            Format("{ENV.USERNAME}.mp4", env: n => n == "USERNAME" ? "masa" : null));

    [Fact]
    public void Env_UndefinedVariable_ExpandsToEmptyString()
        => Assert.Equal("-.mp4", Format("{ENV.NOPE}-.mp4", env: _ => null));

    [Fact]
    public void Env_KeyIsPassedWithoutThePrefix()
    {
        string? observed = null;
        Format("{ENV.PROCESSOR_ARCHITECTURE}", env: n => { observed = n; return "x64"; });
        Assert.Equal("PROCESSOR_ARCHITECTURE", observed);
    }

    [Fact]
    public void Env_CombinedWithOtherPlaceholders()
        => Assert.Equal(
            "20260725_134509_masa_cam.mp4",
            Format("{Now:yyyyMMdd_HHmmss}_{ENV.USERNAME}_{Name}.mp4",
                recorderName: "cam",
                env: _ => "masa"));

    [Fact]
    public void Env_ValueContainingInvalidChars_IsSanitized()
        => Assert.Equal("C：＼tmp.mp4", Format("{ENV.TMP}.mp4", env: _ => @"C:\tmp"));

    // ---- ユーザー定義変数 ----

    [Fact]
    public void Variable_IsSubstituted()
        => Assert.Equal(
            "v1.mp4",
            Format("{Build}.mp4", variables: new Dictionary<string, string> { ["Build"] = "v1" }));

    [Fact]
    public void Variable_UnknownKey_IsLeftVerbatim()
        => Assert.Equal("{Nope}.mp4", Format("{Nope}.mp4"));

    [Fact]
    public void Variable_UnknownKeyWithFormatSpecifier_IsLeftVerbatim()
        => Assert.Equal("{Nope:yyyy}.mp4", Format("{Nope:yyyy}.mp4"));

    [Fact]
    public void Variable_FormatSpecifierOnStringValue_IsIgnored()
        // string は IFormattable ではないため、書式指定子は無視され値がそのまま入る
        => Assert.Equal(
            "abc.mp4",
            Format("{K:yyyy}.mp4", variables: new Dictionary<string, string> { ["K"] = "abc" }));

    [Fact]
    public void Variable_ValueContainingInvalidChars_IsSanitized()
        => Assert.Equal(
            "a＜b＞c.mp4",
            Format("{K}.mp4", variables: new Dictionary<string, string> { ["K"] = "a<b>c" }));

    [Fact]
    public void Variable_EmptyValue_ExpandsToEmptyString()
        => Assert.Equal(
            ".mp4",
            Format("{K}.mp4", variables: new Dictionary<string, string> { ["K"] = "" }));

    [Fact]
    public void Variable_KeyLookupIsCaseSensitive()
        => Assert.Equal(
            "{build}.mp4",
            Format("{build}.mp4", variables: new Dictionary<string, string> { ["Build"] = "v1" }));

    // ---- {{ }} エスケープ: 回帰テスト（展開後の後掛け Replace だと壊れる） ----

    [Fact]
    public void Escape_DoubleBraces_ProduceSingleBraces()
        => Assert.Equal("{Name}.mp4", Format("{{Name}}.mp4"));

    [Fact]
    public void Escape_IsNotAppliedToExpandedValues()
    {
        // 展開された値が "{{" を含んでいても、それはエスケープとして再解釈されてはならない。
        // （展開後に Replace("{{","{") を後掛けする実装だと "{" に潰れる）
        var result = Format("{K}.mp4", variables: new Dictionary<string, string> { ["K"] = "{{x}}" });
        Assert.Equal("{{x}}.mp4", result);
    }

    [Fact]
    public void Escape_MixedWithPlaceholders()
        => Assert.Equal("{literal}-cam.mp4", Format("{{literal}}-{Name}.mp4", recorderName: "cam"));

    [Fact]
    public void Escape_LoneOpeningBrace_IsLeftVerbatim()
        => Assert.Equal("{ .mp4", Format("{ .mp4"));

    // ---- テンプレート本文の扱い ----

    [Fact]
    public void Template_BackslashInLiteralText_IsPreservedAsDirectorySeparator()
        // テンプレート本文の '\' はサブディレクトリ指定として意味を持つため、
        // サニタイズ対象は「展開された値」のみでなければならない
        => Assert.Equal(@"sub\dir\cam.mp4", Format(@"sub\dir\{Name}.mp4", recorderName: "cam"));

    [Fact]
    public void Template_WithoutPlaceholders_IsReturnedUnchanged()
        => Assert.Equal("fixed-name.mp4", Format("fixed-name.mp4"));

    [Fact]
    public void Template_Empty_ReturnsEmpty()
        => Assert.Equal(string.Empty, Format(string.Empty));

    [Fact]
    public void Format_NullArguments_Throw()
    {
        var vars = new Dictionary<string, string>();
        Assert.Throws<ArgumentNullException>(() => FilenameTemplate.Format(null!, "n", FixedNow, vars, _ => null));
        Assert.Throws<ArgumentNullException>(() => FilenameTemplate.Format("{Name}", "n", FixedNow, null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => FilenameTemplate.Format("{Name}", "n", FixedNow, vars, null!));
    }
}

/// <summary>
/// <see cref="FilenameTemplate.SanitizeValue"/>: ファイル名に使えない文字の全角変換。
/// </summary>
public class FilenameSanitizeTests
{
    [Theory]
    [InlineData('\\', '＼')]
    [InlineData('/', '／')]
    [InlineData(':', '：')]
    [InlineData('*', '＊')]
    [InlineData('?', '？')]
    [InlineData('"', '＂')]
    [InlineData('<', '＜')]
    [InlineData('>', '＞')]
    [InlineData('|', '｜')]
    public void MappedInvalidChar_BecomesFullWidthEquivalent(char input, char expected)
        => Assert.Equal($"a{expected}b", FilenameTemplate.SanitizeValue($"a{input}b"));

    [Fact]
    public void UnmappedInvalidChar_BecomesUnderscore()
    {
        // 制御文字は Path.GetInvalidFileNameChars() に含まれるが全角対応表には無い
        Assert.Equal("a_b", FilenameTemplate.SanitizeValue("a\u0001b"));
        Assert.Equal("a_b", FilenameTemplate.SanitizeValue("a\nb"));
    }

    [Fact]
    public void EveryInvalidFileNameChar_IsRemovedFromTheResult()
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = FilenameTemplate.SanitizeValue(new string(invalid));
        Assert.DoesNotContain(sanitized, c => Array.IndexOf(invalid, c) >= 0);
        Assert.Equal(invalid.Length, sanitized.Length);
    }

    [Fact]
    public void ValidString_IsUnchanged()
    {
        const string s = "Recorder #1 - 2026年 (ok)_v1.mp4";
        Assert.Equal(s, FilenameTemplate.SanitizeValue(s));
    }

    [Fact]
    public void Empty_IsUnchanged()
        => Assert.Equal(string.Empty, FilenameTemplate.SanitizeValue(string.Empty));
}
