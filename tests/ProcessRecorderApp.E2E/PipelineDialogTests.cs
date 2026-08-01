using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L3: SrcPipeline の編集支援ダイアログ
/// （「初期化が途中で失敗すると破棄済みオブジェクトを再度触る」不具合の回帰テスト）。
///
/// <para>
/// <b>このダイアログはその不具合への唯一の到達経路。</b> <c>Close()</c> が破棄したフィールドを
/// null 化しないと、<c>Initialize()</c> が途中で失敗したときに catch 内の <c>Close()</c> が
/// 破棄済みオブジェクトを再度触る（<c>Close()</c> は冪等にしてある）。その状況を作れるのは
/// 「不正なパイプライン文字列を入れて OK する」操作だけで、CLI からは到達できない。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class PipelineDialogTests(PublishedApp app)
{
    /// <summary>「…」ボタンの AutomationId（<c>PropertyGridItem.BuilderButtonAutomationId</c>）。</summary>
    private const string BuilderButton = "SrcPipeline.Build";

    /// <summary>
    /// ダイアログで生成した文字列が、OK でモデルまで反映されること。
    /// キャンセルしたときは反映されないこと。
    /// </summary>
    [Fact]
    public void TheDialogAppliesOnOk_AndLeavesThePipelineAloneOnCancel()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        string original = ui.GetPropertyText("SrcPipeline");
        Assert.Contains("videotestsrc", original);

        // ---- キャンセルでは変わらない ----
        ui.Click(BuilderButton);
        // 現在のパイプラインが読み込まれた状態で開くこと
        Assert.Contains("videotestsrc", ui.GetText("PipelineGeneratedText"));
        ui.CancelDialog();
        Assert.Equal(original, ui.GetPropertyText("SrcPipeline"));

        // ---- OK で反映される ----
        // 同じ videotestsrc のまま pattern だけ変える（確実に初期化できる別の文字列）。
        string edited = original.Replace("videotestsrc is-live=true", "videotestsrc is-live=true pattern=1");
        Assert.NotEqual(original, edited);

        ui.Click(BuilderButton);
        ui.SetText("PipelineGeneratedText", edited);
        ui.ConfirmDialog();

        Assert.Equal(edited, ui.WaitForPropertyText("SrcPipeline", edited));

        // モデルまで届いていること ── 実際に録画できる。
        var start = instance.Run("start-recording", "R1");
        Assert.Equal(0, start.ExitCode);
        Thread.Sleep(1500);
        Assert.Equal(0, instance.Run("stop-recording", "R1").ExitCode);
        Assert.Single(instance.ListRecordings());

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// 不正なパイプライン文字列を入れて OK しても
    /// クラッシュせず、<c>LastError</c> に理由が出て、<b>直せば復帰できる</b>こと。
    ///
    /// <para>
    /// <b>1回だけ試すのでは足りない。</b> これは「破棄済みのフィールドを<b>再度</b>触る」
    /// 問題なので、初期化の失敗を挟んでから**もう一度初期化する**ところまで通す必要がある。
    /// ここでは 不正 → 不正 → 正常 と3回初期化させている。
    /// </para>
    /// </summary>
    [Fact]
    public void AnInvalidPipeline_DoesNotCrash_AndTheRecorderRecoversWhenFixed()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        string original = ui.GetPropertyText("SrcPipeline");

        // ---- 1回目: 存在しない要素（**ParseLaunch の時点で**失敗する） ----
        // この経路ではオブジェクトが1つも作られないので、破棄すべきものも無い。
        ApplyPipeline(ui, "no-such-element-xyz ! videoconvert");
        AssertStillAlive(ui, instance, "存在しない要素を入れた直後");

        // ---- 2回目: **パースは通るが再生開始で失敗する** ----
        // ここで狙うのは「Initialize() が**途中で**失敗した」状態なので、
        // パース時点で落ちる入力では不十分 ── 要素が生成・リンクされた後で失敗させる必要がある。
        //
        // 満たせない caps（width=99999999 など）では**足りない**。gst_parse_launch は
        // リンクをその場で解決するので `could not link ...` として**パースの時点で**落ちる
        // （実測で確認した）。
        // filesrc の src パッドは ANY なのでリンクは通り、**ファイルを開けないのは
        // 状態遷移のとき**なので、この形なら生成済みの要素を抱えたまま catch へ入る。
        int before = SettledCandidateFailureCount(instance);
        ApplyPipeline(ui, InvalidAtStateChange("no-such-file-1"));
        AssertFailedAtStateChange(instance, before);
        AssertStillAlive(ui, instance, "再生開始で失敗する入力を入れた直後");

        // ---- 3回目: 同じ失敗をもう一度（＝破棄済みのフィールドを再度触らせる） ----
        before = SettledCandidateFailureCount(instance);
        ApplyPipeline(ui, InvalidAtStateChange("no-such-file-2"));
        AssertFailedAtStateChange(instance, before);
        AssertStillAlive(ui, instance, "失敗する初期化を2回続けた直後");

        // 失敗が UI から見えること（LastError の表示経路）。
        Assert.False(string.IsNullOrWhiteSpace(ui.GetPropertyText("LastError")),
            "不正なパイプラインを適用したのに LastError が空です。" + Environment.NewLine +
            instance.DiagnosticDump());

        // ---- 4回目: 元に戻すと復帰できる ----
        ApplyPipeline(ui, original);
        AssertStillAlive(ui, instance, "正しいパイプラインへ戻した直後");

        var start = instance.Run("start-recording", "R1");
        Assert.Equal(0, start.ExitCode);
        Thread.Sleep(1500);
        Assert.Equal(0, instance.Run("stop-recording", "R1").ExitCode);

        var recordings = instance.ListRecordings();
        Assert.Single(recordings);
        RecordedMp4.AssertUsable(recordings[0], instance);
    }

    /// <summary>
    /// パースとリンクは成功するが、<b>状態遷移で</b>失敗するパイプライン。
    /// <c>filesrc</c> の src パッドは ANY なのでリンクは通り、
    /// 存在しないファイルは READY への遷移で初めて失敗する。
    /// gst_parse_launch ではバックスラッシュがエスケープ文字なので、パスはスラッシュで書く。
    /// </summary>
    private static string InvalidAtStateChange(string stem)
        => $"filesrc location=C:/no-such-directory-for-e2e/{stem}.raw ! videoconvert";

    private static IReadOnlyList<string> CandidateFailures(AppInstance instance)
        => ActivityLogFile.Events(instance.ReadActivityLog(), "gst.encoder candidate-failed");

    /// <summary>
    /// 前の適用ぶんの失敗が<b>出切ってから</b>件数を取る。
    ///
    /// <para>
    /// <b>1回の適用で <c>Initialize()</c> は6候補ぶん試行され、実測で7秒近くかかる。</b>
    /// 出切る前に基準の件数を取ると、前の適用の失敗が次の窓に流れ込み、
    /// 「状態遷移で失敗させるつもりが別の理由で失敗している」という**誤った診断**が出る
    /// ── しかもその文言は<b>入力のせいだと読める</b>ので、時計の問題だと気づきにくい。
    /// </para>
    /// <para>
    /// <b>安定判定の窓は、候補と候補の間隔より十分に広く取る。</b>
    /// この開発機の実測では連続する失敗の最大間隔が 1.72 秒（7秒で6件）。
    /// vCPU の少ない CI ランナーはソフトウェアエンコードでさらに遅くなるので、
    /// ここを 2 秒程度にすると<b>途中で「出切った」と誤判定する</b>。
    /// 「6件になるまで待つ」形にはしない ── 候補の件数を焼き込むことになる。
    /// </para>
    /// </summary>
    private static int SettledCandidateFailureCount(AppInstance instance)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        int last = CandidateFailures(instance).Count;
        var stableSince = System.Diagnostics.Stopwatch.StartNew();

        while (deadline.Elapsed < TimeSpan.FromSeconds(90))
        {
            Thread.Sleep(300);
            int now = CandidateFailures(instance).Count;
            if (now != last)
            {
                last = now;
                stableSince.Restart();
                continue;
            }
            if (stableSince.Elapsed > TimeSpan.FromSeconds(8))
                return now;
        }
        return last;
    }

    /// <summary>
    /// 直前の適用が<b>状態遷移で</b>失敗したことを表明する。
    ///
    /// <para>
    /// <b>この表明が無いと、テストは緑のまま意図した経路を一度も通らなくなる。</b>
    /// 入力が少しでも変わって <c>gst_parse_launch</c> の時点で落ちるようになると
    /// （パスが実在してしまった・sink 側のテンプレートが変わってリンクしなくなった等）、
    /// 要素が生成されないまま終わるので「初期化が途中で失敗した」状態に届かない。
    /// このスイートが狙うのはまさにその状態なので、ここで段を固定する。
    /// </para>
    /// <para>
    /// 判定は ASCII だけで行う（GStreamer の理由文はロケール非依存の英語）。
    /// </para>
    /// </summary>
    private static void AssertFailedAtStateChange(AppInstance instance, int beforeCount)
    {
        // **固定の sleep のあとに1回だけ数えてはいけない。** 候補6件の試行は実測で
        // 7 秒近くかかり、`ApplyPipeline` の 1.5 秒では1件も出ていないことがある
        // （実際にそれで落ちた）。新しい失敗が現れるまで有界で待つ ──
        // 現れた時点で即座に進むので、待つこと自体は遅くならない。
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        List<string> added;
        while (true)
        {
            added = [.. CandidateFailures(instance).Skip(beforeCount)];
            if (added.Count > 0 || deadline.Elapsed > TimeSpan.FromSeconds(30))
                break;
            Thread.Sleep(300);
        }

        Assert.True(added.Count > 0,
            "エンコーダー候補の失敗が1件も記録されていません（初期化が試みられていない）。" +
            Environment.NewLine + instance.DiagnosticDump());

        foreach (string line in added)
        {
            string detail = ActivityLogFile.DetailOf(line);

            Assert.True(detail.Contains("doesn't want to play", StringComparison.Ordinal),
                "状態遷移で失敗させるつもりの入力が、別の理由で失敗しています: " + detail +
                Environment.NewLine + instance.DiagnosticDump());

            Assert.False(detail.Contains("could not link", StringComparison.Ordinal),
                "パース時点（リンク解決）で落ちています ── 要素が生成されないので " +
                "「初期化が途中で失敗した」状態に届いていません: " + detail +
                Environment.NewLine + instance.DiagnosticDump());
        }
    }

    private static void ApplyPipeline(AppUi ui, string pipeline)
    {
        ui.Click(BuilderButton);
        ui.SetText("PipelineGeneratedText", pipeline);
        ui.ConfirmDialog();
        // 初期化（成功・失敗どちらでも）が終わるだけの猶予を取る。
        Thread.Sleep(1500);
    }

    /// <summary>
    /// プロセスが生きていて、UI もまだ応答すること。
    /// <b>プロセスの生存だけでは足りない</b> ── 二重破棄はネイティブ側で起きるので、
    /// UI スレッドが死んでいないことまで見る（要素が引けるか）。
    /// </summary>
    private static void AssertStillAlive(AppUi ui, AppInstance instance, string step)
    {
        Assert.True(ui.IsProcessAlive,
            $"{step}にプロセスが落ちました。" + Environment.NewLine + instance.DiagnosticDump());

        Assert.NotNull(ui.TryFindElement("SrcPipeline", TimeSpan.FromSeconds(20)));

        // CLI も応答すること（常駐ワーカーの UI スレッドが生きている証拠）。
        Assert.Equal(0, instance.Run("ping").ExitCode);

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }
}
