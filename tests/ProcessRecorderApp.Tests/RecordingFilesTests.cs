using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 保存先の録画ファイルの列挙と開封（<see cref="RecordingFiles"/>）。
///
/// <para>
/// <b>列挙の範囲は <c>RecordingCleanup</c> と同じでなければならない。</b>
/// 掃除する側と見せる側でずれると、「一覧に出るのに消えない」
/// 「消えたのに一覧に残る」が起きる。だから同じ拡張子比較・
/// 同じリパースポイントの扱いをここでも固定する。
/// </para>
/// <para>
/// <b>録画中の判定は実際に共有読み取りで開いて確かめる。</b>
/// <c>filesink</c> が握っているファイルは <c>moov</c> が未確定で再生できないので、
/// 一覧に「再生できない」と出す必要がある。
/// </para>
/// </summary>
public sealed class RecordingFilesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pra-files-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>この fixture が作ったリパースポイント。後始末で<b>先に個別に</b>外す。</summary>
    private readonly List<string> _links = [];

    public RecordingFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // ジャンクションを含んだまま recursive で消すと、リンクを外した直後の
        // 親の削除が失敗し、一時ディレクトリが %TEMP% に残る。
        // リンクを先に（辿らずに）外してから、残りを短い再試行付きで消す。
        foreach (string link in _links)
        {
            try { Directory.Delete(link); } catch { /* 後始末の失敗でテストを赤にしない */ }
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception) when (attempt < 3)
            {
                Thread.Sleep(100);
            }
            catch (Exception)
            {
                return;   // 後始末の失敗でテストを赤にしない
            }
        }
    }

    private string CreateFile(string relativePath, int length = 16, DateTime? lastWriteUtc = null)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        if (lastWriteUtc is not null)
            File.SetLastWriteTimeUtc(path, lastWriteUtc.Value);
        return path;
    }

    [Fact]
    public void OnlyMp4FilesAreListed()
    {
        CreateFile("a.mp4");
        CreateFile("B.MP4");
        CreateFile("c.mp4v");
        CreateFile("d.txt");
        CreateFile("e");

        string[] listed = [.. RecordingFiles.Enumerate(_root).Select(f => f.RelativePath).Order(StringComparer.Ordinal)];

        Assert.Equal(["B.MP4", "a.mp4"], listed);
    }

    [Fact]
    public void SubfoldersAreListedWithForwardSlashes()
    {
        CreateFile("2026/07/nested.mp4");

        var listed = RecordingFiles.Enumerate(_root);

        // URL のパスとして使うので、区切りは '/' に正規化する。
        Assert.Equal("2026/07/nested.mp4", Assert.Single(listed).RelativePath);
    }

    [Fact]
    public void AMissingRootListsNothing()
    {
        Assert.Empty(RecordingFiles.Enumerate(Path.Combine(_root, "does-not-exist")));
        Assert.Empty(RecordingFiles.Enumerate(""));
    }

    [Fact]
    public void LengthAndTimeComeFromTheFile()
    {
        var written = new DateTime(2026, 7, 28, 3, 0, 0, DateTimeKind.Utc);
        CreateFile("a.mp4", length: 1234, lastWriteUtc: written);

        var info = Assert.Single(RecordingFiles.Enumerate(_root));

        Assert.Equal(1234, info.Length);
        Assert.Equal(written, info.LastWriteTimeUtc);
        Assert.False(info.InProgress);
    }

    [Fact]
    public void TheNewestComesFirst()
    {
        CreateFile("old.mp4", lastWriteUtc: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        CreateFile("new.mp4", lastWriteUtc: new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc));
        CreateFile("middle.mp4", lastWriteUtc: new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc));

        string[] listed = [.. RecordingFiles.Enumerate(_root).Select(f => f.RelativePath)];

        Assert.Equal(["new.mp4", "middle.mp4", "old.mp4"], listed);
    }

    [Fact]
    public void SameTimestampsAreOrderedByPath()
    {
        var written = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        CreateFile("b.mp4", lastWriteUtc: written);
        CreateFile("a.mp4", lastWriteUtc: written);
        CreateFile("sub/a.mp4", lastWriteUtc: written);

        string[] listed = [.. RecordingFiles.Enumerate(_root).Select(f => f.RelativePath)];

        Assert.Equal(["a.mp4", "b.mp4", "sub/a.mp4"], listed);
    }

    [Fact]
    public void AFileHeldWithoutSharingIsInProgress()
    {
        string path = CreateFile("recording.mp4");
        CreateFile("finished.mp4");

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var listed = RecordingFiles.Enumerate(_root).ToDictionary(f => f.RelativePath, f => f.InProgress);

            Assert.True(listed["recording.mp4"]);
            Assert.False(listed["finished.mp4"]);
        }

        Assert.False(Assert.Single(RecordingFiles.Enumerate(_root), f => f.RelativePath == "recording.mp4").InProgress);
    }

    [Fact]
    public void AFileStillBeingWrittenCanBeOpened()
    {
        // filesink は書き込みで開いたまま読み取り共有を許す。
        // 取得側の共有指定が ReadWrite|Delete でないと、ここが開けない。
        string path = CreateFile("recording.mp4", length: 64);

        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        Assert.True(RecordingFiles.TryOpen(_root, "recording.mp4", out var stream, out string? reason));
        Assert.Null(reason);
        using (stream)
            Assert.Equal(64, stream!.Length);
    }

    [Fact]
    public void AFileUnderTheRootOpens()
    {
        CreateFile("2026/07/a.mp4", length: 7);

        Assert.True(RecordingFiles.TryOpen(_root, "2026/07/a.mp4", out var stream, out string? reason));
        Assert.Null(reason);
        using (stream)
            Assert.Equal(7, stream!.Length);
    }

    [Fact]
    public void APathThatEscapesTheRootIsRejected()
    {
        CreateFile("a.mp4");

        Assert.False(RecordingFiles.TryOpen(_root, @"..\a.mp4", out var stream, out string? reason));
        Assert.Null(stream);
        Assert.Equal("path rejected", reason);
    }

    [Fact]
    public void AMissingFileIsNotFound()
    {
        Assert.False(RecordingFiles.TryOpen(_root, "nope.mp4", out var stream, out string? reason));
        Assert.Null(stream);
        Assert.Equal("not found", reason);

        Assert.False(RecordingFiles.TryOpen(_root, "no-such-folder/nope.mp4", out _, out reason));
        Assert.Equal("not found", reason);
    }

    /// <summary>
    /// リパースポイントを 1 つ作る。作れなければ false。
    ///
    /// <para>
    /// <b>シンボリックリンクが駄目でもジャンクションを試す。</b> ディレクトリの
    /// シンボリックリンクは管理者か開発者モードが要るが、ジャンクションは要らず、
    /// <c>FileAttributes.ReparsePoint</c> は同じように立つ
    /// ── ここで諦めると、CI と開発機の両方でこの検査が<b>常にスキップ</b>になる。
    /// </para>
    /// </summary>
    private bool TryCreateReparsePoint(string link, string target, out string reason)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            _links.Add(link);
            reason = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            reason = "symlink: " + ex.GetType().Name;
        }

        string output = "";
        string error = "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is not null)
            {
                // 読み切ってから待つ ── 出力を読まないまま待つと、パイプが埋まった時点で
                // 相手が止まり、失敗理由も取り出せない。
                output = process.StandardOutput.ReadToEnd();
                error = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(30_000))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or Win32Exception or InvalidOperationException)
        {
            reason += " / junction: " + ex.GetType().Name;
            return false;
        }

        if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
        {
            _links.Add(link);
            reason = "";
            return true;
        }

        reason += " / junction: not created (" + (output + " " + error).Trim() + ")";
        return false;
    }

    [Fact]
    public void ReparsePointsAreNotFollowed()
    {
        string target = Path.Combine(_root, "real");
        Directory.CreateDirectory(target);
        CreateFile("real/inside.mp4");

        string link = Path.Combine(_root, "link");
        if (!TryCreateReparsePoint(link, target, out string skipReason))
        {
            Assert.Skip("no reparse point could be created here (" + skipReason + ")");
            return;
        }

        string[] listed = [.. RecordingFiles.Enumerate(_root).Select(f => f.RelativePath)];

        // リンク先は root の外の実体でありうる。降りると配信範囲が広がる。
        Assert.Equal(["real/inside.mp4"], listed);

        Assert.False(RecordingFiles.TryOpen(_root, "link/inside.mp4", out var stream, out string? reason));
        Assert.Null(stream);
        Assert.Equal("reparse point", reason);
    }

    [Fact]
    public void ARootThatIsItselfAReparsePointStillServesItsFiles()
    {
        // 保存先はユーザーが指定するもので、それ自体がジャンクションであることには
        // 正当性がある。見るのは root より下だけで、root 自身は検査しない。
        string target = Path.Combine(_root, "real");
        Directory.CreateDirectory(target);
        CreateFile("real/inside.mp4", length: 9);

        string linkedRoot = Path.Combine(_root, "linked-root");
        if (!TryCreateReparsePoint(linkedRoot, target, out string skipReason))
        {
            Assert.Skip("no reparse point could be created here (" + skipReason + ")");
            return;
        }

        Assert.True(RecordingFiles.TryOpen(linkedRoot, "inside.mp4", out var stream, out string? reason));
        Assert.Null(reason);
        using (stream)
            Assert.Equal(9, stream!.Length);

        Assert.Equal("inside.mp4", Assert.Single(RecordingFiles.Enumerate(linkedRoot)).RelativePath);
    }
}
