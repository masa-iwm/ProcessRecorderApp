using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// H.264 エンコーダーの候補解決の検証。
///
/// 既定が <c>D3d12</c>=<c>qsvh264enc</c>（Intel 専用）/ <c>System</c>=<c>x264enc</c> の
/// 決め打ちだったため、AMD / NVIDIA 機や GPU の無い環境では録画が一切できなかった。
/// ここでは実機に依存しないよう**プローブを注入**して検証する
/// （GPU の有無に関係なく、どのマシンでも同じ結果になる）。
/// </summary>
public class EncoderCatalogResolveTests
{
    /// <summary>指定した名前だけが「存在する」とみなすプローブ。</summary>
    private static Func<string, bool> ProbeOnly(params string[] available)
    {
        var set = new HashSet<string>(available, StringComparer.Ordinal);
        return set.Contains;
    }

    private static string[] Names(IReadOnlyList<H264EncoderDef> defs)
        => defs.Select(d => d.FactoryName).ToArray();

    [Fact]
    public void NothingAvailable_ResolvesToAnEmptyList()
    {
        Assert.Empty(EncoderCatalog.Resolve(EventRecordingType.System, null, ProbeOnly()));
        Assert.Empty(EncoderCatalog.Resolve(EventRecordingType.D3d12, null, ProbeOnly()));
    }

    [Fact]
    public void OnlyX264Available_System_ResolvesToX264()
    {
        var resolved = EncoderCatalog.Resolve(EventRecordingType.System, null, ProbeOnly("x264enc"));
        Assert.Equal(["x264enc"], Names(resolved));
    }

    [Fact]
    public void OnlyX264Available_D3d12_StillFallsAllTheWayDownToX264()
    {
        // 要点: GPU エンコーダーが1つも無くても D3d12 経路が録画できること
        var resolved = EncoderCatalog.Resolve(EventRecordingType.D3d12, null, ProbeOnly("x264enc"));
        Assert.Equal(["x264enc"], Names(resolved));
    }

    [Fact]
    public void D3d12_PrefersGpuNativeEncodersOverSoftware()
    {
        var resolved = EncoderCatalog.Resolve(
            EventRecordingType.D3d12, null, ProbeOnly("x264enc", "openh264enc", "qsvh264enc", "d3d12h264enc"));

        // d3d12h264enc が最優先、ソフトウェアは末尾
        Assert.Equal(["d3d12h264enc", "qsvh264enc", "openh264enc", "x264enc"], Names(resolved));
    }

    [Fact]
    public void AmdMachine_ResolvesToAmfBeforeSoftware()
    {
        var resolved = EncoderCatalog.Resolve(
            EventRecordingType.D3d12, null, ProbeOnly("amfh264enc", "mfh264enc", "x264enc"));
        Assert.Equal("amfh264enc", resolved[0].FactoryName);
    }

    [Fact]
    public void NvidiaMachine_ResolvesToNvencBeforeSoftware()
    {
        var resolved = EncoderCatalog.Resolve(
            EventRecordingType.D3d12, null, ProbeOnly("nvh264enc", "x264enc"));
        Assert.Equal("nvh264enc", resolved[0].FactoryName);
    }

    [Fact]
    public void Preferred_IsMovedToTheFront_WhenAvailable()
    {
        var resolved = EncoderCatalog.Resolve(
            EventRecordingType.System, "mfh264enc", ProbeOnly("x264enc", "openh264enc", "mfh264enc"));

        Assert.Equal("mfh264enc", resolved[0].FactoryName);
        // 残りは通常の優先順位のまま、フォールバック先として保持される
        Assert.Equal(["mfh264enc", "x264enc", "openh264enc"], Names(resolved));
    }

    [Fact]
    public void Preferred_ThatIsNotAvailable_FallsThroughToTheNormalOrder()
    {
        // 設定ミスで録画が一切できなくなるより、黙って自動選択へ落ちる方が有害でない
        var resolved = EncoderCatalog.Resolve(
            EventRecordingType.System, "qsvh264enc", ProbeOnly("x264enc", "openh264enc"));

        Assert.Equal(["x264enc", "openh264enc"], Names(resolved));
    }

    /// <summary>
    /// <b><c>System</c> 経路で GPU エンコーダーを指名したら、裸のファクトリ名ではなく
    /// カタログの定義（プロパティ付き）が先頭に来ること。</b>
    ///
    /// <para>
    /// 裸名だと <c>gop-size</c> も <c>bitrate</c> も付かない起動文字列になり、
    /// <b>その機械でだけプレビュー／変換の画質プリセットが効かない</b>
    /// （<c>WithBitrateKbps</c> は書き込む先が無いので素通しする）。
    /// ログの <c>encoder=</c> は指名どおりに見えるので、配信物を測るまで気付けない。
    /// </para>
    /// </summary>
    [Fact]
    public void Preferred_AGpuEncoder_OnSystem_UsesTheCatalogDefinitionNotTheBareName()
    {
        var resolved = EncoderCatalog.Resolve(
            EventRecordingType.System, "qsvh264enc", ProbeOnly("x264enc", "qsvh264enc"), gop: 45);

        var pick = resolved[0];
        Assert.Equal("qsvh264enc", pick.FactoryName);
        Assert.Contains("gop-size=45", pick.LaunchString, StringComparison.Ordinal);
        Assert.Contains("bitrate=2000", pick.LaunchString, StringComparison.Ordinal);
        Assert.Contains("rate-control=cbr", pick.LaunchString, StringComparison.Ordinal);

        // 帯域指定が実際に当たること（DashPreviewStream / TranscodeStreams が通る道）。
        Assert.Contains("bitrate=800 ", pick.WithBitrateKbps(800).LaunchString, StringComparison.Ordinal);

        // 通常の優先順位はフォールバック先として後ろに残る。
        Assert.Equal(["qsvh264enc", "x264enc"], Names(resolved));
    }

    /// <summary>
    /// 指名した GPU エンコーダーが実機に無ければ<b>従来どおり黙ってフォールスルー</b>する
    /// ── カタログの定義を挿す枝が、この意味論を変えていないこと。
    /// </summary>
    [Fact]
    public void Preferred_AGpuEncoderThatIsAbsent_StillFallsThrough()
    {
        var resolved = EncoderCatalog.Resolve(
            EventRecordingType.System, "nvh264enc", ProbeOnly("x264enc", "openh264enc"));

        Assert.Equal(["x264enc", "openh264enc"], Names(resolved));
    }

    [Fact]
    public void Preferred_NotInTheCatalog_IsHonouredWhenItExistsOnTheMachine()
    {
        var resolved = EncoderCatalog.Resolve(
            EventRecordingType.System, "somevendorh264enc", ProbeOnly("x264enc", "somevendorh264enc"));

        Assert.Equal("somevendorh264enc", resolved[0].FactoryName);
        // メモリ要件が不明な要素はプロパティを付けない
        Assert.Equal("somevendorh264enc", resolved[0].LaunchString);
    }

