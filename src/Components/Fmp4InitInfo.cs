using System;
using System.Diagnostics.CodeAnalysis;

namespace ProcessRecorderApp.Components;

/// <summary>
/// fMP4 の Init セグメント（<c>ftyp</c> … <c>moov</c>）から、DASH の MPD を書くのに
/// 要る 2 つの値だけを取り出した結果。<b>純関数</b>で、状態も所有権も持たない。
///
/// <para>
/// <b>取るのは最初の video トラック 1 本だけ</b>（<c>hdlr</c> の handler_type が
/// <c>vide</c>）。プレビューの mux は 1 トラックしか作らないが、
/// <c>hdlr</c> を見ずに先頭の <c>trak</c> を採ると、将来 音声が先に並んだ Init が来たときに
/// <b>音声の timescale で映像の時間軸を書く</b>ことになり、再生は始まるのに時刻だけがずれる。
/// </para>
/// <para>
/// 箱の走査は <see cref="Fmp4SegmentSplitter"/> のヘルパーをそのまま使う
/// ── ISO-BMFF の読み方をこのリポジトリの 2 か所に書かない。
/// </para>
/// </summary>
/// <param name="Timescale">映像トラックの <c>mdhd</c> の timescale（1 秒あたりの刻み数）。</param>
/// <param name="Codecs">
/// MPD / MSE の <c>codecs</c> 文字列（<c>avc1.PPCCLL</c>。<c>avcC</c> の
/// profile_idc・constraint_flags・level_idc を 2 桁大文字 16 進で並べたもの）。
/// </param>
public readonly record struct Fmp4InitInfo(uint Timescale, string Codecs)
{
    /// <summary>
    /// Init セグメントを読む。<b>読めなければ false で、
    /// <paramref name="info"/> は <see langword="default"/></b>（＝ <c>Codecs</c> は null）。
    ///
    /// <para>
    /// 失敗させるのは「video トラックが無い」「<c>mdhd</c> が読めない」
    /// 「<c>avcC</c> が無い・短い」の 3 つ。<b>推測で埋めない</b> ── 間違った
    /// <c>codecs</c> を書いた MPD は、ブラウザ側で無音のまま再生されないだけになる。
    /// </para>
    /// </summary>
    /// <param name="init">Init セグメントの全体（<c>ftyp</c> から <c>moov</c> の末尾まで）。</param>
    /// <param name="info">読めた値。</param>
    public static bool TryParse(ReadOnlySpan<byte> init, out Fmp4InitInfo info)
    {
        info = default;

        if (!TryFindTopLevel(init, "moov", out int moovStart, out int moovEnd))
            return false;

        int position = moovStart;
        while (Fmp4SegmentSplitter.TryNextBox(init, ref position, moovEnd,
                                              out int typeOffset, out int trakStart, out int trakEnd))
        {
            if (!Fmp4SegmentSplitter.IsType(init, typeOffset, "trak"))
                continue;

            if (!Fmp4SegmentSplitter.TryFindChild(init, trakStart, trakEnd, "mdia",
                                                  out int mdiaStart, out int mdiaEnd))
                continue;

            if (!IsVideoHandler(init, mdiaStart, mdiaEnd))
                continue;

            if (!TryReadTimescale(init, mdiaStart, mdiaEnd, out uint timescale))
                return false;

            if (!TryReadCodecs(init, mdiaStart, mdiaEnd, out string? codecs))
                return false;

            info = new Fmp4InitInfo(timescale, codecs);
            return true;
        }

        return false;
    }

    /// <summary>最上位の箱から 1 件（<c>moov</c> は <c>ftyp</c> などの後ろに居る）。</summary>
    private static bool TryFindTopLevel(ReadOnlySpan<byte> init, string type, out int contentStart, out int contentEnd)
        => Fmp4SegmentSplitter.TryFindChild(init, 0, init.Length, type, out contentStart, out contentEnd);

    /// <summary><c>mdia/hdlr</c> の handler_type が <c>vide</c> か。</summary>
    private static bool IsVideoHandler(ReadOnlySpan<byte> data, int mdiaStart, int mdiaEnd)
    {
        if (!Fmp4SegmentSplitter.TryFindChild(data, mdiaStart, mdiaEnd, "hdlr", out int start, out int end))
            return false;

        // version/flags(4) pre_defined(4) handler_type(4)
        if (start + 12 > end)
            return false;

        return Fmp4SegmentSplitter.IsType(data, start + 8, "vide");
    }

    /// <summary><c>mdia/mdhd</c> の timescale（version 0 は 32bit の時刻、1 は 64bit）。</summary>
    private static bool TryReadTimescale(ReadOnlySpan<byte> data, int mdiaStart, int mdiaEnd, out uint timescale)
    {
        timescale = 0;
        if (!Fmp4SegmentSplitter.TryFindChild(data, mdiaStart, mdiaEnd, "mdhd", out int start, out int end))
            return false;

        // version(1) flags(3) creation(4|8) modification(4|8) timescale(4) duration(4|8)
        int offset = data[start] == 0 ? start + 12 : start + 20;
        if (offset + 4 > end)
            return false;

        timescale = Fmp4SegmentSplitter.ReadU32AsUInt(data, offset);
        return timescale != 0;
    }

    /// <summary>
    /// <c>mdia/minf/stbl/stsd/avc1/avcC</c> から <c>avc1.PPCCLL</c> を組む。
    /// </summary>
    private static bool TryReadCodecs(
        ReadOnlySpan<byte> data, int mdiaStart, int mdiaEnd, [NotNullWhen(true)] out string? codecs)
    {
        codecs = null;

        if (!Fmp4SegmentSplitter.TryFindChild(data, mdiaStart, mdiaEnd, "minf", out int minfStart, out int minfEnd))
            return false;
        if (!Fmp4SegmentSplitter.TryFindChild(data, minfStart, minfEnd, "stbl", out int stblStart, out int stblEnd))
            return false;
        if (!Fmp4SegmentSplitter.TryFindChild(data, stblStart, stblEnd, "stsd", out int stsdStart, out int stsdEnd))
            return false;

        // stsd は full box: version/flags(4) entry_count(4) の後ろに sample entry が並ぶ。
        int entriesStart = stsdStart + 8;
        if (entriesStart > stsdEnd)
            return false;

        if (!Fmp4SegmentSplitter.TryFindChild(data, entriesStart, stsdEnd, "avc1",
                                              out int avc1Start, out int avc1End))
            return false;

        // VisualSampleEntry の固定部 78 バイト（reserved(6) data_reference_index(2)
        // pre_defined/reserved(16) width(2) height(2) resolution(8) reserved(4)
        // frame_count(2) compressorname(32) depth(2) pre_defined(2)）の後ろが子の箱。
        int childrenStart = avc1Start + 78;
        if (childrenStart > avc1End)
            return false;

        if (!Fmp4SegmentSplitter.TryFindChild(data, childrenStart, avc1End, "avcC",
                                              out int avccStart, out int avccEnd))
            return false;

        // configurationVersion(1) AVCProfileIndication(1) profile_compatibility(1) AVCLevelIndication(1)
        if (avccStart + 4 > avccEnd)
            return false;

        codecs = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"avc1.{data[avccStart + 1]:X2}{data[avccStart + 2]:X2}{data[avccStart + 3]:X2}");
        return true;
    }
}
