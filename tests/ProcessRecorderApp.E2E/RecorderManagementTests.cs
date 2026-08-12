using System.Text.Json;
using Xunit;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// L3: レコーダーの追加と削除。
///
/// <para>
/// <b>このテストだけが `RecorderNavViewBehavior` の購読の張り直し／解除を実行する。</b>
/// ナビ項目は Recorder ごとに <c>PropertyChanged</c> を購読して表示名を追随させており
/// （購読を外す注入で `PropertyEditingTests` が赤になることを確認済み）、
/// その解除は <c>Remove</c> / <c>Reset</c> の各分岐にある。
/// 「<c>Reset</c> では列挙が空なので解除対象を取り違える」という
/// 実際に踏んだ罠を避けるために影リストを持たせてあるが、
/// <b>追加と削除を実際に行わないとその分岐は一度も実行されない</b>。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class RecorderManagementTests(PublishedApp app)
{
    /// <summary>
    /// 追加 → 改名 → 削除 の一巡。
    ///
    /// <para>
    /// <b>最後に「生き残った方」を改名するのが要点。</b> 削除時の購読解除が
    /// キーを取り違えていると、生き残ったレコーダーの購読まで外れて
    /// <b>以後その項目が改名に追随しなくなる</b> ── 例外は出ないので、
    /// 削除できたことだけを見るテストでは素通りする。
    /// </para>
    /// </summary>
    [Fact]
    public void AddingThenRemovingARecorder_KeepsTheSurvivorFullyLive()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("Keeper");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        // ---- 追加 ----
        ui.AddRecorder();

        // **新規レコーダーの名前を決め打ちにしない。** 実際に付くのは
        // `EventRecorder` の ctor が与える `Recorder #N`（N はプロセス通算の連番）で、
        // 既存レコーダーの数に依存する。ここで見たいのは「1件増えたこと」なので、
        // 増えた名前を差分で取る。
        var afterAdd = ui.WaitForRecorderNavItemCount(2);
        Assert.Equal(2, afterAdd.Count);
        Assert.Contains("Keeper", afterAdd);
        string added = Assert.Single(afterAdd, n => n != "Keeper");

        // 設定にも反映されていること（UI だけの見かけで終わっていない）。
        Assert.Contains(added, WaitForSettingsRecorders(instance, 2));

        // ---- 削除 ----
        ui.SelectRecorder(added);
        ui.Click("RemoveRecorderButton");
        ui.ConfirmDialog();

        var afterRemove = ui.WaitForRecorderNavItemCount(1);
        Assert.Equal(["Keeper"], afterRemove);
        Assert.Equal(["Keeper"], WaitForSettingsRecorders(instance, 1));

        // ---- 生き残りがまだ「生きている」か ----
        // 改名が反映される＝購読が外れていない。
        ui.SelectRecorder("Keeper");
        ui.SetPropertyText("Name", "Survivor");
        Assert.Equal("Survivor", ui.WaitForPropertyText("Name", "Survivor"));
        Assert.Contains("Survivor", ui.WaitForRecorderNavItemName("Survivor"));

        // 改名後の名前で CLI から到達できること（モデルまで届いている証拠）。
        instance.RunExpecting(0, "start-recording", "Survivor");
        instance.Run("stop-recording", "Survivor");

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// 削除の確認ダイアログをキャンセルしたら消えないこと
    /// （`ConfirmRecorderRemovalAsync` の戻り値が無視されていないこと）。
    /// </summary>
    [Fact]
    public void CancellingTheRemovalDialog_KeepsTheRecorder()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("R1");
        settings.AddRecorder("R2");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.SelectRecorder("R2");
        ui.Click("RemoveRecorderButton");
        ui.CancelDialog();

        // 消えていないこと。件数が変わらないことを一定時間見てから確定させる
        // （「まだ消えていないだけ」と区別するため）。
        Thread.Sleep(1500);
        var names = ui.RecorderNavItemNames();
        Assert.Contains("R2", names);
        Assert.Equal(2, names.Count);
        Assert.Equal(2, ReadSettingsRecorders(instance).Length);

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// <b>UI から追加したレコーダーの名前が既存と衝突しないこと。</b>
    ///
    /// <para>
    /// CLI の名前解決は「完全一致・先勝ち」なので、同名が2つ並ぶと
    /// 2つ目には CLI から永久に到達できない ── 名前の一意化を入れた理由そのもの。
    /// </para>
    /// <para>
    /// <b>この経路では名前の一意化が効かない。</b> <c>GstControllerViewModel.AddRecorder</c> は
    /// <c>RecorderNaming.MakeUnique</c> を<b>置き換え前の既定名 "Recorder" に対して</b>適用するが、
    /// その後 <c>EventRecorder</c> の ctor が「名前が既定値なら」という条件で
    /// <c>Recorder #N</c>（N はプロセス通算の連番）へ上書きする。
    /// 上書き後の名前は既存と照合されないため、連番が既存の名前に追いつくと衝突する。
    /// </para>
    /// </summary>
    [Fact]
    public void AddingARecorder_NeverProducesADuplicateName()
    {
        // 連番は「このプロセスで作られた EventRecorder の数」。既存1件を
        // `Recorder #2` にしておくと、次に追加される1件が同じ名前になる。
        var settings = new SettingsFile();
        settings.AddRecorder("Recorder #2");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.AddRecorder();

        var names = ui.WaitForRecorderNavItemCount(2);
        Assert.Equal(2, names.Count);

        // **「重複していない」だけでは足りない。** 修正後はそれが常に真になるので、
        // 衝突する状況を作り損ねてもテストは緑のまま通ってしまう（＝何も検出しない）。
        // 一意化が**実際に働いた**ことまで表明する ── 連番が `Recorder #2` を作り、
        // それが既存と衝突して `MakeUnique` が ` (2)` を足した、という結果そのもの。
        // 衝突が起きなくなったら（既定名の形や連番の起点が変わったら）この表明が
        // 落ちて気づける。規則は L1 の
        // `RecorderNamingTests.NameThatAlreadyLooksNumbered_IsSuffixedAgain` が守る。
        Assert.Contains("Recorder #2 (2)", names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        // 設定側でも重複していないこと（CLI が名前で解決する対象はこちら）。
        var saved = WaitForSettingsRecorders(instance, 2);
        Assert.Contains("Recorder #2 (2)", saved);
        Assert.Equal(saved.Length, saved.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// <b>コピーして追加すると、元のレコーダーの設定がそのまま写ること。</b>
    ///
    /// <para>
    /// 複製はソース生成 JSON の往復（<c>AppSettings.CloneRecorder</c>）で行う。
    /// 手書きのプロパティコピーにしなかったのは、レコーダー設定が既に手書きミラーを
    /// 4 箇所持っており（<c>RecorderSettingsMirrorTests</c>）、<b>検出器の無い5つ目</b>を
    /// 作らないため ── したがってここで見るべきは「往復が実際に使われていること」であり、
    /// <b>既定値と異なる値</b>を写して確かめる（既定のままだと、空から作っても同じ結果になる）。
    /// </para>
    /// <para>
    /// 名前だけは一意化される。これが無いと CLI の「完全一致・先勝ち」で
    /// 2 台目へ永久に到達できない ── だから CLI から実際に引けることまで見る。
    /// </para>
    /// </summary>
    [Fact]
    public void AddingARecorderByCopying_CarriesTheSettingsAndUniquifiesTheName()
    {
        var settings = new SettingsFile();
        var source = settings.AddRecorder("Origin");
        // 製品の既定値（10000）ともハーネスの既定（3000）とも違う値にする
        // ── 空から作った場合と必ず区別が付くようにするため。
        source.BufferDuration = 4321;

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.AddRecorderCopyingFrom("Origin");

        var names = ui.WaitForRecorderNavItemCount(2);
        Assert.Contains("Origin", names);
        // コピー元の名前を起点に一意化される（`EventRecorder` の ctor が上書きするのは
        // 名前がちょうど既定値 "Recorder" のときだけなので、ここには当たらない）。
        Assert.Contains("Origin (2)", names);

        // 設定まで届いていること（UI の見かけで終わっていない）。
        var saved = WaitForSettingsRecorders(instance, 2);
        Assert.Contains("Origin (2)", saved);
        Assert.Equal(saved.Length, saved.Distinct(StringComparer.Ordinal).Count());

        // **中身が写っていること。** ここがこのテストの本体。
        Assert.Equal(4321, ReadRecorderBufferDuration(instance, "Origin (2)"));

        // 一意化した名前で CLI から到達できること（名前を分けた目的そのもの）。
        instance.RunExpecting(0, "start-recording", "Origin (2)");
        instance.Run("stop-recording", "Origin (2)");

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    /// <summary>
    /// 追加ダイアログを取り消したら 1 台も増えないこと
    /// （「取り消し」と「既定値から新規」を同じ値で表すと、取り消したのに増える）。
    /// </summary>
    [Fact]
    public void CancellingTheAddDialog_AddsNothing()
    {
        var settings = new SettingsFile();
        settings.AddRecorder("Only");

        using var instance = AppInstance.Create(app, settings);
        using var ui = AppUi.Activate(instance);

        ui.OpenAddRecorderDialog();
        ui.CancelDialog();

        // 増えていないことを一定時間見てから確定させる（「まだ増えていないだけ」と区別する）。
        Thread.Sleep(1500);
        Assert.Equal(["Only"], ui.RecorderNavItemNames());
        Assert.Equal(["Only"], ReadSettingsRecorders(instance));

        Assert.Empty(ActivityLogFile.Events(instance.ReadActivityLog(), "app.error"));
    }

    private static int? ReadRecorderBufferDuration(AppInstance instance, string recorderName)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(instance.SettingsPath));
            if (!document.RootElement.TryGetProperty("Recorders", out var recorders))
                return null;
            foreach (var recorder in recorders.EnumerateArray())
            {
                if (recorder.TryGetProperty("Name", out var name)
                    && name.GetString() == recorderName
                    && recorder.TryGetProperty("BufferDuration", out var buffer))
                {
                    return buffer.GetInt32();
                }
            }
            return null;
        }
        catch (IOException)
        {
            return null; // 保存中に読んだ
        }
    }

    private static string[] WaitForSettingsRecorders(AppInstance instance, int expectedCount)
    {
        // デバウンス自動保存（約1秒）を待つ。
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        string[] names;
        do
        {
            names = ReadSettingsRecorders(instance);
            if (names.Length == expectedCount)
                return names;
            Thread.Sleep(200);
        }
        while (deadline.Elapsed < TimeSpan.FromSeconds(20));
        return names;
    }

    private static string[] ReadSettingsRecorders(AppInstance instance)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(instance.SettingsPath));
            if (!document.RootElement.TryGetProperty("Recorders", out var recorders))
                return [];
            return [.. recorders.EnumerateArray()
                .Select(r => r.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "")];
        }
        catch (IOException)
        {
            return []; // 保存中に読んだ
        }
    }
}
