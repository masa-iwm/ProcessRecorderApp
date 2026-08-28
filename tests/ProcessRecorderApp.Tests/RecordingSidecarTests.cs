using ProcessRecorderApp.Components;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 録画に付く sidecar（<see cref="RecordingSidecar"/>）の読み書き。
///
/// <para>
/// <b>読む側は必ず <see langword="null"/> を扱えること。</b> sidecar は best-effort で、
/// 以前に録った分・書き込みが失敗した分・別の道具が置いた分には無い／壊れている。
/// 例外を投げると一覧そのものが落ちる。
/// </para>
/// </summary>
public sealed class RecordingSidecarTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pra-sidecar-" + Guid.NewGuid().ToString("N")[..8]);

    public RecordingSidecarTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 後始末の失敗でテストを赤にしない */ }
    }

    private string PathFor(string name) => Path.Combine(_root, name);

    private static RecordingSidecar Sample() => new(
        RecordingSidecar.CurrentVersion,
        "cam1",
        new DateTimeOffset(2026, 8, 28, 10, 15, 0, TimeSpan.FromHours(9)),
        new DateTimeOffset(2026, 8, 28, 10, 16, 30, TimeSpan.FromHours(9)),
        90_000,
        "continuous",
        1920,
        1080,
        29.97);

    [Fact]
    public void ItRoundTrips()
    {
        string path = PathFor("a.mp4.json");
        var written = Sample();

        RecordingSidecar.Write(path, written);
        var read = RecordingSidecar.TryRead(path);

        Assert.NotNull(read);
        Assert.Equal(written.Recorder, read.Recorder);
        Assert.Equal(written.DurationMs, read.DurationMs);
        Assert.Equal(written.Trigger, read.Trigger);
        Assert.Equal(written.Width, read.Width);
        Assert.Equal(written.Height, read.Height);
        Assert.Equal(written.Fps, read.Fps);
        // オフセット付きで書いた時刻が、同じ瞬間として戻ること。
        Assert.Equal(written.StartTime.UtcDateTime, read.StartTime.UtcDateTime);
        Assert.Equal(written.EndTime!.Value.UtcDateTime, read.EndTime!.Value.UtcDateTime);
    }

    [Fact]
    public void TheJsonUsesCamelCaseNames()
    {
        // 名前は API の応答ではなくファイルの形なので、ここで固定する
        // （読む側と書く側が別アセンブリにある）。
        string path = PathFor("b.mp4.json");
        RecordingSidecar.Write(path, Sample());

        string json = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains("\"version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"recorder\"", json, StringComparison.Ordinal);
        Assert.Contains("\"startTime\"", json, StringComparison.Ordinal);
        Assert.Contains("\"durationMs\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNullFieldsSurvive()
    {
        string path = PathFor("c.mp4.json");
        var written = new RecordingSidecar(
            RecordingSidecar.CurrentVersion, "cam1",
            new DateTimeOffset(2026, 8, 28, 10, 15, 0, TimeSpan.Zero),
            null, null, null, null, null, null);

        RecordingSidecar.Write(path, written);
        var read = RecordingSidecar.TryRead(path);

        Assert.NotNull(read);
        Assert.Null(read.EndTime);
        Assert.Null(read.DurationMs);
        Assert.Null(read.Trigger);
        Assert.Null(read.Width);
        Assert.Null(read.Height);
        Assert.Null(read.Fps);
    }

    [Fact]
    public void AMissingFileIsNull() => Assert.Null(RecordingSidecar.TryRead(PathFor("missing.mp4.json")));

    [Fact]
    public void BrokenJsonIsNull()
    {
        string path = PathFor("broken.mp4.json");
        File.WriteAllText(path, "{ \"version\": 1, \"recorder\":");

        Assert.Null(RecordingSidecar.TryRead(path));
    }

    [Fact]
    public void AnEmptyFileIsNull()
    {
        string path = PathFor("empty.mp4.json");
        File.WriteAllBytes(path, []);

        Assert.Null(RecordingSidecar.TryRead(path));
    }

    [Fact]
    public void AnUnknownVersionIsNull()
    {
        // 版が違うものは「無い」と同じに畳む ── 意味の分からない項目で一覧を作らない。
        string path = PathFor("future.mp4.json");
        File.WriteAllText(path, "{\"version\":99,\"recorder\":\"cam1\",\"startTime\":\"2026-08-28T10:15:00+09:00\"}");

        Assert.Null(RecordingSidecar.TryRead(path));
    }

    [Fact]
    public void WritingReplacesTheFileWithoutLeavingTheTemporaryOne()
    {
        string path = PathFor("d.mp4.json");
        RecordingSidecar.Write(path, Sample());
        RecordingSidecar.Write(path, Sample() with { Recorder = "cam2" });

        Assert.Equal("cam2", RecordingSidecar.TryRead(path)!.Recorder);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ThePathsAreDerivedFromTheRecording()
    {
        Assert.Equal(@"C:\out\a.mp4.json", RecordingSidecar.PathFor(@"C:\out\a.mp4"));
        Assert.Equal(@"C:\out\a.mp4.png", RecordingSidecar.ThumbnailPathFor(@"C:\out\a.mp4"));
    }
}