    [Fact]
    public void Preferred_NotInTheCatalog_OnD3d12_IsTreatedAsNeedingSystemMemory()
    {
        // 余分な d3d12download が入っても動くが、必要な download が無いと必ずリンクに失敗する
        var resolved = EncoderCatalog.Resolve(
            EventRecordingType.D3d12, "somevendorh264enc", ProbeOnly("x264enc", "somevendorh264enc"));

        Assert.True(resolved[0].NeedsSystemMemory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Preferred_EmptyOrWhitespace_MeansAutomatic(string? preferred)
    {
        var resolved = EncoderCatalog.Resolve(
            EventRecordingType.System, preferred, ProbeOnly("x264enc", "openh264enc"));
        Assert.Equal(["x264enc", "openh264enc"], Names(resolved));
    }

    [Fact]
    public void Preferred_IsTrimmed()
    {
        var resolved = EncoderCatalog.Resolve(
            EventRecordingType.System, "  openh264enc  ", ProbeOnly("x264enc", "openh264enc"));
        Assert.Equal("openh264enc", resolved[0].FactoryName);
    }

    [Fact]
    public void SoftwareEncoders_NeedSystemMemory_ButGpuNativeOnesDoNot()
    {
        // ここを取り違えると、D3d12 経路で d3d12download が入らず AMD/NVIDIA 機で必ず壊れる
        var all = EncoderCatalog.D3d12Candidates.ToDictionary(d => d.FactoryName);

        Assert.False(all["d3d12h264enc"].NeedsSystemMemory);
        Assert.False(all["qsvh264enc"].NeedsSystemMemory);
        // nvd3d11h264enc は D3D11 メモリを受ける要素なので、D3d12 経路の
        // D3D12 メモリはそのままでは渡せない（d3d12download が要る）。
        Assert.True(all["nvd3d11h264enc"].NeedsSystemMemory);
        Assert.True(all["nvh264enc"].NeedsSystemMemory);
        // nvcodec の要素は CUDA / GL / D3D11 / システムメモリを受けるが D3D12 は受けない。
        // 実機の SINK caps では未確認なので安全側（真）に倒してある ── 偽で間違っていると
        // リンク失敗で録画できないが、真で間違っていても余分な変換が入るだけで録画は成立する。
        Assert.True(all["nvautogpuh264enc"].NeedsSystemMemory);
        Assert.True(all["amfh264enc"].NeedsSystemMemory);
        Assert.True(all["mfh264enc"].NeedsSystemMemory);
        Assert.True(all["openh264enc"].NeedsSystemMemory);
        Assert.True(all["x264enc"].NeedsSystemMemory);
    }

    [Fact]
    public void OpenH264_BitrateIsExpressedInBitsPerSecond_NotKilobits()
    {
        // openh264enc の bitrate は bit/sec（x264enc / mfh264enc は kbit/sec）。
        // x264enc の数値をコピーすると 2000 bit/sec ＝ 2kbps になり、実質壊れる。
        var openH264 = EncoderCatalog.SystemCandidates.Single(d => d.FactoryName == "openh264enc");
        Assert.Contains("bitrate=2000000", openH264.LaunchString);

        var x264 = EncoderCatalog.SystemCandidates.Single(d => d.FactoryName == "x264enc");
        Assert.Contains("bitrate=2000", x264.LaunchString);
        Assert.DoesNotContain("bitrate=2000000", x264.LaunchString);
    }

    /// <summary>
    /// GOP 長は<b>フレームレートから「秒」で逆算する</b>こと。
    ///
    /// <para>
    /// フレーム数を固定すると、低いレートの経路で間隔が伸び切る ── <b>実測: 60 フレーム固定の
    /// まま 5fps の常時録画枝を走らせると 12 秒間隔になり、5 秒のセグメントが
    /// キーフレーム待ちで 10 秒へ伸びた</b>（<c>continuous.overshoot</c>）。
    /// ここが壊れると、常時録画の分割間隔とイベント録画の立ち上がりが同時に狂う。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("30/1", 60)]
    [InlineData("15/1", 30)]
    [InlineData("5/1", 10)]
    [InlineData("60/1", 120)]
    [InlineData("30000/1001", 60)]   // 29.97fps
    public void GopForFramerate_KeepsTheKeyframeIntervalAtTheTarget(string framerate, int expected)
    {
        Assert.Equal(expected, EncoderCatalog.GopForFramerate(framerate));

        // 目標間隔（秒）からのずれが 1 フレーム未満であること。
        ContinuousFirstSampleBudget.TryParseFramerate(framerate, out int numerator, out int denominator);
        double fps = (double)numerator / denominator;
        double seconds = EncoderCatalog.GopForFramerate(framerate) / fps;
        Assert.True(Math.Abs(seconds - EncoderCatalog.TargetKeyframeIntervalSeconds) < 1.0 / fps,
            $"{framerate} で {seconds:0.###} 秒間隔（目標 {EncoderCatalog.TargetKeyframeIntervalSeconds} 秒）");
    }

    /// <summary>
    /// framerate が読めないときは既定のフレームレートぶんに倒す。
    /// <b>0 や極端に小さい値を返してはいけない</b> ── そのまま起動文字列に入る。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("variable")]
    [InlineData("30/0")]
    public void GopForFramerate_UnreadableFramerate_FallsBackToTheDefault(string? framerate)
        => Assert.Equal(EncoderCatalog.GopSize, EncoderCatalog.GopForFramerate(framerate));

    /// <summary>
    /// <b>間隔（秒）は引数で受ける。</b> 録画は 2 秒、トランスコードは <c>fragment</c>
    /// 1 つぶん（1 秒）で、逆算の場所は 1 つである。
    ///
    /// <para>
    /// トランスコードでこれが効かないと、実 5fps の本を 30fps プリセットで変換したときに
    /// キーフレームが 30 枚＝6 秒間隔になり、1 秒ごとに切られる <c>fragment</c> のうち
    /// <b>同期サンプルで始まるのは 6 個に 1 個だけ</b>になる（実測）。
    /// </para>
    /// </summary>
    [Theory]
    // トランスコード（1 秒）: 実 fps がそのまま GOP 長になる。
    [InlineData("5/1", 1.0, 5)]
    [InlineData("30/1", 1.0, 30)]
    // 分数のカメラは最近接へ丸める（89/3 ＝ 29.67 → 30 ＝ 要求と同値で組み直しは起きない）。
    [InlineData("89/3", 1.0, 30)]
    // 録画（2 秒）: 既定の呼び出しと同じ値。
    [InlineData("30/1", 2.0, 60)]
    // 0 にはしない（そのまま起動文字列に入る）。
    [InlineData("1/2", 1.0, 1)]
    public void GopForFramerate_TakesTheKeyframeIntervalInSeconds(
        string framerate, double seconds, int expected)
        => Assert.Equal(expected, EncoderCatalog.GopForFramerate(framerate, seconds, fallback: 999));

    /// <summary>
    /// 読めなければ<b>呼び出し側が渡した既定</b>を返す。トランスコードはそこへ
    /// 「要求された GOP 長」を渡すので、測れなかった本は要求のまま走り続ける。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("variable")]
    [InlineData("30/0")]
    public void GopForFramerate_UnreadableFramerate_ReturnsTheCallersFallback(string? framerate)
        => Assert.Equal(30, EncoderCatalog.GopForFramerate(framerate, 1.0, fallback: 30));

    /// <summary>
    /// 既定の呼び出しは「2 秒・<see cref="EncoderCatalog.GopSize"/>」の overload である
    /// （録画の 2 か所の挙動を変えない）。
    /// </summary>
    [Theory]
    [InlineData("30/1")]
    [InlineData("5/1")]
    [InlineData(null)]
    public void GopForFramerate_DefaultOverload_IsTheTwoSecondOne(string? framerate)
        => Assert.Equal(
            EncoderCatalog.GopForFramerate(
                framerate, EncoderCatalog.TargetKeyframeIntervalSeconds, EncoderCatalog.GopSize),
            EncoderCatalog.GopForFramerate(framerate));

    /// <summary>
    /// 低いフレームレートでも候補の起動文字列に反映されること
    /// （<see cref="EncoderCatalog.Resolve"/> の <c>gop</c> 引数を落とすと、
    /// カタログの既定値が黙って使われて上の事故に戻る）。
    /// </summary>
    [Fact]
    public void CandidatesFor_LowFramerate_PinsTheShorterGop()
    {
        var candidates = EncoderCatalog.CandidatesFor(EventRecordingType.D3d12, EncoderCatalog.GopForFramerate("5/1"));

        var qsv = candidates.Single(c => c.FactoryName == "qsvh264enc");
        Assert.Contains("gop-size=10", qsv.LaunchString);

        var x264 = candidates.Single(c => c.FactoryName == "x264enc");
        Assert.Contains("key-int-max=10", x264.LaunchString);
    }

    [Fact]
    public void EveryCandidate_PinsAShortGopLength()
    {
        // GOP 長はアプリの中核契約を成立させるための制約であって画質設定ではない。
        // リングバッファ（BufferDuration）の中に I フレームが1枚も無いと、
        // PushRecordBuffer が事前バッファを丸ごと捨てたうえ、次の I フレームが来るまでの
        // ライブ映像まで失う ──「録画ボタンを押す前の映像が残る」が静かに消える。
        //
        // 実測: gop-size=64（15fps で 4.27 秒間隔）の qsvh264enc は
        // BufferDuration=2000ms / 録画窓3秒に対して生成尺 1.067 秒しか出なかった。
        var all = EncoderCatalog.D3d12Candidates.Concat(EncoderCatalog.SystemCandidates).Distinct();

        foreach (var def in all)
        {
            var m = Regex.Match(def.LaunchString, @"(?:gop-size|key-int-max)=(\d+)");
            Assert.True(m.Success,
                $"'{def.FactoryName}' does not pin a GOP length; the encoder default is typically far longer "
                + "than BufferDuration, which silently destroys the pre-buffer contract.");

            int gop = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

            // **判定は事前バッファ長との比で行う**（GOP の生のフレーム数ではなく）。
            // フレーム数だけを見ていると、フレームレートの既定を上げたときに
            // 「秒では短くなったのにテストが落ちる」という逆の事故になる。
            // 見るのは 2 点 ── 既定のソース（30fps）と、実運用で下げうる下限（15fps）。
            double bufferSeconds = new EventRecorderSettings().BufferDuration / 1000.0;
            foreach (int fps in (int[])[30, 15])
            {
                double gopSeconds = gop / (double)fps;
                Assert.True(0 < gop && gopSeconds * 2 <= bufferSeconds,
                    $"'{def.FactoryName}' uses GOP {gop}; at {fps}fps that is {gopSeconds:0.##}s between "
                    + $"keyframes, and the default BufferDuration is only {bufferSeconds:0.##}s. "
                    + "Keep at least two GOPs inside the pre-buffer, or raise the default "
                    + "BufferDuration together with the GOP.");
            }
        }
    }

    [Fact]
    public void ExpandAttempts_RetriesEachDecoratedCandidateWithoutItsProperties()
    {
        // 実機未確認の GPU エンコーダーでプロパティ名・単位が違っていても、
        // そのエンコーダー自体を取りこぼさないための再試行
        var input = (H264EncoderDef[])[
            new("a264enc", "a264enc bitrate=1", NeedsSystemMemory: false),
            new("b264enc", "b264enc", NeedsSystemMemory: true),
        ];

        var expanded = EncoderCatalog.ExpandAttempts(input).ToArray();

        Assert.Equal(
            ["a264enc bitrate=1", "a264enc", "b264enc"],
            expanded.Select(d => d.LaunchString).ToArray());
    }

    [Fact]
    public void ExpandAttempts_PreservesTheMemoryRequirementOnTheBareRetry()
    {
        var input = (H264EncoderDef[])[new("a264enc", "a264enc bitrate=1", NeedsSystemMemory: true)];
        var expanded = EncoderCatalog.ExpandAttempts(input).ToArray();

        Assert.All(expanded, d => Assert.True(d.NeedsSystemMemory));
    }

    [Fact]
    public void Probe_ReportsEveryCatalogFactory_AndTheResultingOrder()
    {
        var report = EncoderCatalog.Probe(ProbeOnly("x264enc", "d3d12h264enc"));

        Assert.Contains(report.Entries, e => e.FactoryName == "x264enc" && e.Available);
        Assert.Contains(report.Entries, e => e.FactoryName == "qsvh264enc" && !e.Available);
        Assert.Equal((string[])["d3d12h264enc", "x264enc"], report.D3d12Order);
        Assert.Equal((string[])["x264enc"], report.SystemOrder);

        // 診断用の1行に、選択結果と欠落の両方が入っていること
        string line = report.ToLogLine();
        Assert.Contains("x264enc", line);
        Assert.Contains("qsvh264enc", line);
    }
}

/// <summary>
/// sink 側パイプライン文字列の組み立ての検証（純粋関数）。
///
/// <c>parse_launch</c> は変換要素を自動挿入しないため、<c>D3d12</c> 経路で
/// システムメモリ入力のエンコーダーを使う場合は <c>d3d12download</c> を明示的に
/// 挟まなければリンクに失敗する ── まさに AMD / NVIDIA 機で壊れる経路。
/// </summary>
public class BuildSinkPipelineTests
{
    private const string Src = "videotestsrc is-live=true";

    /// <summary>
    /// <b>プレビュー枝の <c>queue</c> が背圧を掛けないこと。</b>
    ///
    /// <para>
    /// <c>queue</c> の既定 <c>max-size-bytes</c> は 10MB で、<b>解像度が上がると
    /// これが「フレーム数の上限」に化ける</b> ── queue は上限超過でも1件目は受け取るので、
    /// 1フレームが 5MB を超える（I420 で約 3.5Mpx ＝ 2560x1440 以上）と
    /// 常に1フレームしか持てなくなる。プレビューの <c>appsink</c> は <c>PAUSED</c> の間
    /// プリロールで止まっているため queue は排出されず、満杯の queue が <c>tee</c> を止め、
    /// エンコーダーが飢えて出力を出さず、録画側 <c>appsink</c> がプリロールできず、
    /// パイプラインが <c>PLAYING</c> に到達せず、プレビューが止まったまま
    /// ── <b>循環待ち。実機で報告された「4K で1フレームも進まない」停止の正体。</b>
    /// </para>
    /// <para>
    /// <b>この表明が守っているのは「上限を外したこと」そのもの</b>であって、
    /// 実際に高解像度で動くことは <c>HighResolutionTests</c>（L2）が見ている。
    /// L1 だけを緑にして満足しないこと。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(EventRecordingType.System)]
    [InlineData(EventRecordingType.D3d12)]
    public void EveryType_MakesThePreviewBranchQueueUnableToBlockTheTee(EventRecordingType type)
    {
        string p = EventRecorder.BuildSinkPipeline(type, Src, "x264enc", needsSystemMemory: false);
        string previewBranch = p[..p.IndexOf("t. !", StringComparison.Ordinal)];

        // tee の直後の queue が対象。エンコーダー枝側の queue（既定のまま＝正しい背圧）と
        // 取り違えないよう、必ず tee より後・"t. !" より前だけを見る。
        int queueAt = previewBranch.IndexOf("tee name=t !", StringComparison.Ordinal);
        Assert.True(queueAt >= 0, "プレビュー枝が tee の直後にありません: " + p);
        string previewQueue = previewBranch[queueAt..];

        Assert.Contains("leaky=downstream", previewQueue);
        Assert.Contains("max-size-bytes=0", previewQueue);
        Assert.Contains("max-size-time=0", previewQueue);
    }

    /// <summary>
    /// <b>エンコーダー枝の <c>queue</c> は既定のままであること。</b>
    /// あちらが <c>tee</c> を止めるのは<b>録画を優先する正しい背圧</b>で、
    /// プレビュー枝と同じ扱いにすると<b>録画フレームを黙って捨てる</b>ようになる。
    /// 「プレビュー枝の停止（N1。<c>tools/Verify-HighResolution.ps1</c> が検証する）を直す」
    /// ついでに両方 leaky にしてしまう改変を止めるための表明。
    /// </summary>
    [Theory]
    [InlineData(EventRecordingType.System)]
    [InlineData(EventRecordingType.D3d12)]
    public void EveryType_KeepsTheEncoderBranchQueueBlocking(EventRecordingType type)
    {
        string p = EventRecorder.BuildSinkPipeline(type, Src, "x264enc", needsSystemMemory: false);
        string encoderBranch = p[p.IndexOf("t. !", StringComparison.Ordinal)..];

        Assert.DoesNotContain("leaky", encoderBranch);
    }

    /// <summary>
    /// <b>RGB のソースでは変換段の colorimetry を固定する。</b>
    ///
    /// <para>
    /// 固定しないと d3d12convert が出力の colorimetry を自分で決め、画面キャプチャの
    /// <c>sRGB</c> から transfer=sRGB を引き継いだまま（mp4 では transfer=13）、
    /// 行列だけ機械依存で選ぶ ── 実測: WARP は 1920x1080 でも BT.601、GPU 実機は BT.709。
    /// 画素は正しいスタジオレンジ（白 Y=235）なのに、再生側が 16-235 を展開せず
    /// <b>白が灰色</b>になる録画ができあがる。
    /// </para>
    /// <para>
    /// <b>画面キャプチャの caps には画素形式が無い</b>（解像度と framerate だけ）ので、
    /// 判定は要素名まで見ないと通らない。ここが縛っているのはその経路である。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("d3d12screencapturesrc monitor-index=0 ! video/x-raw(memory:D3D12Memory), framerate=30/1")]
    [InlineData("d3d11screencapturesrc ! video/x-raw(memory:D3D11Memory), framerate=30/1")]
    [InlineData("mfvideosrc device-index=0 ! video/x-raw, format=BGRA, width=1920, height=1080, framerate=30/1")]
    public void D3d12_WithAnRgbSource_PinsTheColorimetryOfTheConverter(string src)
    {
        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, src, "x264enc", needsSystemMemory: false);

        Assert.Contains("format=NV12, colorimetry=bt709", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>SD は BT.601 を名乗る。</b> 行列を大きさで決めるのは映像の慣例で、
    /// GStreamer 自身の既定も 576 本を境に分かれる（実測: <c>videotestsrc</c> の NV12 は
    /// 576 本まで BT.601、577 本から BT.709 の画素値）── タグを読まずに大きさで
    /// 決める再生系が居るので、そこから外れた組み合わせを名乗らない。
    ///
    /// <para>
    /// 大きさは <c>tee</c> の手前の固定（<c>pinnedResolution</c>）→ ソースの caps の順で見る。
    /// <b>どちらも無ければ HD 扱い</b>（画面キャプチャの既定の構成がこれで、実体はモニターの実寸）。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("d3d12screencapturesrc ! video/x-raw(memory:D3D12Memory), width=720, height=480, framerate=30/1", "", "bt601")]
    [InlineData("d3d12screencapturesrc ! video/x-raw(memory:D3D12Memory), width=1024, height=576, framerate=30/1", "", "bt601")]
    [InlineData("d3d12screencapturesrc ! video/x-raw(memory:D3D12Memory), width=1024, height=577, framerate=30/1", "", "bt709")]
    [InlineData("d3d12screencapturesrc ! video/x-raw(memory:D3D12Memory), framerate=30/1", "", "bt709")]
    [InlineData("d3d12screencapturesrc ! video/x-raw(memory:D3D12Memory), framerate=30/1", "640x480", "bt601")]
    [InlineData("d3d12screencapturesrc ! video/x-raw(memory:D3D12Memory), width=720, height=480, framerate=30/1", "1920x1080", "bt709")]
    public void D3d12_WithAnRgbSource_PicksTheMatrixByHeight(string src, string pinnedResolution, string expected)
    {
        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, src, "x264enc", needsSystemMemory: false,
            continuousBranch: "", pinnedResolution: pinnedResolution);

        Assert.Contains("format=NV12, colorimetry=" + expected, p, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>入力が既に YUV のときは固定しない。</b>
    ///
    /// <para>
    /// d3d12convert は YUV→YUV では行列を変換せず<b>タグだけ書き換える</b>
    /// （実測: BT.601 の画素値のまま bt709 と名乗る）── カメラの NV12 / YUY2 に
    /// bt709 を付けると、画面キャプチャで直したのと同じ嘘を逆向きに作ることになる。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("mfvideosrc device-index=0 ! video/x-raw, format=NV12, width=1920, height=1080, framerate=30/1")]
    [InlineData("d3d12testsrc is-live=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=1280, height=720")]
    [InlineData("videotestsrc is-live=true")]
    public void D3d12_WithAYuvSource_LeavesTheColorimetryToTheConverter(string src)
    {
        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, src, "x264enc", needsSystemMemory: false);

        Assert.DoesNotContain("colorimetry", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>System 経路でも RGB ソースなら colorimetry を固定する。</b>
    ///
    /// <para>
    /// こちらには変換段の出力 caps が無く、RGB→YUV は <c>videoconvert</c> が行う。
    /// 固定しないと画面キャプチャの <c>sRGB</c> から transfer=sRGB を引き継いだままになり
    /// （mp4 では transfer=13）、D3d12 経路で直したのと同じ「白が灰色」の録画ができる
    /// ── <c>d3d11screencapturesrc</c> ＋ <c>Type=System</c> は
    /// <c>show-cursor</c> の逃げ道として文書化された構成なので、ここも塞ぐ。
    /// </para>
    /// <para>
    /// <b>形式は書かない。</b> 形式まで固定すると、それを受けないエンコーダーで
    /// リンクに失敗する（<c>System_AlwaysConvertsBeforeTheEncoder</c> が守っている性質）。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("d3d11screencapturesrc ! video/x-raw(memory:D3D11Memory), framerate=30/1", "bt709")]
    [InlineData("d3d11screencapturesrc ! video/x-raw(memory:D3D11Memory), width=720, height=480, framerate=30/1", "bt601")]
    [InlineData("mfvideosrc device-index=0 ! video/x-raw, format=BGRA, width=1920, height=1080", "bt709")]
    public void System_WithAnRgbSource_PinsTheColorimetryAfterTheConverter(string src, string expected)
    {
        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.System, src, "x264enc", needsSystemMemory: true);

        Assert.Contains(
            "videoconvert ! video/x-raw, colorimetry=" + expected + " ! x264enc", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>System 経路でも、入力が既に YUV なら固定しない。</b>
    /// D3d12 経路と同じ判断 ── 大きさで行列を決める慣例から外れた組み合わせを名乗らせない。
    /// </summary>
    [Theory]
    [InlineData("mfvideosrc device-index=0 ! video/x-raw, format=NV12, width=1920, height=1080")]
    [InlineData("videotestsrc is-live=true")]
    public void System_WithAYuvSource_LeavesTheEncoderBranchUnchanged(string src)
    {
        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.System, src, "x264enc", needsSystemMemory: true);

        Assert.Contains("videoconvert ! x264enc", p, StringComparison.Ordinal);
        Assert.DoesNotContain("colorimetry", p, StringComparison.Ordinal);
    }

    [Fact]
    public void System_UsesTheGivenEncoderAndNeverInsertsD3d12Download()
    {
        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.System, Src, "x264enc bitrate=2000", needsSystemMemory: true);

        Assert.Contains("x264enc bitrate=2000", p);
        Assert.DoesNotContain("d3d12download", p);

        // **`Contains("clockoverlay")` と書いてはいけない。** `dwriteclockoverlay` は
        // その部分文字列を含むので、**どちらの要素でも緑になる**
        // ── 実際この表明は、pango 版から dwrite 版への差し替えを1件も検出しなかった。
        // ここで固定したいのは「cairo 経路の `clockoverlay` を使っていないこと」なので、
        // 要素名の**手前の区切り**まで含めて照合する。
        Assert.Contains("! dwriteclockoverlay ", " " + p.Replace("\r\n", " ").Replace("\n", " "));
        Assert.DoesNotContain("! clockoverlay ", " " + p.Replace("\r\n", " ").Replace("\n", " "));
    }

    [Fact]
    public void D3d12_WithGpuNativeEncoder_DoesNotInsertDownloadBeforeTheEncoder()
    {
        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, Src, "qsvh264enc gop-size=64", needsSystemMemory: false);

        // プレビュー枝には元から d3d12download があるので、エンコーダー直前の分岐だけを見る
        string encoderBranch = p[p.IndexOf("t. !", StringComparison.Ordinal)..];
        Assert.DoesNotContain("d3d12download", encoderBranch);
        Assert.Contains("qsvh264enc gop-size=64", encoderBranch);
    }

    [Fact]
    public void D3d12_WithSystemMemoryEncoder_InsertsDownloadAndConvertBeforeTheEncoder()
    {
        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, Src, "x264enc bitrate=2000", needsSystemMemory: true);

        string encoderBranch = p[p.IndexOf("t. !", StringComparison.Ordinal)..];
        Assert.Contains("d3d12download ! videoconvert ! x264enc bitrate=2000", encoderBranch);
    }

    /// <summary>
    /// <b><c>Type=System</c> はエンコーダーの直前で必ず <c>videoconvert</c> を通す。</b>
    ///
    /// <para>
    /// <c>parse_launch</c> は変換要素を自動挿入しないので、ソースの画素形式が
    /// エンコーダーの sink caps に無いと <b>初期化そのものが失敗する</b>
    /// （<c>could not link queue1 to &lt;enc&gt;0</c>）。
    /// </para>
    /// <para>
    /// <b>実際に踏んだ（GPU 実機・同梱版）:</b>
    /// <c>Type=System</c> の自動選択が <c>mfh264enc</c> を選べず <c>recorder.init fail</c>。
    /// ハードウェアの MediaFoundation MFT は I420 を受けず、ソースは
    /// <c>format=I420</c> 固定だった。<b>同じ機械で <c>Type=D3d12</c> の
    /// <c>mfh264enc</c> 手動指定は通っている</b> ── あちらには
    /// <c>d3d12download ! … ! videoconvert !</c> が在り、差はそこだけ。
    /// </para>
    /// <para>
    /// <b>この検査は「そう書いてある」ことの確認である。</b> 交渉が実際に起きることは
    /// <c>EncoderNegotiationTests</c>（L2）が、ソース形式を変えて確かめる。
    /// </para>
    /// </summary>
    [Fact]
    public void System_AlwaysConvertsBeforeTheEncoder()
    {
        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.System, Src, "mfh264enc bitrate=2000", needsSystemMemory: true);

        string encoderBranch = p[p.IndexOf("t. !", StringComparison.Ordinal)..];
        Assert.Contains("videoconvert ! mfh264enc bitrate=2000", Flatten(encoderBranch));

        // **形式を固定し直してはいけない。** 下流が受け付ける形式へ交渉させることが
        // まさにこの不具合を直している点で、capsfilter を後ろに付けると元に戻る。
        int convert = Flatten(encoderBranch).IndexOf("videoconvert", StringComparison.Ordinal);
        int encoder = Flatten(encoderBranch).IndexOf("mfh264enc", StringComparison.Ordinal);
        Assert.DoesNotContain("video/x-raw", Flatten(encoderBranch)[convert..encoder]);
    }

    /// <summary>
    /// <c>Type=D3d12</c> の GPU ネイティブ経路には <c>videoconvert</c> を入れない。
    /// <b>あそこは D3D12 メモリをそのまま渡しており、<c>videoconvert</c> は扱えない。</b>
    /// System 側の修正をこちらへ広げると、実機で通っている4ケースが壊れる。
    /// </summary>
    [Fact]
    public void D3d12_WithGpuNativeEncoder_DoesNotConvertBeforeTheEncoder()
    {
        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, Src, "d3d12h264enc gop-size=15", needsSystemMemory: false);

        string encoderBranch = p[p.IndexOf("t. !", StringComparison.Ordinal)..];
        Assert.DoesNotContain("videoconvert", encoderBranch);
    }

