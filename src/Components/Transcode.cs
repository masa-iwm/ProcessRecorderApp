using System;
using System.Diagnostics.CodeAnalysis;

namespace ProcessRecorderApp.Components;

/// <summary>
/// この実機で録画トランスコードが成立するか。
///
/// <para>
/// <b>条件は H.264 デコーダーが 1 つ在ることだけ</b>で、候補表
/// （<c>EncoderCatalog.H264DecoderCandidates</c>）は<b>ハードウェアだけ</b>である
/// ── 同梱ランタイムにソフトウェアの H.264 デコーダーは無く（<c>avdec_h264</c> /
/// <c>openh264dec</c> はフルインストールの GStreamer にしか無い）、
/// 無ければ <see cref="Transcode"/> は false になる。
/// 表の外の要素は <see cref="AppEnvironment.H264DecoderVariable"/>（検証用）でだけ選べる。
/// </para>
/// </summary>
/// <param name="Transcode">録画トランスコードを提供できるか。</param>
/// <param name="Decoder">使う H.264 デコーダーの要素名（<see cref="Transcode"/> が false なら null）。</param>
public sealed record TranscodeCapability(bool Transcode, string? Decoder);

/// <summary>
/// 録画トランスコードの失敗理由のうち、<b>呼び出し側が意味で分岐するもの</b>（英語・ログと HTTP 本文で共有）。
/// </summary>
public static class TranscodeReasons
{
    /// <summary>この実機にハードウェア H.264 デコーダーが無い（404）。</summary>
    public const string Unavailable = "transcode unavailable";

    /// <summary>
    /// 補助エンコーダー枠が空いていない（409）。
    /// <b>ライブ DASH と同じ枠</b>なので、<see cref="DashPreviewReasons.Busy"/> と同じ文字列である。
    /// </summary>
    public const string Busy = "auxiliary encoder busy";

    /// <summary>録画中のファイルは変換できない（409）。</summary>
    public const string InProgress = "recording in progress";

    /// <summary>パイプラインは組めたが init（<c>ftyp</c>＋<c>moov</c>）が出てこなかった（503）。</summary>
    public const string StartFailed = "transcode start failed";
}

/// <summary>録画トランスコードの上限値。</summary>
public static class TranscodeLimits
{
    /// <summary>
    /// 読み手が閉じた後も貸出を保持する時間(ms)。
    /// <b>シーク（同じ <c>session</c> での開き直し）を枠の奪い合いにしないための猶予である</b>
    /// ── クライアントは位置を変えるたびに接続を張り直すので、閉じた瞬間に枠を手放すと
    /// 混んでいるときに自分の枠を他人へ渡してしまう。
    /// </summary>
    public const int GraceMs = 10000;

    /// <summary>init が揃うまで待つ上限(ms)。超えたら 503 で断る。</summary>
    public const int FirstChunkTimeoutMs = 10000;

    /// <summary>クライアントが名乗る <c>session</c> の最大長。</summary>
    public const int MaxSessionIdLength = 64;
}

/// <summary>
/// トランスコード 1 本の要求。
///
/// <para>
/// <b>セッションはクライアントが名乗る。</b> 同じ id での再要求は
/// 「位置を変えた同じ再生」として前のパイプラインを畳んで貸出を引き継ぐ
/// ── サーバーが id を発行すると、シークのたびに枠を 1 つ余分に要ることになる。
/// </para>
/// </summary>
/// <param name="SessionId">クライアントが名乗る識別子（<see cref="IsValidSessionId"/> を通ったもの）。</param>
/// <param name="FilePath">変換元の録画ファイルの絶対パス。</param>
/// <param name="StartSeconds">開始位置(秒)。有限かつ 0 以上。</param>
/// <param name="QualityId">
/// <see cref="PreviewQualityPresets.All"/> の id。<b><see cref="PreviewQualityPresets.Custom"/> は受けない</b>
/// ── カスタムはレコーダー設定の 4 値であり、録画済みファイルには対応する設定が無い。
/// </param>
/// <param name="Source">録画の形（sidecar の幅・高さ・fps）。読めなければ null。</param>
public sealed record TranscodeOpen(
    string SessionId,
    string FilePath,
    double StartSeconds,
    string QualityId,
    PreviewSourceInfo? Source)
{
    /// <summary>
    /// 1〜<see cref="TranscodeLimits.MaxSessionIdLength"/> 文字の
    /// <c>[A-Za-z0-9_-]</c> であること。<b>id はログにも枠の <c>Owner</c> にも出る</b>ので、
    /// 空白や記号を通さない。
    /// </summary>
    public static bool IsValidSessionId(string? id)
    {
        if (string.IsNullOrEmpty(id) || TranscodeLimits.MaxSessionIdLength < id.Length)
            return false;

        foreach (char c in id)
        {
            bool ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';
            if (!ok)
                return false;
        }

        return true;
    }
}

/// <summary>
/// トランスコード 1 本の読み出し口。<b><see cref="IDisposable.Dispose"/> は「読み手が閉じた」の意味</b>
/// で、供給側はそこでパイプラインを畳む（貸出の扱いは供給側の管理下にある）。
/// </summary>
public abstract partial class TranscodeReader : IDisposable
{
    /// <summary>
    /// チャンクを 1 つ引く。<b>呼び手のスレッドが最大 <paramref name="timeoutMs"/> だけ待つ</b>
    /// ── 戻り値が false でも <see cref="Ended"/> が false なら、まだ続きがある。
    /// </summary>
    /// <param name="timeoutMs">待つ上限(ms)。</param>
    /// <param name="chunk">取れたバイト列（そのままクライアントへ書ける）。</param>
    public abstract bool TryRead(int timeoutMs, [NotNullWhen(true)] out byte[]? chunk);

    /// <summary>もう続きが来ないか（EOS・エラー・閉じ済み）。</summary>
    public abstract bool Ended { get; }

    /// <summary>破綻したときの理由（ログ用・英語）。無事なら null。</summary>
    public abstract string? Error { get; }

    /// <summary>読み手が閉じたことを供給側へ伝える。冪等。</summary>
    public abstract void Dispose();
}

/// <summary>
/// <see cref="ITranscodeSource.TryOpen"/> を HTTP 層へ渡すための結果 1 組。
/// </summary>
/// <param name="Reader">開けたときの読み出し口。</param>
/// <param name="Reason">開けなかったときの理由（<see cref="TranscodeReasons"/> のどれか）。</param>
public sealed record TranscodeOpenResult(TranscodeReader? Reader, string? Reason);

/// <summary>録画トランスコードの供給元。</summary>
public interface ITranscodeSource
{
    /// <summary>この実機で録画トランスコードが成立するか。</summary>
    TranscodeCapability Capability { get; }

    /// <summary>
    /// トランスコードを 1 本開く。<b>同じ <see cref="TranscodeOpen.SessionId"/> の要求は
    /// 前のパイプラインを畳んで貸出を引き継ぐ</b>（＝シーク）。
    /// </summary>
    /// <param name="open">要求（<see cref="TranscodeOpen.QualityId"/> は検査済みの前提）。</param>
    /// <param name="reader">開けたときの読み出し口。使い終わったら <see cref="IDisposable.Dispose"/>。</param>
    /// <param name="reason">開けなかった理由（<see cref="TranscodeReasons"/> のどれか）。</param>
    bool TryOpen(
        TranscodeOpen open,
        [NotNullWhen(true)] out TranscodeReader? reader,
        [NotNullWhen(false)] out string? reason);
}
