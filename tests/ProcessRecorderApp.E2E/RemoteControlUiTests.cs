using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Xunit;

using static ProcessRecorderApp.E2E.RemoteControlTests;

namespace ProcessRecorderApp.E2E;

/// <summary>
/// リモート操作のうち、<b>画面（UIA）を触らないと成立しないもの</b>。
///
/// <para>
/// <c>RemoteControlTests</c> から分けてあるのは<b>シャードのため</b>である
/// ── <c>AppUi</c> を使うクラスは <c>Category=Gui</c> を持ち、CI では実デスクトップを
/// 持つ runner にまとめて流す（規則は「ファイルが <c>AppUi</c> / <c>TrayUi</c> を参照する
/// ⇔ そのファイルの全テストクラスがクラスレベルの <c>Gui</c> トレイトを持つ」で、
/// L4 の <c>E2EShardSyncTests.GuiTraitMatchesUiaUsage</c> が突き合わせる）。
/// </para>
/// <para>
/// 構成と補助（<c>RemoteSettings</c> / <c>WaitForPort</c> / <c>CreateClient</c> /
/// <c>SendAsync</c> / <c>ExpectAsync</c>）は <see cref="RemoteControlTests"/> の
/// <c>internal static</c> をそのまま使う ── 待ち受けの読み方が 2 か所に分かれないように。
/// </para>
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "Gui")]
public sealed class RemoteControlUiTests(PublishedApp app, ITestOutputHelper output)
{
    /// <summary>
    /// ウィンドウを閉じて正常終了したとき、サーバーが止まったことが記録に残ること。
    ///
    /// <para>
    /// <b>ここでしか確かめられない。</b> <c>Destroying</c> は Ctrl+閉じる かトレイの
    /// 「終了」でしか発火せず、CLI からは到達できない ── 停止を怠っても
    /// プロセスが消える以上ポートは解放されるので、<b>症状が出ない退行</b>になる。
    /// </para>
    /// </summary>
    [Fact]
    public void ClosingTheWindow_StopsTheServer()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance, output);
        output.WriteLine($"port={port}");

        using var ui = AppUi.Activate(instance);
        ui.CloseWindow(holdControl: true);

        Assert.True(ui.WaitForProcessExit(TimeSpan.FromSeconds(420)),
            "Ctrl+閉じる でプロセスが終了しませんでした。" + Environment.NewLine + instance.DiagnosticDump());

        var log = instance.ReadActivityLog();
        Assert.True(0 < ActivityLogFile.Events(log, "remote.stop").Count,
            "正常終了なのに remote.stop がありません（RemoteControlService.Dispose が呼ばれていない）。"
            + Environment.NewLine + instance.DiagnosticDump());
    }

    /// <summary>
    /// <b>SSE を開いたまま</b>ウィンドウを閉じても、プロセスが終わり <c>remote.stop</c> が残ること。
    ///
    /// <para>
    /// <b>開いた接続があって初めて停止が締切に掛かる。</b> Kestrel の停止は開いている
    /// 接続の排出を待つので、記録を排出の後ろに置くと「終了はしたのに記録だけ無い」に
    /// なりうる ── 接続が 1 本も無ければ停止は即座に終わり、この失敗モードは再現しない。
    /// 上の <see cref="ClosingTheWindow_StopsTheServer"/> では見えない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ClosingTheWindow_StopsTheServer_WithAnOpenEventStream()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance, output);
        output.WriteLine($"port={port}");

        using var client = CreateClient(port);
        using var response = await client.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);
        // 1 件だけ読んで、本当に確立した接続であることを確かめる（応答ヘッダーが
        // 返っただけでは排出待ちに掛かる保証が無い）。以後は読まずに開けたままにする。
        output.WriteLine(await ReadEventAsync(reader, "state", TimeSpan.FromSeconds(5)));

        using var ui = AppUi.Activate(instance);
        ui.CloseWindow(holdControl: true);

        Assert.True(ui.WaitForProcessExit(TimeSpan.FromSeconds(420)),
            "SSE を開いたまま Ctrl+閉じる を行うとプロセスが終了しませんでした。"
            + Environment.NewLine + instance.DiagnosticDump());

        var log = instance.ReadActivityLog();
        Assert.True(0 < ActivityLogFile.Events(log, "remote.stop").Count,
            "SSE を開いたままだと remote.stop が残りません（記録が排出待ちの後ろにある）。"
            + Environment.NewLine + instance.DiagnosticDump());
    }

    /// <summary>
    /// <b>設定画面の「…」でトークンを作り直すと、その場で効くこと（L3）。</b>
    /// <c>RemoteControlAccessToken</c> は <c>[ReadOnly(true)]</c> ＋ <c>[ValueBuilder]</c> ＝
    /// 「直接は編集できないが、ビルダーでなら変更できる」行なので、
    /// <b>これが唯一のトークン変更手段</b>である。
    ///
    /// <para>
    /// <b>欄の表示が変わったことでは足りない。</b> ビルダーの結果を対象へ書かずに
    /// 表示値だけ更新しても画面上は同じに見え、<c>settings.json</c> と待ち受け側は
    /// 古いトークンのまま残る ── 古い資格が<b>通らなくなること</b>まで見る。
    /// </para>
    /// </summary>
    [Fact]
    public async Task RegeneratingTheTokenFromTheSettingsScreen_InvalidatesTheOldToken()
    {
        using var instance = AppInstance.Create(app, RemoteSettings());
        int port = WaitForPort(instance, output);
        using var client = CreateClient(port);

        using (var response = await SendAsync(client, HttpMethod.Post, "api/ping"))
        {
            using var body = await ExpectAsync(response, HttpStatusCode.OK, output);
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        }

        using var ui = AppUi.Activate(instance);
        ui.SwitchTo(UiSection.Settings);
        ui.WaitForPropertyRow("RemoteControlAccessToken");
        Assert.Equal(Token, ui.GetPropertyText("RemoteControlAccessToken"));

        ui.Click("RemoteControlAccessToken.Build");

        string shown = ui.GetPropertyText("RemoteControlAccessToken");
        var deadline = Stopwatch.StartNew();
        while (shown == Token && deadline.Elapsed < DebounceMargin)
        {
            await Task.Delay(200, Ct);
            shown = ui.GetPropertyText("RemoteControlAccessToken");
        }
        Assert.NotEqual(Token, shown);

        // トークンの変更は待ち受けの作り直しを起こす（`RemoteControlService` が購読している）。
        // `RemoteControlPort=0` なのでポートも変わる ── 2 本目の `remote.start` を待つ。
        int newPort = WaitForRestartedPort(instance, port);
        using var restarted = CreateClient(newPort);

        // 古いトークンはもう通らない（＝表示だけでなく AppSettings が変わっている）。
        using (var response = await SendAsync(restarted, HttpMethod.Post, "api/ping"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // 新しいトークンで通ること（＝差し替わっただけで、締め出しではない）。
        using (var request = Post("api/ping"))
        {
            request.Headers.Add("Authorization", "Bearer " + shown);
            request.Headers.Add("X-PRApp-Client", "1");
            using var response = await restarted.SendAsync(request, Ct);
            using var body = await ExpectAsync(response, HttpStatusCode.OK, output);
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        }
    }

    /// <summary>作り直された待ち受けのポートを <c>activity.log</c> の 2 本目以降から読む。</summary>
    private int WaitForRestartedPort(AppInstance instance, int previousPort)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartBudget)
        {
            foreach (string line in ActivityLogFile.Events(instance.ReadActivityLog(), "remote.start"))
            {
                var match = BindPattern.Match(ActivityLogFile.DetailOf(line));
                if (match.Success && int.Parse(match.Groups[2].Value) != previousPort)
                {
                    output.WriteLine(line);
                    return int.Parse(match.Groups[2].Value);
                }
            }
            Thread.Sleep(200);
        }

        Assert.Fail(
            $"待ち受けの作り直し（2 本目の remote.start）が {StartBudget.TotalSeconds:F0} 秒以内に"
            + "現れませんでした。" + Environment.NewLine + instance.DiagnosticDump());
        return 0;
    }
}
