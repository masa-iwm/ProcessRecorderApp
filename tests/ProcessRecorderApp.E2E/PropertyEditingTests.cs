using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L3: PropertyGrid での編集（実際にあった不具合2件の回帰テスト）。
///
/// <para>
/// <b>この層でしか押さえられない。</b> PropertyGrid の書き戻し
/// （<c>GstEventRecorderViewModel</c> の各セッター）は CLI からは到達できず、
/// L1 は WinUI アプリプロジェクトを参照していないため実行できない。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class PropertyEditingTests(PublishedApp app)
{
    /// <summary>
    /// 書き戻しガードが <c>==</c>（no-op）に戻ると落ちる回帰テスト。
    ///
    /// <para>
    /// <b>UI に値が残っていることでは足りない。</b> この不具合の壊れ方は
    /// 「TextBox には出ているがモデルにも設定にも届いていない」なので、
    /// 画面を往復させたうえで<b>実際に録画してファイル名に効いていること</b>まで見る。
    /// </para>
    /// </summary>
    [Fact]
    public void EditingTheFilenameTemplate_ReachesTheModel_AndTheNextRecordingUsesIt()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        // 一意な印を入れたテンプレートへ書き換える。絶対パスにしないと発行先に書かれる。
        string marker = "edited" + Guid.NewGuid().ToString("N")[..6];
        string template = Path.Combine(instance.RecordingsDir, marker + "_{Name}.mp4");

        ui.SetPropertyText("FilenameTemplate", template);

        // 画面を往復させる（UI の状態が保持されること）
        ui.SwitchTo(UiSection.Log);
        ui.SwitchTo(UiSection.Preview);
        Assert.Equal(template, ui.WaitForPropertyText("FilenameTemplate", template));

        // ここからが本題 ── モデルまで届いているか。CLI で録画して実ファイル名を見る。
        instance.RunExpecting(0, "start-recording", "R1");
        Thread.Sleep(2000);
        instance.RunExpecting(0, "stop-recording", "R1");

        var recordings = instance.ListRecordings();
        Assert.True(
            recordings.Any(f => Path.GetFileName(f) == marker + "_R1.mp4"),
            $"編集した FilenameTemplate が録画に効いていません。生成物: " +
            $"[{string.Join(", ", recordings.Select(Path.GetFileName))}]" + Environment.NewLine +
            instance.DiagnosticDump());

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// <c>ClampBufferDuration</c> を素通しにすると落ちる回帰テスト。
    /// 丸めた値が<b>画面に返ってくる</b>ことまで見る ── VM が範囲外の値を保持したまま
    /// 表示し続けるのが、「3箇所すべてで丸める」ことにした理由そのもの。
    /// </summary>
    [Theory]
    [InlineData("-1", "0")]
    [InlineData("9999999", "600000")]
    public void BufferDurationOutOfRange_IsClampedInTheUi(string typed, string expected)
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SetPropertyText("BufferDuration", typed);

        // 画面を往復させて、丸めた値が保持されていることを見る。
        ui.SwitchTo(UiSection.Log);
        ui.SwitchTo(UiSection.Preview);

        Assert.Equal(expected, ui.WaitForPropertyText("BufferDuration", expected));
        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// レコーダー名の一意化（<c>RecorderNaming.MakeUnique</c>）の<b>呼び出し側</b>の回帰テスト。
    /// 純粋関数 <c>RecorderNaming.MakeUnique</c> は L1 が守っているが、
    /// <c>GstEventRecorderViewModel.Name</c> のセッターは CLI から到達できず、
    /// ここでしか触れない。
    ///
    /// <para>
    /// 衝突時、セッターは<b>ユーザーが入力したのと違う値</b>を格納する。
    /// TwoWay バインドなので TextBox には <c>R2 (2)</c> が返ってくるはずで、
    /// それを確認する（返ってこなければ、UI と実体が食い違ったまま残る）。
    /// </para>
    /// </summary>
    [Fact]
    public void RenamingARecorderToAnExistingName_ShowsTheUniquifiedNameBack()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");
        settings.AddRecorder("R2");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        // R1 を選んで、既にある "R2" へ改名しようとする。
        ui.SelectRecorder("R1");
        ui.SetPropertyText("Name", "R2");

        Assert.Equal("R2 (2)", ui.WaitForPropertyText("Name", "R2 (2)"));

        // ナビゲーション側の表示も一意化後の名前になっていること
        // （ここが古いままだと、どちらの項目がどのレコーダーか分からなくなる）。
        var names = ui.WaitForRecorderNavItemName("R2 (2)");
        Assert.Contains("R2 (2)", names);
        Assert.Contains("R2", names);

        // CLI から新しい名前で到達できること（＝一意化の目的そのもの）。
        var byNewName = instance.Run("start-recording", "R2 (2)");
        Assert.Equal(0, byNewName.ExitCode);
        instance.Run("stop-recording", "R2 (2)");

        // **2回続けて改名する。** ナビ項目の表示名は「項目を作り直している」のではなく
        // `PropertyChanged` の購読で追随している（購読を外す注入でこのテストが赤に
        // なることを実測で確認済み）。購読の張り直し・解除の順序を間違えると
        // 2回目以降だけが反映されなくなるので、1回では足りない。
        ui.SetPropertyText("Name", "R3");
        Assert.Equal("R3", ui.WaitForPropertyText("Name", "R3"));
        Assert.Contains("R3", ui.WaitForRecorderNavItemName("R3"));

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }
}
