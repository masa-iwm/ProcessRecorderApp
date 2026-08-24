using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace ProcessRecorderApp.Components;

/// <summary>保存先にある録画ファイル 1 件。</summary>
/// <param name="RelativePath">配信 root からの相対パス（区切りは <c>/</c>）。</param>
/// <param name="Length">列挙した時点の長さ。</param>
/// <param name="LastWriteTimeUtc">更新時刻（UTC）。</param>
/// <param name="InProgress">
/// <c>filesink</c> が書き込み中で共有読み取りができないもの。
/// </param>
/// <param name="Fragmented">
/// fragmented MP4（<c>moov</c> の子に <c>mvex</c> が在る）。
/// <b>録画中でも先頭から読めば再生できる</b>形で、<c>moov</c> は最初に書かれたきり
/// 書き直されない。fragmented でないものは <c>moov</c> が確定するまで再生できない。
/// </param>
public sealed record RecordingFileInfo(
    string RelativePath, long Length, DateTime LastWriteTimeUtc, bool InProgress, bool Fragmented);

/// <summary>
/// 保存先の録画ファイルを<b>安全に</b>列挙・開封する。
///
/// <para>
/// <b>列挙規則は <see cref="RecordingCleanup"/> と同じ</b>
/// ── <c>GetFiles()</c>（引数なし）＋自前の拡張子比較、リパースポイントには降りない。
/// 掃除する側と見せる側で範囲がずれると、「一覧に出るのに消えない」
/// 「消えたのに一覧に残る」が起きる。
/// </para>
/// <para>
/// <b>ファイル自身がリンクなら出さない</b>（配信は <see cref="RecordingCleanup"/> より狭い）。
/// 掃除はリンクを消せば済むが、配信はリンク先の実体を読むことになり、
/// それは root の外でありうる。
/// </para>
/// <para>
/// <b>root 自身のリパースポイントは見ない。</b> 保存先はユーザーが指定するもので、
/// ジャンクションであることに正当性がある。降りる先だけを見るのが
/// <see cref="RecordingCleanup"/> と同じ範囲になる。
/// </para>
/// </summary>
public static class RecordingFiles
{
    /// <summary>
    /// <paramref name="root"/> 配下の録画ファイルを列挙する。
    /// 並びは更新時刻の降順、同時刻は相対パスの序数昇順。
    /// root が無ければ空。読めなかったフォルダー・ファイルは黙って飛ばす
    /// （1 件の権限不足で一覧全体を失わせない）。
    /// </summary>
    public static IReadOnlyList<RecordingFileInfo> Enumerate(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return [];

        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        var found = new List<RecordingFileInfo>();
        Collect(fullRoot, fullRoot, found);

        found.Sort(static (a, b) =>
        {
            int byTime = b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.RelativePath, b.RelativePath);
        });

