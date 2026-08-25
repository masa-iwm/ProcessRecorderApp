using System;
using System.Collections.Generic;

namespace ProcessRecorderApp.Components;

/// <summary>切り出した fMP4 セグメント 1 件。</summary>
/// <param name="Kind">種別。</param>
/// <param name="Bytes">この呼び出しのために確定させた連続バイト列（内部緩衝は指さない）。</param>
/// <param name="StartsWithSync">先頭サンプルが同期サンプルか（Init では常に false）。</param>
/// <param name="DecodeTime">
/// この fragment の先頭サンプルの復号時刻（<c>moof/traf/tfdt</c> の
/// <c>baseMediaDecodeTime</c>。単位は <c>mdhd</c> の timescale）。
/// <c>tfdt</c> が無ければ 0、<see cref="PreviewSegmentKind.Init"/> では常に 0。
/// </param>
public readonly record struct Fmp4Segment(
    PreviewSegmentKind Kind, byte[] Bytes, bool StartsWithSync, ulong DecodeTime = 0);

/// <summary>
/// <c>mp4mux fragment-mode=dash-or-mss</c> の appsink 出力を、ISO-BMFF の
/// <b>トップレベル箱の境界</b>で Init / Media に切る逐次状態機械。
///
/// <para>
/// appsink から届く buffer の切れ目は箱の境界と<b>一致しない</b>ので、
/// 何バイトずつ <see cref="Push"/> しても結果が同じになるように状態を持つ。
/// </para>
/// <para>
/// <b>Init</b> は先頭から最初の <c>moov</c> の末尾まで（<c>ftyp</c> 等をそのまま含む）。
/// <b>Media</b> は <c>moof</c> ＋ 直後の <c>mdat</c> の 1 対。
/// Init のあとに現れるそれ以外のトップレベル箱（EOS 時に追記される <c>mfra</c> と
/// 2 つ目の <c>moov</c>、<c>free</c> / <c>sidx</c> / <c>styp</c>）は<b>捨てる</b>
/// ── MSE へ渡す意味が無く、捨てても後続の Media は続く。
/// </para>
/// <para>
/// <b>壊れた入力は握り潰さず <see cref="IsFaulted"/> にする。</b>
/// ライブでは <c>size==0</c>（「ファイル末尾まで」）は決着しないし、
/// <c>moov</c> より前の <c>moof</c> や <c>moof</c> 単独は MSE を復帰不能にする。
/// fault 後の <see cref="Push"/> は無視される（供給側は購読を切って作り直す）。
/// </para>
/// <para>
/// <b>供給側は <see cref="TryDequeue"/> で必ず引き取ること。</b> 確定したセグメントは
/// 取り出されるまで内部に溜まる。溜めたままにすると使用量が青天井になるので、
/// 未取得が <see cref="MaxPendingSegments"/> 件を超えたところで
/// <see cref="IsFaulted"/> にする（黙って捨てると MSE が穴の開いた列を掴む）。
/// </para>
/// <para>スレッド安全ではない。appsink の 1 本のコールバックスレッドからだけ使う。</para>
/// </summary>
public sealed class Fmp4SegmentSplitter
{
    /// <summary>1 箱の上限。これを超えたら壊れた入力とみなす（<c>mdat</c> は 1 fragment ぶん）。</summary>
    public const int MaxBoxSize = 32 * 1024 * 1024;

    /// <summary>
    /// <see cref="TryDequeue"/> で取り出されずに溜められるセグメントの上限。
    /// 超えたら壊れた使い方とみなして <see cref="IsFaulted"/> にする。
    /// </summary>
    public const int MaxPendingSegments = 64;

    /// <summary>size(4) ＋ type(4)。</summary>
    private const int BoxHeaderSize = 8;

    /// <summary>size(4) ＋ type(4) ＋ largesize(8)。</summary>
    private const int LargeBoxHeaderSize = 16;

