using Microsoft.UI.Dispatching;
using ProcessRecorderApp.Components;
using ProcessRecorderApp.RemoteControl;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessRecorderApp.Services;

/// <summary>
/// リモート操作サーバー（<see cref="RemoteControlHost"/>）の常駐ライフサイクル。
/// <c>UiaTriggerService</c> と同じ形 ── 設定の変更を購読し、背景で作り直す。
///
/// <para>
/// <b>6 つの設定のどれが変わっても作り直す。</b> トークン・利用者・ゲスト許可は
/// <see cref="RemoteControlOptions"/> として起動時に渡すので、変更を反映する手段が
/// 再起動しかない ── 待ち受けと資格で扱いを分けても得るものが無いため、
/// 6 つとも同じ扱いに統一してある。<b>作り直すと全セッションが失効する</b>
/// （<c>SessionStore</c> はホストと同じ寿命）。
/// </para>
/// <para>
/// <b>失敗してもアプリの中核（録画）は殺さない。</b> ポートが使われている・
/// アドレスが不正といった失敗は <c>remote.error</c> に残して、サーバー無しで動き続ける。
/// </para>
/// </summary>
// partial なのは CsWinRT1028 のため（UiaTriggerService と同じ理由）。
public sealed partial class RemoteControlService : IDisposable
{
    /// <summary>プロセスで唯一のインスタンス（<c>UiaTriggerService.Current</c> と同じ形）。</summary>
    public static RemoteControlService? Current { get; private set; }

    /// <summary>サービスを生成して最初の読み込みを予約する。冪等。</summary>
    public static RemoteControlService Start(DispatcherQueue dispatcherQueue)
    {
        if (Current is null)
        {
            Current = new RemoteControlService(dispatcherQueue);
            Current.RequestReload();
        }
        return Current;
    }

    /// <summary>
    /// ホストの停止に与える時間（内側の締切）。SSE は開けたままなので、これは必ず打ち切りになる。
    /// </summary>
    private static readonly TimeSpan HostStopTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 停止の完了を待つ外側の締切。<b>必ず <see cref="HostStopTimeout"/> より長くする</b> ──
    /// 同じ値だと、内側が期限いっぱい使っただけで外側も切れ、「打ち切って畳めた」のか
    /// 「待ち切れずに置き去りにした」のかが区別できなくなる。差は <c>DisposeAsync</c> と
    /// 記録の書き出しに充てる。終了経路の待ちはこの値の 2 本分
    /// （<see cref="_gate"/> の取得と停止の完了）で上限が決まる。
    /// </summary>
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 作り直しをまとめる待ち。設定画面の 1 操作が複数の <c>PropertyChanged</c> を出す
    /// （4 つのうち 2 つ以上を続けて変えるのが普通）ので、そのたびに作り直すと
    /// <b>ポートの取り合いを自分で起こす</b> ── 前の待ち受けが閉じ切る前に次が開こうとする。
    /// </summary>
    private static readonly TimeSpan ReloadCoalesceDelay = TimeSpan.FromMilliseconds(300);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly System.Threading.Timer _reloadTimer;
    private RemoteControlHost? _host;
    private volatile bool _disposed;

    private RemoteControlService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        // 起動していない状態で作る（最初の武装は RequestReload が行う）。
        _reloadTimer = new System.Threading.Timer(
            _ => { if (!_disposed) _ = Task.Run(ReloadAsync); },
            null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Settings.AppSettings.Default.PropertyChanged += Settings_PropertyChanged;
    }

