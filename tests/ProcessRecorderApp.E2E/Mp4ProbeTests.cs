using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <b><see cref="Mp4Probe"/> が fragmented MP4 の尺とサンプル数を<b>本当に</b>読めることの実証。
///
/// <para>
/// ここが要る理由は 1 つ ── 録画系 L2 の表明（尺・サンプル数・実効フレームレート・
/// 先頭が同期サンプルか）は<b>全部この読み手の上に乗っている</b>。読み手が黙って 0 や
/// <c>true</c> を返すようになると、<b>どのテストも赤くならないまま</b>検査が消える
/// （実際、既定が fMP4 になった時点で <c>stsz</c>／<c>stss</c> 経路はそうなっていた）。
/// だから答えが<b>先に分かっている入力</b>に対して読み方を固定する。
/// </para>
/// <para>
/// 入力は 2 種類:
/// <list type="bullet">
///   <item><b>合成したバイト列</b> ── 尺もサンプル数も flags も書いた本人が知っている。
///     GStreamer もアプリも要らないので、必ず走る。</item>
///   <item><b><c>gst-launch-1.0</c> に書かせた fMP4</b> ── 本物の <c>mp4mux</c> の出力に対して
///     <c>num-buffers</c> と一致することを見る。合成側だけだと「自分の書いた形は読める」
///     しか言えない。</item>
/// </list>
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class Mp4ProbeTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>合成する入力のメディア timescale。<c>trun</c> のサンプル長はこの単位。</summary>
    private const uint Timescale = 15000;

    /// <summary>1 サンプル＝ 1/15 秒（15fps）。</summary>
    private const uint SampleDuration = 1000;

    /// <summary><c>sample_is_non_sync_sample</c>（ISO/IEC 14496-12, 8.8.3.1）。</summary>
    private const uint NonSync = 0x00010000;

    // ---- 合成したバイト列（答えが先に分かっている入力） ----

    /// <summary>
    /// <b>尺とサンプル数は <c>trun</c> の合算から出る。</b>
    /// 5 サンプル × 3 fragment、1 サンプル 1000 単位／timescale 15000
    /// ── 期待は 15 サンプル・1.000 秒・15fps。
    ///
    /// <para>
    /// <c>mvhd</c> の尺は 0 のまま（fragmented の <c>moov</c> は書き直されない）で、
    /// <b>そこから読んでいたら 0 になる</b>ことを併せて固定する。
    /// 最後の <c>mdat</c> は 64bit の <c>largesize</c> で書いてある。
    /// </para>
    /// </summary>
    [Fact]
    public void Fragmented_ReadsTheDurationAndSampleCountFromTheFragments()
    {
        byte[] file = Concat(
            Ftyp(),
            Moov(mvhdDurationUnits: 0, trexSampleDuration: 0, trexSampleFlags: 0),
            Fragment(sampleCount: 5, sampleDurations: true),
            Fragment(sampleCount: 5, sampleDurations: true),
            Fragment(sampleCount: 5, sampleDurations: true, largeMdat: true));

        var probe = Mp4File.Probe("synthetic-fragmented.mp4", file);
        output.WriteLine(probe.ToString());

        Assert.True(probe.IsFragmented, probe.ToString());
        Assert.Equal(3, probe.FragmentCount);
        Assert.Equal(15u, probe.SampleCount);
        Assert.Equal(1.0, probe.DurationSeconds!.Value, 3);
        Assert.Equal(15.0, probe.EffectiveFramerate!.Value, 3);
        Assert.True(probe.StartsOnASyncSample, probe.ToString());
        Assert.True(probe.IsValid, probe.ToString());

        // 旧来の読み先（mvhd）は 0 のまま ── 尺がそこから出ていないことの裏取り。
        Assert.Equal(0.0, probe.MvhdDurationSeconds!.Value, 3);

        // 寸法は fragmented でも moov の avc1 から読める。
        Assert.Equal(320, probe.FrameWidth);
        Assert.Equal(240, probe.FrameHeight);
    }

    /// <summary>
    /// <b>サンプル長が <c>trun</c> に無ければ <c>tfhd</c>、それも無ければ <c>trex</c> の既定値。</b>
    /// mp4mux は同じ長さのサンプルが続くと <c>trun</c> から項目を落とすので、
    /// ここが読めないと尺が 0 になる。
    ///
    /// <para>
    /// 1 本目は <c>tfhd</c> の既定（3000 単位 × 4 サンプル＝ 12000）、
    /// 2 本目は <c>tfhd</c> に既定が無く <c>trex</c> の 1500 単位 × 4 ＝ 6000。
    /// 合計 18000 ／ timescale 15000 ＝ 1.200 秒・8 サンプル。
    /// </para>
    /// </summary>
    [Fact]
    public void Fragmented_FallsBackToTheTfhdAndTrexSampleDuration()
    {
        byte[] file = Concat(
            Ftyp(),
            Moov(mvhdDurationUnits: 0, trexSampleDuration: 1500, trexSampleFlags: 0),
            Fragment(sampleCount: 4, sampleDurations: false, tfhdSampleDuration: 3000),
            Fragment(sampleCount: 4, sampleDurations: false));

        var probe = Mp4File.Probe("synthetic-defaults.mp4", file);
        output.WriteLine(probe.ToString());

        Assert.Equal(8u, probe.SampleCount);
        Assert.Equal(1.200, probe.DurationSeconds!.Value, 3);
        Assert.True(probe.IsValid, probe.ToString());
    }

    /// <summary>
    /// <b>先頭が非同期サンプルなら <c>StartsOnASyncSample</c> は false。</b>
    ///
    /// <para>
    /// <b>ここが実証の要。</b> fragmented の <c>moov</c> には <c>stss</c> が無く、
    /// 「<c>stss</c> 無し＝全部同期サンプル」の分岐は<b>中身と無関係に true を返す</b>
    /// ── 既定構成では「録画がキーフレームから始まる」の検査が丸ごと消えていた。
    /// 同じバイト列で <c>first_sample_flags</c> だけを差し替え、true と false が
    /// 入れ替わることを見る。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0u, true)]
    [InlineData(NonSync, false)]
    public void Fragmented_ReadsTheSyncFlagOfTheFirstSample(uint firstSampleFlags, bool expected)
    {
        byte[] file = Concat(
            Ftyp(),
            Moov(mvhdDurationUnits: 0, trexSampleDuration: 0, trexSampleFlags: 0),
            Fragment(sampleCount: 5, sampleDurations: true, firstSampleFlags: firstSampleFlags),
            Fragment(sampleCount: 5, sampleDurations: true));

        var probe = Mp4File.Probe("synthetic-sync.mp4", file);
        output.WriteLine($"first_sample_flags=0x{firstSampleFlags:X8} -> {probe}");

        Assert.Equal(expected, probe.StartsOnASyncSample);
        // 非同期始まりでも「MP4 としては妥当」── だから同期判定を別に見る必要がある。
        Assert.True(probe.IsValid, probe.ToString());
    }

    /// <summary>
    /// <b><c>trex</c> の <c>default_sample_flags</c> も同期判定に効く。</b>
    /// <c>trun</c> にも <c>tfhd</c> にも flags が無い形では、ここが最後の拠り所になる。
    /// </summary>
    [Fact]
    public void Fragmented_FallsBackToTheTrexSampleFlags()
    {
        byte[] file = Concat(
            Ftyp(),
            Moov(mvhdDurationUnits: 0, trexSampleDuration: SampleDuration, trexSampleFlags: NonSync),
            Fragment(sampleCount: 5, sampleDurations: false));

        var probe = Mp4File.Probe("synthetic-trex-flags.mp4", file);
        output.WriteLine(probe.ToString());

        Assert.False(probe.StartsOnASyncSample, probe.ToString());
    }

    /// <summary>
    /// <b><c>mvex</c> が無ければ従来どおり <c>moov</c> だけを読む。</b>
    /// fragmented の読み手を足したことで chunked の答えが変わっていないこと
    /// ── <c>moof</c> が 1 つも無いのに fragmented と判定したら、
    /// 既存の録画系 L2 が全部落ちる。
    /// </summary>
    [Fact]
    public void Chunked_StillReadsTheMvhdAndStsz()
    {
        byte[] file = Concat(
            Ftyp(),
            Moov(mvhdDurationUnits: 30000, trexSampleDuration: null, trexSampleFlags: 0, stszSampleCount: 45),
            Box("mdat", new byte[16]));

        var probe = Mp4File.Probe("synthetic-chunked.mp4", file);
        output.WriteLine(probe.ToString());

        Assert.False(probe.IsFragmented, probe.ToString());
        Assert.Equal(0, probe.FragmentCount);
        Assert.Equal(45u, probe.SampleCount);
        // mvhd の timescale は 15000（合成側で固定）。30000 単位 ＝ 2.000 秒。
        Assert.Equal(2.0, probe.DurationSeconds!.Value, 3);
        Assert.True(probe.IsValid, probe.ToString());
    }

    // ---- 本物の mp4mux が書いた fMP4 ----

    /// <summary>クリップの長さ（秒）と fps。<c>num-buffers</c> がそのままサンプル数になる。</summary>
    private const int ClipSeconds = 6;

    /// <inheritdoc cref="ClipSeconds"/>
    private const int ClipFps = 15;

    /// <summary>クリップを書き終える上限。実測は 2 秒前後。</summary>
    private static readonly TimeSpan ClipBudget = TimeSpan.FromSeconds(120);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// <b>本物の <c>mp4mux fragment-mode=dash-or-mss</c> の出力でも、
    /// <c>num-buffers</c> と一致したサンプル数・尺が出る。</b>
    ///
    /// <para>
    /// <c>videotestsrc num-buffers=90</c>／15fps なので、期待は<b>ちょうど 90 サンプル</b>と
    /// <b>6.000 秒</b>（非ライブなので落ちるフレームが無い）。合成したバイト列だけでは
    /// 「自分の書いた形なら読める」しか言えない ── ここが実物との突き合わせである。
    /// </para>
    /// <para>
    /// <c>gst-launch-1.0.exe</c> と <c>x264enc</c> はロードした GStreamer に在るとは限らない
    /// （同梱ランタイムが解決に勝つ機械では GPL のプラグインだけが無い）ので、
    /// 無ければスキップする ── 判定は <see cref="GstLaunchTool"/>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Fragmented_MatchesTheFrameCountOfAKnownClip()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");
        using var instance = AppInstance.Create(app, settings);

        string? launcher = GstLaunchTool.FindLauncher(instance);
        Assert.SkipWhen(launcher is null,
            "ロードした GStreamer の bin に gst-launch-1.0.exe がありません"
            + Environment.NewLine + instance.DiagnosticDump());
        Assert.SkipUnless(GstLaunchTool.HasX264Plugin(launcher!),
            "ロードした GStreamer に x264 のプラグインがありません（同梱ランタイムには入っていない）: "
            + GstLaunchTool.PluginDirectoryOf(launcher!));

        string clip = Path.Combine(instance.RecordingsDir, "known-fragmented.mp4");
        await WriteFragmentedClipAsync(launcher!, clip, instance);

        var probe = Mp4File.Probe(clip);
        output.WriteLine(probe.ToString());

        Assert.True(probe.IsFragmented, "fragmented と判定されていない: " + probe);
        Assert.True(probe.IsValid, probe.ToString());
        // **ちょうど一致することを要求する。** 幅を持たせると、fragment を 1 つ落とす
        // 読み違いが通ってしまう。
        Assert.Equal((uint)(ClipSeconds * ClipFps), probe.SampleCount);
        Assert.Equal((double)ClipSeconds, probe.DurationSeconds!.Value, 3);
        Assert.Equal((double)ClipFps, probe.EffectiveFramerate!.Value, 3);
        // 非ライブの videotestsrc は 1 フレーム目から I フレーム。
        Assert.True(probe.StartsOnASyncSample, probe.ToString());
        // mvhd 側は 0 のまま（＝ここから読んでいたら全部無検査になっていた）。
        Assert.True(probe.MvhdDurationSeconds is null or 0, probe.ToString());
    }

    /// <summary>
    /// <paramref name="path"/> へ <see cref="ClipSeconds"/> 秒ぶんの fMP4 を書く。
    /// <b>実時間を使わない</b> ── <c>videotestsrc</c> を非ライブで <c>num-buffers</c> ぶん
    /// 回すので、生成は数秒で終わり、フレーム数はちょうど指定どおりになる。
    /// </summary>
    private async Task WriteFragmentedClipAsync(string launcher, string path, AppInstance instance)
    {
        await GstLaunchTool.RunAsync(
            launcher,
            [
                "videotestsrc",
                "num-buffers=" + (ClipSeconds * ClipFps).ToString(CultureInfo.InvariantCulture),
                "!",
                $"video/x-raw,format=I420,width=320,height=240,framerate={ClipFps}/1",
                "!",
                "x264enc",
                "speed-preset=ultrafast",
                "key-int-max=" + (ClipFps * 2).ToString(CultureInfo.InvariantCulture),
                "!",
                "h264parse",
                "!",
                "mp4mux",
                "fragment-duration=1000",
                "fragment-mode=dash-or-mss",
                "!",
                "filesink",
                // **区切りは '/' にする。** gst-launch はプロパティ値の '\' を
                // エスケープとして食うので、Windows のパスをそのまま渡すと別のパスになる。
                "location=" + path.Replace('\\', '/'),
            ],
            instance,
            "gst-registry-knownclip.bin",
            ClipBudget,
            Ct);

        output.WriteLine($"{Path.GetFileName(path)}: {new FileInfo(path).Length:N0} bytes");
    }

    // ---- ISO-BMFF の組み立て（答えを知っている入力を作る） ----

    private static byte[] Ftyp() => Box("ftyp", Ascii("isom"), U32(0x200), Ascii("isom"), Ascii("iso6"));

    /// <summary>
    /// <c>moov</c>。<paramref name="trexSampleDuration"/> が null なら <c>mvex</c> を書かない
    /// （＝ chunked の形）。
    /// </summary>
    private static byte[] Moov(
        uint mvhdDurationUnits,
        uint? trexSampleDuration,
        uint trexSampleFlags,
        uint stszSampleCount = 0)
    {
        byte[] mvhd = Box("mvhd",
            U32(0),                                  // version(0) + flags
            U32(0), U32(0),                          // creation / modification
            U32(Timescale),                          // timescale
            U32(mvhdDurationUnits),                  // duration
            new byte[80]);                           // rate 以降（読まないので 0 で埋める）

        byte[] mdhd = Box("mdhd",
            U32(0), U32(0), U32(0),
            U32(Timescale),                          // ここが trun のサンプル長の単位
            U32(mvhdDurationUnits),
            U32(0));

        // VisualSampleEntry: reserved(6) data_reference_index(2) pre_defined(2)
        // reserved(2) pre_defined(12) width(2) height(2) …（以降は読まない）
        byte[] avc1 = Box("avc1",
            new byte[6], U16(1), U16(0), U16(0), new byte[12],
            U16(320), U16(240),
            new byte[14],
            Box("avcC", [0x01, 0x64, 0x00, 0x0A, 0xFF, 0xE0, 0x00, 0x00, 0x01, 0x00, 0x00]));

        byte[] stbl = Box("stbl",
            Box("stsd", U32(0), U32(1), avc1),
            Box("stsz", U32(0), U32(0), U32(stszSampleCount)));

        byte[] trak = Box("trak",
            Box("mdia", mdhd, Box("minf", stbl)));

        byte[] mvex = trexSampleDuration is { } duration
            ? Box("mvex", Box("trex",
                U32(0),                              // version + flags
                U32(1),                              // track_ID
                U32(1),                              // default_sample_description_index
                U32(duration),                       // default_sample_duration
                U32(0),                              // default_sample_size
                U32(trexSampleFlags)))               // default_sample_flags
            : [];

        return Box("moov", mvhd, trak, mvex);
    }

    /// <summary><c>moof</c>＋<c>mdat</c> の対を 1 つ作る。</summary>
    private static byte[] Fragment(
        uint sampleCount,
        bool sampleDurations,
        uint? tfhdSampleDuration = null,
        uint? firstSampleFlags = null,
        bool largeMdat = false)
    {
        // tfhd: flags 0x000008 = default-sample-duration-present
        byte[] tfhd = tfhdSampleDuration is { } duration
            ? Box("tfhd", U24Flags(0x000008), U32(1), U32(duration))
            : Box("tfhd", U24Flags(0), U32(1));

        // trun: 0x000001 data-offset-present / 0x000004 first-sample-flags-present /
        //       0x000100 sample-duration-present / 0x000200 sample-size-present
        uint trunFlags = 0x000001 | 0x000200;
        if (firstSampleFlags is not null)
            trunFlags |= 0x000004;
        if (sampleDurations)
            trunFlags |= 0x000100;

        var body = new List<byte[]>
        {
            U24Flags(trunFlags),
            U32(sampleCount),
            U32(0),                                  // data_offset（読まない）
        };
        if (firstSampleFlags is { } flags)
            body.Add(U32(flags));
        for (uint i = 0; i < sampleCount; i++)
        {
            if (sampleDurations)
                body.Add(U32(SampleDuration));
            body.Add(U32(64));                       // sample_size
        }

        byte[] moof = Box("moof",
            Box("mfhd", U32(0), U32(1)),
            Box("traf", tfhd, Box("trun", [.. body])));

        byte[] payload = new byte[(int)sampleCount * 64];
        return Concat(moof, largeMdat ? LargeBox("mdat", payload) : Box("mdat", payload));
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        byte[] content = Concat(parts);
        return Concat(U32((uint)(content.Length + 8)), Ascii(type), content);
    }

    /// <summary>64bit の <c>largesize</c> で書いた箱（size==1 の経路を踏ませる）。</summary>
    private static byte[] LargeBox(string type, params byte[][] parts)
    {
        byte[] content = Concat(parts);
        byte[] size = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)content.Length + 16);
        return Concat(U32(1), Ascii(type), size, content);
    }

    /// <summary>FullBox の version(1)＋flags(3)。</summary>
    private static byte[] U24Flags(uint flags) => U32(flags & 0x00FFFFFF);

    private static byte[] U32(uint value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] U16(ushort value)
    {
        byte[] buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