    private static string Flatten(string s)
        => string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                             .Select(line => line.Trim()));

    [Fact]
    public void D3d12_PreviewBranchAlwaysDownloadsToSystemMemory()
    {
        // プレビューは appsink で受けるので、GPU ネイティブなエンコーダーを選んでも
        // プレビュー枝のダウンロードは常に必要
        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, Src, "qsvh264enc", needsSystemMemory: false);

        string previewBranch = p[..p.IndexOf("t. !", StringComparison.Ordinal)];
        Assert.Contains("d3d12download ! video/x-raw(memory:SystemMemory) ! appsink", previewBranch);
    }

    [Theory]
    [InlineData(EventRecordingType.System)]
    [InlineData(EventRecordingType.D3d12)]
    public void EveryType_EndsWithTheRequiredOutputCapsAndSinkAppsink(EventRecordingType type)
    {
        // このキャップスを満たせないエンコーダーは「存在しても動かない」。
        // 候補ループの成否判定がここに掛かっている。
        string p = EventRecorder.BuildSinkPipeline(type, Src, "x264enc", needsSystemMemory: true);

        Assert.Contains("video/x-h264, stream-format=byte-stream, alignment=au, profile=main", p);
        Assert.Contains("appsink name=sink", p);
    }

    [Theory]
    [InlineData(EventRecordingType.System)]
    [InlineData(EventRecordingType.D3d12)]
    public void EveryType_RepeatsParameterSetsAtEveryIdr(EventRecordingType type)
    {
        // アプリの中核契約は「録画は任意の瞬間にストリームの途中から開始できる」ことであり、
        // その再開点には SPS/PPS が無ければならない。リングバッファには数秒分しか残らないので、
        // ストリーム先頭で1回だけ送られたパラメータセットは録画開始時には既に捨てられている。
        //
        // これが無いと、パラメータセットを繰り返さないエンコーダー（実機の nvh264enc）では
        // src 側の h264parse が全 NAL を broken/invalid として捨て、
        // エラーにならないまま中身の無い MP4 が残る。
        string p = EventRecorder.BuildSinkPipeline(type, Src, "x264enc", needsSystemMemory: true);

        Assert.Contains("h264parse config-interval=-1", p);

        // エンコーダーより後、appsink より前にあること
        int encoderAt = p.IndexOf("x264enc", StringComparison.Ordinal);
        int parseAt = p.IndexOf("h264parse config-interval=-1", StringComparison.Ordinal);
        int sinkAt = p.IndexOf("appsink name=sink", StringComparison.Ordinal);
        Assert.True(encoderAt < parseAt && parseAt < sinkAt,
            "h264parse must sit between the encoder and the recording appsink.");
    }

    [Theory]
    [InlineData(EventRecordingType.System)]
    [InlineData(EventRecordingType.D3d12)]
    public void EveryType_PinsAccessUnitAlignment(EventRecordingType type)
    {
        // PushRecordBuffer とリングバッファの PTS 退避は「1バッファ＝1フレーム」が前提。
        // nal アラインメントに解決されるとバッファが NAL 単位になり前提が崩れる。
        string p = EventRecorder.BuildSinkPipeline(type, Src, "x264enc", needsSystemMemory: true);
        Assert.Contains("alignment=au", p);
    }

    [Theory]
    [InlineData(EventRecordingType.System)]
    [InlineData(EventRecordingType.D3d12)]
    public void EveryType_StartsWithTheSourcePipelineAndExposesThePreviewAppsink(EventRecordingType type)
    {
        string p = EventRecorder.BuildSinkPipeline(type, Src, "x264enc", needsSystemMemory: true);

        Assert.StartsWith(Src, p);
        Assert.Contains("name=preview", p);
        Assert.Contains("tee name=t", p);
    }
}

