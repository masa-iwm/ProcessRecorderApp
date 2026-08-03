using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ProcessRecorderApp.GStreamer
{
    public partial class Controller : ObservableObject, IDisposable
    {
        public Previewer Previewer { get; init; } = new();
        public GstEventRecorderCollection Recorders { get; init; } = [];

        /// <summary>
        /// Preview イベントを購読済みのレコーダー。
        /// Reset の通知はコレクションが既に空になった後に来るため、
        /// コレクション自体を走査しても購読解除できない（＝購読が漏れる）。
        /// 購読対象を別途保持しておき、これを正として解除する。
        /// </summary>
        private readonly List<EventRecorder> _previewSubscribed = [];

        private void SubscribePreview(EventRecorder r)
        {
            if (_previewSubscribed.Contains(r))
                return;
            r.Preview += GstEventRecorder_Preview;
            _previewSubscribed.Add(r);
        }

        private void UnsubscribePreview(EventRecorder r)
        {
            if (!_previewSubscribed.Remove(r))
                return;
            r.Preview -= GstEventRecorder_Preview;
        }

        private void UnsubscribeAllPreview()
        {
            foreach (var r in _previewSubscribed)
                r.Preview -= GstEventRecorder_Preview;
            _previewSubscribed.Clear();
        }

        public Controller()
        {
            Recorders.CollectionChanged += (_, e) =>
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        if (e.NewItems is not null)
                            foreach (EventRecorder r in e.NewItems)
                                SubscribePreview(r);
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        if (e.OldItems is not null)
                            foreach (EventRecorder r in e.OldItems)
                                UnsubscribePreview(r);
                        break;
                    case NotifyCollectionChangedAction.Replace:
                        if (e.OldItems is not null && e.NewItems is not null
                            && !e.OldItems.OfType<EventRecorder>().SequenceEqual(
                                e.NewItems.OfType<EventRecorder>()))
                        {
                            foreach (EventRecorder r in e.OldItems)
                                UnsubscribePreview(r);
                            foreach (EventRecorder r in e.NewItems)
                                SubscribePreview(r);
                        }
                        break;
                    case NotifyCollectionChangedAction.Reset:
                        UnsubscribeAllPreview();
                        break;
                }
            };
        }


        /// <summary>
        /// プレビュー面（<see cref="Previewer"/> のパイプライン／スワップチェーン）の排他。
        ///
        /// プレビュー面はページ寿命（<c>MainPage</c> の Loaded/Unloaded）で初期化・破棄される一方、
        /// <see cref="GstEventRecorder_Preview"/> は各レコーダーの プレビュー取り出しスレッドから走る。
        /// 無保護だと <c>PushSample</c> 実行中に <c>appsrc</c> が破棄されてネイティブクラッシュするため、
        /// プレビュー面に触る全メンバをこのロックで直列化する。
        /// </summary>
        private readonly object _previewGate = new();

        /// <summary>
        /// プレビュー用パイプライン（d3d12swapchainsink）を初期化する。冪等（初期化済みなら no-op）。
        /// 失敗しても例外は投げずログのみ ── プレビューが出ないことはアプリの中核機能
        /// （常時バッファリングと録画）の停止理由にならない（WARP 等 GPU の無い環境で起こりうる）。
        /// </summary>
        public void InitializePreview()
        {
            lock (_previewGate)
            {
                try
                {
                    Previewer.Initialize();
                }
                catch (Exception ex)
                {
                    DebugLogEx.Log(Gst.DebugLevel.Error, $"Preview initialize failed (recording is unaffected).\n{ex}");
                }
            }
        }

        /// <summary>
        /// プレビュー用パイプラインを解放する。冪等。
        /// <see cref="Previewer"/> 自体は破棄しない（<see cref="InitializePreview"/> で再初期化するため）。
        /// </summary>
        public void ShutdownPreview()
        {
            lock (_previewGate)
                Previewer.Close();
        }

        /// <summary>
        /// プレビュー用スワップチェーンのハンドルを取得する（SwapChainPanel へのバインド用）。
        /// </summary>
        public nint GetSwapChainHandle()
        {
            lock (_previewGate)
                return Previewer.GetSwapChainHandle();
        }

        /// <summary>プレビュー用スワップチェーンの解像度を更新する（SwapChainPanel のサイズ追従用）。</summary>
        public void ResizeSwapChain(int width, int height)
        {
            lock (_previewGate)
                Previewer.ResizeSwapChain(width, height);
        }

        [ObservableProperty]
        public partial EventRecorder? SelectedRecorder { get; set; }

        private void GstEventRecorder_Preview(object? sender, PreviewEventArgs e)
        {
            if (sender?.Equals(SelectedRecorder) != true)
                return;

            // レコーダーのプレビュースレッドを止めないため、プレビュー面の初期化/破棄と
            // 競合したフレームは待たずに捨てる（表示は最新フレームだけあればよい）。
            if (!System.Threading.Monitor.TryEnter(_previewGate))
                return;
            try
            {
                Previewer.PushSample(e.Sample);
            }
            finally
            {
                System.Threading.Monitor.Exit(_previewGate);
            }
        }


        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // プレビュー面を先に閉じてからレコーダーを破棄する
                    // （逆順だと、破棄途中のレコーダーのプレビュースレッドが生きたまま
                    //   PushSample を呼びうる）。
                    ShutdownPreview();
                    Previewer.Dispose();
                    foreach (var r in Recorders)
                        r.Dispose();
                    Recorders.Clear();
                }

                disposedValue = true;
            }
        }

        // ファイナライザは定義しない（Dispose(false) が何も解放しないため。EventRecorder 参照）。
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// いま生きているパイプライン（全レコーダーの sink/src とプレビュー）のグラフを
        /// <paramref name="directory"/> へ書き出し、書いた絶対パスを返す。
        ///
        /// <para>
        /// 1回の呼び出しで<b>時刻を1つだけ</b>使う ── 同じ操作で出たファイルが
        /// 名前で1組と分かるようにするため。
        /// </para>
        /// <para>
        /// 途中で失敗しても他を諦めない。<b>失敗はその場で activity.log に残す</b>
        /// ── 黙って件数が減ると「保存したのに無い」としか見えない。
        /// </para>
        /// </summary>
        public IReadOnlyList<string> WriteDebugGraphs(string directory)
        {
            var timestamp = System.DateTime.Now;
            List<string> written = [];

            foreach (EventRecorder recorder in Recorders)
            {
                try
                {
                    written.AddRange(recorder.WriteDebugGraphs(directory, timestamp));
                }
                catch (Exception ex)
                {
                    Components.ActivityLog.Error("gst.dot", $"recorder='{recorder.Name}' error={ex.Message}");
                }
            }

            try
            {
                written.AddRange(Previewer.WriteDebugGraphs(directory, timestamp));
            }
            catch (Exception ex)
            {
                Components.ActivityLog.Error("gst.dot", $"preview error={ex.Message}");
            }

            return written;
        }

        [SupportedOSPlatform("windows5.0")]
        public static void StaticInitialize()
        {
            // catch からも見えるようにここで宣言する ── 失敗したときこそ
            // 「どこを探したのか」が要る（下の catch を参照）。
            IReadOnlyList<GStreamerRuntimeCandidate> candidates = [];
            GStreamerRuntimeCandidate? chosen = null;
            try
            {
                // GStreamer のネイティブ一式をどこから読むかを決める。
                // 同梱物は**最後の候補**であって既定ではない（GStreamerRuntimeLocator の
                // doc コメントに優先順位と、候補を全部 PATH に繋いではいけない理由がある）。
                candidates = GStreamerRuntimeLocator.Discover(
                    Path.GetDirectoryName(Environment.ProcessPath),
                    RuntimeInformation.RuntimeIdentifier);
                chosen = GStreamerRuntimeLocator.Select(
                    GStreamerRuntimeLocator.SplitPath(Environment.GetEnvironmentVariable("PATH")),
                    candidates,
                    directory => File.Exists(Path.Combine(
                        directory, GStreamerRuntimeLocator.CoreLibraryFileName)));

                Environment.SetEnvironmentVariable("PATH",
                    GStreamerRuntimeLocator.ComposePath(
                        Environment.GetEnvironmentVariable("PATH"), candidates, chosen));

                // パイプへのリダイレクト時も ANSI エスケープで色付きデバッグ出力させる
                // （既定の Windows コンソール API 色はパイプでは無色になるため。
                //   ユーザーが環境変数で明示指定している場合は尊重して上書きしない）
                if (Environment.GetEnvironmentVariable("GST_DEBUG_COLOR_MODE") is null)
                {
                    Environment.SetEnvironmentVariable("GST_DEBUG_COLOR_MODE", "unix");
                }

                GstApp.Module.Initialize();
                GstVideo.Module.Initialize();
                NativeLibrary.SetDllImportResolver(typeof(ExtendMethods).Assembly, ImportResolver.Resolve);
                string[]? args = [];
                Gst.Functions.Init(ref args);

                // ここから先はネイティブを呼んでよい。**この1行だけが立てる**
                // ── AppSettings は Init より前に読み込まれ、その setter から
                // DebugLogEx.TrySetThreshold へ来るので、フラグが早すぎると起動ごと落ちる。
                DebugLogEx.IsGstInitialized = true;

                // **どこから読まれたか**を1行残す。Init の後でなければ意味が無い
                // ── ここで見たいのは「自分が選んだ候補」ではなく
                // 「Windows が実際にロードしたモジュールのパス」で、両者は一致するとは限らない
                // （元の PATH に GStreamer が入っていれば候補より先に勝つ）。
                Components.ActivityLog.Info("gst.runtime",
                    GStreamerRuntimeLocator.DescribeRuntime(candidates, chosen));

                // Gst.Functions.Init の後に1回だけ H.264 エンコーダーの存在を確認する。
                // GPU 系プラグインは対応ハードウェアが無いと要素ファクトリを登録しないため、
                // この結果がそのまま「この実機で使えるエンコーダー」になる。
                // 出力先は activity.log（複写により アプリ内 Log 画面と AppSettings.DebugLogFile へも届く）。
                var report = EncoderCatalog.Probe();
                Components.ActivityLog.Info("gst.encoders", report.ToLogLine());
            }
            catch (Exception ex)
            {
                // **MessageBox より先に activity.log へ書く。** この MessageBox は
                // モーダルで、常駐ワーカーはメッセージループに入る前にここで止まる
                // ── 誰も押さなければプロセスは生き続け、**ログには app.start しか残らない**。
                // GStreamer の解決は実行環境に依存するため、初期化失敗は現実に起こりうる
                // （実測: レジストリ検出の候補を落とす退行注入で、E2E が 180 秒待って
                //   「app.start の1行だけ」で落ちた）。
                //
                // ここが唯一の書き手であることに注意。StaticInitialize は
                // StartResidentWorker の初期化コールバック内、つまり `new App()` より前に走るため、
                // App.LogException の3つの未処理例外ハンドラはまだ張られていない
                // ── この行が無いと理由がどこにも残らない。
                //
                // イベント名は成功時と同じ `gst.runtime` で、レベルだけ ERROR にする
                // （成功と失敗をイベント名で分ける規約は「同じ名前で成功/失敗が混ざる」ことを
                //   避けるためのもので、こちらは詳細の中身自体が別物なので水準で足りる）。
                Components.ActivityLog.Error("gst.runtime",
                    GStreamerRuntimeLocator.DescribeRuntime(candidates, chosen) + $" error={ex}");

                _ = PInvoke.MessageBox(HWND.Null,
                    $"{ex}",
                    "GStreamer initialize error",
                    MESSAGEBOX_STYLE.MB_ICONERROR | MESSAGEBOX_STYLE.MB_OK);
                throw;
            }
        }
    }
}