    /// <summary><c>sample_is_non_sync_sample</c>（ISO/IEC 14496-12, 8.8.3.1 の packed 32bit）。</summary>
    private const uint NonSyncSampleFlag = 0x00010000;

    private readonly Queue<Fmp4Segment> _ready = new();

    private byte[] _buffer = [];
    private int _count;
    private int _scan;

    private bool _initEmitted;
    private uint _trexDefaultSampleFlags;
    private byte[]? _pendingMoof;

    /// <summary>壊れた入力を見つけたか。</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>壊れた理由（ログ用・英語）。</summary>
    public string? Fault { get; private set; }

    /// <summary>appsink から届いたバイト列を流し込む。区切り方は結果に影響しない。</summary>
    public void Push(ReadOnlySpan<byte> bytes)
    {
        if (IsFaulted || bytes.IsEmpty)
            return;

        Append(bytes);
        Parse();

        // init が決まるまで先頭は切り落とせない（Init はストリームの先頭から始まる）。
        // moov が来ないまま溜まり続ける入力は、ここで打ち切らないと緩衝が青天井になる。
        if (!IsFaulted && !_initEmitted && _count > MaxBoxSize)
            SetFault($"buffered {_count} bytes without a moov, over the {MaxBoxSize} byte limit");
    }

    /// <summary>
    /// 確定したセグメントを 1 件取り出す。
    /// <b>供給側はこれで必ず引き取ること</b> ── 溜めたままにすると
    /// <see cref="MaxPendingSegments"/> を超えたところで <see cref="IsFaulted"/> になる。
    /// </summary>
    public bool TryDequeue(out Fmp4Segment segment) => _ready.TryDequeue(out segment);

    private void Append(ReadOnlySpan<byte> bytes)
    {
        int required = _count + bytes.Length;
        if (_buffer.Length < required)
        {
            int capacity = Math.Max(required, _buffer.Length == 0 ? 8192 : _buffer.Length + (_buffer.Length / 2));
            Array.Resize(ref _buffer, capacity);
        }

        bytes.CopyTo(_buffer.AsSpan(_count));
        _count = required;
    }

    private void Parse()
    {
        while (true)
        {
            int available = _count - _scan;
            if (available < BoxHeaderSize)
                break;

            long size = ReadU32(_buffer, _scan);
            int header = BoxHeaderSize;

            if (size == 1)
            {
                if (available < LargeBoxHeaderSize)
                    break;

                size = ReadU64(_buffer, _scan + BoxHeaderSize);
                header = LargeBoxHeaderSize;
            }
            else if (size == 0)
            {
                // 「以後ファイル末尾まで」。長さが決まらないので、ライブでは切りようがない。
                SetFault("top-level box declares size 0 (extends to end of stream)");
                return;
            }

            // 64bit の largesize は負にもなりうる。int へ落とす前にここで弾く。
            if (size < header)
            {
                SetFault($"top-level box '{TypeName(_buffer, _scan + 4)}' declares size {size}, smaller than its header");
                return;
            }
            if (size > MaxBoxSize)
            {
                SetFault($"top-level box '{TypeName(_buffer, _scan + 4)}' declares size {size}, over the {MaxBoxSize} byte limit");
                return;
            }
            if (available < size)
                break;

            HandleBox(_scan, (int)size, header);
            if (IsFaulted)
                return;

            _scan += (int)size;
            Compact();
        }

        Compact();
    }

