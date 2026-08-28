using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace ProcessRecorderApp.Components;

/// <summary>
/// fragmented MP4 のファイルを <c>moof</c> だけ辿って索引にする。
///
/// <para>
/// <b>これが在って初めて任意の位置へシークできる。</b> 録画は <c>moov</c> を書き直さないので
/// <c>mvhd</c> の尺は 0 のままで、ファイルには時間から位置を引く表（<c>sidx</c>）も無い
/// ── ブラウザは「その秒はどのバイトに在るか」を自力では答えられない。
/// </para>
/// <para>
/// <b>読むのは <c>moof</c> だけ</b>（1 つ数 KB）で、<c>mdat</c> は size を足して飛ばす。
/// 完了時に末尾へ足される <c>mfra</c> と 2 つ目の <c>moov</c>、<c>ftyp</c> / <c>free</c> /
/// <c>sidx</c> / <c>styp</c> は読み飛ばす ── どれもフラグメントではない。
/// </para>
/// <para>
/// <b>入力は「途中までしか無い」のが正常である。</b> 録画中のファイルを読むので、
/// 最後の箱は書き掛けでありうる。切れていたらそこで止め、その箱の先頭を
/// <see cref="ScanResult.NextOffset"/> として返す ── 次回はそこから読み足せる。
/// </para>
/// </summary>
public static class Fmp4FragmentIndex
{
    /// <summary>フラグメント 1 件（<c>moof</c> ＋ 直後の <c>mdat</c>）。</summary>
    /// <param name="Offset">ファイル先頭からの <c>moof</c> の位置。</param>
    /// <param name="Size"><c>moof</c> と <c>mdat</c> を合わせた大きさ。</param>
    /// <param name="Time">
    /// 先頭サンプルの復号時刻（<c>tfdt</c> の <c>baseMediaDecodeTime</c>。単位は <c>mdhd</c> の timescale）。
    /// </param>
    /// <param name="Duration">
    /// このフラグメントの尺（<c>trun</c> の <c>sample_duration</c> の総和。
    /// 持っていなければ <c>tfhd</c> の <c>default_sample_duration</c> × <c>sample_count</c>）。
    /// </param>
    /// <param name="Sync">
    /// 先頭サンプルが同期サンプルか。<b>ここが真のフラグメントにしかシークできない</b>
    /// ── 録画は GOP 2 秒・フラグメント 1 秒なので、真でないフラグメントが必ず在る。
    /// </param>
    public readonly record struct Fragment(long Offset, int Size, ulong Time, uint Duration, bool Sync);

    /// <summary>1 回の走査の結果。</summary>
    /// <param name="Fragments">先頭から順に並んだフラグメント（差分走査では前回のぶんを含む）。</param>
    /// <param name="NextOffset">次に読み始める位置（＝書き掛けの箱の先頭、または読み終えた末尾）。</param>
    /// <param name="InitSize">最初の <c>moof</c> の位置 ＝ init セグメント（<c>ftyp</c> ＋ <c>moov</c>）の大きさ。</param>
    /// <param name="TrexDefaultSampleFlags">
    /// <c>moov/mvex/trex</c> の <c>default_sample_flags</c>（無ければ 0）。
    /// <b>差分走査へ持ち越すためにここに在る</b> ── 続きから読み直す走査は <c>moov</c> を通らないので、
    /// 渡さないと同じフラグメントの <see cref="Fragment.Sync"/> が全走査と食い違う。
    /// </param>
    public sealed record ScanResult(
        IReadOnlyList<Fragment> Fragments, long NextOffset, long InitSize, uint TrexDefaultSampleFlags);

    /// <summary>size(4) ＋ type(4)。</summary>
    private const int BoxHeaderSize = 8;

    /// <summary>size(4) ＋ type(4) ＋ largesize(8)。</summary>
    private const int LargeBoxHeaderSize = 16;

    /// <summary>
    /// 読み込む <c>moof</c> の上限。1 フラグメント（1 秒）のサンプル表なので実測は数百バイトで、
    /// これを超える値は壊れた入力である ── 上限が無いと、宣言された size をそのまま確保してしまう。
    /// </summary>
    private const int MaxMoofSize = 1024 * 1024;

