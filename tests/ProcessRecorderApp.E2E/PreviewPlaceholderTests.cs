using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// <b>未初期化のレコーダーを選んだときに、前のレコーダーの映像が残らないこと。</b>
///
/// <para>
/// スワップチェーンは<b>プロセス内で1面を共有</b>しており、フレームを push するのは
/// 選択中のレコーダーだけ（<c>Controller.GstEventRecorder_Preview</c> が
/// <c>SelectedRecorder</c> で絞る）。したがって初期化に失敗したレコーダーへ切り替えると
/// <b>誰も push しなくなり、前のレコーダーの最後の画がそのまま残り続ける</b>
/// ── 利用者からは「別のレコーダーの映像が映っている」ようにしか見えない。
/// </para>
/// <para>
/// <b>重ねて隠すのではなく畳んだのは、この層で確かめられるようにするため。</b>
/// 半透明のオーバーレイ・z 順の誤り・サイズ 0 は、どれも
/// 「プレースホルダーが UIA に見える」だけの表明では<b>全部緑になる</b>
/// ── この repo はプロパティ編集の往復表明とトレイのツールチップで同じ形に2度やられている。
/// 畳めば <c>PreviewSurface</c> が UIA ツリーから消えるので、
/// <b>隠れていること自体</b>を表明できる。
/// </para>
/// <para>
/// <b>両方向を見る。</b> 片方向だけだと「常に出る」「常に出ない」プレースホルダーが通る。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class PreviewPlaceholderTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>
    /// 初期化に必ず失敗するレコーダー。<b>存在しない要素名</b>を使うので
    /// <c>ParseLaunch</c> の時点で落ちる ── 速いうえに、GPU の有無にも
    /// エンコーダーの顔ぶれにも依存しない。
    ///
    /// <para>
    /// <c>EncodingProperties</c> を明示するのは<b>候補の数だけ試行を繰り返さないため</b>
    /// （自動選択にすると存在するエンコーダーの数だけ同じ失敗を繰り返す）。
    /// </para>
    /// </summary>
    private const string BrokenSrcPipeline =
        "videotestsrc is-live=true do-timestamp=true ! nosuchelement_n3 ! " +
        "video/x-raw,format=I420,width=320,height=240,framerate=15/1";

    [Fact]
    public void SelectingAnUninitializedRecorder_ReplacesThePreviewWithAnExplicitMessage()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("Healthy");
        var broken = settings.AddRecorder("Broken");
        broken.SrcPipeline = BrokenSrcPipeline;
        broken.EncodingProperties = SettingsFile.DefaultEncoder;

        using var instance = AppInstance.Create(app, settings, language: "en-US");

        // 前提の確認。ここが崩れているとこのテストは何も検証しない
        // ── 「壊れたレコーダー」が実は初期化できていたら、以降の表明は
        // 単に初期化済み同士の切り替えを見ているだけになる。
        var log = instance.ReadActivityLog();
        Assert.Single(ActivityLogFile.Events(log, "recorder.init ok"));
        Assert.Single(ActivityLogFile.Events(log, "recorder.init fail"));

        using var ui = AppUi.Activate(instance);

        // ---- 初期化済みを選ぶ: プレビュー面が出て、プレースホルダーは出ない ----
        ui.SelectRecorder("Healthy");
        Assert.NotNull(ui.TryFindElement("PreviewSurface"));
        Assert.True(ui.WaitUntilGone("NoPreviewText"),
            "初期化済みのレコーダーを選んでいるのに「表示するものが無い」が出たままです。"
            + Environment.NewLine + instance.DiagnosticDump());

        // ---- 未初期化を選ぶ: プレースホルダーが出て、プレビュー面は消える ----
        ui.SelectRecorder("Broken");

        var placeholder = ui.WaitForElement("NoPreviewText");
        output.WriteLine($"placeholder name: {placeholder.Name}");

        // 文言は resw から取る（訳文をテストへ焼き込まない）。
        Assert.Equal(UiResources.Get("en-US", "MainPage_NoPreview.Text"), placeholder.Name);

        // **これがこのテストの本体。** プレースホルダーが出ているだけでは、
        // 前のレコーダーの画がその裏に残っているかどうかは何も分からない。
        Assert.True(ui.WaitUntilGone("PreviewSurface"),
            "未初期化のレコーダーを選んだのにプレビュー面が残っています"
            + "（＝前のレコーダーの最後の画が映り続けます）。"
            + Environment.NewLine + instance.DiagnosticDump());

        // ---- 戻す: 元に戻ること（片道だけの修正になっていないこと） ----
        ui.SelectRecorder("Healthy");
        Assert.NotNull(ui.WaitForElement("PreviewSurface"));
        Assert.True(ui.WaitUntilGone("NoPreviewText"),
            "初期化済みのレコーダーへ戻したのに「表示するものが無い」が残っています。"
            + Environment.NewLine + instance.DiagnosticDump());
    }
}
