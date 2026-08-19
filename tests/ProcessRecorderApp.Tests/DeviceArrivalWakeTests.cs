using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>復帰の待ちが、デバイスの到着で打ち切られる形のままであること</b>を
/// ソースをテキストとして固定する。
///
/// <para>
/// <b>実行では守れない。</b> 到着シグナルを起こす脚（デバイスプロバイダのバス）は、
/// カメラの抜き差しもモニタの抜き差しもできない開発機・CI では一度も動かない
/// （docs/coverage-gaps.md「デバイス到着の監視」）。E2E が測れるのは
/// 「シグナル → 早期復帰」までで、そちらはテスト用の注入口に依存している。
/// </para>
/// <para>
/// ここが守るのは<b>ただ一つの失敗モード</b>である ──
/// <c>await Task.Delay(delayMs, cts.Token)</c> の 1 行へ戻され、到着を待たなくなること。
/// そうなっても<b>録画は動き、E2E も全部緑になる</b>（復帰は 5 秒後・10 秒後に起きるので、
/// 遅いだけで正しく見える）。つまりこの退行は、この検査以外の誰も気付けない。
/// </para>
/// </summary>
public class DeviceArrivalWakeTests
{
    private static string EventRecorderSource =>
        File.ReadAllText(RepositoryFiles.At("src", "GStreamer.GstSharpNet", "EventRecorder.cs"));

    private const string RestartLoopSignature =
        "private async System.Threading.Tasks.Task RestartLoopAsync(";

    /// <summary>
    /// 復帰ループの待ちが <c>WaitForRetrySlotAsync</c> を通っていること、
    /// かつ素の <c>Task.Delay</c> をループの中で直接待っていないこと。
    /// </summary>
    [Fact]
    public void TheRestartLoop_WaitsThroughTheArrivalAwareSlot()
    {
        string body = SourceMethodBody.Extract(EventRecorderSource, RestartLoopSignature);

        Assert.True(
            SourceMethodBody.ContainsCode(body, "await WaitForRetrySlotAsync("),
            "復帰ループが WaitForRetrySlotAsync を通らなくなっている。"
            + Environment.NewLine
            + "ここを素の Task.Delay へ戻すと、カメラやモニタを挿し直しても"
            + Environment.NewLine
            + "**次のバックオフ（5s/10s/30s/60s）まで待たされる**ようになる。"
            + Environment.NewLine
            + "遅くなるだけで動きはするので、他のどのテストも赤くならない。");

        Assert.False(
            SourceMethodBody.ContainsCode(body, "await System.Threading.Tasks.Task.Delay("),
            "復帰ループが Task.Delay を直接待っている。"
            + "待ちは WaitForRetrySlotAsync に一本化すること（到着で打ち切れなくなる）。");
    }

    /// <summary>
    /// 到着の監視の取得が<b>ループ側</b>にあること。
    /// <c>ScheduleRestart</c> は <c>_busLock</c> を保持したストリーミングスレッドから
    /// 呼ばれるので、そこでネイティブの <c>DeviceProvider.Start</c> を呼ぶと、
    /// <b>デバイス列挙のあいだ当の要素のストリーミングスレッドが止まる</b>。
    /// </summary>
    [Fact]
    public void TheProviderIsAcquired_OnThePoolThreadNotInTheBusHandler()
    {
        string source = EventRecorderSource;

        string loop = SourceMethodBody.Extract(source, RestartLoopSignature);
        Assert.True(
            SourceMethodBody.ContainsCode(loop, "DeviceArrivalWatcher.Instance.Acquire("),
            "復帰ループが到着の監視を取得していない。取得が消えると、"
            + "待ちは張られてもプロバイダが started にならず、到着が一度も post されない。");

        string schedule = SourceMethodBody.Extract(
            source, "private void ScheduleRestart(string elementName, bool rebuildOnly = false)");
        Assert.False(
            SourceMethodBody.ContainsCode(schedule, "DeviceArrivalWatcher.Instance.Acquire("),
            "ScheduleRestart が到着の監視を取得している。"
            + Environment.NewLine
            + "ここは _busLock を保持したストリーミングスレッドで走るので、"
            + Environment.NewLine
            + "ネイティブのデバイス列挙をここで走らせてはいけない。取得はループ側で行うこと。");
    }

    /// <summary>
    /// 到着で起きた回に <c>wake=device-arrival</c> が残ること。
    /// <b>この 1 語だけが、早く起きたことの外から見える証拠</b>である
    /// ── E2E はこれを <c>Contains</c> で拾う。
    /// </summary>
    [Fact]
    public void AnEarlyWake_IsVisibleInTheActivityLog()
    {
        string body = SourceMethodBody.Extract(EventRecorderSource, RestartLoopSignature);

        Assert.True(
            SourceMethodBody.ContainsCode(body, "wake=device-arrival"),
            "早期に起きたことが activity.log に出なくなっている。"
            + "E2E（DeviceArrivalTests）はこの語で判定するので、綴りを変えるならあちらも直すこと。");
    }

    /// <summary>
    /// <b>初期化の失敗が復帰の芽を残すこと。</b> これが消えると、パイプラインもバスも無い
    /// 状態＝<b>二度とエラーが飛ばない状態</b>で連鎖が終わり、デバイスを挿し直しても
    /// 永久に復帰しなくなる（この機能の前は実際にそうなっていた）。
    /// </summary>
    [Fact]
    public void AFailedInitialize_LeavesAChainBehind()
    {
        string source = EventRecorderSource;

        string initialize = SourceMethodBody.Extract(source, "public void Initialize()");
        Assert.True(
            SourceMethodBody.ContainsCode(initialize, "TryScheduleDeviceRebuild();"),
            "Initialize() が失敗しても復帰の連鎖を張らなくなっている。"
            + Environment.NewLine
            + "この 1 行が、起動時にカメラが無い場合・デバイス不在のまま再生成した場合の"
            + Environment.NewLine
            + "**唯一の復帰経路**である（そこにはもうエラーを出すパイプラインが無い）。");

        string plant = SourceMethodBody.Extract(source, "private void TryScheduleDeviceRebuild()");
        Assert.True(
            SourceMethodBody.ContainsCode(plant, "rebuildOnly: true"),
            "TryScheduleDeviceRebuild が rebuildOnly の連鎖を張らなくなっている。"
            + "パイプラインが無い状態で要素単位の再開を試しても、対象が無いだけである。");

        Assert.True(
            SourceMethodBody.ContainsCode(plant, "DeviceKind.None"),
            "TryScheduleDeviceRebuild が種別で絞らなくなっている。"
            + "テストソースや打ち間違いのパイプラインまで 60 秒ごとに永久に再試行することになる。");
    }
}