    private void HandleBox(int start, int length, int header)
    {
        int typeOffset = start + 4;

        if (!_initEmitted)
        {
            if (IsType(_buffer, typeOffset, "moof"))
            {
                SetFault("moof arrived before moov");
                return;
            }

            if (!IsType(_buffer, typeOffset, "moov"))
                return;   // ftyp / free などは init セグメントの一部としてそのまま抱える

            int end = start + length;
            _trexDefaultSampleFlags = ReadTrexDefaultSampleFlags(_buffer, start + header, end);
            Enqueue(new Fmp4Segment(PreviewSegmentKind.Init, _buffer.AsSpan(0, end).ToArray(), StartsWithSync: false));
            if (IsFaulted)
                return;

            _initEmitted = true;
            return;
        }

        if (IsType(_buffer, typeOffset, "moof"))
        {
            if (_pendingMoof is not null)
            {
                SetFault("moof followed by another moof");
                return;
            }

            _pendingMoof = _buffer.AsSpan(start, length).ToArray();
            return;
        }

        if (IsType(_buffer, typeOffset, "mdat"))
        {
            if (_pendingMoof is null)
            {
                SetFault("mdat without a preceding moof");
                return;
            }

            byte[] moof = _pendingMoof;
            _pendingMoof = null;

            byte[] media = new byte[moof.Length + length];
            moof.CopyTo(media, 0);
            _buffer.AsSpan(start, length).CopyTo(media.AsSpan(moof.Length));

            Enqueue(new Fmp4Segment(
                PreviewSegmentKind.Media, media, StartsWithSync(moof), ReadBaseMediaDecodeTime(moof)));
            return;
        }

        if (_pendingMoof is not null)
        {
            SetFault($"moof followed by '{TypeName(_buffer, typeOffset)}' instead of mdat");
            return;
        }

        // mfra・EOS 時に追記される 2 つ目の moov・free / sidx / styp は捨てる。
    }

    /// <summary>
    /// 確定したセグメントをキューへ積む。<see cref="MaxPendingSegments"/> を超えるなら
    /// 積まずに fault にする ── 古い方を捨てると、MSE へ穴の開いた列が渡る。
    /// </summary>
    private void Enqueue(Fmp4Segment segment)
    {
        if (_ready.Count >= MaxPendingSegments)
        {
            SetFault($"{MaxPendingSegments} segments are pending; the supplier must drain them with TryDequeue");
            return;
        }

        _ready.Enqueue(segment);
    }

    /// <summary>消費済みの先頭を切り落とす。init が確定するまでは先頭を保持し続ける。</summary>
    private void Compact()
    {
        int retain = _initEmitted ? _scan : 0;
        if (retain <= 0)
            return;

        Buffer.BlockCopy(_buffer, retain, _buffer, 0, _count - retain);
        _count -= retain;
        _scan -= retain;
    }

    private void SetFault(string reason)
    {
        IsFaulted = true;
        Fault = reason;
        _pendingMoof = null;
    }

    /// <summary>
    /// この fragment の<b>先頭サンプル</b>が同期サンプルか。
    ///
    /// <para>
    /// 優先順は具体的なものから: <c>trun.first_sample_flags</c> →
    /// <c>trun</c> の <c>sample_flags[0]</c> → <c>tfhd.default_sample_flags</c> →
    /// <c>trex.default_sample_flags</c>。どこにも無ければ 0（＝同期）。
    /// <c>trun</c> が無い、または <c>sample_count==0</c> なら false
    /// ── 判定材料が無いものを同期扱いにすると、MSE が復帰できない先頭を掴む。
    /// </para>
    /// </summary>
    private bool StartsWithSync(ReadOnlySpan<byte> moof)
    {
        if (!TryBoxContent(moof, out int moofStart, out int moofEnd))
            return false;
        if (!TryFindChild(moof, moofStart, moofEnd, "traf", out int trafStart, out int trafEnd))
            return false;
        if (!TryFindChild(moof, trafStart, trafEnd, "trun", out int trunStart, out int trunEnd))
            return false;

        // version(1) flags(3) sample_count(4)
        if (trunStart + 8 > trunEnd)
            return false;

        uint trunFlags = ReadU24(moof, trunStart + 1);
        if (ReadU32AsUInt(moof, trunStart + 4) == 0)
            return false;

        uint? firstSampleFlags = null;
        uint? sampleZeroFlags = null;

        int offset = trunStart + 8;
        if ((trunFlags & 0x000001) != 0)
            offset += 4;                                        // data-offset-present
        if ((trunFlags & 0x000004) != 0)
        {
            if (offset + 4 <= trunEnd)
                firstSampleFlags = ReadU32AsUInt(moof, offset);  // first-sample-flags-present
            offset += 4;
        }
        if ((trunFlags & 0x000400) != 0)
        {
            int sampleOffset = offset;
            if ((trunFlags & 0x000100) != 0)
                sampleOffset += 4;                              // sample-duration-present
            if ((trunFlags & 0x000200) != 0)
                sampleOffset += 4;                              // sample-size-present
            if (sampleOffset + 4 <= trunEnd)
                sampleZeroFlags = ReadU32AsUInt(moof, sampleOffset);
        }

        uint effective = firstSampleFlags
            ?? sampleZeroFlags
            ?? ReadTfhdDefaultSampleFlags(moof, trafStart, trafEnd)
            ?? _trexDefaultSampleFlags;

        return (effective & NonSyncSampleFlag) == 0;
    }

