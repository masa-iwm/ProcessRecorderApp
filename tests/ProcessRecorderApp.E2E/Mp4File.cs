using System.Buffers.Binary;
using System.Text;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// MP4（ISO-BMFF）の最小限の検証。<c>gst-discoverer</c> を使わず自前で読むのは、
/// あちらが外部プロセスの起動と <c>GST_PLUGIN_PATH</c> 等の環境整備を要求するため
/// ── ここで答えたいのは「本物の MP4 か・H.264 トラックがあるか・尺は何秒か」だけで、
/// トップレベルのアトムを直接読めば依存ゼロで足りる。
/// </summary>
public sealed record Mp4Probe(
    string Path,
    long Length,
    bool HasFtyp,
    bool HasMoov,
    bool HasMdat,
    bool HasAvcC,
    double? DurationSeconds,
    bool StartsOnASyncSample,
    uint SampleCount,
    int FrameWidth,
    int FrameHeight)
{
    /// <summary>再生可能な MP4 として最低限成立しているか。</summary>
    public bool IsValid => HasFtyp && HasMoov && HasMdat && HasAvcC && DurationSeconds is > 0;

    /// <summary>
    /// 実効フレームレート（サンプル数 ÷ 尺）。<c>ContinuousFramerate</c> が効いているかを
    /// 見るのに使う。<b>厳密には一致しない</b> ── エンコーダーのプライムと丸めがあるので、
    /// 比較は緩い範囲で行うこと。
    /// </summary>
    public double? EffectiveFramerate
        => DurationSeconds is > 0 && 0 < SampleCount ? SampleCount / DurationSeconds : null;

    public override string ToString() =>
        $"{Path} ({Length:N0} bytes) ftyp={HasFtyp} moov={HasMoov} mdat={HasMdat} avcC={HasAvcC} " +
        $"{FrameWidth}x{FrameHeight} " +
        $"duration={(DurationSeconds is { } d ? d.ToString("F3") + "s" : "(none)")} " +
        $"samples={SampleCount} " +
        $"fps={(EffectiveFramerate is { } f ? f.ToString("F2") : "(none)")} " +
        $"startsOnSync={StartsOnASyncSample}";
}

public static class Mp4File
{
    /// <summary>
    /// ファイルを ISO-BMFF として読む。書き込み側がまだ掴んでいる場合は
    /// <see cref="SharingViolation"/> を投げる ── 「停止の同期性」の判定は
    /// <c>moov</c> の有無より共有違反を見る方が鋭い（実測済み）。
    /// </summary>
    public static Mp4Probe Probe(string path)
    {
        using FileStream stream = OpenExclusiveRead(path);
        return Probe(path, stream);
    }

    /// <summary>
    /// 書き込み側がファイルを掴んでいないか。<c>stop-recording</c> の復帰直後に
    /// これが false なら、排出が完了する前に CLI が返っている。
    /// </summary>
    public static bool IsClosedByWriter(string path)
    {
        try
        {
            using var stream = OpenExclusiveRead(path);
            return true;
        }
        catch (SharingViolation)
        {
            return false;
        }
    }

    private static FileStream OpenExclusiveRead(string path)
    {
        try
        {
            // FileShare.Read（＝書き込み共有を許さない）で開く。まだ filesink が
            // 書き込みハンドルを持っていれば IOException になる。
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new SharingViolation($"'{path}' は書き込み側がまだ開いています: {ex.Message}", ex);
        }
    }

