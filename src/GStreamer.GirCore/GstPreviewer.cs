using Gst;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ProcessRecorderApp.GStreamer
{
    public partial class Previewer : IDisposable
    {
        private bool _isInitialized;

        private Pipeline? _pipeline;
        private GstApp.AppSrc? _appSrc;
        private Element? _sink;
        private Bus? _bus;

        /// <summary>実行時障害を記録済みか（1パイプラインにつき1行に抑える）。</summary>
        private bool _busErrorLogged;

        public Previewer()
        {
        }

        /// <summary>
        /// プレビュー用パイプラインを構築して再生を開始する。
        /// 初期化済みの場合は何もしない（画面の再表示などで繰り返し呼ばれるため、
        /// 例外ではなく no-op とする）。
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
                return;
            try
            {
                // d3d12swapchainsink が DXGI コンポジションスワップチェーン
                // (IDXGIFactory2::CreateSwapChainForComposition) を生成する。これを
                // ISwapChainPanelNative.SetSwapChain で SwapChainPanel にバインドする（HWND 不要）。
                const string PipelineStr =
                    "appsrc format=time name=src ! queue ! d3d12swapchainsink name=sink sync=false";

                _pipeline = (Pipeline)Functions.ParseLaunch(PipelineStr);
                _appSrc = (GstApp.AppSrc)_pipeline.GetByName("src")!;
                _sink = _pipeline.GetByName("sink")!;

                // bus watch（OnMessage）は使えない ── GMainLoop が無いので発火しない
                // （EventRecorder と同じ理由）。PushSample の周期でポーリングして汲む。
                _bus = _pipeline.GetBus();
                _busErrorLogged = false;

                if (_pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
                    throw new InvalidOperationException("ERROR: pipeline doesn't want to play.");

                _isInitialized = true;
            }
            catch
            {
                Close();
                throw;
            }
        }

        /// <summary>
        /// d3d12swapchainsink が生成した DXGI コンポジションスワップチェーンのハンドルを返す。
        /// ISwapChainPanelNative.SetSwapChain に渡す。まだ生成されていなければ 0。
        /// </summary>
        public nint GetSwapChainHandle()
            => _sink is null ? 0 : _sink.GetPointerProperty("swapchain");

        /// <summary>
        /// スワップチェーンの解像度を更新する（SwapChainPanel のサイズ追従用）。
        /// swapchain-width/height は読み取り専用のため、"resize" アクションシグナルで行う。
        /// パネルの物理ピクセルサイズを指定すると、映像がパネル全面にフィットする。
        /// </summary>
        public void ResizeSwapChain(int width, int height)
        {
            if (_sink is null || width <= 0 || height <= 0)
                return;
            _sink.EmitResize((uint)width, (uint)height);
        }

        /// <summary>
        /// プレビュー用パイプラインへフレームを供給する。
        /// 未初期化／破棄済みの場合は黙って捨てる（呼び出し元はレコーダーのプレビュースレッドであり、
        /// 画面遷移に伴う破棄と競合しうるため、例外にせず no-op とする）。
        /// </summary>
        public void PushSample(Sample sample)
        {
            if (!_isInitialized || _appSrc is null)
                return;

            DrainBusErrors();

            // appsrc に溜め込みすぎない（表示は最新フレームだけあればよい）
            if (_appSrc.CurrentLevelBuffers < 10)
                _appSrc.PushSample(sample);
        }

        /// <summary>
        /// バスの Error を汲んで記録する。プレビューの失敗は録画を止めない方針のため
        /// 復帰は試みないが、無記録だと「プレビューだけ黙って固まり、activity.log に
        /// 何も残らない」── レコーダー側が DrainBuses で塞いでいる「静かに壊れる」
        /// クラスの穴がこちらにだけ残る。記録は 1 パイプラインにつき 1 行
        /// （エラー後のパイプラインは死んでおり、以後のメッセージに追加情報は無い。
        /// GstBus のキューは無制限なので、汲むこと自体は続ける）。
        /// </summary>
        private void DrainBusErrors()
        {
            // ローカルへ 1 回読む（Close は UI スレッドから走り、ここはプレビュースレッド）。
            var bus = _bus;
            if (bus is null)
                return;

            // 1 周あたりの取り出しは有界にする ── 洪水の最中に無界だと、Close の
            // Join(5000) に間に合わない（EventRecorder.DrainBus と同じ理由）。
            for (int i = 0; i < 32 && bus.TimedPopFiltered(0, MessageType.Error) is { } msg; i++)
            {
                using (msg)
                {
                    if (_busErrorLogged)
                        continue;

                    msg.ParseError(out var gerror, out var debug);
                    string? message;
                    using (gerror)
                        message = gerror.Message;
                    Components.ActivityLog.Error("preview.error", $"{message} debug={debug}");
                    _busErrorLogged = true;
                }
            }
        }


// bus watch（Bus.OnMessage）で書いてはいけない ── 実行中の GMainLoop に紐づく
        // bus watch からしか発火しない（EventRecorder の DrainBuses と同じ理由）。
        // 実行時障害の観測は DrainBusErrors のポーリングで行う。


        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected static void Log(DebugLevel level, string message,
            GObject.Object? @object = null,
            [System.Runtime.CompilerServices.CallerFilePath] string file = "",
            [System.Runtime.CompilerServices.CallerLineNumber] int line = 0,
            [System.Runtime.CompilerServices.CallerMemberName] string function = "")
            => DebugLogEx.Log(level, message, @object, file, line, function);

        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Close();
                }

                disposedValue = true;
            }
        }

        /// <summary>
        /// プレビュー用パイプラインのグラフを <c>.dot</c> として書き出し、書いた絶対パスを返す。
        /// パイプラインが無ければ空を返す。
        ///
        /// <para>
        /// <see cref="EventRecorder.WriteDebugGraphs"/> と違ってロックを取らない
        /// ── こちらの <see cref="Close"/> は UI スレッドからしか呼ばれない
        /// （<c>Controller.ShutdownPreview</c> / <c>Dispose</c>）ので、同じ UI スレッドで
        /// 走るこのメソッドと競合しない。
        /// </para>
        /// </summary>
        public IReadOnlyList<string> WriteDebugGraphs(string directory, System.DateTime timestamp)
            => _pipeline is { } pipeline
                ? (string[])[DebugLogEx.WriteDotFile(pipeline, directory, "preview", timestamp)]
                : (string[])[];

        /// <summary>
        /// パイプラインを解放する。破棄したフィールドは null 化して冪等にしてある
        /// （<see cref="Initialize"/> の失敗時にも catch から呼ばれるため、
        ///  二重解放を防ぐ必要がある）。
        /// </summary>
        public void Close()
        {
            _isInitialized = false;
            _pipeline?.SetState(State.Null);
            _appSrc?.Dispose();
            _appSrc = null;
            _sink?.Dispose();
            _sink = null;
            _bus?.Dispose();
            _bus = null;
            _pipeline?.Dispose();
            _pipeline = null;
        }

        // ファイナライザは定義しない（Dispose(false) が何も解放しないため。EventRecorder 参照）。
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