    private static uint? ReadTfhdDefaultSampleFlags(ReadOnlySpan<byte> data, int trafStart, int trafEnd)
    {
        if (!TryFindChild(data, trafStart, trafEnd, "tfhd", out int start, out int end))
            return null;
        if (start + 8 > end)
            return null;

        uint flags = ReadU24(data, start + 1);

        int offset = start + 8;                                  // version/flags(4) track_ID(4)
        if ((flags & 0x000001) != 0)
            offset += 8;                                         // base-data-offset-present
        if ((flags & 0x000002) != 0)
            offset += 4;                                         // sample-description-index-present
        if ((flags & 0x000008) != 0)
            offset += 4;                                         // default-sample-duration-present
        if ((flags & 0x000010) != 0)
            offset += 4;                                         // default-sample-size-present

        if ((flags & 0x000020) == 0 || offset + 4 > end)
            return null;                                         // default-sample-flags-present

        return ReadU32AsUInt(data, offset);
    }

    /// <summary><c>moov/mvex/trex</c> の <c>default_sample_flags</c>。無ければ 0。</summary>
    private static uint ReadTrexDefaultSampleFlags(ReadOnlySpan<byte> data, int moovStart, int moovEnd)
    {
        if (!TryFindChild(data, moovStart, moovEnd, "mvex", out int mvexStart, out int mvexEnd))
            return 0;
        if (!TryFindChild(data, mvexStart, mvexEnd, "trex", out int trexStart, out int trexEnd))
            return 0;

        // version/flags(4) track_ID(4) default_sample_description_index(4)
        // default_sample_duration(4) default_sample_size(4) default_sample_flags(4)
        if (trexStart + 24 > trexEnd)
            return 0;

        return ReadU32AsUInt(data, trexStart + 20);
    }

    /// <summary>
    /// この fragment の先頭サンプルの復号時刻（<c>moof/traf/tfdt</c> の
    /// <c>baseMediaDecodeTime</c>）。<c>tfdt</c> が無い・短いなら 0。
    ///
    /// <para>
    /// version 0 は 32bit、version 1 は 64bit。<b>version を見ずに幅を決め打ちしない</b>
    /// ── 同梱 mp4mux は version 1 を書くが、それは実装の都合であって規格の要求ではない。
    /// </para>
    /// </summary>
    internal static ulong ReadBaseMediaDecodeTime(ReadOnlySpan<byte> moof)
    {
        if (!TryBoxContent(moof, out int moofStart, out int moofEnd))
            return 0;
        if (!TryFindChild(moof, moofStart, moofEnd, "traf", out int trafStart, out int trafEnd))
            return 0;
        if (!TryFindChild(moof, trafStart, trafEnd, "tfdt", out int start, out int end))
            return 0;

        // **version を読む前に境界を見る**（<c>Fmp4InitInfo</c> の <c>mdhd</c> と同じ理由）
        // ── 切り詰められた <c>tfdt</c> では中身が空になりうる。
        if (start >= end)
            return 0;

        // version(1) flags(3) baseMediaDecodeTime(4 or 8)
        byte version = moof[start];
        if (version == 0)
            return start + 8 <= end ? ReadU32AsUInt(moof, start + 4) : 0;

        return start + 12 <= end ? (ulong)ReadU64(moof, start + 4) : 0;
    }