    /// <summary><c>sample_is_non_sync_sample</c>（ISO/IEC 14496-12, 8.8.3.1 の packed 32bit）。</summary>
    private const uint NonSyncSampleFlag = 0x00010000;

    /// <summary>
    /// <paramref name="fromOffset"/> から <c>moof</c> を拾って索引にする。
    ///
    /// <para>
    /// <paramref name="previous"/> を渡すと、その続きとして積む（録画中のファイルの差分走査）。
    /// <see cref="ScanResult.InitSize"/> はそのとき <paramref name="previous"/> の先頭の位置になる
    /// ── 差分走査では <c>moov</c> より後ろしか見ないので、この場で数え直せない。
    /// </para>
    /// <para>
    /// <paramref name="trexDefaultSampleFlags"/> は前回の走査が <c>moov</c> から読んだ値
    /// （<see cref="ScanResult.TrexDefaultSampleFlags"/>）。差分走査は <c>moov</c> を通らないので、
    /// <b>渡さないと同期の判定の最後の拠り所が落ちる</b>。
    /// </para>
    /// <para>
    /// <b>例外を投げない。</b> 読めなかった時点までを返す ── 削除・切り詰め・権限のどれも、
    /// 録画中のファイルを共有で読む以上ふつうに起こる。
    /// </para>
    /// <para>
    /// <b>加算で溢れさせない。</b> 宣言された size は <c>largesize</c> なら 64bit まで採りうるので、
    /// 「位置 ＋ 大きさ」と書くと壊れた入力で負へ回り、範囲の検査を素通りして
    /// 負の位置を読みに行く（<see cref="Stream.Position"/> が投げる）。引き算の形で比べる。
    /// </para>
    /// </summary>
    public static ScanResult Scan(
        Stream stream, long fromOffset, IReadOnlyList<Fragment>? previous, uint trexDefaultSampleFlags)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var fragments = previous is null ? [] : new List<Fragment>(previous);
        long initSize = fragments.Count == 0 ? 0 : fragments[0].Offset;

        long length;
        try
        {
            length = stream.Length;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            return new ScanResult(fragments, Math.Max(0, fromOffset), initSize, trexDefaultSampleFlags);
        }

        long position = Math.Max(0, fromOffset);

        Span<byte> header = stackalloc byte[LargeBoxHeaderSize];

        while (position <= length && BoxHeaderSize <= length - position)
        {
            if (!TryReadBoxHeader(stream, position, length, header, out long size))
                break;

            // 宣言された大きさぶんが書かれていない ── 次はこの箱の先頭から読み直す。
            if (length - position < size)
                break;

            if (Fmp4SegmentSplitter.IsType(header, 4, "moof"))
            {
                if (MaxMoofSize < size)
                    break;

                byte[] moof = new byte[(int)size];
                if (!TryReadAt(stream, position, moof))
                    break;

                // **moof と mdat は 1 つの単位である。** mdat が書き切られていないうちに
                // フラグメントとして数えると、尺も大きさも実在しない値になる。
                if (!TryReadBoxHeader(stream, position + size, length, header, out long mdatSize)
                    || !Fmp4SegmentSplitter.IsType(header, 4, "mdat")
                    || length - position - size < mdatSize
                    || int.MaxValue - size < mdatSize)
                {
                    break;
                }

                if (fragments.Count == 0)
                    initSize = position;

                fragments.Add(Describe(moof, position, (int)(size + mdatSize), trexDefaultSampleFlags));
                position += size + mdatSize;
                continue;
            }

            if (Fmp4SegmentSplitter.IsType(header, 4, "moov") && size <= RecordingFiles.HeaderProbeBytes)
            {
                // trex の default_sample_flags は、trun も tfhd もフラグを持たないときの
                // 最後の拠り所である（Fmp4SegmentSplitter.StartsWithSync と同じ優先順）。
                byte[] moov = new byte[(int)size];
                if (TryReadAt(stream, position, moov))
                    trexDefaultSampleFlags = ReadTrexDefaultSampleFlags(moov);
            }

            position += size;
        }

