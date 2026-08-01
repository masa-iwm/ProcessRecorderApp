using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// Variables 画面の「保存」列（L3）。
///
/// <para>
/// テンプレート変数の永続化は<b>変数ごとの明示指定</b>で、指定の口は CLI の
/// <c>--persist</c> と<b>この列</b>の2つ。CLI 側は <see cref="PersistenceTests"/> が
/// 押さえているが、<b>チェックボックスから <c>AppSettings</c> までの結線は
/// GUI からしか触れない</b> ── 列を足しただけでバインドが繋がっていなくても、
/// 見た目は正しく出る。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class TemplateVariablePersistenceUiTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>デバウンス保存（約1秒）が書き切るまでの上限。</summary>
    private static readonly TimeSpan SaveBudget = TimeSpan.FromSeconds(15);

    [Fact]
    public void TheKeepColumn_TogglesPersistenceInBothDirections()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");

        using var instance = AppInstance.Create(app, settings);

        // --set だけ（＝セッション限り）で始める。行を1件に保つのは、
        // チェックボックスと行の対応を「単一であること」で確定させるため。
        Assert.Equal(0, instance.Run("--set", "Greeting=hello").ExitCode);

        using var ui = AppUi.Activate(instance);
        ui.SwitchTo(UiSection.Variables);

        var toggle = ui.WaitForElement("VariablePersistToggle").AsCheckBox();
        output.WriteLine(ui.DescribeVisibleElements());

        // 既定はオフ。ここが最初からオンなら「常に保存する」に戻っている。
        Assert.False(toggle.IsChecked, "既定で永続化がオンになっています（--set だけで保存されてしまう）");
        Assert.False(SavedVariables(instance).TryGetProperty("Greeting", out _));

        // オンにする → settings.json に載る
        toggle.IsChecked = true;
        WaitForSavedVariable(instance, "Greeting", "hello");

        // オフに戻す → settings.json から消える（変数自体は残る）
        // **要素を取り直す。** TableView のセルは再生成されうるので、
        // 1つ前の参照を使い回すと製品と無関係な理由で落ちる。
        ui.WaitForElement("VariablePersistToggle").AsCheckBox().IsChecked = false;
        WaitForSavedVariableToVanish(instance, "Greeting");

        var read = instance.Run("--get", "Greeting");
        Assert.Equal(0, read.ExitCode);
        Assert.Equal("hello", read.StdOut.Trim());

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// <c>TemplateVariables</c> が1件も無いときに返すもの。
    /// <b>「セクションが無い」と「セクションが空」は、この検査にとって同じ意味</b>
    /// ── どちらも「永続化された変数は1件も無い」である。
    /// </summary>
    private static readonly JsonElement NoSavedVariables = JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement SavedVariables(AppInstance instance)
    {
        // 書き込みの途中を掴んで JsonException にならないよう、読めるまで少しだけ粘る。
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(instance.SettingsPath));

                // **`GetProperty` を使ってはいけない。** フィクスチャが書く初期 settings.json には
                // `TemplateVariables` が**そもそも無い**（成果物で実物を確認済み）。
                // デバウンス保存（約1秒）が書き込む前にここを読むと `KeyNotFoundException` になり、
                // **上の再試行は `JsonException` と `IOException` しか捕まえないので素通りして落ちる。**
                // CI で実際に踏んだ、時間依存の偽の赤。
                //
                // 呼び出し側3箇所（未永続化の表明 / 出現待ち / 消滅待ち）は**すべて**
                // 「セクションが無い＝1件も永続化されていない」で正しく動く。
                // なお「出現待ち」は肯定側を表明し続けるので、
                // **製品がセクションごと書かなくなる退行はここで見逃さない。**
                return document.RootElement.TryGetProperty("TemplateVariables", out var variables)
                    ? variables.Clone()
                    : NoSavedVariables;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(5))
                    throw;
                Thread.Sleep(100);
            }
        }
    }

    private static void WaitForSavedVariable(AppInstance instance, string key, string expected)
    {
        var deadline = Stopwatch.StartNew();
        do
        {
            if (SavedVariables(instance).TryGetProperty(key, out var value) && value.GetString() == expected)
                return;
            Thread.Sleep(200);
        }
        while (deadline.Elapsed < SaveBudget);

        Assert.Fail($"「保存」列をオンにしても settings.json に '{key}' が現れませんでした。"
                    + Environment.NewLine + File.ReadAllText(instance.SettingsPath));
    }

    private static void WaitForSavedVariableToVanish(AppInstance instance, string key)
    {
        var deadline = Stopwatch.StartNew();
        do
        {
            if (!SavedVariables(instance).TryGetProperty(key, out _))
                return;
            Thread.Sleep(200);
        }
        while (deadline.Elapsed < SaveBudget);

        Assert.Fail($"「保存」列をオフにしても settings.json から '{key}' が消えませんでした。"
                    + Environment.NewLine + File.ReadAllText(instance.SettingsPath));
    }
}
