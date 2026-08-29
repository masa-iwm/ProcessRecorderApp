using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace ProcessRecorderApp.Components;

/// <summary>
/// DASH の 1 セグメント（<c>moof</c>＋<c>mdat</c> を 1 つ以上まとめたもの）。<b>不変</b>。
///
/// <para>
/// <see cref="Bytes"/> は<b>配信側が書き換えない</b>ことを約束した連続バイト列である
/// ── 読むのは HTTP のスレッドで、供給側が再利用する緩衝を指すと読み出しの途中で中身が変わる。
/// </para>
/// </summary>
/// <param name="Time">先頭サンプルの復号時刻（<c>tfdt</c> の <c>baseMediaDecodeTime</c>）。</param>
/// <param name="Duration">次のセグメントの <see cref="Time"/> との差（単位は timescale の刻み）。</param>
/// <param name="Bytes">そのままクライアントへ書き出せる連続したバイト列。</param>
public sealed record DashMediaSegment(ulong Time, ulong Duration, ReadOnlyMemory<byte> Bytes);

/// <summary>
/// DASH プレビューの「いまの姿」1 枚。<b>不変で、発行後にバイト列を書き換えない</b>
/// ── 呼び出し側はこれ 1 つから MPD も init も全セグメントも作れる。
///
/// <para>
/// <b><see cref="Generation"/> が変わったら別の連続体である。</b> mux を組み直すたびに
/// 増えるので、クライアントは Period を切り替える（＝以前の init は使えない）。
/// </para>
/// </summary>
/// <param name="Generation">この連続体の通し番号（mux を組み直すたびに +1）。</param>
/// <param name="Init">Init セグメント（<c>ftyp</c> … <c>moov</c>）。</param>
/// <param name="Timescale">1 秒あたりの刻み数（<c>mdhd</c>）。</param>
/// <param name="Codecs">MPD / MSE の <c>codecs</c> 文字列。</param>
/// <param name="Width">符号化された幅(px)。</param>
/// <param name="Height">符号化された高さ(px)。</param>
/// <param name="Fps">符号化されたフレームレート(fps)。</param>
/// <param name="BitrateKbps">エンコーダーへ指示したビットレート(kbit/sec)。</param>
/// <param name="QualityId">
/// この連続体を組んだときの画質 id（<see cref="PreviewQualityPresets.All"/> の id か
/// <see cref="PreviewQualityPresets.Custom"/>）。<b>指示ではなく実際に動いているもの</b>。
/// </param>
/// <param name="AvailabilityStartTimeUtc">この連続体が始まった時刻（MPD の基準）。</param>
/// <param name="PresentationTimeOffset">最初に確定したセグメントの <see cref="DashMediaSegment.Time"/>。</param>
/// <param name="Segments">保持しているセグメント（古い順）。</param>
public sealed record DashPreviewSnapshot(
    int Generation,
    ReadOnlyMemory<byte> Init,
    uint Timescale,
    string Codecs,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    string QualityId,
    DateTimeOffset AvailabilityStartTimeUtc,
    ulong PresentationTimeOffset,
    IReadOnlyList<DashMediaSegment> Segments);

/// <summary>DASH プレビューの供給元。</summary>
public interface IDashPreviewSource
{
    /// <summary>
    /// <paramref name="target"/> のレコーダーの現在の姿を取る。
    /// 対象の解決規則は <c>RecorderCliRules.ResolveTargetIndex</c> と同じ
    /// （数値はインデックス、それ以外は名前の序数完全一致）。
    ///
    /// <para>
    /// <b>呼ぶこと自体がエンジンを起こす。</b> 供給側は最後に呼ばれた時刻で
    /// <see cref="DashPreviewLimits.LeaseMs"/> の貸出を延ばすので、
    /// 誰も引かなくなれば mux は自然に畳まれる。
    /// </para>
    /// </summary>
    /// <param name="target">インデックスまたはレコーダー名。</param>
    /// <param name="snapshot">成功したときの姿。</param>
    /// <param name="reason">失敗理由（ログ用・英語）。</param>
    bool TryGetSnapshot(
        string target,
        [NotNullWhen(true)] out DashPreviewSnapshot? snapshot,
        [NotNullWhen(false)] out string? reason);

