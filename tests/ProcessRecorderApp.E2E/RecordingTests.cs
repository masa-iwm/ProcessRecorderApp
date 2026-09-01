using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>録画そのものの契約（生成される MP4・CLI の出力・activity.log）。</summary>
[Collection(E2ECollection.Name)]
public sealed partial class RecordingTests(PublishedApp app, ITestOutputHelper output)
{
    private static readonly TimeSpan RecordingWindow = TimeSpan.FromSeconds(3);

    [Fact]
    public void RecordingAll_ProducesOneValidMp4PerRecorder_AndLogsTheContractEvents()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");
        settings.AddRecorder("R2");

        using var instance = AppInstance.Create(app, settings);

        var start = instance.AssertExit(0, instance.Run("start-recording-all"));

        // -all 系の標準出力は1行につき「レコーダー名 <TAB> 解決済みファイル名」。
        // 未展開のテンプレートを返していた不具合の回帰確認でもある。
        var started = ParseAllOutput(start.StdOut);
        Assert.Equal(["R1", "R2"], started.Select(e => e.Name).Order());
        Assert.All(started, e => Assert.True(Path.IsPathRooted(e.File), e.File));
        Assert.All(started, e => Assert.DoesNotContain("{", e.File));

        Thread.Sleep(RecordingWindow);

        var stop = instance.AssertExit(0, instance.Run("stop-recording-all"));
        Assert.Equal(started, ParseAllOutput(stop.StdOut));

        // 2件が独立したファイルとして生成され、どちらも再生可能な MP4 であること。
        var files = instance.ListRecordings();
        Assert.Equal(2, files.Count);
        foreach (string file in files)
        {
            RecordedMp4.AssertUsable(file, instance, output);
        }

        var log = instance.ReadActivityLog();
        Assert.Equal(2, ActivityLogFile.Events(log, "recording.start").Count);
        var stops = ActivityLogFile.Events(log, "recording.stop");
        Assert.Equal(2, stops.Count);
        Assert.All(stops, l => Assert.Contains("result=ok", l));

