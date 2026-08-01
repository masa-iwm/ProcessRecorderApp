using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <b>「優先する H.264 エンコーダー」を選択肢から選べること。</b>
///
/// <para>
/// 自由入力のテキストにすると、綴りを間違えたとき <c>Resolve</c> が黙って飛ばすため
/// <b>指定したつもりのエンコーダーが一度も使われない</b>状態になりうる
/// （実在しない名前 <c>nvd3d12h264enc</c> がカタログに載っていた実例がある）
/// ── 綴り誤りを構造的に防ぐため、選択肢にしている。
/// </para>
/// <para>
/// <b>この層でしか確かめられないことが2つある。</b>
/// </para>
/// <list type="number">
///   <item><description>
///     <b>表示名と保存値が違っても、保存されるのは値の方であること。</b>
///     一覧の見た目は L1 の <c>EncoderChoiceListTests</c> が固定しているが、
///     「印の付いた表示名がそのまま settings.json に書かれてしまう」種類の壊れ方は
///     <b>UI を実際に操作して保存結果を読むまで分からない。</b>
///   </description></item>
///   <item><description>
///     <b>PropertyGrid のリフレクション経路が発行物で生きていること。</b>
///     属性（<c>ChoiceListAttribute</c>）を <c>GetCustomAttribute</c> で読む経路は
///     <b>Native AOT の危険域</b>で、L1 では絶対に検出できない。
///   </description></item>
/// </list>
/// <para>
/// 表示言語を <c>en-US</c> に固定するのは、印の文言を resw から一意に決めるため。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class EncoderChoiceUiTests(PublishedApp app, ITestOutputHelper output)
{
    private const string ComboId = "PreferredH264Encoder";

    /// <summary>デバウンス保存が落ち着くまでの余裕。</summary>
    private static readonly TimeSpan SaveMargin = TimeSpan.FromSeconds(3);

    private static string SavedEncoder(AppInstance instance)
    {
        string json = File.ReadAllText(instance.SettingsPath);
        return JsonNode.Parse(json)?["PreferredH264Encoder"]?.GetValue<string>() ?? "<absent>";
    }

    [Fact]
    public void ThePreferredEncoderIsPickedFromAList_AndTheStoredValueIsTheFactoryName()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, language: "en-US");
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);

        // ---- 一覧の中身 ----
        var names = ui.ChoiceNames(ComboId);
        output.WriteLine("choices: " + string.Join(" | ", names));

        string automatic = UiResources.Get("en-US", "EncoderChoice_Automatic");
        Assert.Equal(automatic, names[0]);

        // この開発機／CI ランナーには GPU が無いので nvh264enc は必ず「無い」側。
        // **その印が実際に画面へ出ていること**を見る（L1 は印を付ける判断までしか見ない）。
        string notFound = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            UiResources.Get("en-US", "EncoderChoice_NotFound").Replace("{0}", "{0}"),
            "nvh264enc");
        Assert.Contains(notFound, names);

        // 実在するものには印が付かない（＝表示は要素名そのまま）。
        Assert.Contains("x264enc", names);

        // ---- 表示名 ≠ 保存値 の要（このテストの本体） ----
        // 「見つかりません」の印が付いた項目を選んでも、保存されるのは要素名だけであること。
        // ここが壊れると settings.json に印つきの文字列が入り、Resolve が黙って飛ばす
        // ── 症状は「指定したのに使われない」で、エラーは何も出ない。
        ui.SelectChoice(ComboId, notFound);
        Thread.Sleep(SaveMargin);

        Assert.Equal("nvh264enc", SavedEncoder(instance));

        // ---- 「自動選択」は空文字として保存されること ----
        ui.SelectChoice(ComboId, automatic);
        Thread.Sleep(SaveMargin);

        Assert.Equal("", SavedEncoder(instance));

        // 画面の表示も追随していること（保存だけ通って表示が古いままなら片道の壊れ方）。
        Assert.Equal(automatic, ui.GetSelectedChoice(ComboId));

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// <b>一覧に無い値を持って開いても、その値を失わないこと。</b>
    ///
    /// <para>
    /// 古い名前・打ち間違い・手で編集された設定ファイルの値がそのままだと、
    /// コンボボックスは<b>空を表示し、利用者が他の行に触れた瞬間にその値を失う</b>
    /// ── しかも編集不可なので打ち直せない。
    /// このプロパティの doc コメントが「設定ミスで録画が一切できなくなる方が有害」と
    /// しているのと同じ判断で、値は残す。
    /// </para>
    /// </summary>
    [Fact]
    public void AnUnknownStoredValueSurvivesOpeningTheSettingsScreen()
    {
        var settings = new SettingsFile { PreferredH264Encoder = "nvd3d12h264enc" };
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, language: "en-US");
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);

        // 印つきで選択済みとして表示されること（空欄ではない）。
        string expected = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            UiResources.Get("en-US", "EncoderChoice_NotInCatalog"),
            "nvd3d12h264enc");

        Assert.Equal(expected, ui.GetSelectedChoice(ComboId));

        // 画面を離れて戻っても、保存値は変わらないこと
        // ── 「開いただけで消える」が一番危ない壊れ方。
        ui.SwitchTo(UiSection.Log);
        ui.SwitchTo(UiSection.Settings);
        Thread.Sleep(SaveMargin);

        Assert.Equal("nvd3d12h264enc", SavedEncoder(instance));
    }
}
