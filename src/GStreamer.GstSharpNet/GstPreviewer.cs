using Gst;
using GstApp = Gst.App;      // 既存の GstApp.AppSrc 修飾名をそのまま生かす
using GObject = Gst.GObject; // 既存の GObject.Object 修飾名をそのまま生かす
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

        /// <summary>
        /// 実行時障害を記録済みか（1パイプラインにつき1行に抑える）。0=未記録 / 1=記録済み。
        /// <b>複数のストリーミングスレッドから同時に来うる</b>ので
        /// <c>Interlocked</c> で 1 回に絞る（バスの同期ハンドラは post 元の
        /// ストリーミングスレッドで走る）。
        /// </summary>
        private int _busErrorLogged;

        /// <summary>バスの購読（<c>Dispose</c> が同期ハンドラを外す）。未購読なら null。</summary>
        private IDisposable? _busSubscription;

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
        /// <b>発火するのは <c>PushSample</c> の呼び出し元</b>
        /// （レコーダーのプレビュー枝のストリーミングスレッド）。
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

                _pipeline = (Pipeline)Global.ParseLaunch(PipelineStr);
                _appSrc = (GstApp.AppSrc)_pipeline.GetByName("src")!;
                _sink = _pipeline.GetByName("sink")!;

                _bus = _pipeline.GetBus();
                _busErrorLogged = 0;

                // **購読は PLAYING より前・NULL 状態のうちに。** まだ誰も post しないので
                // 取り付けと汲み切りを直列化するゲート（EventRecorder.SubscribeBus）は要らない
                // （ContinuousRecorder.SubscribeSegment と同じ理由）。
                SubscribeBus(_bus);

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
        {
            if (_sink is null)
                return 0;
            // Value は所有ラッパーなので必ず破棄する。
            using var v = _sink.GetProperty("swapchain");
            return v.GetPointer();
        }

        /// <summary>
        /// スワップチェーンの解像度を更新する（SwapChainPanel のサイズ追従用）。
        /// swapchain-width/height は読み取り専用のため、"resize" アクションシグナルで行う。
        /// パネルの物理ピクセルサイズを指定すると、映像がパネル全面にフィットする。
        /// </summary>
        public void ResizeSwapChain(int width, int height)
        {
            if (_sink is null || width <= 0 || height <= 0)
                return;
            _sink.EmitSignal("resize", (uint)width, (uint)height);
        }

        /// <summary>
        /// プレビュー用パイプラインへフレームを供給する。
        /// 未初期化／破棄済みの場合は黙って捨てる（呼び出し元はレコーダーの<b>プレビュー枝の
        /// ストリーミングスレッド</b>（<c>appsink</c> のコールバック）であり、
        /// 画面遷移に伴う破棄と競合しうるため、例外にせず no-op とする）。
        /// </summary>
        public void PushSample(Sample sample)
        {
            if (!_isInitialized || _appSrc is null)
                return;

            // **毎フレームは読まない。** サイズが分かるまでの数フレームだけで足りる
            // （解像度が変わるのはパイプラインを組み直したときで、そのとき Close→Initialize と
            //  ResetVideoSize を通る）。毎フレーム読むと、その回数だけ caps／structure の
            //  ラッパー生成と解放（参照の増減・Boxed コピー）が入る。
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
        /// <b>読むのは <c>sample.GetCaps()</c>（＝実際にネゴシエートされた結果）。</b>
        /// <c>_sink.GetCaps()</c> を使ってはいけない ── あちらは要素に設定された
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
            // **必ず破棄する。** GstSharp.Net のラッパーは自分の参照を自分で持つ ──
            // GetCaps() はネイティブ caps への参照を 1 本増やして返すので、using で
            // 解放されるのは**こちらの分だけ**で、サンプルが持つ caps は落ちない。
            // （GirCore 時代に「破棄すると自動復帰のあとプレビューがカタつく」となったのは、
            //  transfer none の借用参照を解放していたため。所有規約が逆転したので、
            //  いまは破棄しない方が漏れになる。EventRecorder / ContinuousRecorder も同じ。）
            // GetStructure(0) も Boxed の**所有コピー**を返すので破棄する。
            using var caps = sample.GetCaps();
            if (caps is null)
                return;
            using var structure = caps.GetStructure(0);
            if (!structure.GetInt("width", out int width)
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
        /// バスを <c>Bus.SubscribeSyncDrop</c> で購読する。<b>拾うのは Error だけ</b> ──
        /// プレビューの失敗は録画を止めない方針なので復帰は試みないが、無記録だと
        /// 「プレビューだけ黙って固まり、activity.log に何も残らない」になる。
        /// 記録は 1 パイプラインにつき 1 行（エラー後のパイプラインは死んでおり、
        /// 以後のメッセージに追加情報は無い）。
        ///
        /// <para>
        /// <b><c>Bus.Message</c> / <c>AddWatch</c> は使えない</b> ── どちらも GMainLoop から
        /// 配送されるが、このアプリは GMainLoop を回していないので発火しない。
        /// メインループ無しで push 型に受けられるのは、バスの同期ハンドラだけである
        /// （<c>EventRecorder.SubscribeBus</c> と同じ理由）。
        /// </para>
        /// <para>
        /// 購読より後に post されたメッセージはキューに入らない（配送も破棄もバインディングが
        /// 行う）ので、パイプラインの寿命の間もキューは伸びない。
        /// </para>
        /// <para>
        /// <b>ハンドラの中で捕まえる。</b> 例外を漏らしてもメッセージは <c>Drop</c> され
        /// （所有権の後始末込み）キューは伸びないが、漏らした 1 件は記録されずに終わる
        /// ── 1 件の失敗で以後のメッセージまで落とさないために、ここで握って次の 1 件へ進む。
        /// </para>
        /// </summary>
        private void SubscribeBus(Bus bus)
        {
            _busSubscription = bus.SubscribeSyncDrop((_, message) =>
            {
                try
                {
                    // メッセージのラッパーはハンドラの実行中だけ有効なので Dispose しない。
                    if (message.Type == MessageType.Error
                        && System.Threading.Interlocked.Exchange(ref _busErrorLogged, 1) == 0)
                    {
                        // ParseError は GError を管理例外 (GException) へ写して返す。
                        // ネイティブ側の解放はバインディングが済ませるので、破棄する物は無い。
                        var (gerror, debug) = message.ParseError();
                        Components.ActivityLog.Error("preview.error", $"{gerror.Message} debug={debug}");
                    }
                }
                catch (Exception ex)
                {
                    // activity.log には出さない（preview.error は 1 行に絞る対象）。
                    Log(DebugLevel.Error, $"the preview bus handler failed!\n{ex}");
                }
            });
        }


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
        /// パイプラインを解放する。フィールドは null 化して冪等にしてある
        /// （<see cref="Initialize"/> の失敗時にも catch から呼ばれるため）。
        /// appsrc / sink / bus は interned な GObject ラッパーで破棄しない（null 化のみ）。
        /// パイプラインだけは、このクラスが作った所有物として <c>SetState(Null)</c> の後に
        /// Dispose する（公認の「作った側が畳む」ケース）。
        ///
        /// <para>
        /// <b>バスの購読は状態を動かす前に外す。</b> 購読の <c>Dispose</c> は冪等で、
        /// 未初期化のまま呼ばれる経路（購読が無い）でも安全である。
        /// </para>
        /// </summary>
        public void Close()
        {
            _isInitialized = false;
            // 面が無くなるので表示サイズも未知へ戻す（補助線を消すため）。
            ResetVideoSize();
            _busSubscription?.Dispose();
            _busSubscription = null;
            _pipeline?.SetState(State.Null);
            _appSrc = null;
            _sink = null;
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
