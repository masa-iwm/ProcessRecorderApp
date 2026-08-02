using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L3: Settings 画面の「再読み込み」ボタン（<c>AppSettings.Reload()</c> の唯一の呼び出し元）。
///
/// <para>
/// <b>この層でしか押さえられない。</b> <c>Reload()</c> は WinUI アプリプロジェクトにあり、
/// L1 のテストプロジェクトは参照していない。CLI にも同等のコマンドは無いので、
/// 「読み直しが実際に画面へ届くこと」を見られるのはここだけ。
/// L1 の <c>AppSettingsReloadTests</c> が見るのは「全プロパティをコピーし忘れていないこと」
/// （ソースのテキスト照合）で、対象が違う。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class SettingsReloadTests(PublishedApp app)
{
    /// <summary>デバウンス保存（約1秒）が書き切るまでの待ち。</summary>
    private static readonly TimeSpan DebounceMargin = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 手で書き換えた settings.json が、再起動せずに画面へ反映されること。
    ///
    /// <para>
    /// <b>外から書き換える前に、アプリ側の自動保存が静まるのを待つ。</b> 常駐ワーカーは
    /// 変更から約1秒のデバウンスで settings.json を丸ごと書き直すので、
    /// ウィンドウの表示や画面切り替えに伴う保存が後から降ってくると、
    /// <b>こちらの書き換えが黙って消える</b> ── そうなると「反映されなかった」赤なのか
    /// 「そもそも書けていない」赤なのか区別がつかない。
    /// </para>
    /// <para>
    /// 対象に <c>GstDebug</c> を選んでいるのは、<b>アプリが自分で書き換えない</b>値だから
    /// （起動時に環境変数へ写すだけ）。アプリ側が触る値だと、反映を見ているのか
    /// 上書きを見ているのか分からなくなる。
    /// </para>
    /// </summary>
    [Fact]
    public void ReloadingPicksUpAnEditMadeOutsideTheApp()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);
        Assert.Equal("", ui.WaitForPropertyText("GstDebug", ""));

        // 起動・表示に伴う保存を出し切らせてから外部の書き換えを行う（上のコメント参照）。
        Thread.Sleep(DebounceMargin);

        const string edited = "GST_TRACER:3";
        instance.Settings.GstDebug = edited;
        instance.WriteSettings();

        ui.Click("ReloadSettingsButton");
        ui.ConfirmDialog();

        // **画面が Settings のままであることも見る。** 再読み込みはレコーダーを総入れ替えするので、
        // ナビが新しいレコーダー項目を選び直すと、その副作用で Preview へ飛ぶ
        // （RecorderNavViewBehavior.ApplySelectedRecorder のガードが外れると再発する）。
        // 押したのは Settings のボタンなので、画面が変わっては困る
        // ── ここが崩れると下の行は「行が見つからない」で落ちるが、
        // 理由がボタンではなくナビ側にあることが分からなくなる。
        Assert.Equal(edited, ui.WaitForPropertyText("GstDebug", edited));
        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// 録画中は押せないこと。
    ///
    /// <para>
    /// <b>この禁止が無いと、動いているエンジンがその場で破棄される</b>
    /// ── <c>Reload()</c> は <c>Recorders</c> を <c>Clear()</c> してから詰め直すので、
    /// UI スレッドが排出の完了（最大 <c>StopFinalizeTimeoutMs</c> × レコーダー数）まで固まる。
    /// </para>
    /// <para>
    /// <b>停止した「直後」まで見る。</b> <c>IsRecording</c> は停止を受け付けた時点で
    /// 同期的に false になるが、その後の排出中もパイプラインは生きている
    /// ── 有効判定を <c>IsRecording</c> の否定に戻す退行は、ここでしか捕まらない。
    /// </para>
    /// </summary>
    [Fact]
    public void ReloadingIsBlockedWhileRecording()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SwitchTo(UiSection.Settings);
        Assert.True(WaitForReloadEnabled(ui, true), "録画していないのにボタンが無効のままです。");

        instance.RunExpecting(0, "start-recording-all");
        Assert.True(WaitForReloadEnabled(ui, false), "録画中なのにボタンが有効のままです。");

        // 停止は排出の完了まで待つ（CLI の stop-recording-all は同期）。
        instance.RunExpecting(0, "stop-recording-all");
        Assert.True(WaitForReloadEnabled(ui, true), "排出が終わってもボタンが無効のままです。");

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// ボタンの有効/無効が期待どおりになるまで待つ。
    /// <b>固定の sleep で読まない</b> ── 録画状態の伝播は
    /// 「レコーダー VM → GstControllerViewModel → MainPageViewModel →
    /// CanExecuteChanged → Button.IsEnabled」と非同期に伝わる。
    /// </summary>
    private static bool WaitForReloadEnabled(AppUi ui, bool expected)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            if (ui.WaitForElement("ReloadSettingsButton").IsEnabled == expected)
                return true;
            Thread.Sleep(150);
        }
        while (deadline.Elapsed < TimeSpan.FromSeconds(15));
        return false;
    }
}
