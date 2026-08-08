using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="RecordingRingBuffer{T}"/> ── 事前バッファの退避規則。
///
/// <para>
/// 実体の要素は <c>Gst.Buffer</c>（ネイティブ）で L1 からは触れないため、
/// 総称型に切り出したロジックを文字列要素で検証する。ここが壊れると
/// 「事前バッファが丸ごと消える」「解放済みバッファを押し込む」という形で
/// アプリの中核価値（録画ボタンを押す前の映像が残る）が静かに失われる。
/// </para>
/// </summary>
public class RecordingRingBufferTests
{
    private const ulong OneSecondNs = 1_000_000_000UL;

    [Fact]
    public void Evict_DropsItemsOlderThanTheWindow_AndReturnsThemForDisposal()
    {
        var buffer = new RecordingRingBuffer<string>();
        buffer.Enqueue(0, "old", 10);
        buffer.Enqueue(1 * OneSecondNs, "at-the-boundary", 10);
        buffer.Enqueue(3 * OneSecondNs, "new", 10);

        // 境界は「ちょうど窓の長さ経過」も退避（>=）。窓 2 秒・現在 3 秒なら、
        // 経過 3 秒の "old" と経過ちょうど 2 秒の "at-the-boundary" が退避される。
        var evicted = buffer.Evict(currentPts: 3 * OneSecondNs, maxAgeNs: 2 * OneSecondNs, maxTotalBytes: ulong.MaxValue);

        Assert.Equal(new[] { "old", "at-the-boundary" }, evicted);
        Assert.Equal(1, buffer.Count);
        Assert.Equal(10UL, buffer.TotalBytes);
    }

    /// <summary>
    /// <b>maxAgeNs=0（事前バッファ無し）でも、いま入れた1件は残る。</b>
    /// 残さないと、呼び出し側が退避分を解放した直後にその要素を押し込むことになり、
    /// 「録画は成功して終了コード 0 なのに、中身の無い MP4 が残る」形で壊れる
    /// ── 0 は UI からも設定できる正当な下限値。
    /// </summary>
    [Fact]
    public void Evict_ZeroWindow_AlwaysKeepsTheNewestItem()
    {
        var buffer = new RecordingRingBuffer<string>();
        buffer.Enqueue(1 * OneSecondNs, "old", 10);
        buffer.Enqueue(2 * OneSecondNs, "new", 10);

        var evicted = buffer.Evict(currentPts: 2 * OneSecondNs, maxAgeNs: 0, maxTotalBytes: ulong.MaxValue);

        Assert.Equal(new[] { "old" }, evicted);
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void Evict_SizeCap_DropsRegardlessOfAge_UntilUnderTheCap()
    {
        var buffer = new RecordingRingBuffer<string>();
        buffer.Enqueue(0, "a", 60);
        buffer.Enqueue(1, "b", 60);
        buffer.Enqueue(2, "c", 60);

        // 全要素が時間窓の内側でも、合計サイズが上限を超えていれば上限内に収まるまで古い方から捨てる
        var evicted = buffer.Evict(currentPts: 2, maxAgeNs: ulong.MaxValue, maxTotalBytes: 150);

        Assert.Equal(new[] { "a" }, evicted);
        Assert.Equal(120UL, buffer.TotalBytes);
    }

    /// <summary>
    /// PTS が巻き戻る（ソースの再起動）と符号なし減算がアンダーフローし、時間条件は
    /// 「全部古い」に倒れる ── それでも直近の1件は残る（現在の実装の制約の固定。
    /// 二次防御としてのサイズ上限と「1件は残す」ガードが、この状況の被害を
    /// 「事前バッファの短縮」までに抑えている）。
    /// </summary>
    [Fact]
    public void Evict_PtsRewind_KeepsOnlyTheNewestItem()
    {
        var buffer = new RecordingRingBuffer<string>();
        buffer.Enqueue(5 * OneSecondNs, "before-rewind", 10);
        buffer.Enqueue(6 * OneSecondNs, "also-before", 10);
        buffer.Enqueue(1, "after-rewind", 10);

        var evicted = buffer.Evict(currentPts: 1, maxAgeNs: 10 * OneSecondNs, maxTotalBytes: ulong.MaxValue);

        Assert.Equal(new[] { "before-rewind", "also-before" }, evicted);
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void Evict_NothingExpired_EvictsNothing()
    {
        var buffer = new RecordingRingBuffer<string>();
        buffer.Enqueue(1 * OneSecondNs, "a", 10);
        buffer.Enqueue(2 * OneSecondNs, "b", 10);

        Assert.Empty(buffer.Evict(currentPts: 2 * OneSecondNs, maxAgeNs: 5 * OneSecondNs, maxTotalBytes: 1000));
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void Enumeration_YieldsItemsInArrivalOrder_WithTheirPts()
    {
        var buffer = new RecordingRingBuffer<string>();
        buffer.Enqueue(1, "a", 10);
        buffer.Enqueue(2, "b", 10);

        Assert.Equal(new[] { (1UL, "a"), (2UL, "b") }, buffer.ToArray());
    }

    [Fact]
    public void DrainAll_EmptiesTheBufferAndReturnsEverything()
    {
        var buffer = new RecordingRingBuffer<string>();
        buffer.Enqueue(1, "a", 10);
        buffer.Enqueue(2, "b", 10);

        Assert.Equal(new[] { "a", "b" }, buffer.DrainAll());
        Assert.Equal(0, buffer.Count);
        Assert.Equal(0UL, buffer.TotalBytes);
    }
}
