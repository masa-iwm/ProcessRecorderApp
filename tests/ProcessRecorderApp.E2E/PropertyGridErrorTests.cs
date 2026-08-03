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
    /// <b>Enter でも確定できる</b>こと（フォーカスを外さずにモデルへ届く）。
    ///
    /// <para>
    /// WinUI の <c>TextBox.Text</c> は TwoWay バインドでもフォーカスを失うまで
    /// ソースへ書き戻さない。<c>PropertyGridView</c> は <c>KeyDown</c> で明示的に
    /// コミットしており、その配線が外れると<b>入力した値が画面に出たまま
    /// モデルには届かない</b>という、いちばん気付きにくい壊れ方をする。
    /// </para>
    /// <para>
    /// 届いたことは <c>settings.json</c> で見る ── 画面の表示は入力そのままなので、
    /// <b>それを見てもコミットされたかどうかは分からない</b>。
    /// </para>
    /// </summary>
    [Fact]
    public void EnterCommitsWithoutLeavingTheField()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);

        // 起動・表示に伴うデバウンス保存を出し切らせてから入力する。
        Thread.Sleep(TimeSpan.FromSeconds(3));

        const string committed = "4321";
        ui.SetPropertyTextWithEnter(Row, committed);

        // Enter は「離れずに確定する」操作なので、フォーカスは行に残っていること。
        Assert.True(ui.WaitForPropertyRow(Row).Properties.HasKeyboardFocus.ValueOrDefault,
            "Enter でフォーカスまで移動しています。");

        Assert.True(WaitForSettingsToContain(instance, $"\"{Row}\": {committed}"),
            "Enter で確定したのに settings.json へ届いていません。");
        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// Enter で出したエラーが、<b>フォーカスを外しただけでは消えない</b>こと。
    ///
    /// <para>
    /// TwoWay バインドは、Enter で確定した直後にフォーカスを失うと<b>同じ文字列を
    /// もう一度書き戻してくる</b>。利用者の操作は 1 回なのに 2 回コミットが来るので、
    /// 素通しすると「入れ直した」と誤認してエラーを畳んでしまい、
    /// <b>Enter で出したばかりの指摘が、離れただけで消える</b>。
    /// Tab だけで確定した場合（<see cref="ReenteringTheRevertedValue_ClearsTheError"/>）と
    /// 挙動が食い違うのが本質的な問題で、この試験がその差を塞ぐ。
    /// </para>
    /// </summary>
    [Fact]
    public void TheErrorSurvivesLeavingTheFieldAfterEnter()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);
        ui.SetPropertyTextWithEnter(Row, "abc");
        Assert.NotNull(FindError(ui));

        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
        Thread.Sleep(700);

        Assert.NotNull(FindError(ui));
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

    /// <summary>
    /// <c>settings.json</c> に指定の断片が現れるまで待つ。
    /// <b>固定 sleep で読まない</b> ── 保存は約 1 秒のデバウンス越しに起きる。
    /// </summary>
    private static bool WaitForSettingsToContain(AppInstance instance, string fragment)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            try
            {
                if (File.ReadAllText(instance.SettingsPath).Contains(fragment, StringComparison.Ordinal))
                    return true;
            }
            catch (IOException)
            {
                // 保存の最中。次の周回で読み直す。
            }
            Thread.Sleep(200);
        }
        while (deadline.Elapsed < TimeSpan.FromSeconds(15));
        return false;
    }
}
