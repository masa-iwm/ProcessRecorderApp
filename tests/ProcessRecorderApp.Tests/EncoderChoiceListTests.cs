using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>設定画面の「優先する H.264 エンコーダー」の選択肢。</b>
///
/// <para>
/// <c>EncoderCatalog.ChoicesFor</c> は純関数で、<b>「どれを出すか・どう印を付けるか」の
/// 判断すべて</b>を持つ。表示文言（ローカライズ）はアプリ層の責務なので含まれない
/// ── そのおかげでこの層で全部検証できる。
/// </para>
/// <para>
/// <b>ここが守っているのは「利用者の値を失わせないこと」と「画面が嘘をつかないこと」。</b>
/// どちらも例外もログも出さずに壊れる種類の欠陥なので、表明で固定するしかない。
/// </para>
/// </summary>
public class EncoderChoiceListTests
{
    /// <summary>カタログの全候補名（順序込み）。</summary>
    private static string[] CatalogNames() =>
        [.. EncoderCatalog.D3d12Candidates
            .Concat(EncoderCatalog.SystemCandidates)
            .Select(c => c.FactoryName)
            .Distinct()];

    private static EncoderProbeReport ProbeWith(params string[] available)
    {
        var entries = CatalogNames()
            .Select(n => new EncoderProbeEntry(n, available.Contains(n, StringComparer.Ordinal)))
            .ToArray();
        return new EncoderProbeReport(entries, [], []);
    }

    /// <summary>
    /// <b>その PC に無いものも出す</b>（意図的な仕様）。絞ると選択肢が機械ごとに変わり、
    /// 設定ファイルを別の機械へ持っていったときに載っていない値が入ることになる。
    /// </summary>
    [Fact]
    public void EveryCatalogEncoderIsOffered_EvenWhenItIsNotPresentOnThisMachine()
    {
        var choices = EncoderCatalog.ChoicesFor("", ProbeWith("x264enc"));

        var offered = choices
            .Where(c => c.Kind == EncoderCatalog.EncoderChoiceKind.Catalog)
            .Select(c => c.Value);

        Assert.Equal(CatalogNames(), offered);
    }

    /// <summary>先頭は必ず「自動選択」＝空文字。既定値がそのまま選べる状態であること。</summary>
    [Fact]
    public void TheFirstChoiceIsAlwaysAutomatic_AndItsValueIsTheEmptyString()
    {
        var choices = EncoderCatalog.ChoicesFor("", ProbeWith());

        Assert.Equal(EncoderCatalog.EncoderChoiceKind.Automatic, choices[0].Kind);
        Assert.Equal("", choices[0].Value);
    }

    /// <summary>実在するものとしないものが、印の有無で区別されること。</summary>
    [Fact]
    public void PresenceOnThisMachineIsMarked()
    {
        var choices = EncoderCatalog.ChoicesFor("", ProbeWith("x264enc", "openh264enc"));

        Assert.True(choices.Single(c => c.Value == "x264enc").Available);
        Assert.True(choices.Single(c => c.Value == "openh264enc").Available);
        Assert.False(choices.Single(c => c.Value == "nvh264enc").Available);
        Assert.False(choices.Single(c => c.Value == "amfh264enc").Available);
    }

    /// <summary>
    /// <b>プローブ結果が無いときは何にも印を付けないこと。</b>
    ///
    /// <para>
    /// <c>EncoderCatalog.Probe</c> は <c>Controller.StaticInitialize()</c> の末尾で走るが、
    /// <c>ProbeWithGStreamer</c> は例外を握り潰すので、走っていない場合がある
    /// （<c>LastProbe</c> は <see langword="null"/>）。
    /// そこで全件に「見つかりません」と付けると<b>画面が嘘をつく</b>
    /// ── 「分からない」を「無い」と表示してはいけない。
    /// </para>
    /// </summary>
    [Fact]
    public void WithoutAProbeResult_NothingIsMarkedAsMissing()
    {
        var choices = EncoderCatalog.ChoicesFor("", probe: null);

        Assert.All(choices, c => Assert.True(c.Available,
            $"'{c.Value}' に「見つかりません」の印が付いている。"
            + Environment.NewLine
            + "プローブが走っていないときは何も分かっていないので、印を付けてはいけない。"));
    }

    /// <summary>
    /// <b>カタログに無い現在値を失わせないこと。</b>
    ///
    /// <para>
    /// 古い名前（<c>nvd3d12h264enc</c> は実際に存在した）・打ち間違い・手で編集された
    /// 設定ファイルの値がそのままだと、コンボボックスは<b>空を表示し、利用者が
    /// 他の行に触れた瞬間にその値を失う</b> ── 編集不可なので打ち直すこともできない。
    /// </para>
    /// </summary>
    [Fact]
    public void AnUnknownCurrentValueIsKeptAsAChoice_SoItCannotBeSilentlyLost()
    {
        var choices = EncoderCatalog.ChoicesFor("nvd3d12h264enc", ProbeWith("x264enc"));

        var kept = choices.SingleOrDefault(c => c.Value == "nvd3d12h264enc");
        Assert.True(kept.Value == "nvd3d12h264enc",
            "カタログに無い現在値が選択肢に含まれていない。"
            + Environment.NewLine
            + "この状態だとコンボボックスは空を表示し、利用者は値を失ったうえ打ち直せない。");

        Assert.Equal(EncoderCatalog.EncoderChoiceKind.Unknown, kept.Kind);

        // 実在するかは調べていない（プローブはカタログの名前しか見ない）ので、
        // 「見つかりません」とは言わない。
        Assert.True(kept.Available);
    }

    /// <summary>
    /// カタログに<b>在る</b>値が現在値のときは、重複して足さないこと。
    /// </summary>
    [Fact]
    public void AKnownCurrentValueIsNotDuplicated()
    {
        var choices = EncoderCatalog.ChoicesFor("nvh264enc", ProbeWith());

        Assert.Single(choices, c => c.Value == "nvh264enc");
        Assert.DoesNotContain(choices, c => c.Kind == EncoderCatalog.EncoderChoiceKind.Unknown);
    }

    /// <summary>
    /// 空・空白・<see langword="null"/> の現在値で「不明」が増えないこと
    /// （空文字は「自動選択」として既に先頭に在る）。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyCurrentValueDoesNotAddAnUnknownChoice(string? current)
    {
        var choices = EncoderCatalog.ChoicesFor(current, ProbeWith());

        Assert.DoesNotContain(choices, c => c.Kind == EncoderCatalog.EncoderChoiceKind.Unknown);
    }

    /// <summary>
    /// <b>保存値が一意であること。</b> 重複があるとコンボボックスの選択が
    /// どちらに当たるか不定になり、表示と保存がずれうる。
    /// </summary>
    [Fact]
    public void ChoiceValuesAreUnique()
    {
        var choices = EncoderCatalog.ChoicesFor("x264enc", ProbeWith("x264enc"));

        Assert.Equal(choices.Count, choices.Select(c => c.Value).Distinct(StringComparer.Ordinal).Count());
    }
}