        return found;
    }

    private static void Collect(string directory, string fullRoot, List<RecordingFileInfo> found)
    {
        FileInfo[] files;
        DirectoryInfo[] subdirectories;
        try
        {
            var info = new DirectoryInfo(directory);
            files = info.GetFiles();
            subdirectories = info.GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            if (!string.Equals(file.Extension, RecordingCleanup.Extension, StringComparison.OrdinalIgnoreCase))
                continue;

            // ファイル自身のリンク。実体は root の外でありうるので、開かずに落とす
            // （開いてしまえばリンク先を配ることになる）。
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            if (!TryProbeInProgress(file.FullName, out bool inProgress))
                continue;

            found.Add(new RecordingFileInfo(
                Path.GetRelativePath(fullRoot, file.FullName).Replace('\\', '/'),
                file.Length,
                file.LastWriteTimeUtc,
                inProgress,
                ProbeFragmented(file.FullName)));
        }

        foreach (var subdirectory in subdirectories)
        {
            // リンク先は root の外の実体でありうる。辿ると配信範囲が root の外へ広がり、
            // 祖先を指す循環リンクでは列挙が終わらない。
            if ((subdirectory.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            Collect(subdirectory.FullName, fullRoot, found);
        }
    }

    /// <summary>
    /// fragmented かどうかを見るために読む先頭の量。<c>moov</c>（init セグメント）は
    /// ファイルの先頭に在り、fragmented では書き直されないので、先頭だけで判定できる。
    /// </summary>
    public const int HeaderProbeBytes = 64 * 1024;

    /// <summary>
    /// 録画中か（<c>filesink</c> が書き込みで握っているか）。
    /// <b>一覧の <c>InProgress</c> と同じ判定</b>で、消えていた・権限が無いものは false。
    /// </summary>
    public static bool IsInProgress(string path)
        => TryProbeInProgress(path, out bool inProgress) && inProgress;

    /// <summary>
    /// 開いてあるストリームの先頭を最大 <see cref="HeaderProbeBytes"/> だけ読む。
    /// <b>位置は呼び出し前へ戻す</b>（配信のために開いたストリームをそのまま渡せる）。
    /// 読めなければ空を返す ── 判定側はどちらも「fragmented ではない」に畳む。
    /// </summary>
    public static byte[] ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long origin = stream.Position;
        try
        {
            stream.Position = 0;

            byte[] buffer = new byte[HeaderProbeBytes];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                    break;
                read += n;
            }

            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        finally
        {
            stream.Position = origin;
        }
    }

    /// <summary>
    /// 一覧のための fragmented 判定。<b>録画中のファイルも読む</b>ので
    /// <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> で開き直す
    /// （<see cref="TryProbeInProgress"/> の共有指定では書き込み中のファイルを読めない）。
    /// </summary>
    private static bool ProbeFragmented(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Fmp4Probe.IsFragmented(ReadHeader(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 録画中かどうかを、共有読み取りで開けるかどうかで判定する。
    /// 開けない（<see cref="IOException"/>）＝ <c>filesink</c> が書き込みで握っている。
    /// 消えていた・権限が無いものは<b>一覧から落とす</b>（false を返す）。
    /// </summary>
    private static bool TryProbeInProgress(string path, out bool inProgress)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            inProgress = false;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // IOException より先に受けること（どちらも IOException の派生）。
            // 列挙してから開くまでの間に消えたものは「録画中」ではない。
            inProgress = false;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            inProgress = false;
            return false;
        }
        catch (IOException)
        {
            inProgress = true;
            return true;
        }
    }

    /// <summary>
    /// 配信のためにファイルを開く。
    ///
    /// <para>
    /// 共有は <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>
    /// ── 録画中のファイルも、自動削除と競合しても読めるようにする
    /// （録画中は取得した時点の長さまでしか意味のある内容が無い）。
    /// </para>
    /// </summary>
    /// <param name="reason">
    /// 失敗理由（ログ用・英語）。<c>path rejected</c>（root の外・拡張子違い）／
    /// <c>reparse point</c>（root と対象の間、または対象自身がリンク ── <see cref="Enumerate"/>
    /// で見えないものは取得もできない）／<c>not found</c>（無い）／
    /// <c>unavailable</c>（在るのに開けない ── 排他で握られている・権限が無い・
    /// 同名のディレクトリ）のいずれか。
    /// </param>
    public static bool TryOpen(
        string root, string relativePath,
        [NotNullWhen(true)] out FileStream? stream,
        [NotNullWhen(false)] out string? reason)
    {
        stream = null;

        if (!RemoteApiRules.TryResolveUnderRoot(root, relativePath, out string? fullPath))
        {
            reason = "path rejected";
            return false;
        }

        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        // root と対象の親の間にリパースポイントがあれば、そのファイルは
        // Enumerate では見えていない ── 見えないものを取得できてはいけない。
        for (string? directory = Path.GetDirectoryName(fullPath);
             directory is not null
                && !string.Equals(Path.TrimEndingDirectorySeparator(directory), fullRoot, StringComparison.OrdinalIgnoreCase);
             directory = Path.GetDirectoryName(directory))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(directory);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                reason = "not found";
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                reason = "reparse point";
                return false;
            }
        }

        // 対象自身のリンク。実体は root の外でありうるので、開かずに断る
        // （Enumerate も同じ理由でリンクを一覧に出さない）。
        FileAttributes target;
        try
        {
            target = File.GetAttributes(fullPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            reason = "not found";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "unavailable";
            return false;
        }

        if ((target & FileAttributes.ReparsePoint) != 0)
        {
            reason = "reparse point";
            return false;
        }

        try
        {
            stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // IOException より先に受けること（どちらも IOException の派生）。
            reason = "not found";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 在るのに開けない ── 排他で握られている・権限が無い・同名のディレクトリ。
            // 「無い」と答えると、消えたのか読めないのかが呼び出し側で区別できない。
            reason = "unavailable";
            return false;
        }

        reason = null;
        return true;
    }
}
