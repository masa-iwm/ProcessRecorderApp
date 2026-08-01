using System.Text;
using System.Text.Json;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// 設定・ログの永続化。いずれも「プロセスを跨いだときにどうなるか」なので L1 では確認できない。
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class PersistenceTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>デバウンス保存（約1秒）が書き切るまでの待ち。</summary>
    private static readonly TimeSpan DebounceMargin = TimeSpan.FromSeconds(3);

    /// <summary>
    /// <c>--persist</c> で指定したテンプレート変数が、強制終了を跨いで残ること。
    ///
    /// <para>
    /// <b>永続化は明示指定したものだけ</b>という意図的な仕様
    /// （<c>--set</c> はセッション限りで、ディスクに書くのは <c>--persist</c> したキーだけ）。
    /// 対になるのが <see cref="TemplateVariables_AreNotWrittenToDiskWithoutAnExplicitRequest"/>
    /// ── 片方だけだと「常に保存する」に戻す退行を検出できない。
    /// </para>
    /// </summary>
    [Fact]
    public void TemplateVariables_SurviveAForcedKill_WhenPersistenceIsRequested()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        Assert.Equal(0, instance.Run("--set", "Greeting=hello").ExitCode);
        Assert.Equal(0, instance.Run("--persist", "Greeting").ExitCode);

        // 保存契機が「終了時だけ」に戻ると、ここで強制終了した瞬間に失われる
        // （デバウンス自動保存の回帰テスト）。
        Thread.Sleep(DebounceMargin);
        instance.KillWorkers();

        string saved = File.ReadAllText(instance.SettingsPath);
        output.WriteLine(saved);
        using (var document = JsonDocument.Parse(saved))
        {
            var variables = document.RootElement.GetProperty("TemplateVariables");
            Assert.Equal("hello", variables.GetProperty("Greeting").GetString());
        }

        // 再起動しても読み戻せること（実際にあった不具合の回帰テスト）。
        instance.StartWorkerAndWaitUntilReady(AppInstance.DefaultReadyBudget);
        var read = instance.Run("--get", "Greeting");
        Assert.Equal(0, read.ExitCode);
        Assert.Equal("hello", read.StdOut.Trim());
    }

    /// <summary>
    /// <c>--set</c> しただけの変数はディスクに出ないこと（セッション限り）。
    ///
    /// <para>
    /// <b>「保存が起きたこと」まで確かめてから「載っていないこと」を見る。</b>
    /// そうしないと「保存自体が走っていない」だけで緑になり、何も検証しない。
    /// ここでは同じ間に <c>--persist</c> した別の変数が書けていることを保存の証拠に使う。
    /// </para>
    /// </summary>
    [Fact]
    public void TemplateVariables_AreNotWrittenToDiskWithoutAnExplicitRequest()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        Assert.Equal(0, instance.Run("--set", "Ephemeral=session-only").ExitCode);
        Assert.Equal(0, instance.Run("--set", "Kept=on-disk").ExitCode);
        Assert.Equal(0, instance.Run("--persist", "Kept").ExitCode);

        Thread.Sleep(DebounceMargin);
        instance.KillWorkers();

        string saved = File.ReadAllText(instance.SettingsPath);
        output.WriteLine(saved);
        using var document = JsonDocument.Parse(saved);
        var variables = Property(document.RootElement, "TemplateVariables");

        // 保存が実際に走ったことの証拠
        Assert.Equal("on-disk", Property(variables, "Kept").GetString());
        // 本命：明示していない変数は載らない
        Assert.False(variables.TryGetProperty("Ephemeral", out _),
            $"--persist していない変数が settings.json に書かれています: {saved}");
    }

    /// <summary>
    /// <c>--no-persist</c> で指定を外すと、次の保存で settings.json から消えること。
    /// 変数そのものはセッション中は残る。
    /// </summary>
    [Fact]
    public void TemplateVariables_CanHaveTheirPersistenceRevoked()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        Assert.Equal(0, instance.Run("--set", "Kept=on-disk").ExitCode);
        Assert.Equal(0, instance.Run("--persist", "Kept").ExitCode);
        Thread.Sleep(DebounceMargin);
        Assert.Contains("on-disk", File.ReadAllText(instance.SettingsPath));

        Assert.Equal(0, instance.Run("--no-persist", "Kept").ExitCode);

        // 変数自体はこのセッションの間は生きている。
        // **強制終了より前に見る** ── 終了後に --get すると新しいワーカーが
        // settings.json から読み直すので、「保存されていない」ことを確認しているだけになる。
        var read = instance.Run("--get", "Kept");
        Assert.Equal(0, read.ExitCode);
        Assert.Equal("on-disk", read.StdOut.Trim());

        Thread.Sleep(DebounceMargin);
        instance.KillWorkers();

        string saved = File.ReadAllText(instance.SettingsPath);
        output.WriteLine(saved);
        using var document = JsonDocument.Parse(saved);
        Assert.False(Property(document.RootElement, "TemplateVariables").TryGetProperty("Kept", out _),
            $"--no-persist の後も settings.json に残っています: {saved}");
    }

    /// <summary>
    /// 未定義のキーを <c>--persist</c> すると、キー名を標準エラーへ出して
    /// <c>--get</c> と同じ終了コード 11 になること。
    /// </summary>
    [Fact]
    public void PersistingAnUndefinedVariable_IsReported()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        var result = instance.Run("--persist", "NoSuchVariable");
        Assert.Equal(11, result.ExitCode);
        Assert.Contains("NoSuchVariable", result.StdErr, StringComparison.Ordinal);
    }

    /// <summary>
    /// 設定に増やしたプロパティが保存で落ちないこと。
    ///
    /// <para>
    /// `ProcessRecorderApp.Tests` は WinUI アプリプロジェクトを参照できないため、
    /// L1 で検証できるのは基底クラスの <c>JsonSettingsBase</c> まで ──
    /// `AppSettings` 固有のプロパティ（`PreferredH264Encoder` / `StopFinalizeTimeoutMs` /
    /// `TemplateVariables` 等）はここでしか守れない。
    /// ここでは「非既定値で書いて起動 → 保存を誘発 → 書き戻されたファイルにまだ残っている」
    /// という経路で見る ── シリアライズ対象から漏れていれば保存の瞬間に消える。
    /// </para>
    /// </summary>
    [Fact]
    public void EveryAddedSetting_SurvivesASaveTriggeredByTheApp()
    {
        var settings = new SettingsFile
        {
            PreferredH264Encoder = SettingsFile.DefaultEncoder,
            StopFinalizeTimeoutMs = 7000,
            // 保存先は「掃除が走らない」値にしておく（RecordingRetentionDays=0）。
            // ここで検証したいのはシリアライズであって削除ではない。
            OutputDirectory = @"D:\not-used-by-this-test",
            RecordingRetentionDays = 0,
            RecordingCleanupIntervalHours = 9,
        };
        var recorder = settings.AddRecorder("R1");
        recorder.BufferDuration = 4321;

        using var instance = AppInstance.Create(app, settings);

        // --set は TemplateVariablesChanged を経由してデバウンス保存を誘発する。
        // **保存契機としてここに置いてある** ── 消すと他の設定の検査まで黙って効かなくなる。
        // --persist も要る。指定しないと TemplateVariables に載らないので、
        // 下の Site の表明が「保存が走らなかった」のか「載らなかった」のか区別できない。
        Assert.Equal(0, instance.Run("--set", "Site=tokyo").ExitCode);
        Assert.Equal(0, instance.Run("--persist", "Site").ExitCode);
        Thread.Sleep(DebounceMargin);
        instance.KillWorkers();

        string saved = File.ReadAllText(instance.SettingsPath);
        output.WriteLine(saved);
        using var document = JsonDocument.Parse(saved);
        var root = document.RootElement;

        Assert.Equal(SettingsFile.DefaultEncoder, Property(root, "PreferredH264Encoder").GetString());
        Assert.Equal(7000, Property(root, "StopFinalizeTimeoutMs").GetInt32());
        Assert.Equal(@"D:\not-used-by-this-test", Property(root, "OutputDirectory").GetString());
        Assert.Equal(0, Property(root, "RecordingRetentionDays").GetInt32());
        Assert.Equal(9, Property(root, "RecordingCleanupIntervalHours").GetInt32());
        Assert.Equal("tokyo", Property(Property(root, "TemplateVariables"), "Site").GetString());

        var savedRecorder = Assert.Single(Property(root, "Recorders").EnumerateArray());
        Assert.Equal("R1", Property(savedRecorder, "Name").GetString());
        Assert.Equal(4321, Property(savedRecorder, "BufferDuration").GetInt32());
    }

    /// <summary>
    /// プロパティが<b>存在すること</b>を先に表明してから値を取り出す。
    /// <c>GetProperty</c> を直接呼ぶと、シリアライズ対象から漏れた場合の失敗が
    /// <c>KeyNotFoundException</c> になり、何が起きたのか読み取れない
    /// ── このテストが守っているのはまさにその失敗モードなので、診断が最も要るところ。
    /// </summary>
    private static JsonElement Property(JsonElement parent, string name)
    {
        Assert.True(parent.TryGetProperty(name, out var value),
            $"保存された JSON に '{name}' がありません（シリアライズ対象から漏れています）");
        return value;
    }

    /// <summary>
    /// 1MB を超えた <c>activity.log</c> が <c>activity.log.1</c> へ退避されること。
    /// L1（<c>ActivityLogTests</c>）は閾値を明示的に差し替えて検証しているので、
    /// <b>既定の閾値で実際に退避が起きる</b>ことを見ているのはこのテストだけ。
    /// </summary>
    [Fact]
    public void ActivityLog_RotatesOnceItExceedsTheDefaultThreshold()
    {
        const long ThresholdBytes = 1024 * 1024;

        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, startWorker: false, configure: i =>
        {
            // 閾値を少し超えた状態から始める。1行の書式は製品と同じにしておく
            // （読み取り側が「壊れた行」として無視してしまわないように）。
            var filler = new StringBuilder();
            string line = "2026-01-01 00:00:00.000 INFO ping " + new string('x', 200);
            while (filler.Length < ThresholdBytes + 1024)
                filler.AppendLine(line);
            File.WriteAllText(i.ActivityLogPath, filler.ToString(), new UTF8Encoding(false));
        });

        Assert.True(new FileInfo(instance.ActivityLogPath).Length > ThresholdBytes);

        instance.StartWorkerAndWaitUntilReady(AppInstance.DefaultReadyBudget);

        Assert.True(File.Exists(instance.RotatedActivityLogPath),
            "activity.log.1 が作られていません: " + instance.DiagnosticDump());

        var current = new FileInfo(instance.ActivityLogPath);
        var rotated = new FileInfo(instance.RotatedActivityLogPath);
        output.WriteLine($"activity.log={current.Length:N0} bytes / activity.log.1={rotated.Length:N0} bytes");

        Assert.True(rotated.Length > ThresholdBytes, $"退避先が小さすぎます: {rotated.Length}");
        Assert.True(current.Length < ThresholdBytes, $"退避後の本体が大きすぎます: {current.Length}");

        // 退避後も通常どおり記録が続くこと。
        Assert.NotEmpty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.start"));
    }
}