/// <summary>
/// カタログの解決結果を実際に <see cref="EventRecorder.BuildSinkPipeline"/> へ通す統合検証。
///
/// <c>EncoderCatalog</c> の <c>NeedsSystemMemory</c> と <c>BuildSinkPipeline</c> を
/// それぞれ単独で検証しただけでは、**両者を繋いだときに正しい文字列になるか**を誰も見ていない
/// （<c>BuildSinkPipeline</c> のテストは <c>needsSystemMemory</c> を引数で直接渡すため、
///  カタログ側のデータが壊れても落ちない）。
/// 実際に壊れるのはこの繋ぎ目なので、ここを固定する。
/// </summary>
public class EncoderCatalogToPipelineTests
{
    private const string D3d12Src =
        "d3d12testsrc is-live=true ! video/x-raw(memory:D3D12Memory), format=NV12, width=320, height=240";

    private static string BuildFor(EventRecordingType type, params string[] availableEncoders)
    {
        var set = new HashSet<string>(availableEncoders, StringComparer.Ordinal);
        var resolved = EncoderCatalog.Resolve(type, preferred: null, probe: set.Contains);
        var best = resolved[0];
        return EventRecorder.BuildSinkPipeline(type, D3d12Src, best.LaunchString, best.NeedsSystemMemory);
    }

