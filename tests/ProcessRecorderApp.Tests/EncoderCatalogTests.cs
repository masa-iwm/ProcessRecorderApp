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

            // 想定される最低フレームレート 15fps で GOP 間隔が 1 秒以内に収まること
            Assert.True(0 < gop && gop <= 15,
                $"'{def.FactoryName}' uses GOP {gop}; at 15fps that is {gop / 15.0:0.##}s between keyframes, "
                + "which must stay comfortably shorter than BufferDuration.");
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
        Assert.Contains("d3d12download ! video/x-raw(memory:SystemMemory) ! videoconvert ! x264enc bitrate=2000", encoderBranch);
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

        Assert.Contains("d3d12download ! video/x-raw(memory:SystemMemory) ! videoconvert !", encoderBranch);
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
        Assert.Contains("d3d12download ! video/x-raw(memory:SystemMemory) ! videoconvert ! " + manual, encoderBranch);
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

        Assert.Contains("d3d12download ! video/x-raw(memory:SystemMemory) ! videoconvert ! x264enc", encoderBranch);
    }
}
