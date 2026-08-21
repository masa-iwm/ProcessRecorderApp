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
    /// 早期ウェイクの回数に上限が掛かっていること。
    ///
    /// <para>
    /// <b>外しても何も赤くならない。</b> 復帰は動くし、むしろ速く見える ──
    /// 壊れ方は「モニターの再構成のような到着の連打で、まだ落ち着いていない機械へ
    /// パイプライン全再生成を掛ける」という、ログを読まないと分からない形である。
    /// </para>
    /// </summary>
    [Fact]
    public void TheEarlyWakes_AreCapped()
    {
        string body = SourceMethodBody.Extract(EventRecorderSource, RestartLoopSignature);

        Assert.True(
            SourceMethodBody.ContainsCode(body, "RestartPolicy.MayWakeEarly("),
            "復帰ループが早期ウェイクの回数を数えなくなっている。"
            + Environment.NewLine
            + "上限が無いと、到着の連打だけでエスカレーションの予算（3 回）が数秒で尽き、"
            + Environment.NewLine
            + "本来 45 秒かけて見極めるはずの判断を数秒で下すようになる。");
    }

    /// <summary>
    /// <b>作り直しだけの連鎖の間隔が、到着で仕切り直されること。</b>
    ///
    /// <para>
    /// <b>実行では守れない。</b> 頭打ち固定へ戻しても復帰は動くし、E2E も
    /// 「いつかは作り直す」ことしか見ないので、遅いだけで正しく見える。
    /// 壊れ方は<b>復帰が丸 1 分遅れる</b>ことだけである。
    /// </para>
    /// </summary>
    [Fact]
    public void TheRebuildInterval_IsRestartedByAnArrival()
    {
        string body = SourceMethodBody.Extract(EventRecorderSource, RestartLoopSignature);

        Assert.True(
            SourceMethodBody.ContainsCode(body, "RestartPolicy.RebuildDelayMs("),
            "作り直しの連鎖の間隔が RebuildDelayMs を通らなくなっている。"
            + Environment.NewLine
            + "頭打ち（60 秒）固定へ戻すと、到着で起こした試行が失敗した時点で"
            + Environment.NewLine
            + "**次の機会が丸 60 秒先**になる ── RDP のセッション復帰のように"
            + Environment.NewLine
            + "到着の直後はまだ撮れない場合に、復帰が 1 分遅れる"
            + "（ケーブルの抜き差しなら同じ 1.5 秒後の試行で成功するので、"
            + "落ち着き待ちを一律に延ばすのは誤り）。");
    }

    /// <summary>
    /// <b>到着で起きた回が、梯子を 1 段目へ戻すこと。</b> 戻さないと
    /// <c>RebuildDelayMs</c> を通していても値は頭打ちのままで、検査だけが緑になる。
    /// </summary>
    [Fact]
    public void AnEarlyWake_ResetsTheRebuildLadder()
    {
        string body = SourceMethodBody.Extract(EventRecorderSource, RestartLoopSignature);

        Assert.True(
            SourceMethodBody.ContainsCode(body, "_rebuildFailuresSinceArrival = 0"),
            "到着で起きたときに作り直しの梯子を戻さなくなっている。"
            + Environment.NewLine
            + "これが無いと間隔は 60 秒のままなので、RDP 復帰のように"
            + Environment.NewLine
            + "到着の直後はまだ撮れない場合に、復帰が 1 分遅れる。"
            + Environment.NewLine
            + "戻すのは**試行の前**であること ── この回の失敗の後に"
            + "次の周回が 5 秒で来るようにするため。");

        // **作り直しだけの連鎖に絞らないこと。** エスカレーションの作り直しも到着で
        // 早められるので、そこで空振りしたときこそ次を早く来させたい ── RDP の
        // セッション復帰は「仮想ディスプレイが先に現れ、実モニタは後から戻る」二段構えで、
        // 一段目の到着で起きた作り直しは必ず失敗する。絞ると次の機会まで丸 1 分空く。
        // **否定形だけでは守れない。** `if (rebuildOnly && early)` と書き直されると
        // 素通りするので、肯定形で「early だけを見ていること」を縛る ──
        // `early` の直後に `)` が要るので、どんな並びの絞り込みも弾ける。
        Assert.True(
            SourceMethodBody.ContainsCode(body, "if (early)"),
            "梯子を戻す条件が early 単独でなくなっている。"
            + Environment.NewLine
            + "エスカレーションの作り直しが到着で空振りしたときに梯子が始まらず、"
            + Environment.NewLine
            + "RDP 復帰のような二段構えの復帰で丸 1 分待つことになる。");

        Assert.False(
            SourceMethodBody.ContainsCode(body, "if (early && rebuildOnly)"),
            "梯子を戻す条件が rebuildOnly の連鎖に絞られている。"
            + Environment.NewLine
            + "エスカレーションの作り直しが到着で空振りしたときに梯子が始まらず、"
            + Environment.NewLine
            + "RDP 復帰のような二段構えの復帰で丸 1 分待つことになる。");

        int reset = SourceMethodBody.IndexOfCode(body, "_rebuildFailuresSinceArrival = 0");
        int rebuild = SourceMethodBody.IndexOfCode(body, "Initialize();");
        Assert.True(reset < rebuild,
            "梯子を戻すのが作り直しより後になっている。"
            + "試行の前に戻さないと、その回の失敗が梯子の 1 段目を食い潰す。");

        // **失敗の計上も作り直しより前であること。** 失敗した Initialize() は自分の中で
        // 次の連鎖を張る（TryScheduleDeviceRebuild）ので、catch へ動かすと
        // その連鎖が到着で 0 に戻した値を読む ── RebuildDelayMs(0) は頭打ち（60 秒）なので、
        // 梯子が丸ごと消えるのに上の検査は緑のままになる。
        int count = SourceMethodBody.IndexOfCode(body, "_rebuildFailuresSinceArrival++");
        Assert.True(0 <= count && count < rebuild,
            "失敗の計上が作り直しより後になっている。"
            + Environment.NewLine
            + "失敗した Initialize() は自分の中で次の連鎖を張るので、"
            + "後から数えたのでは**次の連鎖が古い値（0）を読み**、"
            + "そこから 60 秒待つ ── 到着後の梯子が丸ごと効かなくなる。");
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
