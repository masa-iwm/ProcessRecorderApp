using System;
using System.Collections.Generic;
using ProcessRecorderApp.Components;

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
/// <param name="StartTimeUtc">
/// 録画の開始時刻（UTC）。sidecar があればその値、無ければファイル名
/// （<c>yyyyMMdd_HHmmss_&lt;name&gt;</c>）をローカル時刻として読んだ値。
/// <b>並びの基準はこれ</b>（<see cref="LastWriteTimeUtc"/> ではない ── 長い録画は
/// 始まった順と書き終わった順が食い違う）。
/// </param>
/// <param name="Recorder">
/// 録画したレコーダーの名前（ファイル名テンプレートの <c>{Name}</c> と同じ値）。
/// 推定できなければ空文字。
/// </param>
/// <param name="Trigger">
/// 開始理由（<c>manual</c> / <c>uia:&lt;triggerId&gt;</c> / <c>remote</c> / <c>cli</c> /
/// <c>continuous</c>）。<b>sidecar からしか来ない</b>ので、録画中と
/// sidecar の無いものは <see langword="null"/>。
/// </param>
/// <param name="DurationMs">
/// 尺（ミリ秒）。<b><see langword="null"/> でありうる</b> ── 録画中・fragmented・
/// <c>moov</c> が読めないものでは出ない（fragmented の総尺は
/// <c>/api/recording-fragments/</c> が持つ）。
/// </param>
/// <param name="Width">映像の幅。sidecar にあるときだけ。</param>
/// <param name="Height">映像の高さ。同上。</param>
/// <param name="HasThumbnail"><c>&lt;録画ファイル名&gt;.png</c> が並んでいる。</param>
public sealed record RecordingFileDto(
    string Path, long Length, DateTime LastWriteTimeUtc, bool InProgress, bool Fragmented,
    DateTime StartTimeUtc, string Recorder, string? Trigger, long? DurationMs, int? Width, int? Height,
    bool HasThumbnail);

/// <summary>
/// 保存先の一覧。
/// </summary>
/// <param name="Root">
/// 解決済みの配信 root（絶対パス）。<b>読み取りは認証を要さない</b>ので、
/// これが見える相手には録画の置き場所も見える。
/// </param>
/// <param name="Total">
/// 絞り込みを適用したあと・ページングの<b>前</b>の件数。
/// <c>limit</c> を付けても総数が分かるようにするためのもの。
/// </param>
/// <param name="HasMore"><c>offset</c> ＋ 返した件数が <paramref name="Total"/> に届いていない。</param>
/// <param name="Files">
/// 開始時刻の降順、同時刻は相対パスの序数昇順。
/// <b>項目名は変えない</b> ── Web UI が読んでいる。
/// </param>
public sealed record RecordingsDto(
    string Root, int Total, bool HasMore, IReadOnlyList<RecordingFileDto> Files);

/// <summary>
/// 日付ごとの録画件数（カレンダー表示の材料）。
/// </summary>
/// <param name="Days">日付の昇順。件数 0 の日は<b>出ない</b>。</param>
public sealed record RecordingDaysDto(IReadOnlyList<RecordingDayCount> Days);

/// <summary>
/// 一覧が変わったという合図（SSE の <c>event: recording</c>）。
///
/// <para>
/// <b>これは「一覧を取り直せ」以上の意味を持たない。</b> 配信は best-effort で、
/// 混雑時は購読者の channel から古い順に落ちる ── 内容に依存した差分適用をすると、
/// 落ちた 1 件ぶんだけ永久にずれる。
/// </para>
/// </summary>
/// <param name="Kind"><c>added</c> / <c>completed</c> / <c>removed</c> / <c>updated</c>。</param>
/// <param name="Path">対象の相対パス（区切りは <c>/</c>）。</param>
public sealed record RecordingChangeDto(string Kind, string Path);

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