        return new ScanResult(fragments, position, initSize, trexDefaultSampleFlags);
    }

    /// <summary>
    /// <paramref name="position"/> の箱のヘッダーを読む。<c>size==0</c>（以後ファイル末尾まで）と
    /// ヘッダーより小さい size は<b>読み終わり</b>として扱う ── 打ち切られた本文で無限に回らないため。
    /// </summary>
    private static bool TryReadBoxHeader(
        Stream stream, long position, long length, Span<byte> header, out long size)
    {
        size = 0;
        int headerSize = BoxHeaderSize;

        if (length - position < BoxHeaderSize || !TryReadAt(stream, position, header[..BoxHeaderSize]))
            return false;

        size = BinaryPrimitives.ReadUInt32BigEndian(header);

        if (size == 1)
        {
            if (length - position < LargeBoxHeaderSize || !TryReadAt(stream, position, header))
                return false;

            ulong large = BinaryPrimitives.ReadUInt64BigEndian(header[BoxHeaderSize..]);
            if (large > long.MaxValue)
                return false;

            size = (long)large;
            headerSize = LargeBoxHeaderSize;
        }

        return headerSize <= size;
    }

    /// <summary><paramref name="buffer"/> ぶんを <paramref name="position"/> から読み切る。</summary>
    private static bool TryReadAt(Stream stream, long position, Span<byte> buffer)
    {
        try
        {
            stream.Position = position;
            stream.ReadExactly(buffer);
            return true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            // EndOfStreamException も IOException の派生 ── 切り詰められたファイルはここへ来る。
            return false;
        }
    }

    /// <summary>1 つの <c>moof</c> からフラグメントの記述を作る。</summary>
    private static Fragment Describe(
        ReadOnlySpan<byte> moof, long offset, int size, uint trexDefaultSampleFlags)
    {
        ulong time = Fmp4SegmentSplitter.ReadBaseMediaDecodeTime(moof);
        uint duration = 0;
        bool sync = false;

        if (Fmp4SegmentSplitter.TryBoxContent(moof, out int moofStart, out int moofEnd)
            && Fmp4SegmentSplitter.TryFindChild(moof, moofStart, moofEnd, "traf", out int trafStart, out int trafEnd))
        {
            ReadTrun(moof, trafStart, trafEnd, trexDefaultSampleFlags, out duration, out sync);
        }

        return new Fragment(offset, size, time, duration, sync);
    }

    /// <summary>
    /// <c>traf</c> の <c>trun</c> から尺と「先頭サンプルが同期か」を読む。
    ///
    /// <para>
    /// <b>同期の判定は具体的なものから</b>: <c>trun.first_sample_flags</c> →
    /// <c>trun</c> の <c>sample_flags[0]</c> → <c>tfhd.default_sample_flags</c> →
    /// <c>trex.default_sample_flags</c>。どこにも無ければ 0（＝同期）
    /// ── <c>Fmp4SegmentSplitter.StartsWithSync</c> と同じ規則である。
    /// <c>trun</c> が無い・<c>sample_count==0</c> なら false（判定材料が無いものへは飛ばさない）。
    /// </para>
    /// </summary>
    private static void ReadTrun(
        ReadOnlySpan<byte> moof, int trafStart, int trafEnd, uint trexDefaultSampleFlags,
        out uint duration, out bool sync)
    {
        duration = 0;
        sync = false;

        if (!Fmp4SegmentSplitter.TryFindChild(moof, trafStart, trafEnd, "trun", out int trunStart, out int trunEnd))
            return;

        // version(1) flags(3) sample_count(4)
        if (trunStart + 8 > trunEnd)
            return;

        uint trunFlags = ReadU24(moof, trunStart + 1);
        uint sampleCount = Fmp4SegmentSplitter.ReadU32AsUInt(moof, trunStart + 4);
        if (sampleCount == 0)
            return;

        int offset = trunStart + 8;
        if ((trunFlags & 0x000001) != 0)
            offset += 4;                                        // data-offset-present

        uint? firstSampleFlags = null;
        if ((trunFlags & 0x000004) != 0)
        {
            if (offset + 4 <= trunEnd)
                firstSampleFlags = Fmp4SegmentSplitter.ReadU32AsUInt(moof, offset);
            offset += 4;                                        // first-sample-flags-present
        }

        bool hasDuration = (trunFlags & 0x000100) != 0;
        bool hasSize = (trunFlags & 0x000200) != 0;
        bool hasFlags = (trunFlags & 0x000400) != 0;
        bool hasCompositionOffset = (trunFlags & 0x000800) != 0;

        int stride = (hasDuration ? 4 : 0) + (hasSize ? 4 : 0)
            + (hasFlags ? 4 : 0) + (hasCompositionOffset ? 4 : 0);

        uint? sampleZeroFlags = null;
        if (hasFlags && offset + (hasDuration ? 4 : 0) + (hasSize ? 4 : 0) + 4 <= trunEnd)
            sampleZeroFlags = Fmp4SegmentSplitter.ReadU32AsUInt(moof, offset + (hasDuration ? 4 : 0) + (hasSize ? 4 : 0));

        ReadTfhdDefaults(moof, trafStart, trafEnd, out uint? defaultDuration, out uint? defaultFlags);

        uint effective = firstSampleFlags ?? sampleZeroFlags ?? defaultFlags ?? trexDefaultSampleFlags;
        sync = (effective & NonSyncSampleFlag) == 0;

        if (!hasDuration)
        {
            duration = (uint)Math.Min((ulong)(defaultDuration ?? 0) * sampleCount, uint.MaxValue);
            return;
        }

        // sample_duration の総和。並びが範囲に収まらないなら尺は答えない（0 のまま）。
        if (0 < stride && (long)sampleCount * stride <= trunEnd - offset)
        {
            ulong total = 0;
            for (uint i = 0; i < sampleCount; i++)
                total += Fmp4SegmentSplitter.ReadU32AsUInt(moof, offset + (int)i * stride);

            duration = (uint)Math.Min(total, uint.MaxValue);
        }
    }

    /// <summary><c>tfhd</c> の <c>default_sample_duration</c> と <c>default_sample_flags</c>。</summary>
    private static void ReadTfhdDefaults(
        ReadOnlySpan<byte> moof, int trafStart, int trafEnd,
        out uint? defaultDuration, out uint? defaultFlags)
    {
        defaultDuration = null;
        defaultFlags = null;

        if (!Fmp4SegmentSplitter.TryFindChild(moof, trafStart, trafEnd, "tfhd", out int start, out int end))
            return;
        if (start + 8 > end)
            return;

        uint flags = ReadU24(moof, start + 1);

        int offset = start + 8;                                  // version/flags(4) track_ID(4)
        if ((flags & 0x000001) != 0)
            offset += 8;                                         // base-data-offset-present
        if ((flags & 0x000002) != 0)
            offset += 4;                                         // sample-description-index-present

        if ((flags & 0x000008) != 0)                             // default-sample-duration-present
        {
            if (offset + 4 <= end)
                defaultDuration = Fmp4SegmentSplitter.ReadU32AsUInt(moof, offset);
            offset += 4;
        }
        if ((flags & 0x000010) != 0)
            offset += 4;                                         // default-sample-size-present

        if ((flags & 0x000020) != 0 && offset + 4 <= end)        // default-sample-flags-present
            defaultFlags = Fmp4SegmentSplitter.ReadU32AsUInt(moof, offset);
    }

    /// <summary><c>moov/mvex/trex</c> の <c>default_sample_flags</c>。無ければ 0。</summary>
    private static uint ReadTrexDefaultSampleFlags(ReadOnlySpan<byte> moov)
    {
        if (!Fmp4SegmentSplitter.TryBoxContent(moov, out int start, out int end))
            return 0;
        if (!Fmp4SegmentSplitter.TryFindChild(moov, start, end, "mvex", out int mvexStart, out int mvexEnd))
            return 0;
        if (!Fmp4SegmentSplitter.TryFindChild(moov, mvexStart, mvexEnd, "trex", out int trexStart, out int trexEnd))
            return 0;

        // version/flags(4) track_ID(4) default_sample_description_index(4)
        // default_sample_duration(4) default_sample_size(4) default_sample_flags(4)
        if (trexStart + 24 > trexEnd)
            return 0;

        return Fmp4SegmentSplitter.ReadU32AsUInt(moov, trexStart + 20);
    }

    private static uint ReadU24(ReadOnlySpan<byte> data, int offset)
        => ((uint)data[offset] << 16) | ((uint)data[offset + 1] << 8) | data[offset + 2];
}