    private static Mp4Probe Probe(string path, FileStream stream)
    {
        bool hasFtyp = false, hasMoov = false, hasMdat = false, hasAvcC = false;
        double? duration = null;
        bool startsOnSyncSample = true;
        uint sampleCount = 0;
        int frameWidth = 0, frameHeight = 0;

        Span<byte> header = stackalloc byte[16];
        long position = 0;
        long length = stream.Length;

        while (position + 8 <= length)
        {
            stream.Position = position;
            if (!TryReadExactly(stream, header[..8]))
                break;

            long size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string type = Encoding.ASCII.GetString(header[4..8]);
            long headerSize = 8;

            if (size == 1)
            {
                if (!TryReadExactly(stream, header[8..16]))
                    break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                // 「ファイル末尾まで」の意味。
                size = length - position;
            }

            if (size < headerSize)
                break;

            switch (type)
            {
                case "ftyp":
                    hasFtyp = true;
                    break;
                case "mdat":
                    hasMdat = true;
                    break;
                case "moov":
                {
                    hasMoov = true;
                    int payloadSize = (int)Math.Min(size - headerSize, 8 * 1024 * 1024);
                    byte[] payload = new byte[payloadSize];
                    stream.Position = position + headerSize;
                    if (!TryReadExactly(stream, payload))
                        break;

                    // avcC（H.264 のデコーダー設定レコード）は avc1 サンプルエントリの中にある。
                    // moov の階層を辿らず直接探すのは、ここで知りたいのが
                    // 「H.264 トラックが1本でも書かれたか」だけだから。
                    hasAvcC = IndexOfFourCc(payload, "avcC") >= 0;
                    duration = ReadDurationFromMvhd(payload);
                    startsOnSyncSample = ReadStartsOnSyncSample(payload);
                    sampleCount = ReadSampleCount(payload);
                    (frameWidth, frameHeight) = ReadFrameSize(payload);
                    break;
                }
            }

            position += size;
        }

        return new Mp4Probe(
            path, length, hasFtyp, hasMoov, hasMdat, hasAvcC, duration, startsOnSyncSample, sampleCount,
            frameWidth, frameHeight);
    }

    /// <summary>
    /// 映像の幅・高さを <c>avc1</c> のサンプルエントリから読む。
    ///
    /// <para>
    /// <b>尺やサンプル数では代用できない。</b> 常時録画の枝に解像度を指定すると、その要求が
    /// <c>tee</c> を越えてソースまで伝播し、<b>イベント録画まで一緒に縮む</b>ことがある
    /// ── 出来上がった MP4 は「妥当」なままなので、大きさを直接読む以外に検出できない。
    /// </para>
    /// <para>
    /// VisualSampleEntry の並びは 4cc の直後から
    /// reserved(6) / data_reference_index(2) / pre_defined(2) / reserved(2) /
    /// pre_defined(12) / width(2) / height(2)。
    /// </para>
    /// </summary>
    private static (int Width, int Height) ReadFrameSize(byte[] moovPayload)
    {
        int index = IndexOfFourCc(moovPayload, "avc1");
        if (index < 0)
            return (0, 0);

        var span = moovPayload.AsSpan(index + 4);
        if (span.Length < 32)
            return (0, 0);

        return (BinaryPrimitives.ReadUInt16BigEndian(span[24..26]),
                BinaryPrimitives.ReadUInt16BigEndian(span[26..28]));
    }

    /// <summary>
    /// トラックのサンプル（＝フレーム）数を <c>stsz</c> から読む。
    ///
    /// <para>
    /// 尺と組み合わせると実効フレームレートが出るので、<c>ContinuousFramerate</c> の
    /// 上書きが本当に効いているかを、外部ツール無しで確かめられる
    /// ── <b>尺だけでは分からない</b>（15fps でも 5fps でも 5 秒は 5 秒である）。
    /// </para>
    /// <para>
    /// <c>stsz</c> は 4cc の直後から version(1) flags(3) sample_size(4) sample_count(4)。
    /// 映像トラックしか無い前提なので、最初の 1 つだけを読む。
    /// </para>
    /// </summary>
    private static uint ReadSampleCount(byte[] moovPayload)
    {
        int index = IndexOfFourCc(moovPayload, "stsz");
        if (index < 0)
            return 0;

        var span = moovPayload.AsSpan(index + 4);
        return span.Length < 12 ? 0 : BinaryPrimitives.ReadUInt32BigEndian(span[8..12]);
    }

