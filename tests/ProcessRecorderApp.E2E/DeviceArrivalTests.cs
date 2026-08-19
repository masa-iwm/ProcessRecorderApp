using System.Diagnostics;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <b>デバイスが戻ってきたら、次のバックオフを待たずに復帰すること。</b>
///
/// <para>
/// <b>ここが測れるのは「シグナル → 早期復帰」までである。</b> その手前の
/// 「実デバイス → シグナル」（<c>mfdeviceprovider</c> / <c>d3d12screencapturedeviceprovider</c> の
/// ホットプラグ通知）は、開発機にも CI にもカメラが無く、モニタの抜き差しもできないので
/// <b>自動では一度も走らない</b> ── docs/coverage-gaps.md「デバイス到着の監視」に
/// 手動確認の手順がある。
/// </para>
/// <para>
/// そのため到着シグナルは <c>PROCESSRECORDERAPP_TEST_DEVICE_ARRIVAL</c> で外から起こす。
/// 名前付きイベントはキー接頭辞で隔離されているので、並行して走る他のインスタンスには効かない。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class DeviceArrivalTests(PublishedApp app, ITestOutputHelper output)
{
    private const string ArrivalVariable = "PROCESSRECORDERAPP_TEST_DEVICE_ARRIVAL";

    /// <summary>ログに行が現れるまでの上限。ワーカーの起動そのものは別で待ってある。</summary>
    private static readonly TimeSpan LineBudget = TimeSpan.FromSeconds(40);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 障害から 5 秒後に予約された復帰が、<b>到着シグナルで前倒しされる</b>こと。
    ///
    /// <para>
    /// 見るのは 2 つ ── <c>wake=device-arrival</c> が出ること（早く起きた唯一の証拠）と、
    /// 予約からその行までが <b>5 秒未満</b>であること（前倒しが実際に効いていること）。
    /// 下限は落ち着き待ち（1 秒）を計測の揺れぶん緩めて 900ms で見る
    /// ── 0 に近い値で通ってしまうなら、落ち着き待ちが消えている。
    /// </para>
    /// </summary>
    [Fact]
    public void ADeviceArrival_CutsTheBackoffShort()
    {
        var settings = new SettingsFile();
        var recorder = settings.AddRecorder("R1");

        // ErrorReportingTests と同じ障害注入（15fps で約 2 秒後に本物の Error）。
        // このソースは種別が付かない（DeviceKind.None）ので、**実プロバイダには一切触れない**
        // ── 効くのは注入シグナルだけである。
        recorder.SrcPipeline =
            "videotestsrc is-live=true do-timestamp=true ! identity error-after=30 ! videoconvert ! " +
            "video/x-raw,format=I420,width=320,height=240,framerate=15/1";

        using var instance = AppInstance.Create(app, settings, configure: i =>
            i.ExtraEnvironment[ArrivalVariable] = "1");

        using var arrival = OpenArrivalEvent(instance);

        // **シグナルは予約を見てから送る。** 世代カウンタがあるので早すぎても取りこぼしはしないが、
        // 時刻差の表明が意味を持つのは順序を固定したときだけである。
        string scheduled = WaitForLine(instance, "recorder.restart", " scheduled in 5000ms");
        arrival.Set();

        // **どの行に出るかは障害の形で変わる。** 要素単位で戻せる障害なら
        // "attempt=1 result=..." に、EOS を見た障害（identity のエラーはこちらを誘発する）なら
        // "escalating ... reason=eos" に付く。見たいのは「早く起きたこと」なので、
        // ウェイクの印を持つ最初の行を探す。
        string result = WaitForLine(instance, "recorder.restart", "wake=device-arrival");

        output.WriteLine(string.Join(Environment.NewLine, instance.ReadActivityLog()));

        TimeSpan waited = Elapsed(scheduled, result);
        Assert.True(waited < TimeSpan.FromMilliseconds(5000),
            $"復帰が前倒しされていません（{waited.TotalMilliseconds:F0}ms 待っている）。" + Environment.NewLine
            + scheduled + Environment.NewLine + result);
        Assert.True(TimeSpan.FromMilliseconds(900) <= waited,
            $"落ち着き待ちが効いていません（{waited.TotalMilliseconds:F0}ms）。" + Environment.NewLine
            + "列挙に出た＝開けるとは限らないので、到着の瞬間に試すと"
            + "確実に失敗する試行を 1 回消費する。");
    }

    /// <summary>
    /// <b>初期化に失敗したレコーダーが、到着で作り直されること。</b>
    ///
    /// <para>
    /// <c>mfvideosrc device-index=99</c> は、カメラの無い機械（開発機・CI がそうである）で
    /// <b>決定的に初期化に失敗する</b> ── 「デバイスが無い」状態をハードウェア無しで作れる
    /// 唯一の形である。この状態では<b>パイプラインもバスも無い</b>ので、
    /// 到着の監視が無ければ二度と何も起きない（この機能の前は実際にそうだった）。
    /// </para>
    /// <para>
    /// 見るのは「再試行が実際に走ったこと」までである ── カメラは戻ってこないので
    /// 2 度目も失敗する。<b>失敗の 2 周目こそが、詰みが解けている証拠</b>になる。
    /// </para>
    /// </summary>
    [Fact]
    public void ARecorderThatNeverInitialized_IsRebuiltOnArrival()
    {
        var settings = new SettingsFile();
        var recorder = settings.AddRecorder("R1");
        recorder.SrcPipeline =
            "mfvideosrc device-index=99 ! video/x-raw,format=NV12,width=320,height=240,framerate=15/1";

        using var instance = AppInstance.Create(app, settings, configure: i =>
            i.ExtraEnvironment[ArrivalVariable] = "1");

        using var arrival = OpenArrivalEvent(instance);

        // 初期化に失敗し、カメラの到着を待つ連鎖が張られていること。
        WaitForLine(instance, "recorder.init fail", "");
        string watch = WaitForLine(instance, "device.watch", "kind=camera");
        WaitForLine(instance, "recorder.restart", " scheduled in 60000ms");

        arrival.Set();

        string retry = WaitForLine(instance, "recorder.restart", "retrying the pipeline rebuild");
        string rebuilt = WaitForLine(instance, "recorder.restart", "rebuild result=");

        output.WriteLine(string.Join(Environment.NewLine, instance.ReadActivityLog()));

        Assert.Contains("wake=device-arrival", retry, StringComparison.Ordinal);

        // カメラは戻ってこないので、2 周目も失敗するのが正しい。
        // 見ているのは「作り直しが実際に走った」ことである。
        Assert.Contains("rebuild result=failed", rebuilt, StringComparison.Ordinal);

        // 監視が縮退していたら（プロバイダが無い・通知を出せない）、
        // 上の前倒しはタイマーの偶然ではなく注入によるものだと言えなくなる。
        Assert.Contains("monitor=yes", watch, StringComparison.Ordinal);
    }

    // ---- 補助 ----

    /// <summary>
    /// 注入用の名前付きイベントを用意する。ワーカー側は復帰連鎖を張った時点で
    /// 同じ名前で開くので、どちらが先でもよい。
    /// </summary>
    private static EventWaitHandle OpenArrivalEvent(AppInstance instance)
        => new(false, EventResetMode.AutoReset, instance.KeyPrefix + "-DeviceArrival");

    /// <summary>
    /// 指定のイベント名で <paramref name="contains"/> を含む行が現れるまで待ち、その行を返す。
    /// 同じ内容の行が複数あるときは<b>最初の 1 本</b>を返す（時刻差を測るため）。
    /// </summary>
    private static string WaitForLine(AppInstance instance, string eventName, string contains)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < LineBudget)
        {
            foreach (string line in ActivityLogFile.Events(instance.ReadActivityLog(), eventName))
            {
                if (contains.Length == 0 || line.Contains(contains, StringComparison.Ordinal))
                    return line;
            }

            Thread.Sleep(PollInterval);
        }

        Assert.Fail(
            $"activity.log に '{eventName}' の行（'{contains}' を含む）が {LineBudget.TotalSeconds:F0} 秒以内に出ませんでした。"
            + Environment.NewLine
            + string.Join(Environment.NewLine, instance.ReadActivityLog()));
        return "";
    }

    private static TimeSpan Elapsed(string from, string to)
    {
        DateTime? start = ActivityLogFile.TimestampOf(from);
        DateTime? end = ActivityLogFile.TimestampOf(to);
        Assert.True(start is not null && end is not null,
            "activity.log の時刻を読めませんでした（書式が変わった可能性がある）。");
        return end.Value - start.Value;
    }
}
