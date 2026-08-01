using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// ハーネス自体が正しいことの確認。ここが緑にならないうちは、以降のテストの
/// 赤は「製品の不具合」ではなく「テスト基盤の不具合」でありうる。
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class SmokeTests(PublishedApp app)
{
    [Fact]
    public void Ping_Succeeds_AndTheInstanceIsFullyIsolated()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        var ping = instance.Run("ping");
        Assert.Equal(0, ping.ExitCode);

        // 隔離できていること ── ここが実ユーザーの %LOCALAPPDATA% だと、
        // テストが開発者の本物の設定を書き換えてしまう。
        Assert.True(File.Exists(instance.SettingsPath), instance.SettingsPath);
        Assert.True(File.Exists(instance.ActivityLogPath), instance.DiagnosticDump());

        var log = instance.ReadActivityLog();
        Assert.NotEmpty(ActivityLogFile.Events(log, "app.start"));
        Assert.NotEmpty(ActivityLogFile.Events(log, "ping"));

        // レコーダーが settings.json どおりに初期化されていること
        // （ここが fail だと、以降の録画テストは全部「録画できない」で落ちる）。
        Assert.NotEmpty(ActivityLogFile.Events(log, "recorder.init ok"));
        Assert.Empty(ActivityLogFile.Events(log, "recorder.init fail"));
        Assert.Empty(ActivityLogFile.Events(log, "app.error"));
    }
}
