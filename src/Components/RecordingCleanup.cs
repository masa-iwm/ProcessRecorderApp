using System;
using System.Collections.Generic;
using System.IO;

namespace ProcessRecorderApp.Components;

/// <summary>1回の掃除の結果。</summary>
/// <param name="DeletedFiles">削除できた mp4 の件数。</param>
/// <param name="FreedBytes">削除できた mp4 の合計サイズ。</param>
/// <param name="RemovedDirectories">削除の結果、空になったので消したサブフォルダーの件数。</param>
/// <param name="Failures">
/// 削除できなかったものの理由（1件1行）。<b>件数の上限を設けてある</b>
/// ── 保存先が丸ごとロックされている状況で、1行のログに数千件が並ぶのを避けるため。
/// </param>
public sealed record RecordingCleanupResult(
    int DeletedFiles, long FreedBytes, int RemovedDirectories, IReadOnlyList<string> Failures)
{
    public static readonly RecordingCleanupResult Empty = new(0, 0, 0, []);

    public bool DidSomething => DeletedFiles > 0 || RemovedDirectories > 0 || Failures.Count > 0;

    public string ToLogLine(string root)
        => $"dir='{root}' deleted={DeletedFiles} freedBytes={FreedBytes} removedDirs={RemovedDirectories}"
         + $" failures={Failures.Count}";
}

/// <summary>
/// 保存先フォルダーから、指定日数を過ぎた mp4 を削除する。
///
/// <para>
/// <b>ログは書かない。</b> 呼び出し側（<c>RecordingCleanupScheduler</c>）が
/// 結果を見て <c>cleanup.run</c> / <c>cleanup.error</c> を書く。
/// ここでログを書くと、L1 から呼んだだけで実ユーザーの activity.log が汚れる。
/// </para>
/// <para>
/// <b>mp4 に付く sidecar（<c>.mp4.json</c> / <c>.mp4.png</c>）も一緒に消す。</b>
/// 本体の無い sidecar は誰も読めず一覧にも出ないので、本体と同じ期限で孤児も消す。
/// 件数とサイズ（<see cref="RecordingCleanupResult.DeletedFiles"/> /
/// <see cref="RecordingCleanupResult.FreedBytes"/>）は mp4 のぶんだけを数える。
/// </para>
/// <para>
/// <b>空フォルダーの削除は「実際にファイルを消したフォルダーとその祖先」に限る。</b>
/// 既定の保存先は実行ファイルのあるディレクトリなので、無条件に空フォルダーを掃除すると
/// インストール先の空フォルダーまで巻き込む。root 自体は決して消さない。
/// </para>
/// </summary>
public static class RecordingCleanup
{
    /// <summary>対象の拡張子。</summary>
    public const string Extension = ".mp4";

    /// <summary>
    /// 録画に付く sidecar の、<see cref="Extension"/> の後ろに足す拡張子。
    /// 本体が消えたら一緒に消す ── 残しても誰も読めず、一覧にも出ない。
    /// </summary>
    private static readonly string[] SidecarExtensions =
        [RecordingSidecar.Extension, RecordingSidecar.ThumbnailExtension];

    /// <summary>ログ1行に載せる失敗理由の上限。</summary>
    public const int MaxReportedFailures = 5;

    /// <summary>
    /// 1回掃除する。<paramref name="retentionDays"/> が 0 以下なら何もしない。
    /// 判定は更新時刻（<see cref="FileInfo.LastWriteTime"/>）。
    /// </summary>
    public static RecordingCleanupResult Sweep(string root, int retentionDays, DateTime now)
    {
        if (retentionDays <= 0 || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return RecordingCleanupResult.Empty;

        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        DateTime threshold = now.AddDays(-retentionDays);

        int deleted = 0;
        long freed = 0;
        var failures = new List<string>();
        var touchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DeleteExpiredFiles(fullRoot, threshold, ref deleted, ref freed, failures, touchedDirectories);

        int removedDirectories = RemoveDirectoriesThatBecameEmpty(fullRoot, touchedDirectories, failures);

        return new RecordingCleanupResult(deleted, freed, removedDirectories, failures);
    }

    /// <summary>
    /// 期限切れの mp4 を削除しながら、サブフォルダーへ降りる。
    ///
    /// <para>
    /// <b>列挙は <c>"*"</c> で行い、拡張子は自分で比較する。</b>
    /// Win32 の <c>FindFirstFile</c> は「拡張子がちょうど3文字」の検索パターンを
    /// 8.3 名の互換仕様で緩く扱い、<c>"*.mp4"</c> が <c>.mp4v</c> にも一致する
    /// （.NET のドキュメントが今も警告している挙動で、
    /// <c>MatchType.Win32</c> を明示すると現在の .NET でも再現する）。
    /// <b>ただし .NET (Core) の既定は <c>MatchType.Simple</c> で、この緩さは無い</b>
    /// ── <c>GetFiles("*.mp4")</c> へ差し替える退行は
    /// <c>RecordingCleanupTests.OnlyMp4AndItsSidecarsAreTouched</c> では検出できない（緑のまま通る）。
    /// それでも自分で比較しているのは、削除する側のコードで
    /// 列挙 API の既定に依存したくないから ── ここは間違えると利用者のファイルを消す。
    /// </para>
    /// </summary>
    private static void DeleteExpiredFiles(
        string directory, DateTime threshold,
        ref int deleted, ref long freed, List<string> failures, HashSet<string> touchedDirectories)
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
            AddFailure(failures, $"{directory}: {ex.Message}");
            return;
        }

