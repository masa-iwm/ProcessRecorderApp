using System;
using System.Collections.Generic;

namespace ProcessRecorderApp.Components;

/// <summary>
/// <c>mp4mux</c> が吐く fragment（<see cref="Fmp4SegmentSplitter"/> が切った Media）を
/// <b>DASH のセグメント</b>へ集約する逐次状態機械。
///
/// <para>
/// <b>セグメントの切れ目は同期サンプル（IDR）である。</b> DASH のクライアントは
/// セグメントの先頭から復号を始めるので、非同期始まりの fragment を単独のセグメントとして
/// 出すと、そこから参加したクライアントは<b>永久に絵が出ない</b>。
/// </para>
/// <para>
/// <b>長さは次のセグメントが来るまで決まらない。</b> <c>SegmentTimeline</c> の <c>d</c> は
/// 「次の <c>t</c> との差」なので、保留中の 1 本は次の同期 fragment が来た時点で確定する
/// ── つまり<b>常に 1 セグメント分だけ遅れる</b>のがこの機構の設計である。
/// </para>
/// <para>
/// <b>壊れた入力は握り潰さず <see cref="IsFaulted"/> にする。</b> 時刻の巻き戻しを
/// そのまま流すと <c>SegmentTimeline</c> が単調でなくなり、クライアントは復帰できない。
/// </para>
/// <para>スレッド安全ではない。mux の appsink の 1 本のコールバックスレッドからだけ使う。</para>
/// </summary>
public sealed class DashSegmentAssembler
{
    private readonly Queue<DashMediaSegment> _ready = new();
    private readonly List<byte[]> _pending = [];

    private ulong _pendingTime;
    private bool _hasPending;

    /// <summary>壊れた入力を見つけたか。</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>壊れた理由（ログ用・英語。<c>dash.stream-stop</c> の reason になる）。</summary>
    public string? Fault { get; private set; }

    /// <summary>
    /// 最初の同期 fragment より前に来て捨てた fragment の数。
    /// <b>0 でないことは異常ではない</b> ── mux を起こした直後は IDR まで非同期が続く。
    /// </summary>
    public int DroppedLeading { get; private set; }

    /// <summary>fragment 1 つを流し込む。</summary>
    public void Push(in Fmp4Segment media)
    {
        if (IsFaulted)
            return;

        if (!_hasPending)
        {
            // 保留が無いのに非同期始まりが来た＝まだ最初の IDR に届いていない。
            if (!media.StartsWithSync)
            {
                DroppedLeading++;
                return;
            }

            StartPending(in media);
            return;
        }

        // 時刻が進んでいない。ソースの作り直しや並べ替えで、そのまま流すと
        // SegmentTimeline が単調でなくなる。
        if (media.DecodeTime <= _pendingTime)
        {
            SetFault("pts rewind");
            return;
        }

        if (media.StartsWithSync)
        {
            Complete(media.DecodeTime - _pendingTime);
            StartPending(in media);
            return;
        }

        _pending.Add(media.Bytes);
        if (DashPreviewLimits.MaxPendingFragments <= _pending.Count)
            SetFault("gop too long");
    }

    /// <summary>確定したセグメントを 1 件取り出す。</summary>
    public bool TryDequeue(out DashMediaSegment segment) => _ready.TryDequeue(out segment!);

    private void StartPending(in Fmp4Segment media)
    {
        _pending.Clear();
        _pending.Add(media.Bytes);
        _pendingTime = media.DecodeTime;
        _hasPending = true;
    }

    private void Complete(ulong duration)
    {
        int length = 0;
        foreach (byte[] part in _pending)
            length += part.Length;

        var bytes = new byte[length];
        int offset = 0;
        foreach (byte[] part in _pending)
        {
            part.CopyTo(bytes, offset);
            offset += part.Length;
        }

        _ready.Enqueue(new DashMediaSegment(_pendingTime, duration, bytes));
        _pending.Clear();
        _hasPending = false;
    }

    private void SetFault(string reason)
    {
        IsFaulted = true;
        Fault = reason;
        _pending.Clear();
        _hasPending = false;
    }
}
