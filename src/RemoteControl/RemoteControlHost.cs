using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.RemoteControl.Endpoints;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// リモート操作サーバーの起動設定。
/// </summary>
/// <param name="BindAddress">待ち受ける IP アドレス（<c>0.0.0.0</c> で全インターフェイス）。</param>
/// <param name="Port">待ち受けるポート。<b>0 は「空いているものを OS に選ばせる」</b>。</param>
/// <param name="AccessToken">
/// 書き込み要求に要る秘密。<b>空文字での起動は呼び出し側が拒む</b>
/// （<c>TokenEquals</c> は空文字同士でも false を返すので通りはしないが、
/// 「トークン無しで動いているように見える」状態を作らない）。
/// </param>
public sealed record RemoteControlOptions(string BindAddress, int Port, string AccessToken);

/// <summary>
/// リモート操作サーバー（Kestrel）の 1 実体。
///
/// <para>
/// <b><c>IHostApplicationLifetime</c> は使わない。</b> このプロセスの寿命を決めているのは
/// WinUI のウィンドウであって Web ホストではない ── 停止は
/// <see cref="StopAsync"/> を呼び出し側が明示的に叩く。
/// </para>
/// </summary>
public sealed class RemoteControlHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    /// <summary>畳めなかった起動を打ち切るまでの時間（要求は 1 つも通していない）。</summary>
    private static readonly TimeSpan FailedStartStopTimeout = TimeSpan.FromSeconds(1);

    private RemoteControlHost(WebApplication app, IPEndPoint boundEndpoint)
    {
        _app = app;
        BoundEndpoint = boundEndpoint;
    }

    /// <summary>
    /// 実際に待ち受けている終端。<see cref="RemoteControlOptions.Port"/> が 0 のときは
    /// ここでしか実ポートを知れない。
    /// </summary>
    public IPEndPoint BoundEndpoint { get; }

    /// <summary>
    /// サーバーを起動する。設定が不正なら <see cref="ArgumentException"/>
    /// （呼び出し側が <c>remote.error</c> に書く）。
    /// </summary>
    public static async Task<RemoteControlHost> StartAsync(
        RemoteControlOptions options, IRemoteControlBackend backend, CancellationToken ct)
    {
        if (!IPAddress.TryParse(options.BindAddress, out IPAddress? address))
            throw new ArgumentException($"bind address is not an IP address: '{options.BindAddress}'", nameof(options));
        if (options.Port is < 0 or > 65535)
            throw new ArgumentException($"port is out of range: {options.Port}", nameof(options));

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions());
        // このプロセスにコンソールは無い（常駐ワーカーは非表示で起動する）。
        // 出さない理由はそれだけではなく、要求ごとのアクセスログを activity.log へ
        // 混ぜないためでもある ── 記録するのは remote.* の 4 種だけ。
        builder.Logging.ClearProviders();
        // **UseUrls は使わない。** あちらは文字列を経由するぶん解釈の余地があり、
        // 「0.0.0.0 と書いたのに localhost だけで待っていた」を作りうる。
        builder.WebHost.ConfigureKestrel(k => k.Listen(address, options.Port));

        WebApplication app = builder.Build();

        var auth = new RemoteAuth(options.AccessToken, new SessionStore());

        // 例外の受け口は経路より手前に置く（WebApplication は利用者のミドルウェアを
        // ルーティングとエンドポイント実行の間に挟む）。
        app.Use(async (HttpContext ctx, RequestDelegate next) =>
        {
            try
            {
                await next(ctx);
            }
            catch (RemoteApiException ex)
            {
                if (ctx.Response.HasStarted)
                    return;
                await ApiResponse.WriteExitCodeErrorAsync(ctx, ex.ExitCode, ex.Message, ex.Filename);
            }
            catch (BadHttpRequestException)
            {
                // **要求そのものが不正**（本文が上限を超えた等）。Kestrel が 413 / 400 を書く
                // ので、こちらは何もしない ── ここで捕まえると、送りつけるだけで
                // remote.error を無制限に積める記録になる。
                throw;
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                // **呼び出し側が切った。** 書く相手がもう居ないので何もしない
                // ── これを下の受け口へ落とすと、接続を切るだけで remote.error を積める
                // （長い読み取り・イベント配信では普通に起きる）。
            }
            catch (Exception ex)
            {
                // **本文には型名しか出さない。** 例外のメッセージにはファイルパスや
                // 内部の識別子が入りうるので、全文は activity.log 側へ回す。
                ActivityLog.Error("remote.error", ex.ToString());
                if (ctx.Response.HasStarted)
                    return;
                await ApiResponse.WriteErrorAsync(ctx, 500, 99, ex.GetType().Name);
            }
        });

        // **待ち受けの前に Web UI の資産へ触れる。** 型初期化で埋め込みを全部読むので、
        // 発行物へ入っていなければここで例外になり、起動が remote.error で落ちる
        // ── 要求が来てから 404 になるのでは、原因が資産の欠落だと分からない。
        try
        {
            _ = WebAssets.Manifest.Count;
        }
        catch (TypeInitializationException ex) when (ex.InnerException is not null)
        {
            // 型初期化の例外は「型 X の初期化子が例外を投げた」としか言わない。
            // 記録に残るのは remote.error の 1 行（例外の Message だけ）なので、
            // 欠けている資産の名前を持っている内側の理由をここで表へ出す。
            throw new InvalidOperationException(ex.InnerException.Message, ex);
        }

        RootEndpoint.Map(app, auth);
        PingEndpoint.Map(app, auth);
        RecorderEndpoints.Map(app, backend);
        ControlEndpoints.Map(app, backend, auth);
        VariableEndpoints.Map(app, backend, auth);
        SettingsEndpoints.Map(app, backend, auth);
        EventsEndpoint.Map(app, backend);
        RecordingEndpoints.Map(app, backend);
        PreviewEndpoints.Map(app, backend);

        app.MapFallback(async (HttpContext ctx) =>
            await ApiResponse.WriteErrorAsync(
                ctx, 404, ApiResponse.HttpLayerExitCode, "unknown endpoint"));

        await app.StartAsync(ct);

        int port = TryResolveBoundPort(app, options.Port);
        var host = new RemoteControlHost(app, new IPEndPoint(address, port));

        if (port == 0)
        {
            // **0 のまま返さない。** 実ポートを知る手段は remote.start の記録しか無いので、
            // 0 を配ると「待ち受けているのに誰も繋ぎ方を知らない」サーバーになる。
            // 待ち受けは既に始まっているため、投げる前に必ず畳む（掴み手のいない
            // Kestrel が残ると、次の作り直しが自分自身とポートを取り合う）。
            await host.StopAsync(FailedStartStopTimeout).ConfigureAwait(false);
            await host.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                "the bound port could not be resolved from the server addresses");
        }

        return host;
    }

    /// <summary>
    /// 実際に割り当てられたポート。<see cref="RemoteControlOptions.Port"/> が 0 でなければそれ、
    /// 0 なら起動後にサーバーが公開しているアドレスから読む。
    /// <b>読めなければ 0</b>（呼び出し側が畳んで投げる ── 要求された 0 と混じらないのは、
    /// 指定が 0 でないときは必ずその値を返すため）。
    /// </summary>
    private static int TryResolveBoundPort(WebApplication app, int requestedPort)
    {
        if (requestedPort != 0)
            return requestedPort;

        string? url = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();

        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? parsed.Port : 0;
    }

    /// <summary>
    /// 停止する。<b>投げない</b> ── 呼ぶのはアプリの終了経路と設定の再読込であり、
    /// どちらもここで失敗しても続けるしかない。
    /// <paramref name="timeout"/> を過ぎた接続（SSE は開けたままなので必ず該当する）は
    /// 打ち切られる。
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        try
        {
            using var deadline = new CancellationTokenSource(timeout);
            await _app.StopAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("remote.error", "stop: " + ex.Message);
        }
    }

    /// <summary>解放する。停止は済ませてから呼ぶこと（未停止でも投げない）。</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("remote.error", "dispose: " + ex.Message);
        }
    }

    /// <summary>診断用の <c>bind=&lt;ip&gt;:&lt;port&gt;</c> 表記。</summary>
    public string BindDescription
        => string.Create(CultureInfo.InvariantCulture, $"{BoundEndpoint.Address}:{BoundEndpoint.Port}");
}