        // 走査した時点の mp4。同じ回で消す mp4 の sidecar は本体と一緒に消すので、
        // ここに残るのは「最初から本体が無かった」孤児だけになる。
        var recordings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (string.Equals(file.Extension, Extension, StringComparison.OrdinalIgnoreCase))
                recordings.Add(file.FullName);
        }

        foreach (var file in files)
        {
            if (!string.Equals(file.Extension, Extension, StringComparison.OrdinalIgnoreCase))
            {
                DeleteOrphanSidecar(file, recordings, threshold, failures, touchedDirectories);
                continue;
            }

            try
            {
                if (file.LastWriteTime >= threshold)
                    continue;

                long length = file.Length;
                file.Delete();
                deleted++;
                freed += length;
                touchedDirectories.Add(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 録画中のファイルはロックされている。1件の失敗で掃除全体を止めない。
                AddFailure(failures, $"{file.FullName}: {ex.Message}");
                continue;
            }

            // 本体が消えた後の sidecar は誰も読めない。件数とサイズには数えない
            // （DeletedFiles / FreedBytes は mp4 の数と大きさである）。
            DeleteSidecars(file.FullName, directory, failures, touchedDirectories);
        }

        foreach (var subdirectory in subdirectories)
        {
            // リパースポイント（ジャンクション・ディレクトリシンボリックリンク）には降りない ──
            // リンク先は root の外の実体でありうるので、辿ると「root の外には手を出さない」が
            // 破れて他所のファイルを消す。祖先を指す循環リンクによる際限のない再帰も同時に防ぐ。
            // スキップは記録する ── クラウド同期のプレースホルダーフォルダーも
            // リパースポイントであり、無記録だと「その下だけ掃除されない」が説明不能になる。
            if ((subdirectory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                AddFailure(failures, $"{subdirectory.FullName}: skipped (reparse point)");
                continue;
            }
            DeleteExpiredFiles(subdirectory.FullName, threshold, ref deleted, ref freed, failures, touchedDirectories);
        }
    }

    /// <summary>
    /// ファイルを消したフォルダーが空になっていれば消し、親も空になれば辿って消す。
    /// <paramref name="fullRoot"/> 自体と、その外側には手を出さない。
    /// </summary>
    private static int RemoveDirectoriesThatBecameEmpty(
        string fullRoot, HashSet<string> touchedDirectories, List<string> failures)
    {
        int removed = 0;

        foreach (string touched in touchedDirectories)
        {
            string? current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(touched));
            while (current is not null
                   && !string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase)
                   && IsUnder(fullRoot, current))
            {
                try
                {
                    if (!Directory.Exists(current))
                    {
                        // 別の touched から辿って既に消してある。親の判定は続ける。
                        current = ParentOf(current);
                        continue;
                    }
                    if (Directory.EnumerateFileSystemEntries(current).GetEnumerator().MoveNext())
                        break;   // 空ではない

                    Directory.Delete(current);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AddFailure(failures, $"{current}: {ex.Message}");
                    break;
                }

                current = ParentOf(current);
            }
        }

        return removed;
    }

    private static string? ParentOf(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(parent) ? null : Path.TrimEndingDirectorySeparator(parent);
    }

    /// <summary><paramref name="candidate"/> が <paramref name="root"/> の配下か。</summary>
    private static bool IsUnder(string root, string candidate)
        => candidate.Length > root.Length
           && candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
           && (candidate[root.Length] == Path.DirectorySeparatorChar
               || candidate[root.Length] == Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 消した mp4 に付いていた sidecar（<c>.mp4.json</c> / <c>.mp4.png</c>）を消す。
    /// 失敗は記録するだけ ── 本体は既に消えているので、掃除全体を止める理由が無い。
    /// </summary>
    private static void DeleteSidecars(
        string recordingPath, string directory, List<string> failures, HashSet<string> touchedDirectories)
    {
        foreach (string extension in SidecarExtensions)
        {
            string path = recordingPath + extension;
            try
            {
                if (!File.Exists(path))
                    continue;

                File.Delete(path);
                touchedDirectories.Add(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddFailure(failures, $"{path}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 本体の mp4 が無い sidecar を消す。<b>本体と同じ期限を掛ける</b>
    /// ── 本体が別の道具に消された直後の sidecar を、期限より前に消さないため。
    /// mp4 でも sidecar でもないファイルには触らない。
    /// </summary>
    private static void DeleteOrphanSidecar(
        FileInfo file, HashSet<string> recordings, DateTime threshold,
        List<string> failures, HashSet<string> touchedDirectories)
    {
        foreach (string extension in SidecarExtensions)
        {
            if (!file.Name.EndsWith(Extension + extension, StringComparison.OrdinalIgnoreCase))
                continue;

            string owner = file.FullName[..^extension.Length];
            if (recordings.Contains(owner))
                return;

            try
            {
                if (file.LastWriteTime >= threshold)
                    return;

                string? parent = file.DirectoryName;
                file.Delete();
                if (parent is not null)
                    touchedDirectories.Add(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddFailure(failures, $"{file.FullName}: {ex.Message}");
            }

            return;
        }
    }

    private static void AddFailure(List<string> failures, string message)
    {
        if (failures.Count < MaxReportedFailures)
            failures.Add(message);
    }
}
