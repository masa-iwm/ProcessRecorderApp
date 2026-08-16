using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L2: <b>停止が「使える成果物」を残したかどうかを、CLI が終了コードで伝えること。</b>
///
/// <para>
/// <b>排出（EOS → バス待ち → <c>SetState(Null)</c>）は、1フレームも入っていなくても、
/// ファイルが確定していなくても「成功」しうる。</b> だから停止処理の成否だけを見ると
/// <b>終了コード 0 で使えないファイルを渡す</b> ── ルート README は
/// 「バッチが <c>%ERRORLEVEL%</c> で成否を判定し、<c>stop-recording</c> の直後に
/// <c>copy</c> する」用途を謳っているので、これが実害の本体である。
/// </para>
/// <para>
/// <b>2つの失敗を分けているのは、呼び出し側の扱いが変わるから。</b>
/// <c>16</c>（空）は<b>捨ててよい</b>、<c>17</c>（未確定）は
/// <c>mdat</c> にデータがある一方で <c>moov</c> が未確定なので<b>救済の余地がある</b>。
/// </para>
/// <para>
/// <b>ここで扱えるのは <c>16</c>（空）の側だけ。</b> 症状を<b>注入ではなく設定だけ</b>で
/// 決定的に作れるからである（<c>num-buffers</c> でソースを終わらせる）。
/// </para>
/// <para>
/// <b><c>17</c>（未確定）は E2E に置いていない。</b> 排出の打ち切りを決定論的に踏ませる
/// 手段が無く、<b>較正を繰り返しても機械の速度に負けた</b>
/// （打ち切りを 1ms にしても 0 にしても、排出が間に合うかどうかはスケジューリング次第）。
/// 判定規則は純粋関数へ切り出して
/// <c>RecordingStopRulesTests</c>（L1）が守っており、
/// <b>その規則を CLI へ配線したこと</b>は下の <c>-all</c> のテストが実際に叩いて確かめる。
/// </para>
/// <para>
/// <b>ここで <c>RecordedMp4.AssertUsable</c> を使ってはいけない。</b> あの表明は
/// 壊れた MP4 を<b>すでに</b>落とせるので、使うと
/// <b>新しい信号（終了コードとログ）が効いているかをテストが答えられなくなる</b>
/// ── 見るのは<b>製品が何と言ったか</b>だけにしてある。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class StopOutcomeTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>
    /// <c>ActivationCommands.ExitCode_RecordingProducedNothing</c>。
    /// E2E は製品アセンブリを参照しないので、CLI 契約の値はここで固定する
    /// （<c>src/README.md</c> の終了コード表と対）。
    /// </summary>
    private const int ExitRecordingProducedNothing = 16;

    /// <summary>
    /// <b>1フレームも書けなかった録画を「成功」として返さないこと。</b>
    ///
    /// <para>
    /// これは <c>RecordingAll</c> で断続的に出ていた <b>587 バイトの空 MP4</b> の
    /// <b>症状</b>に対する表明である。<b>原因の修正ではない</b> ── 原因は再現待ちで
    /// 未特定であり、ここで固定するのは「次に起きたときに黙って通り過ぎないこと」。
    /// </para>
    /// <para>
    /// <b>症状は設定だけで作れる。</b> <c>num-buffers</c> でソースを終わらせると、
    /// 初期化は成功したまま供給が止まる。製品側では事前バッファの排出が
    /// 「<c>appsink</c> のコールバックがサンプルを取り出したとき」にしか走らないため、
    /// リングバッファに溜まっていた分すら押し込まれず、<b>MP4 は中身無しで確定する</b>
    /// ── 排出そのものは綺麗に終わる（EOS も返り moov も書かれる）ので、
    /// この信号が無いと <b>終了コード 0 ＋ 空のファイル</b>になる。
    /// </para>
    /// </summary>
    [Fact]
    public void ARecordingThatWroteNoFrames_FailsInsteadOfReturningSuccessWithAnEmptyFile()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1").AsSourceThatEnds();

        using var instance = AppInstance.Create(app, settings);

        // 前提: 初期化は成功していること。ここが fail だと「別のテスト」になっている
        // （num-buffers が短すぎて EOS が初期化に追いついた）。
        var initial = instance.ReadActivityLog();
        Assert.Empty(ActivityLogFile.Events(initial, "recorder.init fail"));
        Assert.NotEmpty(ActivityLogFile.Events(initial, "recorder.init ok"));

        // ソースが終わるのを待つ。**固定 Sleep ではなくイベントで待つ**
        // （ランナーの速度差でどちらの向きにも壊れる）。
        Assert.True(instance.WaitForActivityLogEvent("recorder.eos", TimeSpan.FromSeconds(60)),
            "ソースが EOS に達しませんでした（num-buffers の構成が効いていない可能性があります）。"
            + Environment.NewLine + instance.DiagnosticDump());

        // 供給が止まった状態で録画を開始・停止する。開始は成功する
        // ── レコーダーは初期化済みで、状態としては録画できる。
        instance.AssertExit(0, instance.Run("start-recording", "R1"));

        var stop = instance.Run("stop-recording", "R1");
        output.WriteLine(stop.ToString());

        // **`Assert.Equal` ではなく `AssertExit`。** 前者はメッセージを取らないので、
        // 落ちたときに出るのは「Expected: 16 / Actual: 17」だけになり、
        // **なぜ 17 になったのかを知るには別途 activity.log が要る。**
        // `AssertExit` は activity.log の末尾を添えるので、
        // `recording.stop timeout ... did not drain within 5000ms` がその場で読める。
        instance.AssertExit(ExitRecordingProducedNothing, stop);
        Assert.Contains("R1", stop.StdErr);

        // ファイルのパスは出す（バッチが後始末できるように）。
        Assert.Contains(".mp4", stop.StdOut);

        var log = instance.ReadActivityLog();

        string empty = Assert.Single(ActivityLogFile.Events(log, "recording.stop empty"));
        output.WriteLine(empty);

        // **切り分けの材料が乗っていること。** 「空だった」だけでは次の1件で原因を選べない。
        // このケースはサンプルが一度も来ていない側なので seen も 0 になる。
        Assert.Contains("samplesPushed=0", empty);
        Assert.Contains("samplesSeen=0", empty);
        Assert.Contains("srcState=", empty);

        // 集計行の result も ok のままにしない。
        string summary = Assert.Single(ActivityLogFile.Events(log, "recording.stop"));
        Assert.Contains("result=empty", summary);
    }

    /// <summary>
    /// <b><c>-all</c> でも同じ答えを返すこと。</b>
    ///
    /// <para>
    /// <b>停止の経路は2つある</b>（<c>stop-recording</c> と <c>stop-recording-all</c>）。
    /// 片方だけ直すと、もう片方では <b>終了コード 0 ＋ 使えないファイル</b>が返る
    /// ── 実際に「2つの経路の片方だけを塞いだ」状態が起きたことがある。
    /// 製品側は判定を1箇所（<c>StopFailure</c>）に寄せてあるが、
    /// <b>寄せたことと両方から呼ばれていることは別</b>なので、ここで実際に叩く。
    /// </para>
    /// <para>
    /// あわせて <c>-all</c> 固有の契約も見る ── <b>1本でも壊れていれば失敗</b>であり、
    /// <b>標準エラーには壊れたレコーダーの名前が出る</b>（どれが駄目だったか分からないと、
    /// バッチは全部やり直すしかない）。
    /// </para>
    /// </summary>
    [Fact]
    public void StopRecordingAll_ReportsTheSameFailure_AndNamesTheRecorder()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1").AsSourceThatEnds();

        using var instance = AppInstance.Create(app, settings);

        Assert.True(instance.WaitForActivityLogEvent("recorder.eos", TimeSpan.FromSeconds(60)),
            "ソースが EOS に達しませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        instance.AssertExit(0, instance.Run("start-recording-all"));

        var stop = instance.Run("stop-recording-all");
        output.WriteLine(stop.ToString());

        instance.AssertExit(ExitRecordingProducedNothing, stop);
        Assert.Contains("R1", stop.StdErr);
        Assert.Contains(".mp4", stop.StdOut);
    }

    /// <summary>
    /// <b><c>-all</c> は「今回止めたレコーダー」だけを見ること。</b>
    ///
    /// <para>
    /// <c>LastStopOutcome</c> がリセットされるのは<b>次の録画開始のとき</b>だけなので、
    /// 録画していないレコーダーは前回の結果を保持し続ける。集計が全件走査だと、
    /// <b>今回は正常に録れたのに、前に失敗した別のレコーダーのせいで失敗が返る</b>
    /// ── バッチから見れば「正常なファイルを捨てろ」と言われるのと同じで、
    /// <c>16</c>（空）を返す以上、実害は「空のファイルを運ぶ」のと対称である。
    /// </para>
    /// <para>
    /// <b>レコーダーが2本要る。</b> 上の 2 件はどちらも1本構成なので、この退行を踏まない
    /// ── 1本だと「止めた集合」と「全件」がいつも一致する。
    /// </para>
    /// </summary>
    [Fact]
    public void StopRecordingAll_IgnoresTheStaleOutcomeOfARecorderItDidNotStop()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("Good");
        settings.AddRecorder("Empty").AsSourceThatEnds();

        using var instance = AppInstance.Create(app, settings);

        Assert.True(instance.WaitForActivityLogEvent("recorder.eos", TimeSpan.FromSeconds(60)),
            "ソースが EOS に達しませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        // ① Empty 側だけを録って停止し、失敗の結果を残す（ここが「古い結果」になる）。
        instance.AssertExit(0, instance.Run("start-recording", "Empty"));
        instance.AssertExit(ExitRecordingProducedNothing, instance.Run("stop-recording", "Empty"));

        // ② Good 側だけを録って、-all で停止する。止めたのは Good だけなので成功のはず。
        instance.AssertExit(0, instance.Run("start-recording", "Good"));
        Thread.Sleep(RecordingWindow);

        var stop = instance.Run("stop-recording-all");
        output.WriteLine(stop.ToString());

        instance.AssertExit(0, stop);

        // 出力に載るのも「止めた分」だけ ── 止めていないレコーダーの LastFilename は
        // 前回の（使えない）ファイルを指しており、バッチがそれを運んでしまう。
        Assert.Contains("Good", stop.StdOut);
        Assert.DoesNotContain("Empty", stop.StdOut);
    }

    /// <summary>Good 側が実際にフレームを書けるだけの録画窓（<c>RecordingTests</c> と同値）。</summary>
    private static readonly TimeSpan RecordingWindow = TimeSpan.FromSeconds(3);
}
