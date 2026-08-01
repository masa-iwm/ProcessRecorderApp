using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L3 + L4: 表示言語の強制マトリクス。
/// <c>PROCESSRECORDERAPP_LANG</c> で ja-JP / en-US / de-DE を強制し、
/// <b>画面の文言が指定した言語になること</b>と、
/// <b>どちらでもない言語（de-DE）が en-US へフォールバックすること</b>を見る。
///
/// <para>
/// <b>探すのは AutomationId、照合するのは Name。</b> AutomationId はロケール非依存で、
/// 表示文言（UIA の Name）だけがロケールで変わる ── 各要素に AutomationId を
/// 付けてあるのは、この使い分けを成立させるため。
/// </para>
///
/// <para>
/// <b>期待値は <see cref="UiResources"/> がリポジトリの .resw から読む。</b>
/// テストソースに訳文を焼き込むと、翻訳を直しただけで赤になるうえ、
/// 「resw の値が画面まで届いているか」という本来の主張からずれる。
/// </para>
///
/// <para>
/// <b>【重要】どの行が実際に効くかはホストの表示言語で入れ替わる。</b>
/// 言語強制が完全に効いていない状態でも、<b>ホストの表示言語と一致する行は緑のまま通る</b>
/// ── この開発機（表示言語 ja）では <c>ja-JP</c> の行が、GitHub ランナー（en-US）では
/// <c>en-US</c> と <c>de-DE</c> の行が、それぞれ<b>何も検証していない</b>。
/// 3言語すべてを回すのはこの非対称のためで、<b>1言語だけに減らしてはいけない</b>。
/// 検出が生きているかは <c>ApplyLanguageOverride</c> を無効化する注入で確かめられる
/// ── 表示言語 ja のホストでは en-US / de-DE が赤・ja-JP が緑になる（実測。
/// docs/coverage-gaps.md の「言語強制マトリクス」の節も参照）。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class LanguageMatrixTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>未定義変数の指定で返る終了コード（`src/README.md` の終了コード表）。</summary>
    private const int ExitVariableNotDefined = 11;

    /// <summary>
    /// Preview（既定の画面）で照合する要素と、その文言を供給する resw のキー。
    /// <c>.Content</c> と <c>AutomationProperties.Name</c> が混在しているのは
    /// resw の実際の書き方どおり（<c>x:Uid</c> の解決先はプロパティごとに違う）。
    /// </summary>
    private static readonly (string AutomationId, string ResourceName)[] PreviewSurface =
    [
        ("previewNavItem", "MainPage_PreviewNav.Content"),
        ("logNavItem", "MainPage_LogNav.Content"),
        ("variablesNavItem", "MainPage_VariablesNav.Content"),
        ("TogglePaneButton", "MainPage_TogglePane.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"),
        ("StartAllButton", "MainPage_StartAll.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"),
    ];

    [Theory]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("en-US", "en-US")]
    // en-US でも ja-JP でもない表示言語。ニュートラル言語へのフォールバックを見るための行。
    [InlineData("de-DE", UiResources.NeutralLocale)]
    public void ForcedLanguage_DrivesTheTextOnScreen(string forced, string expectedLocale)
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, language: forced);
        using var ui = AppUi.Activate(instance);

        foreach (var (automationId, resourceName) in PreviewSurface)
            AssertLocalizedName(ui, expectedLocale, automationId, resourceName);

        ui.SwitchTo(UiSection.Log);
        AssertLocalizedName(ui, expectedLocale, "ClearLogButton", "MainPage_Clear.Content");

        ui.SwitchTo(UiSection.Variables);
        AssertLocalizedName(ui, expectedLocale, "AddVariableButton", "MainPage_VarAdd.Content");
        AssertLocalizedName(
            ui, expectedLocale, "VariablesTable",
            "MainPage_VariablesTable.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name");

        // PropertyGrid の**カテゴリ見出し**。`[Category("PropCat_...")]` の属性値を
        // リソースキーとして解決する経路で、解決に失敗すると画面には**キー名がそのまま**出る
        // （実際に踏んだ症状）。L4 の静的検査は「キーが両ロケールに在ること」までしか
        // 見ないので、画面まで届いていることを見るのはここだけ。
        // 見出しには AutomationId が無いので、可視の Name を集めて照合する。
        ui.SwitchTo(UiSection.Settings);
        string expectedCategory = UiResources.Get(expectedLocale, "PropCat_Debug");
        var texts = ui.VisibleTexts();
        Assert.True(
            texts.Contains(expectedCategory),
            $"[{forced}] Settings 画面に PropCat_Debug の見出し '{expectedCategory}' がありません。" +
            Environment.NewLine + $"見えている文言: [{string.Join(" | ", texts)}]");

        // CLI 側。判定に使うのは **en-US の ASCII 断片の有無だけ** ── 出力のバイト列は
        // コンソールのコードページで、UTF-8 として読むと化けるため、日本語を文字列比較の
        // 土台にできない（実際に一度誤読しかけた）。
        //
        // **「非 ASCII が含まれること」も判定に使ってはいけない。** この開発機の
        // コンソールは cp932 なので日本語をそのまま符号化できるが、GitHub ランナー
        // （cp437/1252）では符号化できない文字が `?`（0x3F）に潰れる ── 親側でどう
        // デコードしても復元できない。ここでそれを使うと、**ランナーでだけ ja-JP の行が
        // 偽の赤になる**。その行はランナーで唯一実質的な検査になるところなので、
        // 「ja-JP が落ちた＝製品の欠陥」という読み方そのものを壊してしまう。
        //
        // なお、この行はランチャーと常駐ワーカーのどちらが文言を作ったかまでは切り分けない。
        var notFound = instance.Run("--get", "NoSuchKey");
        Assert.Equal(ExitVariableNotDefined, notFound.ExitCode);
        string neutralFragment = UiResources.Get(UiResources.NeutralLocale, "Cli_VariableNotFound")
            .Split("{0}", StringSplitOptions.None)[0];
        output.WriteLine($"[{forced}] --get NoSuchKey stderr: {notFound.StdErr.Trim()}");

        // 空の stderr で素通りしないように、何か出ていることは先に表明する。
        Assert.NotEmpty(notFound.StdErr.Trim());

        if (expectedLocale == UiResources.NeutralLocale)
            Assert.Contains(neutralFragment, notFound.StdErr, StringComparison.Ordinal);
        else
            Assert.DoesNotContain(neutralFragment, notFound.StdErr, StringComparison.Ordinal);

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    private void AssertLocalizedName(AppUi ui, string expectedLocale, string automationId, string resourceName)
    {
        string expected = UiResources.Get(expectedLocale, resourceName);
        string actual = ui.GetAutomationName(automationId);
        output.WriteLine($"{automationId}: '{actual}' (期待 '{expected}' / {expectedLocale})");
        Assert.True(
            actual == expected,
            $"AutomationId='{automationId}' の表示文言が {expectedLocale} になっていません。" +
            $" 期待 '{expected}' / 実際 '{actual}'（resw キー: {resourceName}）");
    }
}
