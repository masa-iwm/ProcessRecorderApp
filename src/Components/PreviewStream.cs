using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Channels;

namespace ProcessRecorderApp.Components;

/// <summary>fMP4 セグメントの種別。</summary>
public enum PreviewSegmentKind
{
    /// <summary><c>ftyp</c> から最初の <c>moov</c> の末尾まで。1 本の購読で最初の 1 件だけ。</summary>
    Init,

    /// <summary><c>moof</c> ＋ 直後の <c>mdat</c> の 1 対。</summary>
    Media,
}

/// <summary>
/// 購読者へ渡す fMP4 セグメント 1 件。<b>不変</b>。
///
/// <para>
/// <see cref="Bytes"/> は<b>配信側が所有する配列を指してはならない</b>
/// ── 購読者は自分のペースで読むので、配信側が再利用する緩衝を渡すと
/// 読み出しの途中で中身が変わる。呼び出し側が複製してから渡すこと。
/// </para>
/// </summary>
/// <param name="Kind">種別。</param>
/// <param name="Bytes">そのままクライアントへ書き出せる連続したバイト列。</param>
/// <param name="Sequence">配信側が振る通し番号（脱落の検出用）。</param>
/// <param name="StartsWithSync">
/// 先頭サンプルが同期サンプル（IDR）か。<see cref="PreviewSegmentKind.Init"/> では常に false。
/// <b>mp4mux の fragment は時間だけで切られ、キーフレームには揃わない</b>ので、
/// 新規購読者へ最初に渡す <see cref="PreviewSegmentKind.Media"/> はこれが true のものを優先する。
/// </param>
public sealed record PreviewSegment(PreviewSegmentKind Kind, ReadOnlyMemory<byte> Bytes, long Sequence, bool StartsWithSync);

/// <summary>
/// ライブプレビュー配信の上限値。
///
/// <para>
/// <b>上限は供給側だけが持つ。</b> HTTP 層で二重に数えると、片方だけが
/// 減り損ねたときに「席が空いているのに繋がらない」が再現しない形で起きる。
/// </para>
/// </summary>
public static class PreviewStreamLimits
{
    /// <summary>1 レコーダーあたりの同時購読者数の上限。</summary>
    public const int MaxSubscribersPerRecorder = 4;

    /// <summary>プロセス全体の同時購読者数の上限。</summary>
    public const int MaxTotalSubscribers = 8;

    /// <summary>購読者ごとの待ち行列の深さ。溢れたら<b>古い方から捨てる</b>。</summary>
    public const int QueueCapacity = 8;

    /// <summary>
    /// 1 本の購読で許す脱落の累計。これを超えると
    /// <see cref="PreviewSubscription.Publish"/> が false を返し、供給側が切断する。
    /// </summary>
    public const int MaxDroppedSegments = 16;
}

/// <summary>プレビューの供給元。</summary>
public interface IPreviewStreamSource
{
    /// <summary>
    /// <paramref name="target"/> のレコーダーへ購読する。
    /// 対象の解決規則は <c>RecorderCliRules.ResolveTargetIndex</c> と同じ
    /// （数値はインデックス、それ以外は名前の序数完全一致）。
    /// </summary>
    /// <param name="target">インデックスまたはレコーダー名。</param>
    /// <param name="subscription">成功したときの購読。使い終わったら <see cref="IDisposable.Dispose"/>。</param>
    /// <param name="reason">失敗理由（ログ用・英語）。</param>
    bool TrySubscribe(
        string target,
        [NotNullWhen(true)] out PreviewSubscription? subscription,
        [NotNullWhen(false)] out string? reason);
}

/// <summary>
/// 1 購読者ぶんの fMP4 セグメント配信。<b>これが配信側と HTTP 層の契約そのもの</b>。
///
/// <para>
/// <b>最初の 1 件は必ず <see cref="PreviewSegmentKind.Init"/>。</b>
/// MSE は init セグメントより前に media を受け取ると復帰できないので、
/// 順序違反は <see cref="ArgumentException"/> で即座に落とす（黙って捨てない）。
/// </para>
/// <para>
/// <b>2 つ目の Init は来ない。</b> SPS/PPS の変化・PTS の巻き戻し・レコーダーの
/// 閉じ直しは<b>いずれも channel の完了</b>として表現し、クライアントには
/// 再接続させる。1 本の接続の途中で init を差し替える経路を作らないことで、
/// 「途中から別の映像になったのに <c>SourceBuffer</c> は古い init のまま」が構造的に起きない。
/// </para>
/// <para>
/// <b>遅い購読者は配信側を止めない。</b> 待ち行列は
/// <see cref="PreviewStreamLimits.QueueCapacity"/> の bounded で、溢れたら古い方から捨てる。
/// 捨てた件数が <see cref="PreviewStreamLimits.MaxDroppedSegments"/> を超えたら切断する
/// ── 捨て続けても映像は復旧しないので、再接続に倒したほうが早い。
/// 落ちたのが Init なら<b>再送できない</b>ので、その場で完了させる。
/// </para>
/// <para>
/// <see cref="Publish"/> は<b>単一の供給スレッド</b>から呼ぶ（channel は SingleWriter）。
/// <see cref="Complete"/> と <see cref="Dispose"/> は任意のスレッドから何度呼んでもよい。
/// </para>
/// </summary>
public sealed partial class PreviewSubscription : IDisposable
{
    private readonly Channel<PreviewSegment> _channel;
    private readonly Action<PreviewSubscription>? _onDispose;

