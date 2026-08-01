using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// 保存先ディレクトリ（<c>OutputDirectory</c>）と、古い mp4 の自動削除。
///
/// <para>
/// <b>相対パスの基準が実行ファイルのディレクトリであることを固定する。</b>
/// <c>Directory.GetCurrentDirectory()</c> 基準にすると、常駐ワーカーは最初に起動した
/// シェルのカレントディレクトリをプロセス寿命ぶん引きずる
/// ── 「誰がどこから起動したか」で出力先が変わる。L1 の <c>AppDirectoriesTests</c> は
/// 解決規則そのものを見るが、<b>その規則が実際に録画へ届いていること</b>は
/// 発行物を動かさないと分からない。
/// </para>
/// <para>
/// 自動削除は<b>起動直後に1回走る</b>ので、更新時刻を過去に倒したファイルを置いて
/// 起動するだけで検証できる ── 時間の細工もテスト専用の環境変数も要らない。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class OutputDirectoryTests(PublishedApp app, ITestOutputHelper output)
{
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(30);

    [Fact]
    public void ARelativeFilenameTemplate_IsResolvedAgainstTheOutputDirectory()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        // サブフォルダー名は実行ごとに一意にする。**「発行ディレクトリに漏れていない」の
        // 表明は、固定名だと前回の残骸で落ちる**。
        string relativeFolder = "";

        using var instance = AppInstance.Create(app, settings, configure: i =>
        {
            i.Settings.OutputDirectory = Path.Combine(i.DataDir, "out");
            relativeFolder = "clips-" + i.KeyPrefix;
            // 相対パス。サブフォルダーも作られることを併せて見る。
            i.Settings.Recorders[0].FilenameTemplate = Path.Combine(relativeFolder, "{Name}.mp4");
        });

        Assert.Equal(0, instance.Run("start-recording-all").ExitCode);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Assert.Equal(0, instance.Run("stop-recording-all").ExitCode);

        string expected = Path.Combine(instance.DataDir, "out", relativeFolder, "R1.mp4");
        Assert.True(File.Exists(expected),
            $"保存先の下に出ていません: {expected}{Environment.NewLine}{instance.DiagnosticDump()}");
        Assert.True(new FileInfo(expected).Length > 0);

        // 発行ディレクトリに漏れていないこと。
        Assert.False(Directory.Exists(Path.Combine(app.PublishDir, relativeFolder)),
            "発行ディレクトリに書かれています（相対パスの基準が戻っています）");

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    [Fact]
    public void OldRecordingsAreDeletedOnStartup_AndEmptiedFoldersGoWithThem()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        // ワーカーを起動する前に「古い録画」を置く。起動直後の掃除が対象にする。
        using var instance = AppInstance.Create(app, settings, startWorker: false, configure: i =>
        {
            i.Settings.OutputDirectory = Path.Combine(i.DataDir, "out");
            i.Settings.RecordingRetentionDays = 7;
        });

        string root = Path.Combine(instance.DataDir, "out");
        string old = Seed(root, DateTime.Now.AddDays(-30), "2026", "06", "old.mp4");
        string fresh = Seed(root, DateTime.Now, "recent.mp4");
        string other = Seed(root, DateTime.Now.AddDays(-30), "notes.txt");
        string emptyBefore = Path.Combine(root, "already-empty");
        Directory.CreateDirectory(emptyBefore);

        instance.StartWorkerAndWaitUntilReady(AppInstance.DefaultReadyBudget);

        WaitUntil(() => !File.Exists(old),
            () => $"古い録画が削除されませんでした。{instance.DiagnosticDump()}");

        Assert.False(Directory.Exists(Path.Combine(root, "2026", "06")), "空になったサブフォルダーが残っています");
        Assert.False(Directory.Exists(Path.Combine(root, "2026")), "空になった親フォルダーが残っています");

        // 範囲を広げないための表明
        Assert.True(File.Exists(fresh), "期限内のファイルが消えています");
        Assert.True(File.Exists(other), "mp4 以外が消えています");
        Assert.True(Directory.Exists(emptyBefore), "元から空だったフォルダーまで消えています");
        Assert.True(Directory.Exists(root), "保存先そのものが消えています");

        var log = instance.ReadActivityLog();
        output.WriteLine(string.Join(Environment.NewLine, ActivityLogFile.Events(log, "cleanup.run")));
        Assert.NotEmpty(ActivityLogFile.Events(log, "cleanup.run"));
        Assert.Empty(ActivityLogFile.Events(log, "app.error"));
    }

    [Fact]
    public void NothingIsDeletedWhenTheRetentionIsZero()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings, startWorker: false, configure: i =>
        {
            i.Settings.OutputDirectory = Path.Combine(i.DataDir, "out");
            i.Settings.RecordingRetentionDays = 0;   // 既定。明示して意図を残す
        });

        string root = Path.Combine(instance.DataDir, "out");
        string ancient = Seed(root, DateTime.Now.AddDays(-3650), "ancient.mp4");

        instance.StartWorkerAndWaitUntilReady(AppInstance.DefaultReadyBudget);

        // 掃除が走るなら起動直後に走る。上のケースと同じだけ待ってから見る。
        Thread.Sleep(TimeSpan.FromSeconds(5));

        Assert.True(File.Exists(ancient), "0 日指定なのに削除されました");
        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "cleanup.run"));
    }

    private static string Seed(string root, DateTime lastWrite, params string[] parts)
    {
        string path = Path.Combine([root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[64]);
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    private static void WaitUntil(Func<bool> condition, Func<string> describeFailure)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            if (condition())
                return;
            Thread.Sleep(250);
        }
        while (deadline.Elapsed < CleanupBudget);

        Assert.Fail(describeFailure());
    }
}
