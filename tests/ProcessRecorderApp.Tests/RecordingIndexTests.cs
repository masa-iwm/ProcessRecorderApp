using ProcessRecorderApp.Components;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 保存先のメモリ索引（<see cref="RecordingIndex"/>）。
///
/// <para>
/// <b>sidecar が無いのが既定である。</b> 以前に録った分・別の道具が置いた分には
/// sidecar が無いので、ファイル名と <c>moov</c> からのフォールバックが
/// 一覧の中身をそのまま決める。ここが崩れると「日付で絞れない録画」が生まれる。
/// </para>
/// <para>
/// <b>MP4 はバイト列を手で組む。</b> 実ファイルを置くと「そのファイルが読めること」しか
/// 言えない（GStreamer を回さずに尺の読み取りを固定したい）。
/// </para>
/// </summary>
public sealed class RecordingIndexTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pra-index-" + Guid.NewGuid().ToString("N")[..8]);

    public RecordingIndexTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 後始末の失敗でテストを赤にしない */ }
    }

    // ---- MP4 の組み立て -------------------------------------------------

    private static byte[] Box(string type, params byte[][] parts)
    {
        var payload = new List<byte>();
        foreach (byte[] part in parts)
            payload.AddRange(part);

        byte[] box = new byte[8 + payload.Count];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] U32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Mvhd(uint durationMs)
        => Box("mvhd", [0, 0, 0, 0], new byte[8], U32(1000), U32(durationMs));

    /// <summary>完成した（fragmented ではない）MP4。<c>moov</c> は <c>faststart</c> と同じく先頭。</summary>
    private static byte[] PlainMp4(uint durationMs)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Box("ftyp", Encoding.ASCII.GetBytes("isom")));
        bytes.AddRange(Box("moov", Mvhd(durationMs)));
        bytes.AddRange(Box("mdat", new byte[64]));
        return [.. bytes];
    }

    /// <summary>fragmented MP4（<c>mvex</c> が在り、<c>mvhd</c> の <c>duration</c> は 0）。</summary>
    private static byte[] FragmentedMp4()
    {
        var bytes = new List<byte>();
        bytes.AddRange(Box("ftyp", Encoding.ASCII.GetBytes("isom")));
        bytes.AddRange(Box("moov", Mvhd(0), Box("mvex", Box("trex", new byte[8]))));
        bytes.AddRange(Box("moof", new byte[16]));
        return [.. bytes];
    }

    // ---- 置く -----------------------------------------------------------

    private string WriteRecording(string name, byte[]? content = null, DateTime? lastWriteUtc = null)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content ?? PlainMp4(2000));
        if (lastWriteUtc is DateTime stamp)
            File.SetLastWriteTimeUtc(path, stamp);
        return path;
    }

    private void WriteSidecar(string recordingPath, RecordingSidecar sidecar)
        => RecordingSidecar.Write(RecordingSidecar.PathFor(recordingPath), sidecar);

    private static RecordingEntry Single(RecordingIndex index)
        => Assert.Single(index.Snapshot());

    // ---- 合成 -----------------------------------------------------------

    [Fact]
    public void TheSidecarSuppliesTheMetadata()
    {
        string path = WriteRecording("20260101_000000_other.mp4");
        WriteSidecar(path, new RecordingSidecar(
            RecordingSidecar.CurrentVersion, "cam1",
            new DateTimeOffset(2026, 8, 28, 10, 15, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 28, 10, 16, 0, TimeSpan.Zero),
            60_000, "continuous", 1920, 1080, 30));

        using var index = new RecordingIndex(_root);
        var entry = Single(index);

        Assert.Equal("cam1", entry.Recorder);
        Assert.Equal(new DateTime(2026, 8, 28, 10, 15, 0, DateTimeKind.Utc), entry.StartTimeUtc);
        Assert.Equal(60_000, entry.DurationMs);
        Assert.Equal(1920, entry.Width);
        Assert.Equal(1080, entry.Height);
        Assert.False(entry.InProgress);
        Assert.False(entry.HasThumbnail);
    }

    [Fact]
    public void TheFilenameIsTheFallbackForTheStartTimeAndRecorder()
    {
        WriteRecording("20260828_101500_cam1.mp4");

        using var index = new RecordingIndex(_root);
        var entry = Single(index);

        // ファイル名の時刻はローカル時刻として書かれている。
        var expected = DateTime.SpecifyKind(
            new DateTime(2026, 8, 28, 10, 15, 0), DateTimeKind.Local).ToUniversalTime();

        Assert.Equal("cam1", entry.Recorder);
        Assert.Equal(expected, entry.StartTimeUtc);
    }

    [Fact]
    public void TheContinuousSegmentSuffixIsNotPartOfTheRecorderName()
    {
        WriteRecording("20260828_101500_cam1_c00003.mp4");

        using var index = new RecordingIndex(_root);

        Assert.Equal("cam1", Single(index).Recorder);
    }

    [Fact]
    public void AFilenameThatDoesNotMatchFallsBackToTheLastWriteTime()
    {
        var stamp = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        WriteRecording("whatever.mp4", PlainMp4(2000), stamp);

        using var index = new RecordingIndex(_root);
        var entry = Single(index);

        Assert.Equal("", entry.Recorder);
        // 尺が読めているので、更新時刻から尺を引いた時刻を開始とみなす。
        Assert.Equal(stamp.AddMilliseconds(-2000), entry.StartTimeUtc);
    }

    [Fact]
    public void AFileWithNoDurationFallsBackToTheLastWriteTimeItself()
    {
        var stamp = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        WriteRecording("whatever.mp4", Encoding.ASCII.GetBytes("not an mp4 at all"), stamp);

        using var index = new RecordingIndex(_root);
        var entry = Single(index);

        Assert.Null(entry.DurationMs);
        Assert.Equal(stamp, entry.StartTimeUtc);
    }

    [Fact]
    public void TheDurationComesFromTheMovieHeader()
    {
        WriteRecording("20260828_101500_cam1.mp4", PlainMp4(4500));

        using var index = new RecordingIndex(_root);
        var entry = Single(index);

        Assert.Equal(4500, entry.DurationMs);
        Assert.False(entry.Fragmented);
    }

    [Fact]
    public void AFragmentedRecordingHasNoDuration()
    {
        WriteRecording("20260828_101500_cam1.mp4", FragmentedMp4());

        using var index = new RecordingIndex(_root);
        var entry = Single(index);

        Assert.True(entry.Fragmented);
        Assert.Null(entry.DurationMs);
    }

    [Fact]
    public void AThumbnailNextToTheRecordingIsReported()
    {
        string path = WriteRecording("20260828_101500_cam1.mp4");
        File.WriteAllBytes(RecordingSidecar.ThumbnailPathFor(path), new byte[8]);

        using var index = new RecordingIndex(_root);

        Assert.True(Single(index).HasThumbnail);
    }

    [Fact]
    public void OnlyMp4FilesAreListed()
    {
        WriteRecording("20260828_101500_cam1.mp4");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "20260828_101500_cam1.mp4.json"), "{}");

        using var index = new RecordingIndex(_root);

        Assert.Single(index.Snapshot());
    }

    [Fact]
    public void SubfoldersAreIncludedWithForwardSlashes()
    {
        WriteRecording(Path.Combine("2026", "08", "20260828_101500_cam1.mp4"));

        using var index = new RecordingIndex(_root);

        Assert.Equal("2026/08/20260828_101500_cam1.mp4", Single(index).RelativePath);
    }

    [Fact]
    public void TheOrderIsNewestFirstThenPath()
    {
        WriteRecording("20260828_101500_cam1.mp4");
        WriteRecording("20260829_101500_cam1.mp4");
        WriteRecording("20260827_101500_cam1.mp4");

        using var index = new RecordingIndex(_root);

        Assert.Equal(
            new[] { "20260829_101500_cam1.mp4", "20260828_101500_cam1.mp4", "20260827_101500_cam1.mp4" },
            index.Snapshot().Select(static e => e.RelativePath).ToArray());
    }

    [Fact]
    public void AMissingRootIsEmpty()
    {
        using var index = new RecordingIndex(Path.Combine(_root, "does-not-exist"));

        Assert.Empty(index.Snapshot());
    }

    [Fact]
    public void AnEmptyRootIsEmpty()
    {
        using var index = new RecordingIndex("");

        Assert.Equal("", index.Root);
        Assert.Empty(index.Snapshot());
    }

    // ---- 差し替えと再構築 -----------------------------------------------

    [Fact]
    public void RebindingSwitchesTheListedFolder()
    {
        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        WriteRecording("20260828_101500_cam1.mp4");
        File.WriteAllBytes(Path.Combine(other, "20260829_101500_cam2.mp4"), PlainMp4(1000));

        using var index = new RecordingIndex(_root);
        // root 直下の 1 件と、サブフォルダーの 1 件。
        Assert.Equal(2, index.Snapshot().Count);

        index.Rebind(other);

        Assert.Equal(Path.TrimEndingDirectorySeparator(other), index.Root);
        Assert.Equal("20260829_101500_cam2.mp4", Single(index).RelativePath);
    }

    [Fact]
    public void RebindingToTheSameFolderDoesNotChangeAnything()
    {
        WriteRecording("20260828_101500_cam1.mp4");

        using var index = new RecordingIndex(_root);
        var before = index.Snapshot();

        index.Rebind(_root + Path.DirectorySeparatorChar);

        Assert.Same(before, index.Snapshot());
    }

    [Fact]
    public void RebuildingPicksUpNewFilesAndReportsThem()
    {
        using var index = new RecordingIndex(_root);
        var changes = new ConcurrentQueue<RecordingIndexChange>();
        index.Changed += changes.Enqueue;

        WriteRecording("20260828_101500_cam1.mp4");
        index.Rebuild();

        Assert.Single(index.Snapshot());
        var change = Assert.Single(changes);
        Assert.Equal(RecordingIndexChangeKind.Added, change.Kind);
        Assert.Equal("20260828_101500_cam1.mp4", change.RelativePath);
    }

    [Fact]
    public void ARemovedFileIsReported()
    {
        string path = WriteRecording("20260828_101500_cam1.mp4");

        using var index = new RecordingIndex(_root);
        var changes = new ConcurrentQueue<RecordingIndexChange>();
        index.Changed += changes.Enqueue;

        File.Delete(path);
        index.Rebuild();

        Assert.Empty(index.Snapshot());
        Assert.Equal(RecordingIndexChangeKind.Removed, Assert.Single(changes).Kind);
    }

    [Fact]
    public void AnAppearingSidecarIsReportedAsAnUpdate()
    {
        string path = WriteRecording("20260828_101500_cam1.mp4");

        using var index = new RecordingIndex(_root);
        var changes = new ConcurrentQueue<RecordingIndexChange>();
        index.Changed += changes.Enqueue;

        WriteSidecar(path, new RecordingSidecar(
            RecordingSidecar.CurrentVersion, "cam1",
            new DateTimeOffset(2026, 8, 28, 1, 15, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 28, 1, 16, 0, TimeSpan.Zero),
            60_000, null, 1280, 720, 30));
        index.Rebuild();

        Assert.Equal(RecordingIndexChangeKind.Updated, Assert.Single(changes).Kind);
        Assert.Equal(1280, Single(index).Width);
    }

    [Fact]
    public void NothingIsReportedWhenNothingChanged()
    {
        WriteRecording("20260828_101500_cam1.mp4");

        using var index = new RecordingIndex(_root);
        var changes = new ConcurrentQueue<RecordingIndexChange>();
        index.Changed += changes.Enqueue;

        index.Rebuild();

        Assert.Empty(changes);
    }

    [Fact]
    public void ACompletedRecordingIsReportedAsCompleted()
    {
        // 差分の規則そのものを固定する（録画中のファイルは L1 では作れない ──
        // filesink が握っている状態を再現できるのは E2E だけ）。
        var recording = new RecordingEntry(
            "a.mp4", 100, DateTime.UnixEpoch, true, false, DateTime.UnixEpoch, "cam1", null, null, null, false);
        var completed = recording with { InProgress = false, Length = 200 };

        var changes = RecordingIndex.Diff(new[] { recording }, new[] { completed });

        Assert.Equal(RecordingIndexChangeKind.Completed, Assert.Single(changes).Kind);
    }

    [Fact]
    public void AGrowingRecordingIsReportedAsAnUpdate()
    {
        var recording = new RecordingEntry(
            "a.mp4", 100, DateTime.UnixEpoch, true, false, DateTime.UnixEpoch, "cam1", null, null, null, false);

        var changes = RecordingIndex.Diff(new[] { recording }, new[] { recording with { Length = 200 } });

        Assert.Equal(RecordingIndexChangeKind.Updated, Assert.Single(changes).Kind);
    }

    // ---- watcher --------------------------------------------------------

    [Fact]
    public void TheWatcherPicksUpANewRecording()
    {
        using var index = new RecordingIndex(_root);
        using var seen = new ManualResetEventSlim();
        RecordingIndexChange? observed = null;

        index.Changed += change =>
        {
            observed = change;
            seen.Set();
        };

        WriteRecording("20260828_101500_cam1.mp4");

        // デバウンスは 500ms。実機の通知の遅れを見込んで余裕を取る。
        Assert.True(seen.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "the watcher did not report the new recording");
        Assert.Equal(RecordingIndexChangeKind.Added, observed!.Kind);
        Assert.Single(index.Snapshot());
    }

    [Fact]
    public void TheWatcherCoalescesABurst()
    {
        // **1 本のファイルを続けて書く。** 複数のファイルを足すと、デバウンスが無くても
        // Added の総数は同じになる（畳み込みを検査できない）── 同じ 1 本への変更なら、
        // 作り直しが 1 回なら差分も 1 件、作り直しのたびに差分が出るなら複数件になる。
        string path = WriteRecording("20260828_101500_cam1.mp4");

        using var index = new RecordingIndex(_root);
        var changes = new ConcurrentQueue<RecordingIndexChange>();
        index.Changed += changes.Enqueue;

        for (int i = 0; i < 10; i++)
        {
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.WriteByte((byte)i);
            Thread.Sleep(20);
        }

        var deadline = Stopwatch.StartNew();
        while (changes.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(20))
            Thread.Sleep(50);

        // 最後の通知が遅れて届く場合に備え、デバウンスの 2 倍だけ待ってから数える。
        Thread.Sleep(RecordingIndex.DebounceMilliseconds * 2);

        var single = Assert.Single(changes);
        Assert.Equal(RecordingIndexChangeKind.Updated, single.Kind);
        Assert.Equal("20260828_101500_cam1.mp4", single.RelativePath);
    }

    [Fact]
    public void TheWatcherListsAFileWhileItIsStillBeingWritten()
    {
        // **索引を先に作る。** 構築時の走査は同期なので、ファイルを先に置くと
        // 「作り直しが動いた」ではなく「最初の走査で拾えた」だけになる。
        using var index = new RecordingIndex(_root);
        Assert.Empty(index.Snapshot());

        const string name = "20260828_101500_cam1.mp4";

        // 録画中の filesink と同じ握り方（FileShare.Read で開きっぱなし＝ InProgress）。
        using var stream = new FileStream(
            Path.Combine(_root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(PlainMp4(2000));
        stream.Flush(flushToDisk: true);

        // **書き終わるのを待たずに一覧へ出ること。** filesink は buffer-mode=unbuffered で
        // 毎バッファ書くので、通知が止むまで待つデバウンスだと録画が終わるまで一覧に出ない。
        var elapsed = Stopwatch.StartNew();
        TimeSpan? listedAt = null;

        while (elapsed.Elapsed < TimeSpan.FromSeconds(3))
        {
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
            Thread.Sleep(50);

            if (listedAt is null && index.Snapshot().Any(e => e.RelativePath == name))
                listedAt = elapsed.Elapsed;
        }

        Assert.True(listedAt is not null,
            "書き込みが続いている 3 秒の間、一覧に一度も出なかった（作り直しが飢えている）。");
        Assert.True(listedAt <= TimeSpan.FromSeconds(1.5),
            $"一覧に出るまで {listedAt!.Value.TotalMilliseconds:F0}ms かかった"
            + $"（デバウンスは {RecordingIndex.DebounceMilliseconds}ms）。");
    }

    [Fact]
    public void TheWatcherPicksUpANewSidecar()
    {
        string path = WriteRecording("20260828_101500_cam1.mp4");

        using var index = new RecordingIndex(_root);
        using var seen = new ManualResetEventSlim();
        RecordingIndexChange? observed = null;

        index.Changed += change =>
        {
            observed = change;
            seen.Set();
        };

        // 録画本体から読める値と違う値を入れる（同じだと差分にならない）。
        WriteSidecar(path, new RecordingSidecar(
            RecordingSidecar.CurrentVersion, "other",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero),
            60_000, "continuous", 1280, 720, 30));

        Assert.True(seen.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken),
            "the watcher did not report the new sidecar");
        Assert.Equal(RecordingIndexChangeKind.Updated, observed!.Kind);
        Assert.Equal("other", Single(index).Recorder);
    }

    [Fact]
    public void TheWatcherIsArmedOnceTheRootAppears()
    {
        // 保存先は初回の録画のときに作られる。設定した直後は root がまだ無い。
        string root = Path.Combine(_root, "later");

        using var index = new RecordingIndex(root);
        Assert.Empty(index.Snapshot());

        Directory.CreateDirectory(root);
        index.Rebind(root);

        using var seen = new ManualResetEventSlim();
        RecordingIndexChange? observed = null;

        index.Changed += change =>
        {
            observed = change;
            seen.Set();
        };

        File.WriteAllBytes(Path.Combine(root, "20260828_101500_cam1.mp4"), PlainMp4(2000));

        Assert.True(seen.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken),
            "the watcher was not armed after the root appeared");
        Assert.Equal(RecordingIndexChangeKind.Added, observed!.Kind);
        Assert.Single(index.Snapshot());
    }

    [Fact]
    public void DisposingStopsTheWatcher()
    {
        // ハンドラの中で Assert すると、作り直し側の catch-all に飲まれて緑のまま通る。
        // 呼ばれた事実だけを持ち帰って、外側で検査する。
        var index = new RecordingIndex(_root);
        int fired = 0;
        index.Changed += _ => Interlocked.Increment(ref fired);
        index.Dispose();

        WriteRecording("20260828_101500_cam1.mp4");
        Thread.Sleep(TimeSpan.FromSeconds(2));

        Assert.Equal(0, Volatile.Read(ref fired));
        Assert.Empty(index.Snapshot());
    }
}
