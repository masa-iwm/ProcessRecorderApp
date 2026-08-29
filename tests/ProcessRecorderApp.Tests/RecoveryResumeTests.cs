using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>自動復帰の作り直しで畳んだ録画を録り直す</b>配線を、ソースをテキストとして固定する。
///
/// <para>
/// 作り直し（<c>Initialize()</c>）は先頭の <c>Close()</c> で進行中の録画を確定させる
/// ── ファイルは壊れないが、録画は終わる。常時録画は <c>InitializeWith</c> の末尾で
/// 作り直されるのに<b>イベント録画だけ再開しない</b>という非対称が長らくあり、
/// ここはそれを消した配線である。
/// </para>
/// <para>
/// <b>外れても何も赤くならない。</b> 復帰は成功し、ログも正常に見え、
/// 消えるのは「録画が戻ってくること」だけである。
/// </para>
/// </summary>
public class RecoveryResumeTests
{
    private static string EventRecorderSource =>
        File.ReadAllText(RepositoryFiles.At("src", "GStreamer.GstSharpNet", "EventRecorder.cs"));

    private const string RestartLoopSignature =
        "private async System.Threading.Tasks.Task RestartLoopAsync(";

    private const string StopAsyncSignature =
        "public System.Threading.Tasks.Task StopAsync()";

    /// <summary>
    /// 作り直しの前に意図を控え、成功したら録り直すこと。
    /// <b>控えるのが先</b>でなければならない ── <c>Initialize()</c> が
    /// <c>Close()</c> を通った後では <c>_IsRecording</c> は既に false である。
    /// </summary>
    [Fact]
    public void TheRestartLoop_RemembersTheRecordingAndResumesIt()
    {
        string body = SourceMethodBody.Extract(EventRecorderSource, RestartLoopSignature);

        int armed = SourceMethodBody.IndexOfCode(body, "_resumeAfterRecovery = true;");
        Assert.True(armed >= 0,
            "作り直しの前に「録り直す」意図を控えていない。"
            + Environment.NewLine
            + "Initialize() は先頭の Close() で録画を確定させるので、控えなければ"
            + Environment.NewLine
            + "**復帰しても録画だけが戻らない**（常時録画とのあいだに非対称が戻る）。");

        int initialized = SourceMethodBody.IndexOfCode(body, "Initialize();");
        Assert.True(initialized >= 0, "復帰ループから Initialize() が消えている。");
        Assert.True(armed < initialized,
            "意図を控えるのが Initialize() より後になっている。"
            + "その時点では Close() が済んでいて _IsRecording は false なので、何も控えられない。");

        Assert.True(
            SourceMethodBody.ContainsCode(body, "ResumeRecordingIfPending();"),
            "作り直しに成功しても録り直していない。");
    }

    /// <summary>
    /// <b>停止は「録画中でないとき」にも届かなければならない。</b>
    ///
    /// <para>
    /// 作り直しのあいだは <c>_IsRecording</c> が false なので、取り消しを
    /// <c>StopAsync</c> の早期 return より後に置くと<b>停止がどこにも届かない</b>
    /// ── 利用者が止めた場合も、UiaTrigger の停止条件が立った場合も、
    /// 復帰した瞬間に録画が勝手に再開する。
    /// </para>
    /// </summary>
    [Fact]
    public void AStopDuringTheRebuild_CancelsTheResume()
    {
        string body = SourceMethodBody.Extract(EventRecorderSource, StopAsyncSignature);

        int cancel = SourceMethodBody.IndexOfCode(body, "CancelRecoveryResume(");
        Assert.True(cancel >= 0,
            "StopAsync が録り直しの意図を取り消していない。"
            + "抜けているあいだの停止が、復帰後に無視されることになる。");

        int earlyReturn = SourceMethodBody.IndexOfCode(body, "if (!_IsRecording)");
        Assert.True(earlyReturn >= 0,
            "StopAsync の早期 return が見つからない。改名したならこの検査も一緒に直すこと。");

        Assert.True(cancel < earlyReturn,
            "取り消しが早期 return より後にある。"
            + Environment.NewLine
            + "作り直しのあいだは _IsRecording が false なので、この順序では"
            + Environment.NewLine
            + "**停止が 1 行も実行されずに返る** ── UiaTrigger の停止条件も同じく無視される。");
    }

    /// <summary>
    /// <b>意図の検査から開始までは <c>_stateLock</c> の下で切れ目なく行うこと。</b>
    ///
    /// <para>
    /// ロック外で意図を読むと、<c>Initialize()</c> の握るロックを待って止まっている
    /// 停止より先に進める ── そこで <c>StopAsync</c> が入っても
    /// <c>CancelRecoveryResume</c> は空振りし、<c>_IsRecording</c> も false なので
    /// <b>1 行も実行せずに返る</b>。直後にこちらが開始するので、
    /// <b>利用者が止めた録画が戻ってくる</b>。
    /// </para>
    /// <para>
    /// 併せて<b>開始理由を畳んだ本から引き継ぐこと</b>も見る。復帰は利用者の操作ではない。
    /// </para>
    /// </summary>
    [Fact]
    public void TheResume_IsAtomicAgainstAStop()
    {
        string body = SourceMethodBody.Extract(EventRecorderSource, "private void ResumeRecordingIfPending()");

        int guard = SourceMethodBody.IndexOfCode(body, "lock (_stateLock)");
        Assert.True(guard >= 0,
            "録り直しが _stateLock の下に無い。停止と同じロックで直列化しないと、"
            + "作り直しのあいだに届いた停止を追い越して録画を再開しうる。");

        int cleared = SourceMethodBody.IndexOfCode(body, "_resumeAfterRecovery = false;");
        int started = SourceMethodBody.IndexOfCode(body, "Start(_resumeTrigger ?? \"manual\");");
        Assert.True(cleared >= 0, "録り直しの意図を降ろしていない。");
        Assert.True(started >= 0,
            "録り直しが Start(_resumeTrigger ?? \"manual\") を呼んでいない。"
            + Environment.NewLine
            + "復帰は利用者の操作ではないので、畳んだ本の理由を引き継ぐこと"
            + "── \"manual\" で置くと uia:<id> 起点の録画が sidecar でだけ理由を偽る。");
        Assert.True(guard < cleared && guard < started,
            "意図の検査・取り下げ・開始のどれかが _stateLock の外にある。"
            + "この 3 つは 1 つのロックの下で切れ目なく行うこと。");
    }

    /// <summary>
    /// 録画が始まったら意図は消えること（二重開始の防止はここが担う）。
    /// </summary>
    [Fact]
    public void StartingARecording_ClearsThePendingResume()
    {
        string body = SourceMethodBody.Extract(EventRecorderSource, "private void StartCore(string trigger)");

        Assert.True(
            SourceMethodBody.ContainsCode(body, "_resumeAfterRecovery = false;"),
            "録画を開始しても録り直しの意図が残る。"
            + "利用者が作り直し直後に自分で開始した場合に、復帰側がもう一度開始しうる。");
    }
}
