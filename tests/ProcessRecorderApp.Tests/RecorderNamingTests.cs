using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// レコーダー名の一意化（<see cref="RecorderNaming.MakeUnique"/>）。
///
/// <para>
/// 守っている契約は「CLI から全てのレコーダーへ到達できること」。
/// CLI の解決は<b>完全一致・先勝ち</b>なので、同名が2つあると2つ目は永久に選べない。
/// 画面上は普通に2件並んで見えるため、「コマンドが効かない」ではなく
/// 「毎回1つ目が動く」という気付きにくい形で現れる。
/// </para>
/// </summary>
public class RecorderNamingTests
{
    [Fact]
    public void UnusedName_IsReturnedUnchanged()
    {
        Assert.Equal("Recorder", RecorderNaming.MakeUnique("Recorder", (string[])["Camera", "Screen"]));
    }

    [Fact]
    public void NoExistingNames_ReturnsTheNameUnchanged()
    {
        Assert.Equal("Recorder", RecorderNaming.MakeUnique("Recorder", (string[])[]));
    }

    [Fact]
    public void CollidingName_GetsATwoSuffix()
    {
        Assert.Equal("Recorder (2)", RecorderNaming.MakeUnique("Recorder", (string[])["Recorder"]));
    }

    /// <summary>「追加」を繰り返しても番号が飛ばず、詰めて振られること。</summary>
    [Fact]
    public void RepeatedAdditions_NumberSequentially()
    {
        List<string> existing = [];
        for (int i = 0; i < 4; i++)
            existing.Add(RecorderNaming.MakeUnique("Recorder", existing));

        Assert.Equal(["Recorder", "Recorder (2)", "Recorder (3)", "Recorder (4)"], existing);
    }

    /// <summary>
    /// 途中の番号が空いていればそこを埋める（削除して再追加したときに
    /// 番号が単調増加し続けないこと）。
    /// </summary>
    [Fact]
    public void GapInTheNumbering_IsFilled()
    {
        Assert.Equal("Recorder (3)",
            RecorderNaming.MakeUnique("Recorder", (string[])["Recorder", "Recorder (2)", "Recorder (4)"]));
    }

    /// <summary>
    /// 既に <c> (2)</c> が付いた名前を明示的に指定した場合も、衝突すれば次の番号を足す
    /// （<c>Recorder (2) (2)</c> になる）。見た目は不格好だが、
    /// <b>一意であることの方が重要</b> ── 名前が重複した瞬間に CLI から到達できなくなる。
    /// </summary>
    [Fact]
    public void NameThatAlreadyLooksNumbered_IsSuffixedAgain()
    {
        Assert.Equal("Recorder (2) (2)",
            RecorderNaming.MakeUnique("Recorder (2)", (string[])["Recorder", "Recorder (2)"]));
    }

    /// <summary>
    /// 比較は序数（大文字小文字を区別する）。CLI の解決が <c>==</c> なので、
    /// <c>Recorder</c> と <c>recorder</c> は CLI から別物として到達できる
    /// ── ここで「衝突」と判定すると、到達できる名前を勝手に改名することになる。
    /// </summary>
    [Fact]
    public void ComparisonIsOrdinal_SoCaseDifferencesAreNotCollisions()
    {
        Assert.Equal("recorder", RecorderNaming.MakeUnique("recorder", (string[])["Recorder"]));
    }

    /// <summary>重複した既存名が渡されても無限ループにならず、正しく次の空きを返すこと。</summary>
    [Fact]
    public void DuplicatedExistingNames_AreTolerated()
    {
        Assert.Equal("Recorder (2)",
            RecorderNaming.MakeUnique("Recorder", (string[])["Recorder", "Recorder", "Recorder"]));
    }

    /// <summary>空文字列も名前として扱う（一意化の規則に例外を作らない）。</summary>
    [Fact]
    public void EmptyName_IsTreatedLikeAnyOtherName()
    {
        Assert.Equal(" (2)", RecorderNaming.MakeUnique("", (string[])[""]));
    }
}