    /// <summary>
    /// 先頭のサンプルが同期サンプル（＝キーフレーム）か。
    ///
    /// <para>
    /// <c>stss</c>（同期サンプルテーブル）はキーフレームのサンプル番号を 1 始まりで並べる。
    /// 録画がキーフレーム以外から始まっていれば先頭の項目が 1 にならない。
    /// <b>これは「MP4 として妥当か」では絶対に分からない</b> ── 途中から始まっても
    /// <c>ftyp</c>/<c>moov</c>/<c>mdat</c>/<c>avcC</c> は揃い、尺も入るため
    /// （実際に <c>isIframeFound</c> の I フレーム待ちを外す注入は、この検査を足すまで
    /// どのテストでも検出できなかった）。参照フレームの無いスライスから始まる録画は、
    /// 先頭が壊れて見える映像になる。
    /// </para>
    ///
    /// <para>
    /// <c>stss</c> が無い場合は「全サンプルが同期サンプル」の意味なので true。
    /// </para>
    /// </summary>
    private static bool ReadStartsOnSyncSample(byte[] moovPayload)
    {
        int index = IndexOfFourCc(moovPayload, "stss");
        if (index < 0)
            return true;

        // 4cc の直後: version(1) flags(3) entry_count(4) entries(4 × n)
        var span = moovPayload.AsSpan(index + 4);
        if (span.Length < 12)
            return true;

        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(span[4..8]);
        if (entryCount == 0)
            return false;

        return BinaryPrimitives.ReadUInt32BigEndian(span[8..12]) == 1;
    }

    /// <summary>moov のペイロードから mvhd を探して尺（秒）を取り出す。</summary>
    private static double? ReadDurationFromMvhd(byte[] moovPayload)
    {
        int index = IndexOfFourCc(moovPayload, "mvhd");
        if (index < 0)
            return null;

        // mvhd の中身は 4cc の直後から: version(1) flags(3) …
        var span = moovPayload.AsSpan(index + 4);
        if (span.Length < 4)
            return null;

        byte version = span[0];
        span = span[4..];

        uint timescale;
        ulong durationUnits;
        if (version == 1)
        {
            if (span.Length < 28)
                return null;
            timescale = BinaryPrimitives.ReadUInt32BigEndian(span[16..20]);
            durationUnits = BinaryPrimitives.ReadUInt64BigEndian(span[20..28]);
        }
        else
        {
            if (span.Length < 16)
                return null;
            timescale = BinaryPrimitives.ReadUInt32BigEndian(span[8..12]);
            durationUnits = BinaryPrimitives.ReadUInt32BigEndian(span[12..16]);
        }

        if (timescale == 0)
            return null;
        return Math.Round((double)durationUnits / timescale, 3);
    }

    private static int IndexOfFourCc(byte[] buffer, string fourCc)
    {
        Span<byte> needle = stackalloc byte[4];
        Encoding.ASCII.GetBytes(fourCc, needle);
        return buffer.AsSpan().IndexOf(needle);
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }
}

