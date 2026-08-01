using System.Text.Json;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L3: ウィンドウを閉じる経路（トレイ格納と正常終了）。
///
/// <para>
/// <c>AppWindow.Destroying</c> → <c>Save()</c> → <c>engine.Dispose()</c> は
/// 終了処理で最も順序に敏感なコードだが、<c>Stop-Process -Force</c> による強制終了では
/// 一切走らない（強制終了だけで検証していると、ここが壊れていても気付けない）。
/// 到達経路は Ctrl+閉じる かトレイの「終了」だけで、どちらも GUI 操作なので L2 では届かない。
/// </para>
///
/// <para>
/// <b><c>app.exit exitCode=0</c> だけを見てはいけない。</b>
/// <c>App_UnhandledException</c> が <c>e.Handled = true</c> を立てるので、
/// <c>Destroying</c> ハンドラの中で例外が出ても<b>プロセスは 0 で終了し
/// <c>app.exit</c> も書かれる</b>。必ず <c>app.error</c> が無いことをセットで確認する。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class ShutdownTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>
    /// 終了を待つ予算。排出（<c>mp4mux faststart</c>）を含むので短くしない。
    ///
    /// <para>
    /// <b>これは「下限の表明」ではなく打ち切りなので、厚く取る。</b>
    /// <c>CtrlClose_WhileRecording_FinalizesEveryFile</c> は約 20Mbit を 10 秒録ってから
    /// 終了するため、終了経路で <b>約 30MB のファイル全体の書き直し</b>が走る
    /// ── vCPU の少ない CI ランナーでは、この開発機の何倍もかかりうる。
    /// ここで打ち切ると失敗メッセージが「排出でハングした可能性」となり、
    /// <b>製品のハングだと誤読させる</b>（＝何も分からないまま1サイクル失う）。
    /// </para>
    /// <para>
    /// <b>vCPU の少ない CI ランナーでは 180 秒では届かない</b>
    /// （排出が進んでいても、打ち切りだけが先に来る）。
    /// ここは打ち切り（異常検出の上限）なので厚く取ってよい ── 420 秒にしてある。
    /// <b>これは「テストを甘くした」のではない</b> ── ここは上限であって、
    /// 検出したい退行（排出を待たずに終了する）は<b>ファイルが壊れる形で</b>現れる。
    /// 対照的に <c>StopSynchronicityTests</c> の 20MB は<b>下限</b>なので緩めてはいけない。
    /// </para>
    /// </summary>
    private static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(420);

    /// <summary>
    /// Ctrl+閉じる で正常終了する。破棄パスが例外なく走り、設定が保存されることまで見る。
    ///
    /// <para>
    /// <b>settings.json の中身まで確認するのには理由がある。</b>
    /// <c>Destroying</c> は <c>Save()</c> の<b>後で</b> <c>engine.Dispose()</c> を呼ぶ。
    /// もしページ破棄側がエンジンを先に壊すと、<c>Save()</c> は空になった
    /// <c>Recorders</c> を書き出しうる ── 終了コードにもログにも出ない壊れ方なので、
    /// 保存されたファイルの中身を直接見る。
    /// </para>
    /// </summary>
    [Fact]
    public void CtrlClose_ExitsCleanly_AndSavesTheSettings()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");
        settings.AddRecorder("R2");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.CloseWindow(holdControl: true);

        Assert.True(ui.WaitForProcessExit(ExitBudget),
            "Ctrl+閉じる でプロセスが終了しませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        var log = instance.ReadActivityLog();
        Assert.NotEmpty(ActivityLogFile.Events(log, "app.exit"));
        AssertNoAppError(instance, log);

        // 保存された設定にレコーダーが残っていること（破棄順序が壊れていない証拠）。
        var saved = ReadRecorderNames(instance);
        Assert.Equal(["R1", "R2"], saved);
    }

    /// <summary>
    /// 録画中に Ctrl+閉じる。終了経路の排出は同期のままなので、ここが最も危ない組み合わせ。
    /// ハングせず、かつ MP4 が確定していることを見る。
    ///
    /// <para>
    /// <b>1本は大きく録る。</b> <c>mp4mux faststart=true</c> は EOS 後にファイル全体を
    /// 書き直すので、排出コストはファイルサイズにほぼ比例する ── 小さい録画だけだと、
    /// 終了経路の排出をプールへ逃がす退行を入れても<b>排出がレースに勝ってしまい、
    /// 何も検出しない</b>（注入して実測。320x240/3秒では検出できなかった）。
    /// L2 の「停止の同期性」で先に踏んだのとまったく同じ性質で、こちらでも同じ手当てが要る。
    /// </para>
    /// <para>
    /// <b>ただし「大きく」＝「重く」ではない。</b> ここは<b>このスイートで唯一、
    /// 録画しながら GUI を操作する</b>ケースで、<see cref="RecorderSpec.AsLarge"/>
    /// （1280x720/30fps）だと GPU の無い 2 vCPU のランナーで UI スレッドが飢え、
    /// <b>UIA の要素が 0 件</b>になるところまで応答しなくなった。
    /// <see cref="RecorderSpec.AsBulkyButCheapToEncode"/> は<b>バイト数と排出時間を据え置いたまま</b>
    /// 画素数だけを落とす ── つまり<b>検出力は下げずに負荷だけを下げている</b>
    /// （較正の実測値は <see cref="SettingsFile.BulkyCheapVideoTestSrc"/>）。
    /// </para>
    /// </summary>
    [Fact]
    public void CtrlClose_WhileRecording_FinalizesEveryFile()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1").AsBulkyButCheapToEncode();
        settings.AddRecorder("R2");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        instance.RunExpecting(0, "start-recording-all");
        // 大きい方が「排出に時間がかかる」状態になるまで録る。
        Thread.Sleep(10_000);

        ui.CloseWindow(holdControl: true);

        Assert.True(ui.WaitForProcessExit(ExitBudget),
            $"録画中の Ctrl+閉じる でプロセスが {ExitBudget.TotalSeconds:F0} 秒以内に終了しませんでした。" +
            "**製品のハングと決めつけないこと** ── ここは約 20Mbit を 10 秒録ってから" +
            "終了するので、終了経路で約 30MB のファイル全体の書き直しが走る。" +
            "vCPU の少ないランナーでは単に遅いだけのことがある（activity.log に " +
            "`recording.stop` が出ているかで切り分ける）。" +
            Environment.NewLine + instance.DiagnosticDump());

        var log = instance.ReadActivityLog();
        Assert.NotEmpty(ActivityLogFile.Events(log, "app.exit"));
        AssertNoAppError(instance, log);

        // 2件とも正常に停止として記録されていること
        Assert.Equal(2, ActivityLogFile.Events(log, "recording.stop").Count);

        var recordings = instance.ListRecordings();
        Assert.Equal(2, recordings.Count);
        foreach (string path in recordings)
            RecordedMp4.AssertUsable(path, instance, output);
    }

    /// <summary>
    /// Ctrl を押さずに閉じるボタン → トレイ格納。プロセスは生き続け、
    /// <c>activate</c> で<b>同じ pid</b> のウィンドウが戻る。
    ///
    /// <para>
    /// これは実バグの回帰テスト ── 判定が <c>!= CoreVirtualKeyStates.None</c> だった頃、
    /// Ctrl に触れていなくても <c>Locked</c> が報告される環境（切断中の RDP セッション）で
    /// <b>ウィンドウを閉じると常駐バッファリングごとプロセスが落ちていた</b>。
    /// 規則そのものは L1（<c>CloseToTrayTests</c>）が守るが、
    /// <b>その規則が実際に配線されていること</b>はここでしか確認できない。
    /// </para>
    /// </summary>
    [Fact]
    public void CloseWithoutCtrl_HidesToTray_AndTheProcessKeepsRunning()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);
        int pid = ui.ProcessId;

        ui.CloseWindow(holdControl: false);

        // 「終了しないこと」を待つのではなく、格納されたことを確かめてから生存を見る。
        Assert.True(ui.WaitUntilWindowIsGone(TimeSpan.FromSeconds(20)),
            "閉じるボタンでウィンドウが消えませんでした。" + Environment.NewLine + instance.DiagnosticDump());
        Assert.True(ui.IsProcessAlive,
            "閉じるボタン（Ctrl なし）でプロセスが終了しました ── トレイ格納されるはずです。" +
            Environment.NewLine + instance.DiagnosticDump());

        // 常駐しているので CLI は引き続き応答する（＝常時バッファリングが生きている）。
        Assert.Equal(0, instance.Run("ping").ExitCode);

        // activate で戻ってくるのは同じプロセスであること。
        using var again = AppUi.Activate(instance);
        Assert.Equal(pid, again.ProcessId);

        var log = instance.ReadActivityLog();
        Assert.Empty(ActivityLogFile.Events(log, "app.exit"));
        AssertNoAppError(instance, log);
    }

    private static void AssertNoAppError(AppInstance instance, IReadOnlyList<string> log)
    {
        var errors = ActivityLogFile.Events(log, "app.error");
        Assert.True(errors.Count == 0,
            "破棄パスで例外が握り潰されています（app.error が記録されました）:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors) + Environment.NewLine + instance.DiagnosticDump());
    }

    private static string[] ReadRecorderNames(AppInstance instance)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(instance.SettingsPath));
        if (!document.RootElement.TryGetProperty("Recorders", out var recorders))
            return [];
        return [.. recorders.EnumerateArray()
            .Select(r => r.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "")];
    }
}
