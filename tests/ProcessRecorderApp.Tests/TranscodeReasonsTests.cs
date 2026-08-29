using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 録画トランスコードの文言と <c>session</c> の受理規則。
///
/// <para>
/// <b>文言は HTTP の本文そのものである。</b> クライアントは 409 の本文で
/// 「枠が空くのを待てばよい」を判断し、E2E は完全一致で読む
/// ── 書き換えると、どちらも黙って別の意味になる。
/// </para>
/// </summary>
public sealed class TranscodeReasonsTests
{
    [Fact]
    public void TheReasonsAreTheFrozenStrings()
    {
        Assert.Equal("transcode unavailable", TranscodeReasons.Unavailable);
        Assert.Equal("auxiliary encoder busy", TranscodeReasons.Busy);
        Assert.Equal("recording in progress", TranscodeReasons.InProgress);
        Assert.Equal("transcode start failed", TranscodeReasons.StartFailed);
    }

    /// <summary>
    /// <b>DASH の busy と録画トランスコードの busy は同じ文字列である。</b>
    /// 取り合っているのは同じ計数器なので、片方だけ書き換えると
    /// クライアントは「同じ理由の 409」を 2 通りに扱うことになる。
    /// </summary>
    [Fact]
    public void TheDashBusyReasonIsTheTranscodeBusyReason()
        => Assert.Equal(TranscodeReasons.Busy, DashPreviewReasons.Busy);

    [Theory]
    [InlineData("v1")]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("A-Z_az-09")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")] // 64 文字
    public void ValidSessionIdsAreAccepted(string id)
        => Assert.True(TranscodeOpen.IsValidSessionId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("dot.id")]
    [InlineData("slash/id")]
    [InlineData("back\\slash")]
    [InlineData("quote\"id")]
    [InlineData("パス")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")] // 65 文字
    public void InvalidSessionIdsAreRejected(string? id)
        => Assert.False(TranscodeOpen.IsValidSessionId(id));

    /// <summary>受理する最大長は定数と一致すること（片方だけ動かしても落ちる）。</summary>
    [Fact]
    public void TheLengthBoundMatchesTheConstant()
    {
        Assert.True(TranscodeOpen.IsValidSessionId(new string('a', TranscodeLimits.MaxSessionIdLength)));
        Assert.False(TranscodeOpen.IsValidSessionId(new string('a', TranscodeLimits.MaxSessionIdLength + 1)));
    }

    /// <summary>
    /// 猶予は<b>ライブ DASH の貸出と同じ 10 秒</b>。片方だけ動かすと
    /// 「DASH は消えたのにトランスコードの枠だけ残る」時間帯ができる。
    /// </summary>
    [Fact]
    public void TheGraceMatchesTheDashLease()
    {
        Assert.Equal(10000, TranscodeLimits.GraceMs);
        Assert.Equal(DashPreviewLimits.LeaseMs, TranscodeLimits.GraceMs);
    }
}