    /// <summary>
    /// <paramref name="position"/> にある箱を 1 つ読み進める。
    /// <see cref="TryFindChild"/> は最初の 1 件しか返さないので、<b>同じ型の兄弟を
    /// 全部見たい側</b>（<see cref="Fmp4InitInfo"/> の trak 走査）はこちらを使う。
    /// </summary>
    /// <returns>読めたら true（<paramref name="position"/> は次の箱へ進む）。</returns>
    internal static bool TryNextBox(
        ReadOnlySpan<byte> data, ref int position, int end,
        out int typeOffset, out int contentStart, out int contentEnd)
    {
        typeOffset = 0;
        contentStart = 0;
        contentEnd = 0;

        if (position + BoxHeaderSize > end)
            return false;

        long size = ReadU32(data, position);
        int header = BoxHeaderSize;

        if (size == 1)
        {
            if (position + LargeBoxHeaderSize > end)
                return false;
            size = ReadU64(data, position + BoxHeaderSize);
            header = LargeBoxHeaderSize;
        }
        else if (size == 0)
        {
            // 「以後この段の終わりまで」。1 段の走査では最後の箱にしかなりえない。
            size = end - position;
        }

        if (size < header || position + size > end)
            return false;

        typeOffset = position + 4;
        contentStart = position + header;
        contentEnd = (int)(position + size);
        position = contentEnd;
        return true;
    }

    /// <summary>単独の箱として渡された配列の中身の範囲。</summary>
    internal static bool TryBoxContent(ReadOnlySpan<byte> box, out int contentStart, out int contentEnd)
    {
        contentStart = 0;
        contentEnd = 0;
        if (box.Length < BoxHeaderSize)
            return false;

        int header = ReadU32(box, 0) == 1 ? LargeBoxHeaderSize : BoxHeaderSize;
        if (box.Length < header)
            return false;

        contentStart = header;
        contentEnd = box.Length;
        return true;
    }

    /// <summary><paramref name="start"/> から <paramref name="end"/> の 1 段の箱から最初の 1 件を探す。</summary>
    internal static bool TryFindChild(
        ReadOnlySpan<byte> data, int start, int end, string type, out int contentStart, out int contentEnd)
    {
        int position = start;
        while (TryNextBox(data, ref position, end, out int typeOffset, out contentStart, out contentEnd))
        {
            if (IsType(data, typeOffset, type))
                return true;
        }

        contentStart = 0;
        contentEnd = 0;
        return false;
    }

    internal static bool IsType(ReadOnlySpan<byte> data, int offset, string type)
        => data[offset] == type[0]
        && data[offset + 1] == type[1]
        && data[offset + 2] == type[2]
        && data[offset + 3] == type[3];

    /// <summary>失敗メッセージ用。印字できない型は <c>?</c> にする。</summary>
    internal static string TypeName(ReadOnlySpan<byte> data, int offset)
    {
        Span<char> characters = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte value = data[offset + i];
            characters[i] = value is >= 0x20 and < 0x7F ? (char)value : '?';
        }
        return new string(characters);
    }

    internal static long ReadU32(ReadOnlySpan<byte> data, int offset)
        => ((long)data[offset] << 24) | ((long)data[offset + 1] << 16)
         | ((long)data[offset + 2] << 8) | data[offset + 3];

    internal static uint ReadU32AsUInt(ReadOnlySpan<byte> data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
         | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static uint ReadU24(ReadOnlySpan<byte> data, int offset)
        => ((uint)data[offset] << 16) | ((uint)data[offset + 1] << 8) | data[offset + 2];

    internal static long ReadU64(ReadOnlySpan<byte> data, int offset)
    {
        long value = 0;
        for (int i = 0; i < 8; i++)
            value = (value << 8) | data[offset + i];
        return value;
    }
}
