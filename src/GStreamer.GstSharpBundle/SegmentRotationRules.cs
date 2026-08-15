namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// <b>常時録画のセグメントをいつ切り替えるかの規則（純粋関数）。</b>
///
/// <para>
/// 実装（<c>ContinuousRecorder</c>）は GStreamer を必要とするため L1 から動かせない。
/// 規則だけをここへ切り出して単体テストから直接検証する
/// （<c>RecordingCommandState</c> / <c>StopDrainRules</c> / <c>RecorderNaming</c> と同じ方針）。
/// </para>
/// </summary>
public static class SegmentRotationRules
{
    /// <summary>1 秒のナノ秒数（GStreamer の時刻はすべてナノ秒）。</summary>
    public const long NanosecondsPerSecond = 1_000_000_000L;

    /// <summary>
    /// 超過を報告し始めるまでの猶予。<see cref="IsOvershooting"/> は
    /// 「セグメント長の 2 倍」と「セグメント長 ＋ この猶予」の<b>厳しい方</b>で判定する。
    /// </summary>
    public const long OvershootGraceNs = 30 * NanosecondsPerSecond;

    /// <summary>セグメント長(秒)をナノ秒へ。0 以下は「切り替えない」を意味する。</summary>
    public static long SegmentNanoseconds(int seconds)
        => seconds <= 0 ? 0 : (long)seconds * NanosecondsPerSecond;

    /// <summary>
    /// このバッファでセグメントを切り替えるか。
    ///
    /// <para>
    /// <b>非キーフレームでは絶対に切らない。</b> 次のセグメントの先頭が P フレームになると、
    /// 書き出し側の I フレームゲートが次の I が来るまで捨てるので、そのぶんが丸ごと欠ける
    /// ── 「早く切る」ために「映像を失う」のは割に合わない。
    /// 経過時間を過ぎたあとの<b>最初のキーフレーム</b>で切る。
    /// </para>
    /// </summary>
    public static bool ShouldRotate(long elapsedNs, long segmentNs, bool isKeyframe)
        => 0 < segmentNs && segmentNs <= elapsedNs && isKeyframe;

    /// <summary>
    /// セグメントが想定より長くなっているか（＝キーフレームが来ていない）。
    ///
    /// <para>
    /// 原因はほぼ GOP 長である。自動選択のエンコーダーは
    /// <c>EncoderCatalog.GopSize</c> が入るので超過はその数フレーム分で収まるが、
    /// <c>ContinuousEncodingProperties</c> を手書きすると GOP がエンコーダー既定
    /// （数百フレーム）になり得る。<b>黙って長いファイルを作らない</b>ための検出。
    /// </para>
    /// </summary>
    public static bool IsOvershooting(long elapsedNs, long segmentNs)
    {
        if (segmentNs <= 0)
            return false;

        long doubled = segmentNs <= long.MaxValue / 2 ? segmentNs * 2 : long.MaxValue;
        long withGrace = segmentNs <= long.MaxValue - OvershootGraceNs
            ? segmentNs + OvershootGraceNs
            : long.MaxValue;
        long threshold = doubled < withGrace ? doubled : withGrace;
        return threshold <= elapsedNs;
    }
}
