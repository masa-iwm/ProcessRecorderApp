using System.Globalization;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="ActivityLog"/> の契約。
///
/// このログの存在意義は「いつ録れて、いつ失敗したかがプロセス終了後も残る」ことなので、
/// 検証する契約もその1点に集約される:
///   - 1イベント＝1行（詳細に改行が混じっても崩れない）
///   - 書式はインバリアント・機械可読（L2 が正規表現で読む）
///   - 閾値を超えたらローテーションし、直前の内容は <c>activity.log.1</c> に残る
///   - **何があっても例外を投げない**（ログの失敗でアプリを止めない）
///
/// <see cref="ActivityLog"/> は static な出力先を持つため、このクラスのテストは
/// xunit の既定（同一クラス内は逐次実行）に依存している。
/// </summary>
public class ActivityLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ProcessRecorderApp.Tests", Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_dir, ActivityLog.FileName);
    private string RotatedPath => Path.Combine(_dir, ActivityLog.RotatedFileName);

    public ActivityLogTests() => ActivityLog.Initialize(_dir, mirrorToConsole: false);

    public void Dispose()
    {
        ActivityLog.RotationThresholdBytes = ActivityLog.DefaultRotationThresholdBytes;
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ---- 書式（純粋関数） ----

    [Fact]
    public void FormatLine_HasTimestampLevelEventAndDetail()
    {
        var at = new DateTimeOffset(2026, 7, 26, 13, 4, 5, 678, TimeSpan.FromHours(9));

        string line = ActivityLog.FormatLine(at, "INFO", "recording.start", "recorder='Rec 1'");

        Assert.Equal("2026-07-26 13:04:05.678 INFO recording.start recorder='Rec 1'", line);
    }

    [Fact]
    public void FormatLine_WithoutDetail_EndsAtTheEventName()
    {
        var at = new DateTimeOffset(2026, 7, 26, 13, 4, 5, 678, TimeSpan.Zero);

        Assert.Equal("2026-07-26 13:04:05.678 INFO app.start", ActivityLog.FormatLine(at, "INFO", "app.start", null));
        Assert.Equal("2026-07-26 13:04:05.678 INFO app.start", ActivityLog.FormatLine(at, "INFO", "app.start", ""));
    }

    [Fact]
    public void FormatLine_MultilineDetail_IsFlattenedIntoOneLine()
    {
        // 例外の ToString() をそのまま渡しても行指向の読み取りが壊れないこと
        string line = ActivityLog.FormatLine(
            DateTimeOffset.Now, "ERROR", "recorder.init fail", "boom\r\n   at Foo()\n   at Bar()");

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("boom\t   at Foo()\t   at Bar()", line);
    }

    [Fact]
    public void FormatLine_IsCultureInvariant()
    {
        // 製品側は InvariantGlobalization=true だが、テストホストは既定（false）。
        // カルチャ依存の書式で書かれると L2 の正規表現が環境で壊れる。
        //
        // カルチャは th-TH を使う。**ja-JP では意味が無い** ── 既定の暦がグレゴリオ暦なので
        // `yyyy` は 2026 のままになり、InvariantCulture の指定を消しても落ちない
        // （＝何も守らない緑のテストになる）。th-TH の既定の暦は仏暦で 2026 → 2569 になる。
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var at = new DateTimeOffset(2026, 7, 26, 13, 4, 5, 678, TimeSpan.Zero);

            Assert.StartsWith("2026-07-26 13:04:05.678 ", ActivityLog.FormatLine(at, "INFO", "ping", null));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ---- 追記 ----

    [Fact]
    public void Write_CreatesTheFileAndAppendsOneLinePerEvent()
    {
        ActivityLog.Info("app.start", "pid=1");
        ActivityLog.Warn("gst.encoder candidate-failed", "no element");
        ActivityLog.Error("recorder.error", "disk full");

        string[] lines = File.ReadAllLines(LogPath);

        Assert.Equal(3, lines.Length);
        Assert.Contains(" INFO app.start pid=1", lines[0]);
        Assert.Contains(" WARN gst.encoder candidate-failed no element", lines[1]);
        Assert.Contains(" ERROR recorder.error disk full", lines[2]);
    }

    [Fact]
    public void Write_UnwritablePath_DoesNotThrow()
    {
        // ディレクトリとして作成できない名前を出力先にする（NUL は Win32 の予約デバイス名）
        ActivityLog.Initialize(Path.Combine(_dir, "NUL", "sub"), mirrorToConsole: false);

        ActivityLog.Info("recording.start", "recorder='X'");   // 例外が出ないことが契約
    }

    // ---- ローテーション ----

    [Fact]
    public void Write_BeyondTheThreshold_RotatesToTheBackupFile()
    {
        // 1行 41 バイト（固定長）。閾値 200 なら 5 行目でちょうど1回だけローテーションする。
        ActivityLog.RotationThresholdBytes = 200;

        for (int i = 0; i < 6; i++)
            ActivityLog.Info("ping", $"seq={i}");

        Assert.True(File.Exists(RotatedPath), "activity.log.1 が作られていない");
        Assert.True(new FileInfo(LogPath).Length <= ActivityLog.RotationThresholdBytes);

        // ローテーションは「捨てる」ことではない。1世代目までは1行も失われないこと。
        Assert.Contains("seq=5", File.ReadAllText(LogPath));
        string all = File.ReadAllText(RotatedPath) + File.ReadAllText(LogPath);
        for (int i = 0; i < 6; i++)
            Assert.Contains($"seq={i}", all);
    }

    [Fact]
    public void Write_BelowTheThreshold_DoesNotRotate()
    {
        ActivityLog.RotationThresholdBytes = ActivityLog.DefaultRotationThresholdBytes;

        for (int i = 0; i < 50; i++)
            ActivityLog.Info("ping", $"seq={i}");

        Assert.False(File.Exists(RotatedPath));
        Assert.Equal(50, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public void Rotate_KeepsOnlyOneGeneration()
    {
        ActivityLog.RotationThresholdBytes = 200;

        for (int i = 0; i < 40; i++)
            ActivityLog.Info("ping", $"seq={i}");

        // 世代は activity.log と activity.log.1 の2つだけ（.2 以降は作らない）
        string[] files = Directory.GetFiles(_dir);
        Assert.Equal(2, files.Length);
        Assert.False(File.Exists(Path.Combine(_dir, ActivityLog.FileName + ".2")));
    }

    [Fact]
    public void DefaultRotationThreshold_IsOneMegabyte()
    {
        // 「1MB でローテーションする」は両 README に書かれた契約。
        Assert.Equal(1024 * 1024, ActivityLog.DefaultRotationThresholdBytes);
    }
}