    private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Settings.AppSettings.RemoteControlEnabled)
            or nameof(Settings.AppSettings.RemoteControlBindAddress)
            or nameof(Settings.AppSettings.RemoteControlPort)
            or nameof(Settings.AppSettings.RemoteControlAccessToken)
            or nameof(Settings.AppSettings.RemoteUsers)
            or nameof(Settings.AppSettings.RemoteControlAllowGuestRead))
        {
            RequestReload();
        }
    }

    /// <summary>
    /// 設定からサーバーを作り直す（バックグラウンドで実行、失敗は activity.log へ）。
    ///
    /// <para>
    /// <b>まとめてから 1 回だけ作り直す。</b> <see cref="ReloadCoalesceDelay"/> の
    /// タイマーを<b>押し直す</b>ので、続けて何度呼ばれても作り直しは最後の 1 回になる
    /// （待っている間に来た要求は時計を延ばすだけで消える）。
    /// </para>
    /// </summary>
    public void RequestReload()
    {
        if (_disposed)
            return;
        try
        {
            _reloadTimer.Change(ReloadCoalesceDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Dispose と競り合った。作り直す相手はもう無い。
        }
    }

    private async Task ReloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            await StopHostAsync().ConfigureAwait(false);

            var settings = Settings.AppSettings.Default;
            if (!settings.RemoteControlEnabled)
                return;

            string token = settings.RemoteControlAccessToken;
            if (string.IsNullOrEmpty(token))
            {
                // 有効化の経路（OnRemoteControlEnabledChanged）は必ずトークンを作るので、
                // ここへ来るのは settings.json を手で書いた場合だけ。
                // **トークン無しで待ち受けない。**
                ActivityLog.Error("remote.error", "detail=access token is empty");
                return;
            }

            // **利用者は写しを渡す。** 正本の List は UI スレッド（設定画面の編集）が
            // 差し替える対象で、そのまま渡すと要求スレッドが読んでいる最中に
            // 書き換わりうる。null は手で編集された settings.json で起こりうる。
            Components.RemoteUserDefinition[] users =
                settings.RemoteUsers is { } definitions ? [.. definitions] : [];

            var options = new RemoteControlOptions(
                settings.RemoteControlBindAddress,
                settings.RemoteControlPort,
                token,
                users,
                settings.RemoteControlAllowGuestRead);

            try
            {
                _host = await RemoteControlHost
                    .StartAsync(options, new RemoteControlBackend(_dispatcherQueue), CancellationToken.None)
                    .ConfigureAwait(false);
                ActivityLog.Info("remote.start", $"bind={_host.BindDescription}");
            }
            catch (Exception ex)
            {
                // ポートの取り合い（Kestrel は IOException で包む）・不正なアドレス
                // （ArgumentException）・権限。どれもサーバー無しで続ける。
                _host = null;
                ActivityLog.Error("remote.error", $"detail={ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            ActivityLog.Error("remote.error", ex.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>動いているサーバーを止めて解放する（<see cref="_gate"/> の中で呼ぶ）。</summary>
    private async Task StopHostAsync()
    {
        if (_host is not { } host)
            return;

        _host = null;
        // **待つ前に記録する。** 排出は締切いっぱいまで掛かるのが普通で
        //（SSE は開けたままなので必ず打ち切りになる）、終了経路ではその間に
        // プロセスが落ちうる ── 後に書くと「止めたのに記録が無い」になる。
        ActivityLog.Info("remote.stop");
        await host.StopAsync(HostStopTimeout).ConfigureAwait(false);
        await host.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 停止して解放する。複数回呼んでも安全。終了経路なので待ちはすべて有界。
    ///
    /// <para>
    /// <b>UI スレッドから同期的に待つ。</b> 呼び出し元は <c>AppWindow.Destroying</c> で、
    /// ここを抜けた後にプロセスが落ちるため「後で止める」は成立しない。
    /// 停止そのものは <see cref="Task.Run(Func{Task})"/> の上で回す ──
    /// UI スレッド上で <c>await</c> の継続を待つと、その継続が
    /// ディスパッチャキューへ戻ろうとしてデッドロックする。
    /// </para>
    /// <para>
    /// <b><see cref="_gate"/> は破棄しない</b>（<c>UiaTriggerService.Dispose</c> と同じ理由 ──
    /// 並んでいる <see cref="ReloadAsync"/> の <c>Release()</c> が
    /// <see cref="ObjectDisposedException"/> になる）。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (ReferenceEquals(Current, this))
            Current = null;
        Settings.AppSettings.Default.PropertyChanged -= Settings_PropertyChanged;
        // まとめ待ちのタイマーを止める。発火済みの ReloadAsync は _disposed で降りる。
        _reloadTimer.Dispose();

        if (!_gate.Wait(ShutdownWaitTimeout))
        {
            // 作り直しが走ったまま終わる。**黙って戻らない** ── このとき
            // remote.stop は出ないので、記録が無ければ「止め損ねた」と読めなくなる。
            ActivityLog.Warn("remote.error", "detail=dispose timed out waiting for reload");
            return;
        }
        try
        {
            Task.Run(StopHostAsync).Wait(ShutdownWaitTimeout);
        }
        catch (AggregateException ex)
        {
            ActivityLog.Warn("remote.error", ex.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }
}