    [Theory]
    [InlineData("x264enc")]
    [InlineData("openh264enc")]
    [InlineData("mfh264enc")]
    [InlineData("nvh264enc")]
    [InlineData("amfh264enc")]
    public void D3d12_WithASystemMemoryEncoder_TheBuiltPipelineDownloadsBeforeEncoding(string encoder)
    {
        // これが AMD / NVIDIA 機で実際に壊れていた経路。
        // d3d12download が入らないと ParseLaunch が「リンクできない」で失敗する。
        string p = BuildFor(EventRecordingType.D3d12, encoder);
        string encoderBranch = p[p.IndexOf("t. !", StringComparison.Ordinal)..];

        Assert.Contains("d3d12download ! videoconvert !", encoderBranch);
        Assert.Contains(encoder, encoderBranch);
    }

    [Theory]
    [InlineData("d3d12h264enc")]
    [InlineData("qsvh264enc")]
    public void D3d12_WithAGpuNativeEncoder_TheBuiltPipelineFeedsItDirectly(string encoder)
    {
        // GPU ネイティブなエンコーダーに不要なダウンロードを挟むと、
        // せっかくの GPU 経路が毎フレーム CPU へコピーされることになる。
        string p = BuildFor(EventRecordingType.D3d12, encoder);
        string encoderBranch = p[p.IndexOf("t. !", StringComparison.Ordinal)..];

        Assert.DoesNotContain("d3d12download", encoderBranch);
        Assert.Contains(encoder, encoderBranch);
    }

