using ProcessRecorderApp.Components;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 古い録画の自動削除（<see cref="RecordingCleanup.Sweep"/>）。
///
/// <para>
/// <b>削除する側のコードなので、境界と範囲をここで固定する。</b> 既定の保存先は
/// 実行ファイルのあるディレクトリなので、範囲を1段でも広く取ると
/// インストール先のファイルを消しかねない。
/// </para>
/// <para>
/// 時刻は引数で受け取る形にしてある ── そうしないと「数日待つ」以外に
/// 境界を検証する方法が無くなる。
/// </para>
/// </summary>
public sealed class RecordingCleanupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Local);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pra-cleanup-" + Guid.NewGuid().ToString("N")[..8]);

    public RecordingCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 後始末の失敗でテストを赤にしない */ }
    }

    /// <summary>指定した「日数前」の更新時刻でファイルを作る。</summary>
    private string CreateFile(double ageDays, params string[] relativeParts)
    {
        string path = Path.Combine([_root, .. relativeParts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
        File.SetLastWriteTime(path, Now.AddDays(-ageDays));
        return path;
    }

    private RecordingCleanupResult Sweep(int retentionDays) =>
        RecordingCleanup.Sweep(_root, retentionDays, Now);

    [Fact]
    public void ZeroDaysDeletesNothing()
    {
        string old = CreateFile(ageDays: 400, "old.mp4");

        var result = Sweep(0);

        Assert.Equal(RecordingCleanupResult.Empty, result);
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void OnlyFilesOlderThanTheRetentionAreDeleted()
    {
        // 境界: しきい値ちょうどのものは残す（`<` であって `<=` ではない）。
        string justInside = CreateFile(ageDays: 7, "exactly-at-the-threshold.mp4");
        string older = CreateFile(ageDays: 7.001, "older.mp4");
        string newer = CreateFile(ageDays: 6.999, "newer.mp4");

        var result = Sweep(7);

        Assert.True(File.Exists(justInside));
        Assert.True(File.Exists(newer));
        Assert.False(File.Exists(older));
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(16, result.FreedBytes);
    }

    [Fact]
    public void SubfoldersAreSearched()
    {
        string nested = CreateFile(ageDays: 30, "2026", "07", "nested.mp4");

        var result = Sweep(1);

        Assert.False(File.Exists(nested));
        Assert.Equal(1, result.DeletedFiles);
    }

    [Fact]
    public void OnlyMp4IsTouched()
    {
        // **この1件は退行検出器ではない**。`GetFiles("*.mp4")` へ
        // 差し替える注入は検出できなかった ── Win32 の 8.3 互換で "*.mp4" が
        // .mp4v にも一致するのは MatchType.Win32 の話で、.NET (Core) の既定
        // （MatchType.Simple）にはその緩さが無い。
        // 「mp4 以外に触らない」という不変条件の表明として置いてある。
        string mp4 = CreateFile(ageDays: 30, "recording.mp4");
        string mp4v = CreateFile(ageDays: 30, "recording.mp4v");
        string log = CreateFile(ageDays: 30, "notes.txt");
        string noExtension = CreateFile(ageDays: 30, "README");

        var result = Sweep(1);

        Assert.False(File.Exists(mp4));
        Assert.True(File.Exists(mp4v));
        Assert.True(File.Exists(log));
        Assert.True(File.Exists(noExtension));
        Assert.Equal(1, result.DeletedFiles);
    }

    [Fact]
    public void TheExtensionMatchIsCaseInsensitive()
    {
        string upper = CreateFile(ageDays: 30, "RECORDING.MP4");

        Assert.Equal(1, Sweep(1).DeletedFiles);
        Assert.False(File.Exists(upper));
    }

    [Fact]
    public void ASubfolderThatBecameEmptyIsRemoved()
    {
        CreateFile(ageDays: 30, "2026", "07", "a.mp4");

        var result = Sweep(1);

        Assert.False(Directory.Exists(Path.Combine(_root, "2026", "07")));
        // 親も空になったので併せて消える
        Assert.False(Directory.Exists(Path.Combine(_root, "2026")));
        Assert.Equal(2, result.RemovedDirectories);
    }

    [Fact]
    public void ASubfolderThatStillHasSomethingIsKept()
    {
        CreateFile(ageDays: 30, "2026", "old.mp4");
        CreateFile(ageDays: 0, "2026", "new.mp4");

        var result = Sweep(1);

        Assert.True(Directory.Exists(Path.Combine(_root, "2026")));
        Assert.Equal(0, result.RemovedDirectories);
    }

    [Fact]
    public void AnAlreadyEmptySubfolderIsLeftAlone()
    {
        // **範囲を広げないための1件。** 「空フォルダーを掃除する」実装に変えると落ちる。
        // 既定の保存先は実行ファイルのあるディレクトリなので、
        // ここを緩めるとインストール先の空フォルダーまで消すことになる。
        string untouched = Path.Combine(_root, "empty-but-not-ours");
        Directory.CreateDirectory(untouched);
        CreateFile(ageDays: 30, "old.mp4");

        var result = Sweep(1);

        Assert.True(Directory.Exists(untouched));
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(0, result.RemovedDirectories);
    }

    [Fact]
    public void TheRootItselfIsNeverRemoved()
    {
        CreateFile(ageDays: 30, "old.mp4");

        Assert.Equal(1, Sweep(1).DeletedFiles);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void AJunctionInsideTheRootIsNotFollowed()
    {
        // リンク先は root の外の実体でありうる ── 辿ると「root の外には手を出さない」が
        // 破れて他所のファイルを消す。ジャンクション（mklink /J）は管理者権限なしで作れる。
        string outside = Path.Combine(Path.GetTempPath(), "pra-cleanup-outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        string link = Path.Combine(_root, "link");
        try
        {
            string victim = Path.Combine(outside, "victim.mp4");
            File.WriteAllText(victim, "x");
            File.SetLastWriteTime(victim, Now.AddDays(-400));

            var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);

            var result = Sweep(1);

            Assert.True(File.Exists(victim), "ジャンクションの先（root の外）のファイルが削除された");
            Assert.Equal(0, result.DeletedFiles);
            // スキップの事実は記録される（無記録だと「その下だけ掃除されない」が説明不能になる）
            Assert.Contains(result.Failures, f => f.Contains("reparse point", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(link); } catch { /* 後始末の失敗でテストを赤にしない */ }
            try { Directory.Delete(outside, recursive: true); } catch { /* 同上 */ }
        }
    }

    [Fact]
    public void ALockedFileIsReportedButDoesNotStopTheSweep()
    {
        string locked = CreateFile(ageDays: 30, "locked.mp4");
        string other = CreateFile(ageDays: 30, "other.mp4");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = Sweep(1);

            // 録画中のファイルはロックされている。1件の失敗で全体を止めない。
            Assert.False(File.Exists(other));
            Assert.True(File.Exists(locked));
            Assert.Equal(1, result.DeletedFiles);
            Assert.Contains(result.Failures, f => f.Contains("locked.mp4", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void FailuresAreCapped()
    {
        var streams = Enumerable.Range(0, RecordingCleanup.MaxReportedFailures + 3)
            .Select(i => CreateFile(ageDays: 30, $"locked{i}.mp4"))
            .Select(p => new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None))
            .ToArray();
        try
        {
            var result = Sweep(1);
            Assert.Equal(RecordingCleanup.MaxReportedFailures, result.Failures.Count);
        }
        finally
        {
            foreach (var s in streams)
                s.Dispose();
        }
    }

    [Fact]
    public void AMissingRootIsNotAnError()
    {
        string missing = Path.Combine(_root, "does-not-exist");

        // 保存先がまだ作られていない（＝1本も録っていない）だけなので、失敗にしない。
        Assert.Equal(RecordingCleanupResult.Empty, RecordingCleanup.Sweep(missing, 1, Now));
    }
}
