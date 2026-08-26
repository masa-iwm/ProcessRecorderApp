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
        settings.FragmentedOutput = false;
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
        settings.FragmentedOutput = false;
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
        settings.FragmentedOutput = false;
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

    [Fact]
    public void PreferredEncoder_ThatDoesNotExist_FallsThroughAndStillRecords()
    {
        var settings = new SettingsFile { PreferredH264Encoder = "nosuchh264enc", FragmentedOutput = false };
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