    // ---- 手動指定（EventRecorder.EncodingProperties）経路 ----------------------------
    //
    // レコーダーごとの EncodingProperties は候補を1件に固定しフォールバックしないため、
    // メモリ要件を取り違えると即 IsInitialized=false になる（回復の機会が無い）。

    [Theory]
    [InlineData("x264enc")]
    [InlineData("openh264enc")]
    [InlineData("mfh264enc")]
    [InlineData("nvh264enc")]
    [InlineData("nvd3d11h264enc")]
    [InlineData("amfh264enc")]
    public void ManualOverride_OnD3d12_WithAKnownSystemMemoryEncoder_NeedsTheDownload(string factory)
    {
        // AMD / NVIDIA 機のユーザーが最も自然に取る回避策がこれ。
        // ここで false にすると d3d12download が入らず、D3D12 経路にシステムメモリの
        // エンコーダーを直結するリンク失敗がそのまま再現する。
        Assert.True(EncoderCatalog.NeedsSystemMemoryFor(factory, EventRecordingType.D3d12));
    }

    [Theory]
    [InlineData("qsvh264enc")]
    [InlineData("d3d12h264enc")]
    public void ManualOverride_OnD3d12_WithAKnownGpuNativeEncoder_DoesNotNeedTheDownload(string factory)
    {
        // 一律 true にすると、Intel ユーザーが qsvh264enc を手動指定したときに
        // 不要な d3d12download が入り、GPU 経路が毎フレーム CPU へコピーされる。
        Assert.False(EncoderCatalog.NeedsSystemMemoryFor(factory, EventRecordingType.D3d12));
    }

    [Fact]
    public void ManualOverride_WithAnUnknownEncoder_FallsToTheSafeSidePerType()
    {
        Assert.True(EncoderCatalog.NeedsSystemMemoryFor("somevendorh264enc", EventRecordingType.D3d12));
        Assert.False(EncoderCatalog.NeedsSystemMemoryFor("somevendorh264enc", EventRecordingType.System));
    }

    [Fact]
    public void ManualOverride_OnD3d12_WithX264_TheBuiltPipelineDownloadsBeforeEncoding()
    {
        // EventRecorder.BuildEncoderCandidates と同じ導出をここで固定する
        const string manual = "x264enc tune=zerolatency bitrate=2000 speed-preset=ultrafast key-int-max=30";
        string factory = manual.Split(' ')[0];

        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, D3d12Src, manual,
            EncoderCatalog.NeedsSystemMemoryFor(factory, EventRecordingType.D3d12));

        string encoderBranch = p[p.IndexOf("t. !", StringComparison.Ordinal)..];
        Assert.Contains("d3d12download ! videoconvert ! " + manual, encoderBranch);
    }

    [Fact]
    public void ManualOverride_OnD3d12_WithQsv_KeepsTheGpuPathIntact()
    {
        const string manual = "qsvh264enc rate-control=icq icq-quality=30 gop-size=64";
        string factory = manual.Split(' ')[0];

        string p = EventRecorder.BuildSinkPipeline(
            EventRecordingType.D3d12, D3d12Src, manual,
            EncoderCatalog.NeedsSystemMemoryFor(factory, EventRecordingType.D3d12));

        string encoderBranch = p[p.IndexOf("t. !", StringComparison.Ordinal)..];
        Assert.DoesNotContain("d3d12download", encoderBranch);
    }

    [Fact]
    public void TryGetKnown_DistinguishesCatalogEntriesFromArbitraryNames()
    {
        Assert.True(EncoderCatalog.TryGetKnown("x264enc", out var known));
        Assert.Equal("x264enc", known.FactoryName);
        Assert.False(EncoderCatalog.TryGetKnown("somevendorh264enc", out _));
    }

    [Fact]
    public void D3d12_OnAMachineWithNoGpuEncoderAtAll_StillProducesALinkablePipeline()
    {
        // GPU が1つも無い環境（CI の WARP、この開発機）でも D3d12 経路が成立すること
        string p = BuildFor(EventRecordingType.D3d12, "x264enc");
        string encoderBranch = p[p.IndexOf("t. !", StringComparison.Ordinal)..];

        Assert.Contains("d3d12download ! videoconvert ! x264enc", encoderBranch);
    }
}
/// <summary>
/// <c>H264EncoderDef.WithBitrateKbps</c> ── <b>単位差を 1 か所に閉じ込める</b>ための変換。
///
/// <para>
/// <c>x264enc</c> / <c>mfh264enc</c> の <c>bitrate</c> は kbit/sec だが
/// <c>openh264enc</c> は bit/sec で、数値をそのまま写すと 2000 bit/sec（＝2kbps）になる。
/// 呼び出し側が単位を知らずに済むよう、定義そのものに単位を持たせてある。
/// </para>
/// <para>
/// <b>既定値の往復が文字列同一であることを固定する。</b> <c>LaunchString</c> の既定は
/// <c>EncoderCatalogScriptSyncTests</c> と <c>tools/Verify-GpuEncoders.ps1</c> が
/// 完全一致で縛っており、ここが崩れると<b>実機検証のケースだけが古い文字列で回る</b>。
/// </para>
/// </summary>
public class EncoderBitrateParameterizationTests
{
    private static H264EncoderDef Def(string factoryName)
        => EncoderCatalog.D3d12Candidates.Concat(EncoderCatalog.SystemCandidates)
            .First(c => string.Equals(c.FactoryName, factoryName, StringComparison.Ordinal));