/// <summary>ファイルを書き込み側がまだ掴んでいた（＝排出が完了していない）。</summary>
public sealed class SharingViolation(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// fragmented MP4（ライブプレビューの本文）の最小限の検証。
///
/// <para>
/// <b>見るのはトップレベルの箱の並びだけ。</b> 答えたいのは「MSE へ渡せる形か」
/// ── 先頭が <c>ftyp</c>＋<c>moov</c>（<c>mvex</c> と <c>avc1</c>/<c>avcC</c> 入り）で、
/// 以後が <c>moof</c>＋<c>mdat</c> の対で続いているか ── だけである。
/// </para>
/// <para>
/// <b>末尾は必ず切れている。</b> 打ち切って保存したファイルなので、最後の箱は
/// 途中までしか無いことがある。長さが足りない箱に出会ったらそこで読み終える。
/// </para>
/// <para>
/// <b>同期判定は製品側（<c>Fmp4SegmentSplitter.StartsWithSync</c>）の規則を
/// 書き写したもので、独立検査ではない。</b> E2E は <c>GstSharpNet</c> を参照しない
/// ので共有できず、規則が 2 か所にある ── <b>製品側の規則が間違っていれば
/// こちらも同じように間違う</b>。中身が本当に読めるかを製品と無関係に確かめているのは
/// <c>qtdemux</c> を通す <c>PreviewStreamTests</c> の方である。
/// </para>
/// </summary>
public sealed record Fmp4Probe(
    string Path,
    long Length,
    IReadOnlyList<string> Boxes,
    bool HasMvex,
    bool HasAvc1,
    bool HasAvcC,
    IReadOnlyList<bool> MoofStartsWithSync,
    // ParsedLength: 完全な箱として読めた先頭からの長さ。打ち切って保存した本文の末尾は
    // 必ず途中なので、そこまでを切り出したものが「箱の境界で閉じた」ファイルになる。
    int ParsedLength)
{
    /// <summary><c>moof</c> の個数（＝取り出せた fragment の数）。</summary>
    public int MoofCount => MoofStartsWithSync.Count;

    /// <summary>先頭が <c>ftyp</c> → <c>moov</c> で始まるか（＝ init セグメントが先頭に在る）。</summary>
    public bool StartsWithInitSegment
        => 2 <= Boxes.Count && Boxes[0] == "ftyp" && Boxes[1] == "moov";

    /// <summary><c>moov</c> の後ろが <c>moof</c>／<c>mdat</c> の交互になっているか。</summary>
    public bool MediaSegmentsAlternate()
    {
        for (int i = 2; i + 1 < Boxes.Count; i += 2)
        {
            if (Boxes[i] != "moof" || Boxes[i + 1] != "mdat")
                return false;
        }
        return true;
    }

    public override string ToString()
        => $"{Path} ({Length:N0} bytes, {ParsedLength:N0} parsed) mvex={HasMvex} avc1={HasAvc1} avcC={HasAvcC} "
         + $"moof={MoofCount} sync=[{string.Join(",", MoofStartsWithSync)}] "
         + $"boxes=[{string.Join(",", Boxes)}]";
}

public static class Fmp4File
{
    /// <summary><c>sample_is_non_sync_sample</c>（ISO/IEC 14496-12, 8.8.3.1 の packed 32bit）。</summary>
    private const uint NonSyncSampleFlag = 0x00010000;

    private const int BoxHeaderSize = 8;
    private const int LargeBoxHeaderSize = 16;

    public static Fmp4Probe Probe(string path) => Probe(path, File.ReadAllBytes(path));

    public static Fmp4Probe Probe(string path, byte[] data)
    {
        var boxes = new List<string>();
        var sync = new List<bool>();
        bool hasMvex = false, hasAvc1 = false, hasAvcC = false;
        uint trexDefaultSampleFlags = 0;

        int position = 0;
        while (position + BoxHeaderSize <= data.Length)
        {
            long size = ReadU32(data, position);
            int header = BoxHeaderSize;

            if (size == 1)
            {
                if (position + LargeBoxHeaderSize > data.Length)
                    break;
                size = ReadU64(data, position + BoxHeaderSize);
                header = LargeBoxHeaderSize;
            }
            else if (size == 0)
            {
                // 「以後ファイル末尾まで」。ライブの切り落としでは決着しない。
                break;
            }

            // 切り落とした末尾（最後の箱が途中まで）はここで読み終える。
            if (size < header || data.Length < position + size)
                break;

            string type = TypeName(data, position + 4);
            boxes.Add(type);

            int contentStart = position + header;
            int contentEnd = (int)(position + size);

            if (type == "moov")
            {
                hasMvex = TryFindChild(data, contentStart, contentEnd, "mvex", out int mvexStart, out int mvexEnd);
                hasAvc1 = 0 <= IndexOfFourCc(data, contentStart, contentEnd, "avc1");
                hasAvcC = 0 <= IndexOfFourCc(data, contentStart, contentEnd, "avcC");
                if (hasMvex)
                    trexDefaultSampleFlags = ReadTrexDefaultSampleFlags(data, mvexStart, mvexEnd);
            }
            else if (type == "moof")
            {
                sync.Add(StartsWithSync(data, contentStart, contentEnd, trexDefaultSampleFlags));
            }

            position += (int)size;
        }

        return new Fmp4Probe(path, data.Length, boxes, hasMvex, hasAvc1, hasAvcC, sync, position);
    }

    /// <summary>
    /// この fragment の<b>先頭サンプル</b>が同期サンプルか。優先順は具体的なものから:
    /// <c>trun.first_sample_flags</c> → <c>trun</c> の <c>sample_flags[0]</c> →
    /// <c>tfhd.default_sample_flags</c> → <c>trex.default_sample_flags</c>。
    /// <c>trun</c> が無い・<c>sample_count==0</c> なら false。
    /// </summary>
    private static bool StartsWithSync(byte[] data, int moofStart, int moofEnd, uint trexDefaultSampleFlags)
    {
        if (!TryFindChild(data, moofStart, moofEnd, "traf", out int trafStart, out int trafEnd))
            return false;
        if (!TryFindChild(data, trafStart, trafEnd, "trun", out int trunStart, out int trunEnd))
            return false;
        if (trunStart + 8 > trunEnd)
            return false;

        uint trunFlags = ReadU24(data, trunStart + 1);
        if (ReadU32AsUInt(data, trunStart + 4) == 0)
            return false;

        uint? firstSampleFlags = null;
        uint? sampleZeroFlags = null;

        int offset = trunStart + 8;
        if ((trunFlags & 0x000001) != 0)
            offset += 4;                                        // data-offset-present
        if ((trunFlags & 0x000004) != 0)
        {
            if (offset + 4 <= trunEnd)
                firstSampleFlags = ReadU32AsUInt(data, offset);  // first-sample-flags-present
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
                sampleZeroFlags = ReadU32AsUInt(data, sampleOffset);
        }

        uint effective = firstSampleFlags
            ?? sampleZeroFlags
            ?? ReadTfhdDefaultSampleFlags(data, trafStart, trafEnd)
            ?? trexDefaultSampleFlags;

        return (effective & NonSyncSampleFlag) == 0;
    }

    private static uint? ReadTfhdDefaultSampleFlags(byte[] data, int trafStart, int trafEnd)
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

    private static uint ReadTrexDefaultSampleFlags(byte[] data, int mvexStart, int mvexEnd)
    {
        if (!TryFindChild(data, mvexStart, mvexEnd, "trex", out int trexStart, out int trexEnd))
            return 0;

        // version/flags(4) track_ID(4) default_sample_description_index(4)
        // default_sample_duration(4) default_sample_size(4) default_sample_flags(4)
        return trexStart + 24 > trexEnd ? 0 : ReadU32AsUInt(data, trexStart + 20);
    }

    /// <summary>指定した範囲の 1 段の箱から最初の 1 件を探す。</summary>
    private static bool TryFindChild(byte[] data, int start, int end, string type, out int contentStart, out int contentEnd)
    {
        contentStart = 0;
        contentEnd = 0;

        int position = start;
        while (position + BoxHeaderSize <= end)
        {
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
                size = end - position;
            }

            if (size < header || end < position + size)
                return false;

            if (IsType(data, position + 4, type))
            {
                contentStart = position + header;
                contentEnd = (int)(position + size);
                return true;
            }

            position += (int)size;
        }

        return false;
    }

    /// <summary>範囲内の 4cc の位置（見つからなければ -1）。階層は辿らない。</summary>
    private static int IndexOfFourCc(byte[] data, int start, int end, string fourCc)
    {
        for (int i = start; i + 4 <= end; i++)
        {
            if (IsType(data, i, fourCc))
                return i;
        }
        return -1;
    }

    private static bool IsType(byte[] data, int offset, string type)
        => data[offset] == type[0]
        && data[offset + 1] == type[1]
        && data[offset + 2] == type[2]
        && data[offset + 3] == type[3];

    private static string TypeName(byte[] data, int offset)
    {
        Span<char> characters = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte value = data[offset + i];
            characters[i] = value is >= 0x20 and < 0x7F ? (char)value : '?';
        }
        return new string(characters);
    }

    private static long ReadU32(byte[] data, int offset)
        => ((long)data[offset] << 24) | ((long)data[offset + 1] << 16)
         | ((long)data[offset + 2] << 8) | data[offset + 3];

    private static uint ReadU32AsUInt(byte[] data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
         | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static uint ReadU24(byte[] data, int offset)
        => ((uint)data[offset] << 16) | ((uint)data[offset + 1] << 8) | data[offset + 2];

    private static long ReadU64(byte[] data, int offset)
    {
        long value = 0;
        for (int i = 0; i < 8; i++)
            value = (value << 8) | data[offset + i];
        return value;
    }
}