    private long _dropped;
    private int _completed;
    private int _disposed;
    private int _receivedInit;

    /// <param name="recorderName">配信元のレコーダー名（ログ用）。</param>
    /// <param name="onDispose">
    /// 席の返却。<see cref="Dispose"/> が<b>ちょうど 1 回だけ</b>呼ぶ
    /// ── 2 回呼ぶと上限の残席が増え、上限が事実上無くなる。
    /// </param>
    public PreviewSubscription(string recorderName, Action<PreviewSubscription>? onDispose)
    {
        RecorderName = recorderName;
        _onDispose = onDispose;

        _channel = Channel.CreateBounded<PreviewSegment>(
            new BoundedChannelOptions(PreviewStreamLimits.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,

                // 継続を書き込み側のスレッドで走らせない ── ここは appsink の
                // コールバックスレッドなので、HTTP の書き出しが乗ると mux が止まる。
                AllowSynchronousContinuations = false,
            },
            OnItemDropped);
    }

    /// <summary>配信元のレコーダー名。</summary>
    public string RecorderName { get; }

    /// <summary>購読者が読む側。完了したら再接続すること。</summary>
    public ChannelReader<PreviewSegment> Segments => _channel.Reader;

    /// <summary>この購読で捨てられたセグメントの累計。</summary>
    public long DroppedSegments => Volatile.Read(ref _dropped);

    /// <summary><see cref="PreviewSegmentKind.Init"/> を受け付け済みか。</summary>
    public bool HasReceivedInit => Volatile.Read(ref _receivedInit) != 0;

    /// <summary>
    /// 供給側が 1 件流す。受け付けたら true。
    ///
    /// <para>false を返すのは次の場合で、いずれも<b>この購読はもう使えない</b>:
    /// 既に <see cref="Dispose"/> / <see cref="Complete"/> 済み、2 つ目の Init、
    /// Init が待ち行列から落ちた、脱落が
    /// <see cref="PreviewStreamLimits.MaxDroppedSegments"/> を超えた。</para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// 最初の 1 件が <see cref="PreviewSegmentKind.Init"/> でない。
    /// </exception>
    public bool Publish(PreviewSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _completed) != 0)
            return false;

        if (!HasReceivedInit)
        {
            if (segment.Kind != PreviewSegmentKind.Init)
            {
                throw new ArgumentException(
                    "the first published segment must be an init segment", nameof(segment));
            }
        }
        else if (segment.Kind == PreviewSegmentKind.Init)
        {
            // 2 つ目の init は契約上あり得ない。完了させて再接続に倒す。
            Complete();
            return false;
        }

        if (!_channel.Writer.TryWrite(segment))
        {
            Complete();
            return false;
        }

        if (segment.Kind == PreviewSegmentKind.Init)
            Volatile.Write(ref _receivedInit, 1);

        // TryWrite の中で古い 1 件が落ちていることがある（DropOldest）。
        // 落ちたのが init なら OnItemDropped が既に完了させている。
        if (Volatile.Read(ref _completed) != 0)
            return false;

        if (Volatile.Read(ref _dropped) > PreviewStreamLimits.MaxDroppedSegments)
        {
            Complete();
            return false;
        }

        return true;
    }

    /// <summary>channel を完了させる。以後 <see cref="Publish"/> は false。何度呼んでもよい。</summary>
    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _channel.Writer.TryComplete();
    }

    /// <summary><see cref="Complete"/> ＋ 席の返却。並行に何度呼ばれても返却は 1 回。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Complete();
        _onDispose?.Invoke(this);
    }

    /// <summary>
    /// DropOldest で押し出された 1 件。channel の内部ロックの外で呼ばれるので、
    /// ここから <see cref="Complete"/> を呼んでよい。
    /// </summary>
    private void OnItemDropped(PreviewSegment segment)
    {
        Interlocked.Increment(ref _dropped);

        // init は再送できない ── 落とした時点でこの購読は復帰不能。
        if (segment.Kind == PreviewSegmentKind.Init)
            Complete();
    }
}
