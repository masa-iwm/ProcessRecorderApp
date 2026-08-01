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

#if false
                _bus = _pipeline.GetBus();
                _bus.OnMessage += OnBusMessage;
#endif

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

            // appsrc に溜め込みすぎない（表示は最新フレームだけあればよい）
            if (_appSrc.CurrentLevelBuffers < 10)
                _appSrc.PushSample(sample);
        }


#if false
        private void OnBusMessage(Bus sender, Bus.MessageSignalArgs e)
        {
            var message = e.Message;
            switch (message.Type)
            {
                case MessageType.Info:
                    {
                        using var src = message.Handle.GetSrc() == 0 ? null : Gst.Object.NewFromPointer(message.Handle.GetSrc(), false);
                        var name = src?.GetPathString();

                        message.ParseInfo(out var gerror, out var debug);
                        using (gerror)
                        {
                            if (debug is not null)
                                Console.Error.WriteLine($"INFO:\n{debug}");
                        }
                    }
                    break;
                case MessageType.Warning:
                    {
                        using var src = message.Handle.GetSrc() == 0 ? null : Gst.Object.NewFromPointer(message.Handle.GetSrc(), false);
                        var name = src?.GetPathString();

                        /* dump graph on warning */
                        var pipeline = sender.Parent as Bin;
                        if (pipeline is not null)
                            Functions.DebugBinToDotFileWithTs(pipeline, DebugGraphDetails.All, $"{nameof(GstPreviewer)}.{pipeline.Name ?? "unknown"}.warning");

                        message.ParseWarning(out var gerror, out var debug);
                        using (gerror)
                        {
                            Console.Error.WriteLine($"WARNING: from element {name}: {gerror.Message}");
                            if (debug is not null)
                                Console.Error.WriteLine($"Additional debug info:\n{debug}");
                        }
                    }
                    break;
                default:
                    break;
            }
        }
#endif


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
