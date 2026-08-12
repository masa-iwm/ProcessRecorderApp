using System.Text.Json.Nodes;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <b>構図補助線が訳された名前で選べ、settings.json には列挙型の名前で入ること。</b>
///
/// <para>
/// <c>FramingGrid</c> は<b>列挙型のプロパティ</b>でありながら
/// <c>ChoiceListAttribute</c> で表示だけを差し替えている。PropertyGrid はこの属性を
/// 列挙型の判定より先に見て、選ばれた表示名ではなく<b>保存値の方</b>を書き戻す
/// ── その書き戻しは「文字列 → 列挙型」の変換を通るので、
/// <b>訳した文言がそのまま settings.json に入る</b>形の壊れ方をしうる。
/// そうなると次回の読み込みで値が落ちて、補助線が黙って消える。
/// </para>
/// <para>
/// <b>L1 では検出できない。</b> 一覧そのものは L1 の <c>FramingGridChoicesTests</c> が
/// 固定しているが、<c>PropertyGridView</c> は <c>Controls</c> プロジェクトにあり
/// L1 は参照していない。そのうえ属性をリフレクションで読む経路は
/// <b>Native AOT の危険域</b>で、発行物を動かす以外に確かめる手段が無い。
/// </para>
/// <para>
/// 表示言語を <c>en-US</c> に固定するのは、表示名を resw から一意に決めるため。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class FramingGridChoiceUiTests(PublishedApp app, ITestOutputHelper output)
{
    private const string ComboId = "FramingGrid";

    /// <summary>デバウンス保存が落ち着くまでの余裕。</summary>
    private static readonly TimeSpan SaveMargin = TimeSpan.FromSeconds(3);

    private static string SavedGrid(AppInstance instance)
    {
        string json = File.ReadAllText(instance.SettingsPath);
        return JsonNode.Parse(json)?["FramingGrid"]?.GetValue<string>() ?? "<absent>";
    }

    [Fact]
    public void TheFramingGridIsPickedByItsTranslatedName_AndTheStoredValueIsTheEnumName()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, language: "en-US");
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);

        // ---- 一覧が訳されていること（列挙型の名前がそのまま出ていない） ----
        var names = ui.ChoiceNames(ComboId);
        output.WriteLine("choices: " + string.Join(" | ", names));

        string off = UiResources.Get("en-US", "FramingGrid_None");
        string thirds = UiResources.Get("en-US", "FramingGrid_Thirds");
        string square = UiResources.Get("en-US", "FramingGrid_Square");

        Assert.Equal(off, names[0]);
        Assert.Contains(thirds, names);
        Assert.Contains(square, names);

        // 訳した名前と列挙型の名前が別物であること自体を確かめる
        // ── 同じなら、このテストは「保存値が正しい」ことを何も見ていない。
        Assert.NotEqual("Thirds", thirds);

        // ---- 表示名 ≠ 保存値 の要（このテストの本体） ----
        ui.SelectChoice(ComboId, thirds);
        Thread.Sleep(SaveMargin);

        Assert.Equal("Thirds", SavedGrid(instance));

        // ---- 別の値へ移してから戻せること（片道だけ通る壊れ方を防ぐ） ----
        ui.SelectChoice(ComboId, square);
        Thread.Sleep(SaveMargin);

        Assert.Equal("Square", SavedGrid(instance));

        ui.SelectChoice(ComboId, off);
        Thread.Sleep(SaveMargin);

        Assert.Equal("None", SavedGrid(instance));
    }
}
