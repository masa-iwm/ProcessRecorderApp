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
/// 書き込み中のもの。<b>取得はできる</b>（返るのは開いた時点の長さまで）が、
/// <paramref name="Fragmented"/> でなければ <c>moov</c> が未確定なので再生はできない。
/// </param>
/// <param name="Fragmented">
/// fragmented MP4（<c>moov</c> の子に <c>mvex</c>）。<b>録画中でも先頭から順に取れば再生できる</b>
/// ── ただし <c>&lt;video src&gt;</c> 直結ではなく MSE で食わせる必要がある
/// （<c>moov</c> は書き直されないので尺が 0 のまま）。
/// </param>
public sealed record RecordingFileDto(
    string Path, long Length, DateTime LastWriteTimeUtc, bool InProgress, bool Fragmented);

/// <summary>
/// 保存先の一覧。
/// </summary>
/// <param name="Root">
/// 解決済みの配信 root（絶対パス）。<b>読み取りは認証を要さない</b>ので、
/// これが見える相手には録画の置き場所も見える。
/// </param>
/// <param name="Files">更新時刻の降順、同時刻は相対パスの序数昇順。</param>
public sealed record RecordingsDto(string Root, IReadOnlyList<RecordingFileDto> Files);
