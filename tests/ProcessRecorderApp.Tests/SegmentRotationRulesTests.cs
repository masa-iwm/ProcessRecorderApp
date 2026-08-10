using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>常時録画のセグメントをいつ切り替えるかの規則。</b>
/// 実装（<c>ContinuousRecorder</c>）は GStreamer を要するので L1 から動かせない。
/// 規則そのものはここで縛る。
/// </summary>
public class SegmentRotationRulesTests
{
    private static readonly long FiveSeconds = SegmentRotationRules.SegmentNanoseconds(5);

    [Fact]
    public void SecondsBecomeNanoseconds()
    {
        Assert.Equal(5_000_000_000L, SegmentRotationRules.SegmentNanoseconds(5));
        Assert.Equal(0L, SegmentRotationRules.SegmentNanoseconds(0));
        Assert.Equal(0L, SegmentRotationRules.SegmentNanoseconds(-1));
    }

    [Fact]
    public void BeforeTheSegmentIsFull_NothingRotates()
    {
        Assert.False(SegmentRotationRules.ShouldRotate(FiveSeconds - 1, FiveSeconds, isKeyframe: true));
        Assert.False(SegmentRotationRules.ShouldRotate(0, FiveSeconds, isKeyframe: true));
    }

    /// <summary>
    /// <b>これが本体。</b> 非キーフレームで切ると、次のセグメントの先頭が P フレームになり、
    /// 書き出し側の I フレームゲートが次の I まで捨てる ── そのぶんの映像が丸ごと欠ける。
    /// 「時間どおりに切る」より「1 フレームも落とさない」を優先する。
    /// </summary>
    [Fact]
    public void ANonKeyframe_NeverRotatesEvenWhenTheSegmentIsOverdue()
    {
        Assert.False(SegmentRotationRules.ShouldRotate(FiveSeconds, FiveSeconds, isKeyframe: false));
        Assert.False(SegmentRotationRules.ShouldRotate(FiveSeconds * 100, FiveSeconds, isKeyframe: false));
    }

    [Fact]
    public void TheFirstKeyframeAtOrAfterTheSegmentLength_Rotates()
    {
        Assert.True(SegmentRotationRules.ShouldRotate(FiveSeconds, FiveSeconds, isKeyframe: true));
        Assert.True(SegmentRotationRules.ShouldRotate(FiveSeconds + 1, FiveSeconds, isKeyframe: true));
    }

    [Fact]
    public void ASegmentLengthOfZero_MeansNeverRotate()
    {
        Assert.False(SegmentRotationRules.ShouldRotate(long.MaxValue, 0, isKeyframe: true));
        Assert.False(SegmentRotationRules.IsOvershooting(long.MaxValue, 0));
    }

    /// <summary>
    /// 超過の閾値は「2 倍」と「＋30 秒」の<b>厳しい方</b>。短いセグメントでは 2 倍、
    /// 長いセグメントでは ＋30 秒が効く。
    /// </summary>
    [Fact]
    public void ShortSegments_ReportOvershootAtTwiceTheLength()
    {
        Assert.False(SegmentRotationRules.IsOvershooting(FiveSeconds * 2 - 1, FiveSeconds));
        Assert.True(SegmentRotationRules.IsOvershooting(FiveSeconds * 2, FiveSeconds));
    }

    [Fact]
    public void LongSegments_ReportOvershootAfterTheGraceInsteadOfDoubling()
    {
        long tenMinutes = SegmentRotationRules.SegmentNanoseconds(600);

        Assert.False(SegmentRotationRules.IsOvershooting(tenMinutes + SegmentRotationRules.OvershootGraceNs - 1, tenMinutes));
        Assert.True(SegmentRotationRules.IsOvershooting(tenMinutes + SegmentRotationRules.OvershootGraceNs, tenMinutes));
        // 2 倍（20 分）を待たずに報告される
        Assert.True(SegmentRotationRules.IsOvershooting(tenMinutes + SegmentRotationRules.OvershootGraceNs, tenMinutes));
    }

    [Fact]
    public void OnTimeSegments_AreNotReportedAsOvershooting()
    {
        Assert.False(SegmentRotationRules.IsOvershooting(FiveSeconds, FiveSeconds));
    }

    /// <summary>閾値の計算が <see cref="long"/> を溢れさせないこと。</summary>
    [Fact]
    public void AnAbsurdSegmentLength_DoesNotOverflowIntoAFalsePositive()
    {
        Assert.False(SegmentRotationRules.IsOvershooting(0, long.MaxValue));
        Assert.False(SegmentRotationRules.IsOvershooting(long.MaxValue - 1, long.MaxValue));
    }
}