    /// <summary>
    /// <c>bitrate=&lt;数値&gt;</c> のトークン。<b>製品の
    /// <c>H264EncoderDef.BitrateTokenRegex</c> をそのまま引く</b> ──
    /// パターンを写すと、緩い <c>bitrate=</c> へ片方だけ直した日に
    /// <c>max-bitrate=</c> / <c>target-bitrate=</c> にも一致するようになり、
    /// カタログの不変条件が「別のプロパティで満たされている」定義を見逃す。
    /// </summary>
    private static bool HasBitrateToken(string launchString)
        => H264EncoderDef.BitrateTokenRegex().IsMatch(launchString);

    [Fact]
    public void X264_TakesKilobitsPerSecondUnchanged()
    {
        var def = Def("x264enc").WithBitrateKbps(8000);
        Assert.Contains("bitrate=8000", def.LaunchString, StringComparison.Ordinal);
        Assert.DoesNotContain("bitrate=2000 ", def.LaunchString, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenH264_TakesBitsPerSecond()
    {
        var def = Def("openh264enc").WithBitrateKbps(8000);
        Assert.Contains("bitrate=8000000", def.LaunchString, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaFoundation_TakesKilobitsPerSecondUnchanged()
    {
        var def = Def("mfh264enc").WithBitrateKbps(8000);
        Assert.Contains("bitrate=8000", def.LaunchString, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Intel QSV / NVENC の <c>bitrate</c> は kbit/sec</b>（同梱 DLL の property blurb:
    /// qsv は "Target bitrate in kbit/sec, Ignored when rate-control is cqp/icq"、
    /// nvcodec は "Bitrate in kbit/sec (0 = automatic)"）。
    ///
    /// <para>
    /// <b>レート制御のモードも一緒に見る。</b> 値が書けても <c>qsvh264enc</c> の
    /// <c>cqp</c> / <c>icq</c> では <c>bitrate</c> が無視されるので、
    /// モードが品質基準へ戻った日に「指定した帯域が効かない」形で壊れる
    /// ── 起動文字列からは分からず、配信物を測るまで気付けない。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("qsvh264enc", "rate-control=cbr")]
    [InlineData("nvh264enc", "rc-mode=cbr")]
    [InlineData("nvd3d11h264enc", "rc-mode=cbr")]
    [InlineData("nvautogpuh264enc", "rc-mode=cbr")]
    public void GpuEncodersWithAConfirmedUnit_TakeKilobitsPerSecondUnchanged(string factoryName, string rateControl)
    {
        var def = Def(factoryName);
        Assert.Equal(1, def.BitrateUnitPerKbps);

        var applied = def.WithBitrateKbps(800);

        Assert.Contains("bitrate=800 ", applied.LaunchString, StringComparison.Ordinal);
        Assert.DoesNotContain("bitrate=2000", applied.LaunchString, StringComparison.Ordinal);
        Assert.Contains(rateControl, applied.LaunchString, StringComparison.Ordinal);
        // GOP は帯域の書き換えで巻き添えにならない。
        Assert.Contains($"gop-size={EncoderCatalog.GopSize}", applied.LaunchString, StringComparison.Ordinal);
    }

    /// <summary>
    /// 単位を持たない定義（実機で <c>bitrate</c> を確認できていない GPU 系）は
    /// <b>同じインスタンスをそのまま返す</b> ── 当て推量のプロパティを書かない。
    /// </summary>
    [Theory]
    [InlineData("d3d12h264enc")]
    [InlineData("amfh264enc")]
    public void DefinitionsWithoutABitrateUnit_AreReturnedAsIs(string factoryName)
    {
        var def = Def(factoryName);
        Assert.Null(def.BitrateUnitPerKbps);
        Assert.Same(def, def.WithBitrateKbps(8000));
    }

    /// <summary>
    /// <b>既定値（2000 kbit/sec）を与えた結果が現行の起動文字列と 1 文字も違わないこと。</b>
    /// これが成り立っているあいだは、<c>Verify-GpuEncoders.ps1</c> との文字列一致検査を
    /// 巻き込まずに bitrate をパラメータ化できている。
    /// </summary>
    [Fact]
    public void ApplyingTheDefaultBitrate_ReproducesTheCurrentLaunchStrings()
    {
        // 見るのは**単位を持つ定義**だけ ── 単位の無い定義は同じインスタンスが返るだけの
        // 自明な行で、それは DefinitionsWithoutABitrateUnit_AreReturnedAsIs の担当である。
        // 名前で一意化する（x264 / openh264 / mfh264 は両方の候補列に居る）。
        var withAUnit = EncoderCatalog.D3d12Candidates.Concat(EncoderCatalog.SystemCandidates)
            .DistinctBy(c => c.FactoryName)
            .Where(c => c.BitrateUnitPerKbps is not null)
            .ToArray();

        Assert.NotEmpty(withAUnit);

        foreach (var def in withAUnit)
            Assert.Equal(def.LaunchString, def.WithBitrateKbps(2000).LaunchString);
    }

    /// <summary>
    /// カタログ全体で「単位を持つ ⇔ <c>bitrate=</c> トークンがある」が成り立つこと。
    /// <b><c>WithoutProperties()</c> の後も見る</b> ── プロパティを落とした定義に単位が
    /// 残っていると、<c>WithoutProperties().WithBitrateKbps(…)</c> が実行時に投げる。
    /// </summary>
    [Fact]
    public void EveryDefinitionAgreesOnWhetherItHasABitrate()
    {
        var all = EncoderCatalog.D3d12Candidates.Concat(EncoderCatalog.SystemCandidates).ToArray();
        Assert.NotEmpty(all);

        foreach (var def in all)
        {
            AssertAgrees(def);
            AssertAgrees(def.WithoutProperties());
        }

        static void AssertAgrees(H264EncoderDef def)
        {
            bool hasToken = HasBitrateToken(def.LaunchString);
            Assert.True(def.BitrateUnitPerKbps is null || hasToken,
                $"{def.FactoryName}: BitrateUnitPerKbps が非 null なのに 'bitrate=' が無い: {def.LaunchString}");
            Assert.True(!hasToken || def.BitrateUnitPerKbps is not null,
                $"{def.FactoryName}: 'bitrate=' があるのに BitrateUnitPerKbps が null: {def.LaunchString}");
        }
    }

    /// <summary>
    /// 単位だけが残った定義は<b>投げる</b>（黙って元の値を返さない）。
    /// カタログの不変条件が壊れたことを、指定が効かない録画物ではなく例外で知る。
    /// </summary>
    [Fact]
    public void AUnitWithoutABitrateToken_Throws()
    {
        var broken = new H264EncoderDef("x264enc", "x264enc tune=zerolatency", NeedsSystemMemory: true,
            BitrateUnitPerKbps: 1);

        Assert.Throws<InvalidOperationException>(() => broken.WithBitrateKbps(8000));
    }

    /// <summary>
    /// <b><c>max-bitrate=</c> のような別のプロパティは書き換えない。</b>
    /// <c>-</c> は語の境界なので <c>\bbitrate=</c> では一致してしまう ──
    /// 一致すると、指定した帯域は当のプロパティに入らないまま
    /// <b>上限だけが黙って書き換わった</b>起動文字列ができる。
    /// </summary>
    [Fact]
    public void OnlyTheBareBitratePropertyIsRewritten()
    {
        var def = new H264EncoderDef(
            "x264enc", "x264enc max-bitrate=2000 bitrate=1000", NeedsSystemMemory: false,
            BitrateUnitPerKbps: 1);

        var applied = def.WithBitrateKbps(8000);

        Assert.Equal("x264enc max-bitrate=2000 bitrate=8000", applied.LaunchString);
    }

    /// <summary>
    /// <c>target-bitrate=</c> しか持たない定義は<b>「トークンが無い」と判定されて投げる</b>
    /// ── 緩い正規表現だと一致してしまい、別のプロパティが書き換わって正常終了する。
    /// </summary>
    [Fact]
    public void APrefixedBitratePropertyDoesNotCountAsTheToken()
    {
        var def = new H264EncoderDef(
            "x264enc", "x264enc target-bitrate=2000", NeedsSystemMemory: false,
            BitrateUnitPerKbps: 1);

        Assert.Throws<InvalidOperationException>(() => def.WithBitrateKbps(8000));
    }

    /// <summary>
    /// <b>単位との積は <c>checked</c> である。</b> 単位 1000 の定義（bit/sec で受ける
    /// <c>openh264enc</c>）に大きな kbps を渡すと <c>int</c> を溢れる ──
    /// 溢れたまま書くと<b>負の bitrate</b> の起動文字列ができ、例外も出ずに
    /// 「指定が効かない」形で現れる。
    /// </summary>
    [Fact]
    public void AKilobitValueThatOverflowsTheUnit_Throws()
    {
        var def = Def("openh264enc");
        Assert.Equal(1000, def.BitrateUnitPerKbps);

        Assert.Throws<OverflowException>(() => def.WithBitrateKbps(int.MaxValue));
        Assert.Throws<OverflowException>(() => def.WithBitrateKbps((int.MaxValue / 1000) + 1));
    }
}

/// <summary>
/// 録画トランスコードに使う H.264 デコーダーの検出。
///
/// <para>
/// <b>候補はハードウェアだけである。</b> 同梱ランタイムにソフトウェアの H.264 デコーダーは
/// 入れていないので、1 つも見つからない機械では録画トランスコードを提供しない
/// ── ここで「見つかったことにする」種類の緩和を入れると、GPU の無い機械で
/// <c>parse_launch</c> が落ちる形になる。
/// </para>
/// <para>
/// 実機に依存しないよう<b>プローブを注入</b>して検証する。
/// </para>
/// </summary>
public sealed class H264DecoderProbeTests
{
    /// <summary>候補の並び。<c>d3d11h264dec</c> は DXVA 経由でベンダに依存しないので先頭。</summary>
    [Fact]
    public void TheCandidatesAreTheFrozenHardwareList()
    {
        string[] expected = ["d3d11h264dec", "d3d12h264dec", "nvh264dec", "qsvh264dec"];
        string[] actual = [.. EncoderCatalog.H264DecoderCandidates];

        Assert.Equal<IEnumerable<string>>(expected, actual);
    }

    /// <summary>候補順で最初に見つかったものを返す（後ろのものは選ばれない）。</summary>
    [Theory]
    [InlineData("d3d11h264dec")]
    [InlineData("d3d12h264dec")]
    [InlineData("nvh264dec")]
    [InlineData("qsvh264dec")]
    public void TheFirstAvailableCandidateWins(string available)
    {
        // 「その 1 つだけ在る」ときは必ずそれが選ばれる。
        Assert.Equal(available, EncoderCatalog.ProbeH264Decoder(n => n == available));
    }

    [Fact]
    public void EarlierCandidatesBeatLaterOnes()
    {
        Assert.Equal("d3d11h264dec", EncoderCatalog.ProbeH264Decoder(_ => true));
        Assert.Equal("d3d12h264dec",
            EncoderCatalog.ProbeH264Decoder(n => n != "d3d11h264dec"));
        Assert.Equal("nvh264dec",
            EncoderCatalog.ProbeH264Decoder(n => n is "nvh264dec" or "qsvh264dec"));
    }

    /// <summary>
    /// 1 つも無ければ <see langword="null"/>。<b>ソフトウェアへ落とさない</b>
    /// ── 同梱していないものを名指しても <c>parse_launch</c> が落ちるだけである。
    /// </summary>
    [Fact]
    public void NoCandidateMeansNull()
    {
        Assert.Null(EncoderCatalog.ProbeH264Decoder(_ => false));
        Assert.Null(EncoderCatalog.ProbeH264Decoder(n => n == "avdec_h264"));
        Assert.Null(EncoderCatalog.ProbeH264Decoder(n => n == "openh264dec"));
    }

    /// <summary>結果は <c>LastH264Decoder</c> にも残る（診断と能力の応答が同じ値を見る）。</summary>
    [Fact]
    public void TheResultIsKeptInLastH264Decoder()
    {
        EncoderCatalog.ProbeH264Decoder(n => n == "qsvh264dec");
        Assert.Equal("qsvh264dec", EncoderCatalog.LastH264Decoder);

        EncoderCatalog.ProbeH264Decoder(_ => false);
        Assert.Null(EncoderCatalog.LastH264Decoder);
    }

    [Fact]
    public void ANullProbeIsRejected()
        => Assert.Throws<ArgumentNullException>(() => EncoderCatalog.ProbeH264Decoder(null!));

    /// <summary>
    /// 名指し（<c>PROCESSRECORDERAPP_H264_DECODER</c>）が在れば<b>候補表より先</b>に採られる。
    /// 表の中の要素を名指しても同じ（順序が入れ替わる）。
    /// </summary>
    [Fact]
    public void ThePreferredDecoderBeatsTheCandidates()
    {
        // 表に無いソフトウェアのデコーダーでも、実機に在れば採られる。
        Assert.Equal("openh264dec",
            EncoderCatalog.ProbeH264Decoder(_ => true, "openh264dec"));
        Assert.Equal("avdec_h264",
            EncoderCatalog.ProbeH264Decoder(n => n is "avdec_h264" or "d3d11h264dec", "avdec_h264"));

        // 表の中の後ろの要素を名指せば、先頭の候補を飛び越す。
        Assert.Equal("qsvh264dec", EncoderCatalog.ProbeH264Decoder(_ => true, "qsvh264dec"));

        // 結果は名指しでも LastH264Decoder に残る（能力の応答が読むのは同じ値）。
        Assert.Equal("qsvh264dec", EncoderCatalog.LastH264Decoder);
    }

    /// <summary>
    /// 名指したものが<b>実機に無ければ候補表へ落ちる</b>
    /// ── 綴りを間違えても「デコーダーが無い」以上の壊れ方はしない。
    /// </summary>
    [Fact]
    public void AnAbsentPreferredDecoderFallsBackToTheCandidates()
    {
        Assert.Equal("d3d11h264dec",
            EncoderCatalog.ProbeH264Decoder(n => n != "openh264dec", "openh264dec"));
        Assert.Equal("nvh264dec",
            EncoderCatalog.ProbeH264Decoder(n => n is "nvh264dec" or "qsvh264dec", "typo-h264dec"));

        // 候補も 1 つも無ければ null（名指しの有無で答えは変わらない）。
        Assert.Null(EncoderCatalog.ProbeH264Decoder(_ => false, "openh264dec"));
    }

    /// <summary>
    /// 空・空白のみの名指しは<b>無かったことにする</b>（存在確認にも掛けない）
    /// ── 環境変数を空で置く形は「設定していない」と同じでなければならない。
    /// </summary>
    [Fact]
    public void AnEmptyPreferredDecoderIsIgnored()
    {
        foreach (string? preferred in new string?[] { null, "", "   " })
        {
            var probed = new List<string>();
            Assert.Equal("d3d11h264dec", EncoderCatalog.ProbeH264Decoder(
                n => { probed.Add(n); return true; }, preferred));
            Assert.Equal(["d3d11h264dec"], probed);
        }
    }
}