    /// <summary>
    /// <paramref name="target"/> のライブ画質の姿を読む。対象の解決規則は
    /// <see cref="TryGetSnapshot"/> と同じ。
    ///
    /// <para>
    /// <b>貸出を延ばさない。</b> 見ていない相手の第 2 パイプラインを起こさずに
    /// 選択肢だけを答えるための口である ── レコーダーが初期化前でも成功し、
    /// <see cref="PreviewQualityState.Source"/> と
    /// <see cref="PreviewQualityState.Effective"/> が <see langword="null"/> になる。
    /// </para>
    /// </summary>
    /// <param name="target">インデックスまたはレコーダー名。</param>
    /// <param name="state">成功したときの姿。</param>
    /// <param name="reason">失敗理由（ログ用・英語）。対象が無いときだけ意味で分岐する。</param>
    bool TryGetQuality(
        string target,
        [NotNullWhen(true)] out PreviewQualityState? state,
        [NotNullWhen(false)] out string? reason);

    /// <summary>
    /// <paramref name="target"/> のライブ画質を切り替える。<b>非永続</b>で、レコーダー単位・
    /// 全視聴者共有・最後勝ちであり、アプリを終えると消える。
    ///
    /// <para>
    /// <b>貸出を延ばさない</b>（<see cref="TryGetQuality"/> と同じ）。反映は次のサンプルで
    /// mux を組み直す形なので、戻り値の <see cref="PreviewQualityState.Effective"/> は
    /// まだ古い連続体を指していることがある。
    /// </para>
    /// </summary>
    /// <param name="target">インデックスまたはレコーダー名。</param>
    /// <param name="qualityId">
    /// <see cref="PreviewQualityPresets.All"/> の id か
    /// <see cref="PreviewQualityPresets.Custom"/>。<b>検査済みの前提</b>で、
    /// それ以外は <see cref="ArgumentException"/>。
    /// </param>
    /// <param name="state">成功したときの姿。</param>
    /// <param name="reason">失敗理由（ログ用・英語）。</param>
    bool TrySetQuality(
        string target,
        string qualityId,
        [NotNullWhen(true)] out PreviewQualityState? state,
        [NotNullWhen(false)] out string? reason);
}

/// <summary>
/// <see cref="IDashPreviewSource.TryGetSnapshot"/> が返す失敗理由のうち、
/// <b>呼び出し側が意味で分岐するもの</b>。
///
/// <para>
/// 「対象が無い」は <see cref="PreviewStreamReasons.RecorderNotFound"/> を共有する
/// ── HTTP の 404（終了コード 13）と 503（12）を分ける判定は 1 か所しか無い。
/// </para>
/// </summary>
public static class DashPreviewReasons
{
    /// <summary>エンジンは動いているが、まだ init も 1 セグメントも揃っていない。</summary>
    public const string Starting = "dash preview is starting";

    /// <summary>候補のエンコーダーが尽きた（この寿命では二度と組めない）。</summary>
    public const string EncoderUnavailable = "no encoder accepts the preview settings";
}

/// <summary>DASH プレビューの上限値。</summary>
public static class DashPreviewLimits
{
    /// <summary>
    /// 最後に読まれてから mux を畳むまでの猶予(ms)。
    /// <b>読み手が居なくなれば第 2 パイプラインは消える</b>のがこの機構の要で、
    /// 明示的な購読解除を持たない代わりにこれで有界にしている。
    /// </summary>
    public const int LeaseMs = 10000;

    /// <summary>保持するセグメント数。溢れたら古い方から捨てる。</summary>
    public const int RingDepth = 6;

    /// <summary>
    /// 1 セグメントへまとめてよい fragment の数の上限。
    /// GOP 1 秒・<c>fragment-duration</c> 1000ms なら 1:1 になるので、これは安全網である。
    /// </summary>
    public const int MaxPendingFragments = 8;
}
