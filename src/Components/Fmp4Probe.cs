using System;
using System.Buffers.Binary;
using System.Globalization;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 録画ファイルの<b>先頭だけ</b>を見て、fragmented MP4 かどうかと H.264 の codec 文字列を答える。
///
/// <para>
/// <b>純関数で、渡された範囲の外は読まない。</b> 呼び出し側は先頭 64KB だけを渡す
/// （<c>moov</c> は init セグメントの一部なのでファイルの先頭に在り、fragmented では
/// 書き直されない）。短い・壊れている・見つからないはすべて「fragmented ではない」
/// 「codec 文字列は無い」に畳む ── 録画中のファイルを読むので、
/// 途中までしか無い状態が正常な入力である。
/// </para>
/// <para>
/// <b><c>mvex</c> の有無だけで判定する。</b> <c>moov</c> の子に <c>mvex</c>（movie extends）が
/// 在るのは、以後のサンプルが <c>moof</c> で記述されることの宣言そのもので、
/// <c>moof</c> が 1 つも書かれていない開始直後でも真になる。
/// </para>
/// </summary>
public static class Fmp4Probe
{
    /// <summary>ボックスヘッダー（size 4 ＋ type 4）。</summary>
    private const int BoxHeaderSize = 8;

    /// <summary>64bit の size を持つボックスのヘッダー（size 4 ＋ type 4 ＋ largesize 8）。</summary>
    private const int LargeBoxHeaderSize = 16;

    /// <summary><c>avcC</c> の中身の最小長（version・profile・compat・level の 4 バイト）。</summary>
    private const int MinAvcCPayload = 4;

    /// <summary>
    /// <paramref name="header"/>（ファイルの先頭からの並び）が fragmented MP4 か
    /// ── <c>moov</c> の子に <c>mvex</c> が在るか。
    /// </summary>
    public static bool IsFragmented(ReadOnlySpan<byte> header)
    {
        if (!TryFindMoov(header, out ReadOnlySpan<byte> moov))
            return false;

        // moov の子を 1 段だけ辿る（mvex は moov の直接の子）。
        for (int position = 0; position + BoxHeaderSize <= moov.Length;)
        {
            if (!TryReadBox(moov, position, out long size, out ReadOnlySpan<byte> type))
                return false;

            if (type.SequenceEqual("mvex"u8))
                return true;

            position += (int)Math.Min(size, moov.Length - position);
        }

        return false;
    }

    /// <summary>
    /// MSE の <c>codecs</c> パラメータ（<c>avc1.PPCCLL</c>）。
    /// <c>avcC</c>（AVCDecoderConfigurationRecord）の profile / compatibility / level を
    /// そのまま 16 進 6 桁にしたもので、見つからなければ <see langword="null"/>。
    /// </summary>
    public static string? CodecString(ReadOnlySpan<byte> header)
    {
        if (!TryFindMoov(header, out ReadOnlySpan<byte> moov))
            return null;

        // avcC は trak > mdia > minf > stbl > stsd > avc1 の下に在る。階層を辿らず
        // 四文字コードで探すのは、答えたいのが「H.264 の設定レコードが在るか」だけだから。
        for (int i = 0; i + 4 + MinAvcCPayload <= moov.Length; i++)
        {
            if (!moov.Slice(i, 4).SequenceEqual("avcC"u8))
                continue;

            ReadOnlySpan<byte> record = moov[(i + 4)..];
            // record[0] は configurationVersion（常に 1）。profile / compat / level が続く。
            return string.Create(
                CultureInfo.InvariantCulture,
                $"avc1.{record[1]:x2}{record[2]:x2}{record[3]:x2}");
        }

        return null;
    }

    /// <summary>
    /// メディアの時間の単位（<c>moov</c> &gt; <c>trak</c> &gt; <c>mdia</c> &gt; <c>mdhd</c> の
    /// <c>timescale</c>）。<c>moof</c> の <c>tfdt</c> と <c>trun</c> が数える単位そのもので、
    /// これが読めなければ <c>moof</c> の索引は秒へ直せない。
    ///
    /// <para>
    /// <b>version 0 と 1 で幅が変わる</b>（creation / modification が 32bit か 64bit か）ので、
    /// version を見てから位置を決める。<c>trak</c> は複数ありうるので、
    /// <c>mdhd</c> の在る最初のものを採る（録画は映像 1 本）。
    /// 0 は timescale として意味を持たないので「読めなかった」に畳む。
    /// </para>
    /// </summary>
    public static bool TryReadMediaTimescale(ReadOnlySpan<byte> header, out uint timescale)
    {
        timescale = 0;

        if (!TryFindMoov(header, out ReadOnlySpan<byte> moov))
            return false;

        for (int position = 0; position + BoxHeaderSize <= moov.Length;)
        {
            if (!TryReadBox(moov, position, out long size, out ReadOnlySpan<byte> type))
                return false;

            if (type.SequenceEqual("trak"u8)
                && TryChildContent(moov, position, size, out ReadOnlySpan<byte> trak)
                && TryFindChild(trak, "mdia"u8, out ReadOnlySpan<byte> mdia)
                && TryFindChild(mdia, "mdhd"u8, out ReadOnlySpan<byte> mdhd)
                && TryReadTimescale(mdhd, out timescale))
            {
                return true;
            }

            // trak より手前の箱が末尾で切れていたら、そこで読み終える。
            if (moov.Length - position < size)
                return false;

            position += (int)size;
        }

        return false;
    }

