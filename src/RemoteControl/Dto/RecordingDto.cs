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

/// <summary>
/// fragmented 録画のフラグメント 1 件（<c>moof</c> ＋ 直後の <c>mdat</c>）。
/// </summary>
/// <param name="Offset">ファイル先頭からの <c>moof</c> の位置（<c>Range</c> の起点にそのまま使える）。</param>
/// <param name="Size"><c>moof</c> と <c>mdat</c> を合わせた大きさ。</param>
/// <param name="Time">
/// 先頭サンプルの復号時刻。単位は <see cref="RecordingFragmentsDto.Timescale"/>。
/// </param>
/// <param name="Duration">このフラグメントの尺（同じ単位）。</param>
/// <param name="Sync">
/// 先頭サンプルが同期サンプルか。<b>ここが真のフラグメントにしかシークできない</b>
/// ── フラグメントは 1 秒・GOP は 2 秒なので、真でないものが必ず在る。
/// </param>
public sealed record RecordingFragmentDto(long Offset, int Size, ulong Time, uint Duration, bool Sync);

/// <summary>
/// fragmented 録画の索引。<b>ブラウザが任意の位置へシークするための唯一の材料である</b>
/// ── ファイルは <c>mvhd</c> の尺が 0 で <c>sidx</c> も持たないので、
/// 「その秒はどのバイトに在るか」を他に答えるものが無い。
/// </summary>
/// <param name="Timescale"><c>mdhd</c> の timescale。<paramref name="Fragments"/> の時間はこの単位。</param>
/// <param name="Codecs">MSE の <c>codecs</c> パラメータ（読めなければ <see langword="null"/>）。</param>
/// <param name="InProgress">まだ書かれている最中か。真のあいだは索引が伸び続ける。</param>
/// <param name="InitSize">
/// init セグメント（<c>ftyp</c> ＋ <c>moov</c>）の大きさ ＝ 最初の <c>moof</c> の位置。
/// シークのたびに <c>SourceBuffer</c> へ入れ直すのはこの先頭 N バイトである。
/// </param>
/// <param name="NextOffset">次に索引を引き直すときの <c>from</c>（差分だけが返る）。</param>
/// <param name="TotalDuration">最後のフラグメントの <c>Time</c> ＋ <c>Duration</c>。</param>
/// <param name="Fragments"><c>from</c> 以降のフラグメント（位置の昇順）。</param>
public sealed record RecordingFragmentsDto(
    uint Timescale, string? Codecs, bool InProgress, long InitSize, long NextOffset,
    ulong TotalDuration, RecordingFragmentDto[] Fragments);
