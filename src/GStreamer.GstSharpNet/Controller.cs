using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
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
        /// ライブプレビューの供給元（リモート操作の <c>preview.mp4</c> が引く）。
        /// <b>呼ぶのは UI スレッド</b> ── <see cref="Recorders"/> は UI スレッド所有である。
        /// </summary>
        public Components.IPreviewStreamSource PreviewStreams { get; }

        /// <summary>
        /// <see cref="Controller.PreviewStreams"/> の実体。<b>対象の解決規則は CLI と同じ</b>
        /// （<see cref="RecorderCliRules.ResolveTargetIndex"/>: 数値はインデックス、
        /// それ以外は名前の序数完全一致）── 規則を 2 か所に書かないため、
        /// ここは同じ並びの一覧に対してその関数を呼ぶだけにしてある。
        /// </summary>
        private sealed class PreviewStreamSource(Controller owner) : Components.IPreviewStreamSource
        {
            public bool TrySubscribe(
                string target,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Components.PreviewSubscription? subscription,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? reason)
            {
                subscription = null;

                string[] names = [.. owner.Recorders.Select(r => r.Name)];
                int index = RecorderCliRules.ResolveTargetIndex(names, target);
                if (index < 0)
                {
                    // 呼び出し側（HTTP）はこの文字列で 404 と 503 を分ける。
                    reason = Components.PreviewStreamReasons.RecorderNotFound;
                    return false;
                }

                // 初期化が済んでいなければ配信の器そのものが無い（＝「まだ動いていない」）。
                if (owner.Recorders[index].LivePreview is not { } live)
                {
                    reason = "recorder is not running";
                    return false;
                }

                return live.TrySubscribe(out subscription, out reason);
            }
        }

        /// <summary>
        /// DASH プレビューの供給元（<c>DashEndpoints</c> が要求ごとに引く）。
        /// <b>呼ぶのは UI スレッド</b> ── <see cref="Recorders"/> は UI スレッド所有である。
        /// </summary>
        public Components.IDashPreviewSource DashPreviews { get; }

        /// <summary>
        /// <see cref="Controller.DashPreviews"/> の実体。<b>対象の解決規則は CLI と同じ</b>
        /// （<see cref="RecorderCliRules.ResolveTargetIndex"/>）── <see cref="PreviewStreamSource"/>
        /// と同じ並びの一覧に対して同じ関数を呼ぶだけにしてある。
        /// </summary>
        private sealed class DashPreviewSource(Controller owner) : Components.IDashPreviewSource
        {
            public bool TryGetSnapshot(
                string target,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Components.DashPreviewSnapshot? snapshot,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? reason)
            {
                snapshot = null;

                string[] names = [.. owner.Recorders.Select(r => r.Name)];
                int index = RecorderCliRules.ResolveTargetIndex(names, target);
                if (index < 0)
                {
                    // 呼び出し側（HTTP）はこの文字列で 404 と 503 を分ける。
                    reason = Components.PreviewStreamReasons.RecorderNotFound;
                    return false;
                }

                // 初期化が済んでいなければ配信の器そのものが無い（＝「まだ動いていない」）。
                if (owner.Recorders[index].DashPreview is not { } dash)
                {
                    reason = "recorder is not running";
                    return false;
                }

                return dash.TryGetSnapshot(out snapshot, out reason);
            }

            public bool TryGetQuality(
                string target,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Components.PreviewQualityState? state,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? reason)
            {
                if (Resolve(target, out var recorder, out reason))
                {
                    state = recorder.GetPreviewQualityState();
                    return true;
                }

                state = null;
                return false;
            }

            public bool TrySetQuality(
                string target,
                string qualityId,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Components.PreviewQualityState? state,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? reason)
            {
                if (!Components.PreviewQualityPresets.IsValidId(qualityId))
                {
                    throw new ArgumentException(
                        $"'{qualityId}' is not a preview quality id", nameof(qualityId));
                }

                if (!Resolve(target, out var recorder, out reason))
                {
                    state = null;
                    return false;
                }

                // **カスタムは「指示なし」。** null に戻すことで、配信は設定 4 値へ戻る。
                recorder.PreviewQualityPreset =
                    string.Equals(qualityId, Components.PreviewQualityPresets.Custom, StringComparison.Ordinal)
                        ? null
                        : qualityId;

                state = recorder.GetPreviewQualityState();
                return true;
            }

            /// <summary>
            /// 対象を 1 つに解決する。<b>画質の 2 つは配信エンジンの有無を条件にしない</b>
            /// ── 初期化前でも選択肢と指示は答えられる（<see cref="TryGetSnapshot"/> は
            /// 配信物そのものを返すので、あちらだけが「まだ動いていない」で失敗する）。
            /// </summary>
            private bool Resolve(
                string target,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EventRecorder? recorder,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? reason)
            {
                string[] names = [.. owner.Recorders.Select(r => r.Name)];
                int index = RecorderCliRules.ResolveTargetIndex(names, target);
                if (index < 0)
                {
                    recorder = null;
                    reason = Components.PreviewStreamReasons.RecorderNotFound;
                    return false;
                }

                recorder = owner.Recorders[index];
                reason = null;
                return true;
            }
        }

        /// <summary>
        /// 録画トランスコードの供給元（<c>RecordingEndpoints</c> が要求ごとに引く）。
        /// <b>レコーダーには触らないので、呼ぶスレッドを問わない</b>
        /// ── 変換元は録画済みのファイルで、動いているパイプラインとは無関係である。
        /// </summary>
        public Components.ITranscodeSource Transcodes => _transcodes;

        private readonly TranscodeStreams _transcodes;

        /// <summary>
        /// この実機の能力。<b>要求ごとに読む</b> ── 判定は
        /// <see cref="StaticInitialize"/> のプローブが済んでいることが前提で、
        /// <see cref="Controller"/> の生成との前後関係に依存させない。
        /// </summary>
        private static Components.TranscodeCapability CurrentCapability()
            => EncoderCatalog.LastH264Decoder is { } decoder
                ? new Components.TranscodeCapability(true, decoder)
                : new Components.TranscodeCapability(false, null);

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
            PreviewStreams = new PreviewStreamSource(this);
            DashPreviews = new DashPreviewSource(this);
            _transcodes = new TranscodeStreams(
                CurrentCapability, Components.AuxiliaryEncoderSlots.Shared);

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
        /// <see cref="GstEventRecorder_Preview"/> は各レコーダーのプレビュー枝の <c>appsink</c>
        /// コールバック（＝枝のストリーミングスレッド）から走る。
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

        /// <summary>
        /// プレビューの映像の表示サイズが変わったことを素通しする
        /// （<see cref="Previewer.VideoSizeChanged"/>）。構図補助線の配置に使う。
        /// <b>発火はプレビュー用スレッド</b>なので、UI 側が <c>DispatcherQueue</c> で移すこと。
        /// </summary>
        public event EventHandler<PreviewVideoSizeEventArgs>? PreviewVideoSizeChanged
        {
            add => Previewer.VideoSizeChanged += value;
            remove => Previewer.VideoSizeChanged -= value;
        }

        [ObservableProperty]
        public partial EventRecorder? SelectedRecorder { get; set; }

        partial void OnSelectedRecorderChanged(EventRecorder? value)
        {
            // 切り替えた瞬間に表示サイズを「未知」へ戻す。戻さないと、新しいレコーダーの
            // 最初のフレームが届くまでのあいだ**前のレコーダーのアスペクトで補助線が引かれる**
            // （フレームを push するのは選択中のレコーダーだけなので、未初期化のレコーダーへ
            // 切り替えた場合はそのまま残り続ける）。
            lock (_previewGate)
                Previewer.ResetVideoSize();
        }

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
                    // トランスコードを先に畳む（録画とは無関係だが、握っている
                    // 補助エンコーダー枠を返してからレコーダーを止める）。
                    _transcodes.Dispose();

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
            try
            {
                // パイプへのリダイレクト時も ANSI エスケープで色付きデバッグ出力させる
                // （既定の Windows コンソール API 色はパイプでは無色になるため。
                //   ユーザーが環境変数で明示指定している場合は尊重して上書きしない）
                if (Environment.GetEnvironmentVariable("GST_DEBUG_COLOR_MODE") is null)
                {
                    Environment.SetEnvironmentVariable("GST_DEBUG_COLOR_MODE", "unix");
                }

                // **ネイティブ一式をどこから読むかはアプリが決めない。** 探索の順序
                // （PATH のディレクトリ走査 → 環境変数 → レジストリ → 既定の導入先 → MSYS2 →
                //  同梱の runtimes\<rid>）はバインディングのローダーが持ち、勝った段と
                // ディレクトリ・系統（MinGW / MSVC）を ResolvedOrigin / ResolvedDirectory /
                // ResolvedFlavor で公開する ── アプリはそれを下の gst.runtime に写すだけ。
                // 混成（本体と glib が別の根）はローダーのピン（最初にロードした根に固定）が防ぐ。
                Gst.App.GstApp.Initialize();   // ネイティブのロード + gst_init + App 型の登録
                Gst.Base.GstBase.Initialize(); // BaseSrc 等の決定的な型登録（msg.Src is BaseSrc 用）

                // 診断の購読: 基底型へのフォールバック（型未登録の兆候）と、
                // ネイティブコールバック境界で捕捉された例外を activity.log へ残す。
                global::GstSharp.TypeFallback += f => Components.ActivityLog.Info("gst.typefallback",
                    $"instance={f.InstanceType} wrapped-as={f.WrapperType}");
                global::GstSharp.UnhandledCallbackException += ex => Components.ActivityLog.Error("gst.callback", $"{ex}");

                // ここから先はネイティブを呼んでよい。**この1行だけが立てる**
                // ── AppSettings は Initialize より前に読み込まれ、その setter から
                // DebugLogEx.TrySetThreshold へ来る。フラグが早すぎると、その呼び出しが
                // **gst_init より前にネイティブを解決してピン**してしまい、
                // 下の gst.runtime も本来の初期化の結果ではなくなる。
                DebugLogEx.IsGstInitialized = true;

                // **どこから読まれたか**を1行残す。Initialize の後でなければ意味が無い
                // ── ローダーが解決するのは最初の Initialize なので、それより前は全部空になる。
                Components.ActivityLog.Info("gst.runtime", DescribeRuntime());

                // Initialize の後に1回だけ H.264 エンコーダーの存在を確認する。
                // GPU 系プラグインは対応ハードウェアが無いと要素ファクトリを登録しないため、
                // この結果がそのまま「この実機で使えるエンコーダー」になる。
                // 出力先は activity.log（複写により アプリ内 Log 画面と AppSettings.DebugLogFile へも届く）。
                var report = EncoderCatalog.Probe();
                Components.ActivityLog.Info("gst.encoders", report.ToLogLine());

                // 同じ段で H.264 デコーダーも 1 回だけ確認する（録画トランスコードの可否）。
                // **候補はハードウェアだけ**なので、無い機械では録画トランスコードを提供しない。
                string? decoder = EncoderCatalog.ProbeH264Decoder(EncoderCatalog.ProbeWithGStreamer);
                Components.ActivityLog.Info("gst.decoders",
                    $"h264={decoder ?? "(none)"} transcode={(decoder is not null ? "True" : "False")}");
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
                //
                // ロードに失敗したときはバインディングが**実際に試したパス**を持っている
                // （各項目に「なぜ試したか」が付く）。error= より前に置く ── ex は複数行になり、
                // 後ろへ足すと行解析から見えなくなる。
                string detail = DescribeRuntime();
                if (ex is Gst.Interop.GstNativeLoadException loadEx)
                    detail += $" attemptedPaths=[{string.Join(", ", loadEx.AttemptedPaths)}]";
                Components.ActivityLog.Error("gst.runtime", detail + $" error={ex}");

                _ = PInvoke.MessageBox(HWND.Null,
                    $"{ex}",
                    "GStreamer initialize error",
                    MESSAGEBOX_STYLE.MB_ICONERROR | MESSAGEBOX_STYLE.MB_OK);
                throw;
            }
        }

        /// <summary>
        /// <c>activity.log</c> の <c>gst.runtime</c> に出す1行を組み立てる。
        ///
        /// <para>
        /// 出すのは<b>ローダーが選んだ結果</b>（<c>selected</c> / <c>flavor</c> / <c>dir</c> /
        /// <c>source</c>）と、<b>Windows が実際にロードしたモジュールのパス</b>
        /// （<c>core</c> / <c>glib</c>）の両方。前者だけでは、防ごうとしている混成そのものを
        /// 見逃す。<c>mixed</c> は本体と glib が同じディレクトリから来たかで、
        /// どちらかのパスが取れなければ判定しない（<c>unknown</c>）。
        /// </para>
        /// <para>
        /// <c>dir</c> が <c>(search-path)</c> なのは、OS の探索パスで見つかって
        /// ローダーがディレクトリをピンしていない場合（<c>ResolvedDirectory</c> が null）。
        /// <c>source=</c> は空白を含む説明文なので<b>必ず最後</b>に置く ── 前へ入れると、
        /// 後ろの項目を位置で読む道具（<c>tools/Verify-GpuEncoders.ps1</c>）から見えなくなる。
        /// </para>
        /// </summary>
        private static string DescribeRuntime()
        {
            string? corePath = Gst.Interop.NativeLoader.GetLoadedModulePath("Gst");
            string? glibPath = Gst.Interop.NativeLoader.GetLoadedModulePath("GLib");

            // 同じディレクトリから来ていれば混成ではない。どちらかが取れないときは判定しない。
            string mixed = corePath is null || glibPath is null
                ? "unknown"
                : (string.Equals(Path.GetDirectoryName(corePath), Path.GetDirectoryName(glibPath),
                                 StringComparison.OrdinalIgnoreCase) ? "False" : "True");

            return $"selected={Gst.Interop.NativeLoader.ResolvedOrigin?.ToString() ?? "(none)"}"
                 + $" flavor={Gst.Interop.NativeLoader.ResolvedFlavor?.ToString() ?? "(none)"}"
                 + $" dir={Gst.Interop.NativeLoader.ResolvedDirectory ?? "(search-path)"}"
                 + $" core={corePath ?? "(not loaded)"} glib={glibPath ?? "(not loaded)"}"
                 + $" mixed={mixed}"
                 + $" source={Gst.Interop.NativeLoader.ResolvedSourceDescription ?? "(none)"}";
        }
    }
}