    /// <summary><c>mdhd</c> の中身から <c>timescale</c> を読む。</summary>
    private static bool TryReadTimescale(ReadOnlySpan<byte> mdhd, out uint timescale)
    {
        timescale = 0;

        // version(1) flags(3) creation modification timescale duration
        if (mdhd.Length < 4)
            return false;

        int offset = mdhd[0] == 0 ? 12 : 20;
        if (mdhd.Length < offset + 4)
            return false;

        timescale = BinaryPrimitives.ReadUInt32BigEndian(mdhd[offset..]);
        return timescale != 0;
    }

    /// <summary>1 段の子から最初の 1 件の中身を切り出す。</summary>
    private static bool TryFindChild(
        ReadOnlySpan<byte> parent, ReadOnlySpan<byte> type, out ReadOnlySpan<byte> content)
    {
        content = default;

        for (int position = 0; position + BoxHeaderSize <= parent.Length;)
        {
            if (!TryReadBox(parent, position, out long size, out ReadOnlySpan<byte> boxType))
                return false;

            if (boxType.SequenceEqual(type))
                return TryChildContent(parent, position, size, out content);

            if (parent.Length - position < size)
                return false;

            position += (int)size;
        }

        return false;
    }

    /// <summary>
    /// <paramref name="position"/> の箱の中身。<b>途中で切れていても読めるところまでを返す</b>
    /// （渡されるのは先頭 64KB だけである）。
    /// </summary>
    private static bool TryChildContent(
        ReadOnlySpan<byte> parent, int position, long size, out ReadOnlySpan<byte> content)
    {
        content = default;

        int headerSize = BinaryPrimitives.ReadUInt32BigEndian(parent[position..]) == 1
            ? LargeBoxHeaderSize
            : BoxHeaderSize;

        int start = position + headerSize;
        int end = (int)Math.Min(position + size, parent.Length);
        if (end <= start)
            return false;

        content = parent[start..end];
        return true;
    }

    /// <summary>最上位のボックスを走査して <c>moov</c> の中身を切り出す。</summary>
    private static bool TryFindMoov(ReadOnlySpan<byte> header, out ReadOnlySpan<byte> moov)
    {
        moov = default;

        for (int position = 0; position + BoxHeaderSize <= header.Length;)
        {
            if (!TryReadBox(header, position, out long size, out ReadOnlySpan<byte> type))
                return false;

            int headerSize = BinaryPrimitives.ReadUInt32BigEndian(header[position..]) == 1
                ? LargeBoxHeaderSize
                : BoxHeaderSize;

            if (type.SequenceEqual("moov"u8))
            {
                // 途中で切れていても、読めるところまでを返す（録画中の先頭を渡されうる）。
                int start = position + headerSize;
                int end = (int)Math.Min(position + size, header.Length);
                if (end <= start)
                    return false;

                moov = header[start..end];
                return true;
            }

            // moov より手前の箱（ftyp / free）が末尾で切れていたら、そこで読み終える。
            if (header.Length - position < size)
                return false;

            position += (int)size;
        }

        return false;
    }

    /// <summary>
    /// <paramref name="position"/> のボックスの大きさと種別を読む。
    /// <c>size==0</c>（以後ファイル末尾まで）と、ヘッダーより小さい size は
    /// <b>読み終わり</b>として扱う ── 打ち切られた本文で無限に回らないため。
    /// </summary>
    private static bool TryReadBox(
        ReadOnlySpan<byte> buffer, int position, out long size, out ReadOnlySpan<byte> type)
    {
        size = BinaryPrimitives.ReadUInt32BigEndian(buffer[position..]);
        type = buffer.Slice(position + 4, 4);

        if (size == 1)
        {
            if (buffer.Length < position + LargeBoxHeaderSize)
                return false;

            ulong large = BinaryPrimitives.ReadUInt64BigEndian(buffer[(position + BoxHeaderSize)..]);
            if (large < LargeBoxHeaderSize || (ulong)int.MaxValue < large)
                return false;

            size = (long)large;
            return true;
        }

        return BoxHeaderSize <= size;
    }
}
