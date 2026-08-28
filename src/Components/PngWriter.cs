using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace ProcessRecorderApp.Components;

/// <summary>
/// 8bit トゥルーカラー（color type 2）の PNG だけを書く最小の符号化器。
///
/// <para>
/// <b>追加パッケージも追加ネイティブも使わない。</b> 圧縮は
/// <see cref="ZLibStream"/>（BCL）、CRC32 は自前の表で行う。
/// 書けるのは「署名 → <c>IHDR</c> → <c>IDAT</c> 1 つ → <c>IEND</c>」の形だけで、
/// パレット・アルファ・インターレース・補助 chunk は扱わない。
/// </para>
/// </summary>
public static class PngWriter
{
    /// <summary>PNG の署名（8 バイト）。</summary>
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>1 画素のバイト数（RGB 8bit 固定）。</summary>
    private const int BytesPerPixel = 3;

    /// <summary>行の先頭に置くフィルタ種別（4 = Paeth）。</summary>
    private const byte PaethFilter = 4;

    /// <summary>CRC32（多項式 0xEDB88320）の表。</summary>
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    /// <summary>
    /// RGB 8bit の画素列を PNG として <paramref name="output"/> へ書く。
    /// </summary>
    /// <param name="output">書き込み先。位置は呼び出し側の管理。</param>
    /// <param name="width">幅（1 以上）。</param>
    /// <param name="height">高さ（1 以上）。</param>
    /// <param name="rgb24">
    /// 行優先・パディング無しの RGB 画素列。長さは <c>width×height×3</c> でなければならない。
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="rgb24"/> の長さが <c>width×height×3</c> と違う。
    /// </exception>
    public static void Write(Stream output, int width, int height, ReadOnlySpan<byte> rgb24)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        long expected = (long)width * height * BytesPerPixel;
        if (rgb24.Length != expected)
        {
            throw new ArgumentException(
                $"the pixel buffer must be {expected} bytes for {width}x{height} RGB, not {rgb24.Length}",
                nameof(rgb24));
        }

        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // color type: truecolor
        header[10] = 0; // compression: deflate
        header[11] = 0; // filter: adaptive
        header[12] = 0; // interlace: none
        WriteChunk(output, "IHDR"u8, header);

        WriteChunk(output, "IDAT"u8, Deflate(width, height, rgb24));
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// 各行に Paeth フィルタを掛けて zlib で圧縮する。
    /// <b>長さが先に要る</b>ので、いったんメモリへ溜めてから <c>IDAT</c> に載せる。
    /// </summary>
    private static byte[] Deflate(int width, int height, ReadOnlySpan<byte> rgb24)
    {
        int stride = width * BytesPerPixel;
        using var compressed = new MemoryStream();

        // **ZLibStream は先に閉じる。** 閉じないと最後のブロックと adler32 が出ない。
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            Span<byte> line = stride <= 4096 ? stackalloc byte[stride] : new byte[stride];
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> raw = rgb24.Slice(y * stride, stride);
                ReadOnlySpan<byte> prior = y == 0
                    ? ReadOnlySpan<byte>.Empty
                    : rgb24.Slice((y - 1) * stride, stride);

                for (int x = 0; x < stride; x++)
                {
                    byte a = BytesPerPixel <= x ? raw[x - BytesPerPixel] : (byte)0;
                    byte b = prior.IsEmpty ? (byte)0 : prior[x];
                    byte c = prior.IsEmpty || x < BytesPerPixel ? (byte)0 : prior[x - BytesPerPixel];
                    line[x] = (byte)(raw[x] - Paeth(a, b, c));
                }

                zlib.WriteByte(PaethFilter);
                zlib.Write(line);
            }
        }

        return compressed.ToArray();
    }

    /// <summary>Paeth 予測子（PNG 仕様 9.4）。</summary>
    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc)
            return a;

        return pb <= pc ? b : c;
    }

    /// <summary>chunk 1 つ（長さ → 種別 → データ → CRC）。<b>CRC は種別とデータだけ</b>を覆う。</summary>
    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);

        output.Write(type);
        output.Write(data);

        uint crc = Crc32(Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);

        return crc;
    }
}