        // 失敗側のイベント名が1件も出ていないこと。
        // 「recording.stop が2件ある」だけでは、timeout/error で終わった停止と区別できない。
        Assert.Empty(ActivityLogFile.Events(log, "recording.stop timeout"));
        Assert.Empty(ActivityLogFile.Events(log, "recording.stop error"));
        Assert.Empty(ActivityLogFile.Events(log, "recording.start fail"));
        Assert.Empty(ActivityLogFile.Events(log, "recorder.error"));
        Assert.Empty(ActivityLogFile.Events(log, "app.error"));
    }

    [Fact]
    public void ColdStart_RecordsWithoutAPreExistingResidentWorker()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        // 常駐ワーカーを起こさずに作る。最初のコマンドがランチャー経由でワーカーを起動し、
        // レコーダーの登録が終わるより先にコマンドが処理されると 14（実行できない）になる
        // ── IsReady（起動直後のコマンドが登録完了を待つこと）の回帰テスト。
        using var instance = AppInstance.Create(app, settings, startWorker: false);

        var start = instance.Run("start-recording-all");
        Assert.True(start.ExitCode == 0, start + Environment.NewLine + instance.DiagnosticDump());
        Assert.Single(SplitLines(start.StdOut));

        Thread.Sleep(RecordingWindow);

        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        string file = Assert.Single(instance.ListRecordings());
        output.WriteLine($"cold start: {start.Elapsed.TotalMilliseconds:F0}ms");
        RecordedMp4.AssertUsable(file, instance, output);
    }

    [Fact]
    public void FilenameTemplate_ExpandsEveryPlaceholderKind()
    {
        const string EnvVarName = "PRA_E2E_SITE";
        const string EnvVarValue = "taipei";

        var settings = new SettingsFile();
        var recorder = settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, configure: i =>
        {
            // {ENV.x} の検証を実行環境に依存させない。USERNAME は機械によって非 ASCII になる。
            i.ExtraEnvironment[EnvVarName] = EnvVarValue;
        });

        // テンプレートは常駐ワーカーの起動時に読まれるので、書いてから起動し直す必要がある
        // ── ここでは Create の前に決めておく方が素直だが、録画先ディレクトリは
        // インスタンス生成後にしか分からないため、テンプレートだけ後から差し替えて再起動する。
        recorder.FilenameTemplate =
            Path.Combine(instance.RecordingsDir, $"{{Now:yyyyMMdd}}_{{ENV.{EnvVarName}}}_{{Stage}}_{{Name}}.mp4");
        instance.WriteSettings();
        instance.KillWorkers();
        instance.StartWorkerAndWaitUntilReady(AppInstance.DefaultReadyBudget);

        Assert.Equal(0, instance.Run("--set", "Stage=alpha").ExitCode);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        Thread.Sleep(RecordingWindow);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        string file = Assert.Single(instance.ListRecordings());
        string name = Path.GetFileName(file);
        output.WriteLine(name);

        // {ENV.x} が展開されていること（実際にあった不具合の回帰テスト。
        // 正規表現が \w+ だとドットを含まず、"{ENV.PRA_E2E_SITE}" が
        // そのままファイル名に残る）。
        Assert.DoesNotContain("{", name);
        Assert.Matches(ExpandedNamePattern(), name);
        Assert.Contains(EnvVarValue, name);
        Assert.Contains("alpha", name);

        RecordedMp4.AssertUsable(file, instance, output);
    }

    /// <summary>
    /// <b>録画の既定ビットレートがソースの大きさから決まること</b>を、発行物の
    /// <c>gst.encoder selected</c> 1 行で断定する。
    ///
    /// <para>
    /// ソースは caps で <c>1280x720</c>・<c>30/1</c> を名乗るので、式
    /// （<c>幅 × 高さ × fps × 0.1 / 1000</c>）は 2765 kbit/sec、ピークはその 1.5 倍で
    /// 4148 kbit/sec になる。<b>床（300）にも天井（40000）にも当たらない値</b>を選んである
    /// ── 当たる値だと、式が壊れていても clamp のおかげで同じ数字が出る。
    /// </para>
    /// <para>
    /// <b>主の断定は CI の既定エンコーダー（<c>x264enc</c>）で行う。</b> あちらは ABR で
    /// <c>max-bitrate</c> を持たないので、見えるのは目標だけ ── それでも
    /// 「式 → <c>WithBitrateKbps</c> → 起動文字列」という配線の証明としては足りる
    /// （どの定義へ当てるかは L1 の担当）。
    /// </para>
    /// </summary>
    [Fact]
    public void RecordingBitrate_FollowsTheSourceSize_AndReachesTheEncoderString()
    {
        var settings = new SettingsFile();
        var recorder = settings.AddRecorder("R1");
        // 1280x720 / 30fps の caps 付きソース（EncodingProperties は付けない ──
        // 手書きの起動文字列は式を通らないので、それでは配線を見たことにならない）。
        recorder.SrcPipeline = SettingsFile.LargeVideoTestSrc;

        using var instance = AppInstance.Create(app, settings);

        string selected = Assert.Single(
            ActivityLogFile.Events(instance.ReadActivityLog(), "gst.encoder selected"));
        output.WriteLine(selected);

        // 大きさの出所は caps（モニターでも仮定値でも手動指定でもない）。
        Assert.Contains("size-source=caps", selected, StringComparison.Ordinal);
        Assert.Contains("bitrate-kbps=2765", selected, StringComparison.Ordinal);
        Assert.Contains(SettingsFile.DefaultEncoder, selected, StringComparison.Ordinal);
        Assert.Contains("bitrate=2765 ", selected, StringComparison.Ordinal);

        // 式の値で実際に録れること（通らない起動文字列を組み立てても意味が無い）。
        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        Thread.Sleep(RecordingWindow);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        RecordedMp4.AssertUsable(Assert.Single(instance.ListRecordings()), instance, output);
    }

    /// <summary>
    /// <b><c>max-bitrate</c> まで端から端で見られる唯一の経路。</b>
    /// <c>mfh264enc</c>（Media Foundation のソフト MFT）は
    /// <c>rc-mode=pcvbr</c> で目標と上限の両方を取る定義で、GPU の無いこの層で
    /// <b>ピークが式の 1.5 倍になって実際に <c>parse_launch</c> を通ることを確かめられる</b>
    /// のはこれだけである（<c>x264enc</c> は ABR、GPU の定義は要素が無い）。
    ///
    /// <para>
    /// <b>実機に <c>mfh264enc</c> が無ければ Skip する</b>（判定は <c>gst.encoders</c> の
    /// <c>available=[…]</c>。Media Foundation の H.264 エンコーダーは Windows の SKU に依る）
    /// ── 不在を赤にすると製品の欠陥に見える。<b>緑だから走ったとは限らないので、
    /// 実行結果の skip 件数を見ること。</b>
    /// </para>
    /// </summary>
    [Fact]
    public void RecordingBitrate_OnMediaFoundation_AlsoSetsThePeak()
    {
        var settings = new SettingsFile { PreferredH264Encoder = "mfh264enc" };
        var recorder = settings.AddRecorder("R1");
        recorder.SrcPipeline = SettingsFile.LargeVideoTestSrc;

        using var instance = AppInstance.Create(app, settings);

        var log = instance.ReadActivityLog();
        string probe = Assert.Single(ActivityLogFile.Events(log, "gst.encoders"));
        output.WriteLine(probe);

        Assert.SkipUnless(
            probe.Contains("available=[", StringComparison.Ordinal)
                && probe[probe.IndexOf("available=[", StringComparison.Ordinal)..]
                    .Split(']')[0].Contains("mfh264enc", StringComparison.Ordinal),
            "this machine has no mfh264enc (Media Foundation H.264 encoder), "
                + "so the max-bitrate assertion cannot run here");

        string selected = Assert.Single(ActivityLogFile.Events(log, "gst.encoder selected"));
        output.WriteLine(selected);

        Assert.Contains("size-source=caps", selected, StringComparison.Ordinal);
        Assert.Contains("bitrate-kbps=2765", selected, StringComparison.Ordinal);
        Assert.Contains(
            "encoder='mfh264enc rc-mode=pcvbr bitrate=2765 max-bitrate=4148 gop-size=60 low-latency=true'",
            selected, StringComparison.Ordinal);
        Assert.Contains("failedAttempts=0", selected, StringComparison.Ordinal);

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        Thread.Sleep(RecordingWindow);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        RecordedMp4.AssertUsable(Assert.Single(instance.ListRecordings()), instance, output);
    }

    /// <summary>
    /// <b>手動指定（<c>EncodingProperties</c>）では式を通らないので、
    /// <c>bitrate-kbps=</c> を出さない</b>（<c>size-source=manual</c> だけ）。
    /// 出すと、実際には流れていない値が起動文字列の隣に並ぶ。
    /// </summary>
    [Fact]
    public void ManualEncodingProperties_ReportNoDerivedBitrate()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1").AsLarge();

        using var instance = AppInstance.Create(app, settings);

        string selected = Assert.Single(
            ActivityLogFile.Events(instance.ReadActivityLog(), "gst.encoder selected"));
        output.WriteLine(selected);

        Assert.Contains("size-source=manual", selected, StringComparison.Ordinal);
        Assert.DoesNotContain("bitrate-kbps=", selected, StringComparison.Ordinal);
        // 手で書いた値はそのまま流れる。
        Assert.Contains("bitrate=20000", selected, StringComparison.Ordinal);
    }

    [Fact]
    public void PreferredEncoder_ThatDoesNotExist_FallsThroughAndStillRecords()
    {
        var settings = new SettingsFile { PreferredH264Encoder = "nosuchh264enc" };
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        var log = instance.ReadActivityLog();

        // プローブ結果と選択結果が activity.log に残ること（実際にあった不具合の回帰テスト）。
        string probeLine = Assert.Single(ActivityLogFile.Events(log, "gst.encoders"));
        output.WriteLine(probeLine);
        Assert.Contains("available=[", probeLine);

        string selected = Assert.Single(ActivityLogFile.Events(log, "gst.encoder selected"));
        output.WriteLine(selected);
        Assert.DoesNotContain("nosuchh264enc", selected);

        // 設定ミスで録画不能にならないこと。
        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        Thread.Sleep(RecordingWindow);
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        string file = Assert.Single(instance.ListRecordings());
        RecordedMp4.AssertUsable(file, instance, output);
    }

    /// <summary><c>-all</c> 系の出力（<c>名前 &lt;TAB&gt; ファイル名</c> の行）を分解する。</summary>
    private static (string Name, string File)[] ParseAllOutput(string stdout) =>
        [.. SplitLines(stdout).Select(l => l.Split('\t', 2))
            .Select(p => (Name: p[0], File: p.Length > 1 ? p[1] : ""))
            .OrderBy(e => e.Name, StringComparer.Ordinal)];

    private static string[] SplitLines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim('\r', ' ')).Where(l => l.Length > 0)];

    /// <summary><c>yyyyMMdd_&lt;env&gt;_&lt;変数&gt;_&lt;レコーダー名&gt;.mp4</c>。</summary>
    [GeneratedRegex(@"^\d{8}_[^_]+_[^_]+_R1\.mp4$")]
    private static partial Regex ExpandedNamePattern();
}
