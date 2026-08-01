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
    bool StartsOnASyncSample)
{
    /// <summary>再生可能な MP4 として最低限成立しているか。</summary>
    public bool IsValid => HasFtyp && HasMoov && HasMdat && HasAvcC && DurationSeconds is > 0;

    public override string ToString() =>
        $"{Path} ({Length:N0} bytes) ftyp={HasFtyp} moov={HasMoov} mdat={HasMdat} avcC={HasAvcC} " +
        $"duration={(DurationSeconds is { } d ? d.ToString("F3") + "s" : "(none)")} " +
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
                    break;
                }
            }

            position += size;
        }

        return new Mp4Probe(path, length, hasFtyp, hasMoov, hasMdat, hasAvcC, duration, startsOnSyncSample);
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
