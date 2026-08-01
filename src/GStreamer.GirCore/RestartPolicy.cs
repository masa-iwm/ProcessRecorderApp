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
}
