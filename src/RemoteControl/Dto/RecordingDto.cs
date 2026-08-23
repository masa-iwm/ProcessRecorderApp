using System;
using System.Collections.Generic;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// 保存先にある録画ファイル 1 件。
/// </summary>
/// <param name="Path">
/// 配信 root からの相対パス（<b>区切りは <c>/</c></b>）。
/// そのまま <c>/api/recordings/</c> の後ろへ ── セグメントごとに符号化して ── 付ける。
/// </param>
/// <param name="Length">列挙した時点の長さ。</param>
/// <param name="LastWriteTimeUtc">更新時刻（UTC）。</param>
/// <param name="InProgress">
/// 書き込み中で <c>moov</c> が未確定のもの。<b>取得はできるが再生はできない</b>
/// （返るのは開いた時点の長さまで）。
/// </param>
public sealed record RecordingFileDto(string Path, long Length, DateTime LastWriteTimeUtc, bool InProgress);

/// <summary>
/// 保存先の一覧。
/// </summary>
/// <param name="Root">
/// 解決済みの配信 root（絶対パス）。<b>読み取りは認証を要さない</b>ので、
/// これが見える相手には録画の置き場所も見える。
/// </param>
/// <param name="Files">更新時刻の降順、同時刻は相対パスの序数昇順。</param>
public sealed record RecordingsDto(string Root, IReadOnlyList<RecordingFileDto> Files);
