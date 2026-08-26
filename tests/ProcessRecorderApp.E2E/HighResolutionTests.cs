using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <b>高解像度でパイプラインが動き出すこと。</b>
///
/// <para>
/// 実機（4K モニタ・<c>Type=D3d12</c> + <c>qsvh264enc</c>）で
/// 「<c>IsInitialized=on</c> / <c>LastError=null</c> なのに録画もプレビューも
/// 1フレームも進まない」という報告があった。原因は解像度依存の
/// <b>デッドロック</b>で、エンコーダーでもベンダーでも GPU でもない:
/// </para>
/// <list type="number">
///   <item><description>
///     プレビュー枝の <c>queue</c> は既定のまま（<c>max-size-bytes=10485760</c>）。
///     4K の1フレームは 12〜13MB あるので、この queue は<b>1フレームで満杯</b>になる。
///   </description></item>
///   <item><description>
///     プレビューの <c>appsink</c> は <c>PAUSED</c> の間プリロールで止まっているので
///     queue は排出されず、満杯の queue が <c>tee</c> を止める。
///   </description></item>
///   <item><description>
///     <c>tee</c> が止まるとエンコーダー枝にもフレームが来ない。ハードウェアエンコーダーは
///     最初の1フレームを出すまでに数フレーム溜めるので、<b>出力が1つも出ない</b>。
///   </description></item>
///   <item><description>
///     録画側 <c>appsink name=sink</c> がプリロールできないのでパイプラインは
///     <c>PAUSED</c> のまま <c>PLAYING</c> に到達せず、プレビューの <c>appsink</c> も
///     ずっと止まったまま ── <b>1 に戻る。循環待ちで、自然に解けることはない。</b>
///   </description></item>
/// </list>
/// <para>
/// <b>解像度を上げただけで壊れるので、開発機の 320x240 では永久に踏まない。</b>
/// GPU 無しの開発機で解像度だけを変え、他は同一の条件で測った閾値:
/// 1920x1080 は 0.41 秒で <c>PLAYING</c>、<b>2560x1440 と 3840x2160 は 15 秒経っても到達しない。</b>
/// </para>
/// <para>
/// <b>GPU は要らない。</b> <c>Type=System</c> でも同じ形（<c>tee</c> の先に
/// 既定の <c>queue</c> とプリロール待ちの <c>appsink</c>）なので、
/// このテストは GPU の無い CI ランナーでも実際に退行を検出する。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class HighResolutionTests(PublishedApp app, ITestOutputHelper output)
{
    private static readonly TimeSpan RecordingWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 1フレームがプレビュー枝の <c>queue</c> の上限を超える解像度でも、
    /// 初期化が成立し、実際に録画できること。
    /// </summary>
    [Fact]
    public void AResolutionWhoseFramesFillThePreviewQueue_StillInitializesAndRecords()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1").AsOversizedFrames();

        using var instance = AppInstance.Create(app, settings);

        // 初期化そのものが成立していること。
        // 退行するとここが recorder.init fail（PLAYING へ到達しない）になる。
        var log = instance.ReadActivityLog();
        Assert.Empty(ActivityLogFile.Events(log, "recorder.init fail"));
        Assert.Single(ActivityLogFile.Events(log, "recorder.init ok"));

        // 「初期化できた」だけでは足りない ── 実機で報告された症状はまさに
        // 「初期化は成功しているのに1フレームも流れない」だった。
        // 実際にフレームが最後まで通ることを、生成された MP4 で確かめる。
        var start = instance.AssertExit(0, instance.Run("start-recording-all"));
        output.WriteLine(start.StdOut);

        Thread.Sleep(RecordingWindow);

        instance.AssertExit(0, instance.Run("stop-recording-all"));

        string file = Assert.Single(instance.ListRecordings());
        RecordedMp4.AssertUsable(file, instance, output);

        log = instance.ReadActivityLog();
        Assert.Single(ActivityLogFile.Events(log, "recording.start"));
        Assert.All(ActivityLogFile.Events(log, "recording.stop"), l => Assert.Contains("result=ok", l));
        Assert.Empty(ActivityLogFile.Events(log, "recording.start fail"));
        Assert.Empty(ActivityLogFile.Events(log, "app.error"));
    }

    /// <summary>
    /// <b>「初期化は成功したが何も流れない」を成功として報告しないこと。</b>
    ///
    /// <para>
    /// 上のテストは<b>直った</b>ことを見るもので、<c>WaitUntilPlaying</c> を外しても緑のまま
    /// ── プレビュー枝の queue が直っている以上パイプラインは <c>PLAYING</c> に達するので、
    /// <b>検出器そのものは1件も守られない。</b> こちらがその穴を塞ぐ。
    /// </para>
    /// <para>
    /// <c>identity drop-probability=1.0</c> は caps を通してバッファだけを捨てるので、
    /// <c>ParseLaunch</c> もリンクも <c>SetState(Playing)</c> も成功する
    /// ── <b>実機で報告された 4K 停止の外見（<c>IsInitialized=on</c> / エラー無し /
    /// 何も流れない）を決定的に再現できる唯一の形。</b>
    /// </para>
    /// <para>
    /// <b>この失敗は「うるさく」あるべき。</b> 黙って初期化済みを名乗ると、
    /// 利用者は録画できないことに気付かないまま使い続ける
    /// ── 実機で実際にそうなった。
    /// </para>
    /// </summary>
    [Fact]
    public void ASourceThatNeverDeliversAFrame_FailsInitializationInsteadOfClaimingSuccess()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1").AsSilentSource();

        using var instance = AppInstance.Create(app, settings);

        var log = instance.ReadActivityLog();

        string failure = Assert.Single(ActivityLogFile.Events(log, "recorder.init fail"));
        output.WriteLine(failure);

        // 失敗の理由まで見る。「何かに失敗した」だけでは、リンク失敗や要素の欠落と
        // 区別が付かない ── ここで守りたいのは「PLAYING に到達しないこと」の検出。
        Assert.Contains("never reached PLAYING", failure);

        Assert.Empty(ActivityLogFile.Events(log, "recorder.init ok"));

        // 初期化できていないのだから、録画は始まらない（黙って成功してはいけない）。
        // 具体的な終了コードはここでは固定しない ── 守りたいのは「0 を返さないこと」で、
        // どの失敗コードになるかは CLI 側の契約（CliContractTests の担当）。
        var start = instance.Run("start-recording-all");
        output.WriteLine(start.ToString());
        Assert.NotEqual(0, start.ExitCode);
        Assert.Empty(instance.ListRecordings());
    }
}
