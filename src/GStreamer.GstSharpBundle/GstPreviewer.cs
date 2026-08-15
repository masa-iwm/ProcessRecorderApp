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
        private Gst.App.AppSrc? _appSrc;
        private Element? _sink;
        private Bus? _bus;

        /// <summary>実行時障害を記録済みか（1パイプラインにつき1行に抑える）。</summary>
        private bool _busErrorLogged;

        /// <summary>直近に通知した表示サイズ（変化したときだけ通知するための控え）。</summary>
        private int _videoWidth;
        private int _videoHeight;

        /// <summary>
        /// プレビューへ流れている映像の<b>表示サイズ</b>が変わったときに発火する
        /// （0x0 は「未知」＝まだ 1 枚も来ていない／別のレコーダーへ切り替えた直後）。
        ///
        /// <para>
        /// 用途は構図補助線の配置。<c>d3d12swapchainsink</c> は
        /// <c>force-aspect-ratio=true</c> でレターボックスを作るので、
        /// <b>パネルの大きさだけでは映像が実際に出ている範囲が分からない</b>。
        /// </para>
        /// <para>
        /// <b>発火するのはプレビュー用スレッド</b>（<c>PushSample</c> の呼び出し元）。
        /// UI へ反映する側が <c>DispatcherQueue</c> で移すこと。
        /// </para>
        /// </summary>
        public event EventHandler<PreviewVideoSizeEventArgs>? VideoSizeChanged;

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

                _pipeline = (Pipeline)Parse.Launch(PipelineStr);
                _appSrc = (Gst.App.AppSrc)_pipeline.GetByName("src")!;
                _sink = _pipeline.GetByName("sink")!;

                // bus watch（OnMessage）は使えない ── GMainLoop が無いので発火しない
                // （EventRecorder と同じ理由）。PushSample の周期でポーリングして汲む。
                _bus = _pipeline.Bus;
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

            // **毎フレームは読まない。** サイズが分かるまでの数フレームだけで足りる
            // （解像度が変わるのはパイプラインを組み直したときで、そのとき Close→Initialize と
            //  ResetVideoSize を通る）。毎フレーム読むと、その回数だけ下の借用参照を触ることになる。
            if (_videoWidth <= 0)
                UpdateVideoSize(sample);

            // appsrc に溜め込みすぎない（表示は最新フレームだけあればよい）
            if (_appSrc.CurrentLevelBuffers < 10)
                _appSrc.PushSample(sample);
        }

        /// <summary>
        /// サンプルのキャップスから表示サイズを読み、変わっていたら通知する。
        ///
        /// <para>
        /// <b>読むのは <c>sample.Caps</c>（＝実際にネゴシエートされた結果）。</b>
        /// シンク要素側の caps を使ってはいけない ── あちらは要素に設定された
        /// （テンプレート由来でしばしば <c>ANY</c> な）キャップスであってネゴシエート結果ではない
        /// （<c>EventRecorder</c> が <c>appsrc</c> のキャップスで踏んでいるのと同じ罠）。
        /// </para>
        /// <para>
        /// <b>毎フレーム通知しない。</b> ここは 1 秒間に何十回も通る経路で、
        /// 通知先は UI スレッドへの投函を行う ── 変化時だけに絞らないと
        /// ディスパッチャを埋める。
        /// </para>
        /// <para>
        /// 画素比（<c>pixel-aspect-ratio</c>）が 1:1 でない場合は<b>幅へ掛けて表示幅にする</b>。
        /// シンクがアスペクトを保つのは表示アスペクト（DAR）に対してなので、
        /// 画素のままの幅で計算すると補助線が映像の縁とずれる。
        /// </para>
        /// </summary>
        private void UpdateVideoSize(Sample sample)
        {
            // **破棄しない。** gst_sample_get_caps() は transfer none で、caps を所有するのは
            // サンプルの側である ── Dispose を呼ぶと借り物の参照を解放することになり、
            // まだ使われている caps が落ちる。毎フレーム通る経路だったので影響が出やすく、
            // **自動復帰のあとプレビューがカタつく**という形で実機に現れた
            // （パイプラインを組み直すと直るのは、caps が作り直されるため）。
            // 既存の 2 箇所（EventRecorder / ContinuousRecorder）も破棄していない。
            var caps = sample.Caps;
            var structure = caps?.GetStructure(0);
            if (structure is null
                || !structure.GetInt("width", out int width)
                || !structure.GetInt("height", out int height)
                || width <= 0 || height <= 0)
            {
                return;
            }

            if (structure.GetFraction("pixel-aspect-ratio", out int parNumerator, out int parDenominator)
                && parNumerator > 0 && parDenominator > 0 && parNumerator != parDenominator)
            {
                width = (int)Math.Round((double)width * parNumerator / parDenominator);
            }

            if (width == _videoWidth && height == _videoHeight)
                return;

            _videoWidth = width;
            _videoHeight = height;
            VideoSizeChanged?.Invoke(this, new PreviewVideoSizeEventArgs(width, height));
        }

        /// <summary>
        /// 表示サイズを「未知」（0x0）へ戻して通知する。レコーダーを切り替えたときに呼ぶ
        /// ── 呼ばないと、次のフレームが来るまで<b>前のレコーダーのアスペクトで
        /// 補助線が引かれたまま</b>になる。
        /// </summary>
        public void ResetVideoSize()
        {
            if (_videoWidth == 0 && _videoHeight == 0)
                return;

            _videoWidth = 0;
            _videoHeight = 0;
            VideoSizeChanged?.Invoke(this, new PreviewVideoSizeEventArgs(0, 0));
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

                    // GException のコンストラクタが g_error_free まで面倒を見る（Dispose 不要）。
                    msg.ParseError(out GLib.GException gerror, out string debug);
                    Components.ActivityLog.Error("preview.error", $"{gerror.Message} debug={debug}");
                    _busErrorLogged = true;
                }
            }
        }


// bus watch（Bus.OnMessage）で書いてはいけない ── 実行中の GMainLoop に紐づく
        // bus watch からしか発火しない（EventRecorder の DrainBuses と同じ理由）。
        // 実行時障害の観測は DrainBusErrors のポーリングで行う。


        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected static void Log(DebugLevel level, string message,
            GLib.Object? @object = null,
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
            // 面が無くなるので表示サイズも未知へ戻す（補助線を消すため）。
            ResetVideoSize();
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

    /// <summary>プレビューの映像の表示サイズ。0x0 は「未知」。</summary>
    public sealed class PreviewVideoSizeEventArgs(int width, int height) : EventArgs
    {
        /// <summary>表示幅（画素比を掛けたあと）。未知なら 0。</summary>
        public int Width { get; } = width;

        /// <summary>表示高さ。未知なら 0。</summary>
        public int Height { get; } = height;
    }
}
