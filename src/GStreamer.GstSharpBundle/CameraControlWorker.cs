using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// カメラ制御セッションを<b>専用スレッド 1 本の上だけ</b>で開き・使い・畳むための実行役。
///
/// <para>
/// <b>なぜ要るか。</b> <c>MFCreateDeviceSource</c> はデバイスを実際に起動するので、
/// 他アプリが占有している・ドライバの応答が遅いときは数百 ms〜数秒返らない。
/// これを UI スレッドで同期に呼ぶと、「…」を押してからダイアログが出るまで
/// <b>プレビュー描画も録画ボタンも含めてウィンドウ全体が固まる</b>。
/// </para>
/// <para>
/// <b>なぜスレッドプールではないか。</b> COM ポインタをスレッドをまたいで持ち回らない、
/// という制約（<see cref="CameraControl"/> の doc）を保つため。プールに投げると
/// 開いたスレッドと後で <c>Set</c> するスレッドが別になりうる。
/// ここは<b>所有スレッドが 1 本に固定される</b>ので、開く・読む・設定する・畳むの
/// すべてが同じスレッドで起きる。
/// </para>
/// <para>
/// スレッドは MTA（Media Foundation は自由スレッドモデル）。
/// </para>
/// </summary>
public sealed partial class CameraControlWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private CameraControlSession? _session;
    private bool _disposed;

    private CameraControlWorker()
    {
        _thread = new Thread(Pump)
        {
            IsBackground = true,   // 畳み忘れてもプロセス終了を妨げない
            Name = "CameraControl",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Pump()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            // 1 件の失敗で実行役を終わらせない（呼び出し側が Task 越しに受け取る）。
            try { work(); } catch (Exception) { }
        }

        // キューを閉じたあとで畳む ── セッションを作ったのと同じスレッドで解放する。
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// 専用スレッドで<b>デバイスの解決から</b>行い、開けたら実行役を返す。
    ///
    /// <para>
    /// <b>解決も専用スレッドで行う。</b> <c>device-name</c> / <c>device-index</c> から
    /// パスを引くにはデバイスの列挙が要り、これもそれなりに時間がかかる
    /// ── UI スレッドでやると「…」を押してから画面が出るまで固まる。
    /// </para>
    /// </summary>
    /// <param name="srcPipeline">レコーダーの映像ソースのパイプライン文字列。</param>
    public static async Task<(CameraControlWorker? Worker, CameraDeviceResolution Resolution)> OpenAsync(
        string? srcPipeline)
    {
        var worker = new CameraControlWorker();
        var opened = await worker.RunAsync(() =>
        {
            var resolution = CameraControl.ResolveDevicePath(srcPipeline, out string? devicePath);
            var session = resolution == CameraDeviceResolution.Ok
                ? CameraControl.TryOpen(devicePath)
                : null;

            // **開くたびに必ず 1 行残す。** デバイスの列挙（camera.devices）は
            // device-path が既に書かれていれば走らないので、そちらだけを頼りにすると
            // **通常の構成では「どう解決したのか・開けたのか」が一切分からない**
            // ── 実際に「ログに何も出ない」という形で困った。
            // 「カメラ設定が効かない」ときに最初に見る行になる。
            Components.ActivityLog.Info("camera.open",
                $"resolution={resolution} device='{devicePath}' opened={session is not null}"
                + $" controls={session?.SupportedItems().Count ?? 0}");

            return (Session: session, Resolution: resolution);
        }).ConfigureAwait(true);

        if (opened.Session is null)
        {
            worker.Dispose();
            return (null, opened.Resolution);
        }

        worker._session = opened.Session;
        return (worker, CameraDeviceResolution.Ok);
    }

    /// <summary>専用スレッドで <paramref name="work"/> を実行して結果を返す。</summary>
    private Task<T> RunAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryPost(() =>
            {
                try { completion.SetResult(work()); }
                catch (Exception ex) { completion.SetException(ex); }
            }))
        {
            completion.SetCanceled();
        }
        return completion.Task;
    }

    /// <summary>開いているセッションに対する操作を専用スレッドで実行する。</summary>
    public Task<T> UseAsync<T>(Func<CameraControlSession, T> work)
        => RunAsync(() => _session is { } session ? work(session) : default!);

    /// <summary>
    /// 結果を待たずに投函する（スライダー操作の反映用）。
    /// <b>待たない</b>のは、つまみを動かすたびに UI スレッドを止めないため。
    /// </summary>
    public void Post(Action<CameraControlSession> work)
        => TryPost(() =>
        {
            if (_session is { } session)
                work(session);
        });

    private bool TryPost(Action work)
    {
        if (_disposed)
            return false;
        try
        {
            _queue.Add(work);
            return true;
        }
        catch (Exception)
        {
            // 既に閉じている（Dispose と競合した）
            return false;
        }
    }

    /// <summary>
    /// 実行役を畳む。<b>待たない</b> ── 呼び出し元は UI スレッドで、
    /// 畳む処理にはデバイスの <c>Shutdown</c> が含まれるため。
    /// 残った仕事を流し切ってからセッションを解放するのは <see cref="Pump"/> の役目。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _queue.CompleteAdding(); } catch (Exception) { }
    }
}
