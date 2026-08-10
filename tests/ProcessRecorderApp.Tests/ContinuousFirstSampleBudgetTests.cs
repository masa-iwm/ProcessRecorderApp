using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>常時枝から最初のフレームが来るのを待つ予算。</b>
///
/// <para>
/// <c>appsink async=false</c> のおかげで、この枝が 1 フレームも出さなくても
/// パイプラインは <c>PLAYING</c> になる ── イベント録画を人質に取らせないための設計だが、
/// 代わりに「常時録画 on なのに何も起きない」が無音の失敗になる。
/// その無音を破るのがこの予算なので、<b>短すぎても長すぎても役に立たない</b>。
/// </para>
/// </summary>
public class ContinuousFirstSampleBudgetTests
{
    /// <summary>
    /// 下限はイベント側の <c>PLAYING</c> 到達予算と同じ値でなければならない
    /// （測っているものが同じ「エンコーダーが最初の 1 枚を出すまで」だから）。
    /// </summary>
    [Fact]
    public void TheFloorIsTheSameAsThePlayingStateBudget()
    {
        Assert.Equal(EventRecorder.PlayingStateTimeoutMs, ContinuousFirstSampleBudget.MinimumMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0/1")]
    [InlineData("5/0")]
    [InlineData("-5/1")]
    [InlineData("5/1/1")]
    public void AnUnreadableFramerate_FallsBackToTheFloor(string? framerate)
    {
        Assert.False(ContinuousFirstSampleBudget.TryParseFramerate(framerate, out _, out _));
        Assert.Equal(ContinuousFirstSampleBudget.MinimumMs, ContinuousFirstSampleBudget.For(framerate));
    }

    [Fact]
    public void AFractionAndABareNumberAreBothRead()
    {
        Assert.True(ContinuousFirstSampleBudget.TryParseFramerate("30/1", out int n, out int d));
        Assert.Equal(30, n);
        Assert.Equal(1, d);

        Assert.True(ContinuousFirstSampleBudget.TryParseFramerate("15", out n, out d));
        Assert.Equal(15, n);
        Assert.Equal(1, d);

        Assert.True(ContinuousFirstSampleBudget.TryParseFramerate("30000/1001", out n, out d));
        Assert.Equal(30000, n);
        Assert.Equal(1001, d);
    }

    /// <summary>
    /// <b>低いフレームレートでは予算が伸びる。</b> ここが伸びないと、
    /// 1fps で回している常時録画が毎回「来ない」と誤報を出す
    /// ── <c>PlayingStateTimeoutMs</c> の doc が名指ししている唯一の誤検出形そのもの。
    /// </summary>
    [Fact]
    public void ALowFramerate_GetsMoreTimeThanTheFloor()
    {
        int oneFps = ContinuousFirstSampleBudget.For("1/1");
        Assert.True(ContinuousFirstSampleBudget.MinimumMs < oneFps,
            $"1fps の予算が下限のままでは誤報になる（{oneFps}ms）");
    }

    [Fact]
    public void AHighFramerate_StaysAtTheFloor()
    {
        // 60fps ならプライム分は 8 フレーム＝約 130ms。下限からわずかに伸びるだけで、
        // 「速いソースなのに待たされる」ことは無い。
        int fast = ContinuousFirstSampleBudget.For("60/1");
        Assert.InRange(fast, ContinuousFirstSampleBudget.MinimumMs, ContinuousFirstSampleBudget.MinimumMs + 250);
    }

    [Fact]
    public void AnAbsurdlyLowFramerate_IsCappedInsteadOfWaitingForever()
    {
        Assert.Equal(ContinuousFirstSampleBudget.MaximumMs, ContinuousFirstSampleBudget.For("1/60"));
        Assert.True(ContinuousFirstSampleBudget.MinimumMs < ContinuousFirstSampleBudget.MaximumMs);
    }

    [Fact]
    public void TheBudgetNeverGoesBackwardsAsTheFramerateDrops()
    {
        int fast = ContinuousFirstSampleBudget.For("30/1");
        int medium = ContinuousFirstSampleBudget.For("5/1");
        int slow = ContinuousFirstSampleBudget.For("1/1");

        Assert.True(fast <= medium);
        Assert.True(medium <= slow);
    }
}
