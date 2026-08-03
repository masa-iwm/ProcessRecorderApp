using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L3: PropertyGrid の入力エラー表示が<b>出ること</b>と<b>消えること</b>。
///
/// <para>
/// <b>この層でしか押さえられない。</b> <c>PropertyGridItem</c> は WinUI に依存する
/// <c>src/Controls</c> にあり、L1 のテストプロジェクトは参照していない。
/// しかも「エラーが残る」はモデルを読むだけでは分からない
/// ── 表示値が差し戻されるせいで、<b>画面には正しい値が出たままエラーだけが残る</b>。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class PropertyGridErrorTests(PublishedApp app)
{
    /// <summary>エラー表示の有無を見る行（型変換に失敗しうる int のプロパティ）。</summary>
    private const string Row = "LogScrollbackLines";

    /// <summary>エラー表示の AutomationId（<c>PropertyGridItem.ErrorAutomationId</c>）。</summary>
    private const string ErrorId = Row + ".Error";

    /// <summary>
    /// 変換できない値を入れるとエラーが出て<b>表示値は元へ戻り</b>、
    /// そこで<b>同じ値を入れ直すとエラーが消える</b>こと。
    ///
    /// <para>
    /// <b>「同じ値」がこの試験の要点。</b> 変換に失敗すると表示は元の値へ差し戻されるので、
    /// 利用者の目には「正しい値が入っているのにエラーが出ている」と映る。そこで入力し直すと
    /// <c>Value</c> セッターの「値が変わっていなければ何もしない」ガードに当たるため、
    /// エラーを畳む処理をそこへ入れておかないと<b>二度と消えない</b>。
    /// 別の値を入れた場合は通常のコミット経路で消えるので、この不具合をすり抜ける。
    /// </para>
    /// <para>
    /// 文言はロケールで変わるので<b>有無だけ</b>を見る（<c>Visibility=Collapsed</c> の要素は
    /// UIA ツリーに出ないので、消えたことは要素が見つからないことで分かる）。
    /// </para>
    /// </summary>
    [Fact]
    public void ReenteringTheRevertedValue_ClearsTheError()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);
        string original = ui.GetPropertyText(Row);
        Assert.Null(FindError(ui));

        ui.SetPropertyText(Row, "abc");
        Assert.NotNull(FindError(ui));
        // 差し戻しが効いていること。これが崩れると下の「同じ値」の意味が変わる。
        Assert.Equal(original, ui.GetPropertyText(Row));

        ui.SetPropertyText(Row, original);
        Assert.Null(FindError(ui));

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// 別の正しい値を入れてもエラーが消えること（通常のコミット経路の回帰）。
    /// 上の試験と対で置いてある ── <b>片方だけでは「消える経路」を取り違える</b>。
    /// </summary>
    [Fact]
    public void EnteringADifferentValidValue_ClearsTheError()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);
        ui.SetPropertyText(Row, "abc");
        Assert.NotNull(FindError(ui));

        const string valid = "2345";
        ui.SetPropertyText(Row, valid);
        Assert.Equal(valid, ui.WaitForPropertyText(Row, valid));
        Assert.Null(FindError(ui));

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// エラー文言が<b>表示言語どおりに解決される</b>こと。
    ///
    /// <para>
    /// この文言は長らく <c>src/Controls</c> に日本語で直書きされており、
    /// <b>en-US で起動しても日本語が出ていた</b>。L4 は「属性値のキーが両ロケールに在ること」
    /// までしか見ず、直書きの文字列は誰も検出しない ── 画面まで届いていることを見るのはここだけ
    /// （<c>LanguageMatrixTests</c> が <c>PropCat_Debug</c> について同じ理由で置かれている）。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void TheConversionErrorMessageIsLocalized(string locale)
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, language: locale);
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);
        ui.SetPropertyText(Row, "abc");

        var error = FindError(ui);
        Assert.NotNull(error);
        Assert.Equal(UiResources.GetControls(locale, "PropGrid_ValueConversionFailed"), error.Name);
    }

    /// <summary>
    /// エラー表示を探す。消えている場合は <see langword="null"/>。
    /// 待ちを短くしてあるのは「無いこと」を見る用途が主で、長い締め切りだと
    /// 成功のたびにその時間を必ず払うため。出る側は入力の確定（Tab）が
    /// <c>SetPropertyText</c> の中で同期的に済んでいる。
    /// </summary>
    private static FlaUI.Core.AutomationElements.AutomationElement? FindError(AppUi ui)
        => ui.TryFindElement(ErrorId, TimeSpan.FromSeconds(1));
}
