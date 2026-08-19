using System;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// ソース障害からの自動復帰の間隔と、パイプライン再生成へ切り替える基準。
///
/// <para>
/// エラー1件ごとに無条件で復帰（<c>Task.Delay(30000).ContinueWith(...)</c> の類）を
/// 積んではいけない。監視対象のモニタを抜くと数十件のエラーが連続で出るため、
/// 復帰試行が数十本並走し、しかも <c>_errorSinkSrc</c> を上書きし合うことになる。
/// </para>
///
/// <para>
/// 方針:
/// <list type="bullet">
///   <item>間隔は 5s → 10s → 30s → 60s で頭打ち。最初の試行を早くするのは、
///     一瞬の切断（ケーブルの接触・モード切替）なら 30 秒も待つ必要がないため。</item>
///   <item><b>試行回数は無制限。</b> 監視対象のモニタが1時間抜けていても、
///     戻ってきたら復帰すべきアプリなので諦めない。</item>
///   <item>ただし <see cref="EscalateAfterAttempts"/> 回続けて失敗したら、
///     要素単位の再 Playing ではなく <c>Initialize()</c> によるパイプライン再生成へ切り替える
///     ── デバイスが別のキャップスで戻ってきた場合、要素を Playing にし直すだけでは復帰できない。</item>
///   <item><b>間隔は上限であって、待ち切る義務ではない。</b> デバイスの到着を観測したら
///     <see cref="SettleAfterArrivalMs"/> だけ置いて即座に試す
///     （<c>DeviceArrivalWatcher</c>）。<b>試行回数の数え方は変えない</b> ──
///     早く起きた回も通常の 1 回として数え、エスカレーションの基準もそのまま使う。</item>
/// </list>
/// </para>
/// </summary>
internal static class RestartPolicy
{
    /// <summary>この回数だけ連続で失敗したら、パイプラインの再生成へ切り替える。</summary>
    public const int EscalateAfterAttempts = 3;

    private static readonly int[] _backoffMs = [5_000, 10_000, 30_000, 60_000];

    /// <summary>
    /// <paramref name="attempt"/> 回目（1 始まり）の試行までの待ち時間(ms)。
    /// 表を超えたら最後の値で頭打ちにする。
    /// </summary>
    public static int DelayForAttempt(int attempt)
    {
        if (attempt < 1)
            attempt = 1;
        int index = Math.Min(attempt - 1, _backoffMs.Length - 1);
        return _backoffMs[index];
    }

    /// <summary>
    /// <paramref name="attempt"/> 回目の失敗の後、パイプライン再生成へ切り替えるべきか。
    /// </summary>
    public static bool ShouldEscalate(int attempt) => EscalateAfterAttempts <= attempt;

    /// <summary>
    /// バックオフ表の頭打ちの値(ms)。<see cref="DelayForAttempt"/> が最終的に返す値と
    /// 同じであることを L1 が縛る。パイプライン再生成だけを待つ連鎖
    /// （<c>rebuildOnly</c>）の間隔はこれを使う ── そちらは要素単位の再開を試さないので、
    /// 短い間隔で回しても得るものが無い。
    /// </summary>
    public const int MaxDelayMs = 60_000;

    /// <summary>
    /// デバイスの到着を観測してから実際に試すまでに置く「落ち着き待ち」(ms)。
    ///
    /// <para>
    /// <b>列挙に出た＝開けるとは限らない。</b> USB カメラのデバイスインターフェイスの
    /// 到着通知はドライバが使える状態になる前に飛びうるし、ディスプレイの再構成は
    /// <c>WM_DISPLAYCHANGE</c> の時点ではまだ途中でありうる。0 にすると、
    /// 到着のたびに確実に失敗する試行を 1 回消費することになる。
    /// </para>
    /// </summary>
    public const int EarlyWakeSettleMs = 1_000;

    /// <summary>
    /// デバイスの到着で待ちを打ち切るとき、そこからさらに何 ms 待つか。
    ///
    /// <para>
    /// <b>元の待ち時間を超えない。</b> 到着が遅く（<paramref name="elapsedMs"/> が
    /// <paramref name="fullDelayMs"/> に近い）来た場合に落ち着き待ちを足すと、
    /// 「早期復帰」が本来より遅くなってしまう。
    /// </para>
    /// </summary>
    public static int SettleAfterArrivalMs(int fullDelayMs, int elapsedMs)
        => Math.Clamp(EarlyWakeSettleMs, 0, Math.Max(0, fullDelayMs - elapsedMs));
}
