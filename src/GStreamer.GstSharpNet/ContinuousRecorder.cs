using Gst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GObject = Gst.GObject;
using GstApp = Gst.App;
using STTask = System.Threading.Tasks.Task;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// 常時録画エンジンが宿主（<see cref="EventRecorder"/>）へ結果を返すための最小の口。
/// エンジンは <c>EventRecorder</c> の内部状態も <c>_stateLock</c> も知らない。
/// </summary>
internal interface IContinuousRecorderHost
{
    /// <summary>ログ・診断に使うレコーダー名。</summary>
    string Name { get; }

    /// <summary>テンプレートを展開し、出力ディレクトリを解決した絶対パスを返す。</summary>
    string ResolveSegmentPath(string template, int segmentIndex);

    /// <summary>稼働状態・現在のファイル・書いたセグメント数を報告する。</summary>
    void OnContinuousStatus(bool running, string? currentFile, int segmentCount);

    /// <summary>常時録画側だけで起きた障害を報告する（イベント録画の状態には触らない）。</summary>
    void OnContinuousError(string message);
}

/// <summary>
/// <b>常時録画エンジン。</b> <c>tee</c> の常時枝の終端 <c>appsink</c> から H.264 の
/// アクセスユニットを引き、セグメント単位のファイルへ書き出す。
///
/// <para>
/// <b>分割は「書き出しパイプラインの作り直し」で行う。</b> 切り替え時は
/// (1) 次のセグメント用のパイプラインを先に <c>PLAYING</c> にし、
/// (2) 旧パイプラインへ EOS を送って<b>排出はプールスレッドへ逃がし</b>、
/// (3) 当のキーフレームは新しい方へ押し込む ── フレームの欠落はゼロで、
/// <c>splitmuxsink async-finalize</c> と同じことを既存の要素だけで行う
/// （<c>splitmuxsink</c> は同梱ランタイムに無い）。
/// </para>
/// <para>
/// <b>サンプルは <c>appsink</c> のコールバックで受ける。</b> 呼ばれるのは枝の
/// ストリーミングスレッドなので、<c>EventRecorder._stateLock</c> は絶対に取らない
/// ── <c>CloseCore</c> はそのロックを保持したまま sink パイプラインを <c>Null</c> へ
/// 落とし、<b>実行中のコールバックの復帰を待つ</b>。コールバックが処理を続けるかどうかは
/// <see cref="_isAlive"/>（volatile）だけで決め、セグメントの状態に触るのは
/// <see cref="_sampleLock"/> の下だけに閉じる（排他の理由は <see cref="Close"/> の doc）。
/// </para>
/// </summary>
internal sealed partial class ContinuousRecorder : IDisposable
{
    /// <summary>
    /// <see cref="Close"/> が「進行中のサンプル処理が抜ける」のを待つ上限(ms)。
    /// <c>EventRecorder</c> の quiesce（<c>SinkQuiesceTimeoutMs</c>）と同じ 5 秒。
    /// </summary>
    internal const int CallbackExitTimeoutMs = 5000;

    /// <summary>
    /// 同時に排出中にしてよい書き出しパイプラインの本数。
    /// 無制限にすると、mux が詰まったときにネイティブのパイプラインが際限なく積み上がる。
    /// </summary>
    internal const int MaxFinalizersInFlight = 2;

    private readonly IContinuousRecorderHost _host;
    private readonly GstApp.AppSink _source;
    private readonly string _template;
    private readonly long _segmentNs;
    private readonly int _firstSampleBudgetMs;

    private readonly object _finalizerLock = new();

    /// <summary>
    /// セグメントの状態（<see cref="_writer"/> とその周辺）に触ってよい者を 1 人に絞るロック。
    /// 取るのはサンプルのコールバックと <see cref="Close"/> の「最後のセグメントの確定」だけ。
    ///
    /// <para>
    /// <b>保持中に <c>Join</c> / 無期限の待ちを行わない。</b> 中で待つのは
    /// <see cref="WaitForFinalizers"/>（在庫の上限ぶんの有界待ち）と、
    /// 自前で作った書き出しパイプラインの <c>SetState</c> だけ ──
    /// sink パイプラインには触らないので、<c>CloseCore</c> の quiesce と組んだ輪はできない。
    /// </para>
    /// </summary>
    private readonly object _sampleLock = new();

    /// <summary>
    /// 排出中のセグメント（タスクと、そのタスクがまだ書いているパス）。
    /// <b>パスを持たせるのは名前の衝突を防ぐため</b> ── 排出は非同期なので、
    /// 「直前のセグメント」だけを見ていると、さらに前のセグメントと同じ名前を
    /// 引き当てたときに <c>filesink</c> が排出中のファイルを切り詰める。
    /// </summary>
    private readonly List<(STTask Task, string? Path)> _finalizers = [];

    /// <summary>
    /// 押し込みの拒否の抑制。<b>巻き戻しとは別のインスタンスにする</b>
    /// ── 1 つを共有すると、片方の連続の直後に出たもう片方が
    /// 相手の抑制件数を <c>repeated=N</c> として引き継ぎ、診断のための表示が診断を誤らせる
    /// （<c>EventRecorder</c> が Error と Warning を分けているのと同じ理由）。
    /// </summary>
    private readonly BusMessageThrottle _pushWarnings = new();

    /// <summary>PTS の巻き戻しの抑制（B フレームを出すエンコーダーでは毎フレーム起こりうる）。</summary>
    private readonly BusMessageThrottle _rewindWarnings = new();

    private volatile bool _isAlive;

    /// <summary>
    /// 最初のサンプルが予算内に来ないことを見張るタイマー
    /// （<see cref="ReportMissingFirstSample"/>）。サンプルが来たら畳む。
    /// </summary>
    private System.Threading.Timer? _firstSampleTimer;

    /// <summary>いま書き出し中のセグメント（キーフレーム待ちの間は <see langword="null"/>）。</summary>
    private SegmentWriter? _writer;

    private int _segmentIndex;
    private string? _currentPath;
    private string? _previousPath;
    private ulong _segmentStartPts;
    private bool _overshootReported;

    /// <summary>
    /// 「最初のサンプルの見張り」を畳んだ者がいるか（0 = まだ、1 = 済み）。
    /// <b><see cref="Interlocked"/> で立てる</b> ── サンプルの到着（枝のストリーミング
    /// スレッド）とタイマーの発火（プールスレッド）は同時に起こりうるので、
    /// フラグを立てられた側だけが報告する。
    /// </summary>
    private int _firstSampleReported;

    private bool _disposed;

    /// <summary>
    /// セグメントの排出待ちが受け取る結果。<b>ラッパーではなく写した値を運ぶ</b> ──
    /// <c>Message</c> のラッパーはハンドラの実行中しか有効でない。
    /// </summary>
    private readonly record struct SegmentDrainResult(StopDrainSignal Signal, string? ErrorMessage, string? Debug);

    /// <summary>
    /// 書き出し中のセグメント 1 本ぶんの状態。
    ///
    /// <para>
    /// <b>排出はバスの同期ハンドラで受ける。</b> このアプリは GMainLoop を回さないので
    /// <c>Bus.Message</c> / <c>AddWatch</c> は発火しない。購読より後に post された
    /// メッセージはキューに入らない（配送も破棄もバインディングが行う）ので、
    /// セグメントの寿命の間もキューは伸びない。
    /// </para>
    /// <para>
    /// <b><see cref="Drain"/> はセグメントを開いた時点で武装する</b>
    /// ── Error は EOS より前に届きうるので、確定の直前に武装したのでは取りこぼす。
    /// 生成に <c>RunContinuationsAsynchronously</c> が要るのは、継続が
    /// post 元のストリーミングスレッドで走ると <c>SetState(Null)</c> が自スレッドで
    /// 走って固まるため（<c>EventRecorder._stopDrain</c> と同じ理由）。
    /// </para>
    /// </summary>
    private sealed class SegmentWriter(Pipeline pipeline, Bus? bus, GstApp.AppSrc? src, string path)
    {
        public Pipeline Pipeline { get; } = pipeline;
        public Bus? Bus { get; } = bus;
        public GstApp.AppSrc? Src { get; } = src;
        public string Path { get; } = path;

        public System.Threading.Tasks.TaskCompletionSource<SegmentDrainResult> Drain { get; } =
            new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>バスの購読（<c>Dispose</c> が同期ハンドラを外す）。未購読なら null。</summary>
        public IDisposable? Subscription { get; set; }
    }

    public ContinuousRecorder(
        IContinuousRecorderHost host,
        GstApp.AppSink source,
        string template,
        int segmentSeconds,
        int firstSampleBudgetMs)
    {
        _host = host;
        _source = source;
        _template = template;
        _segmentNs = SegmentRotationRules.SegmentNanoseconds(segmentSeconds);
        _firstSampleBudgetMs = firstSampleBudgetMs;
    }

    /// <summary>書き出したセグメントの本数。</summary>
    public int SegmentCount => _segmentIndex;

    /// <summary>
    /// 常時枝の <c>appsink</c> にサンプルのコールバックを取り付け、
    /// 最初のサンプルの見張りを武装する。
    /// <b>取り付けのゲートは要らない</b> ── 呼び出し元（<c>EventRecorder.StartContinuous</c>）は
    /// sink パイプラインが <c>PLAYING</c> に達してからここへ来る。
    /// </summary>
    public void Start()
    {
        _isAlive = true;

        // **予算の見張りはタイマーで持つ。** サンプルを待つループが無いので、
        // 「1 枚も来ない」を観測する者は他にいない。
        _firstSampleTimer = new System.Threading.Timer(
            static state => ((ContinuousRecorder)state!).ReportMissingFirstSample(),
            this, _firstSampleBudgetMs, Timeout.Infinite);

        _source.SetSimpleCallbacks(onNewSample: sink =>
        {
            // **例外を漏らさない。** トランポリンは抜けた例外を FlowReturn.Error へ
            // 変換するので、漏らすと枝が止まる（サンプル 1 枚の失敗で常時録画を殺さない）。
            try
            {
                // teardown 中は触らない。Close は _isAlive を倒してから
                // 最後のセグメントの確定に入る。
                if (!_isAlive)
                    return FlowReturn.Ok;

                // **1 プルでは足りない。** appsink は 1 render につき 1 回しか呼ばないので、
                // 取り付け前に溜まった分は初回に吸い切る（sink / preview の枝と同じ形）。
                while (sink!.TryPullSample(ClockTime.Zero) is { } sample)
                {
                    using (sample)
                    {
                        lock (_sampleLock)
                        {
                            // Close がロックを取れた側だった場合はここで降りる
                            // （確定済みのセグメントの後に新しいセグメントを開かない）。
                            if (!_isAlive)
                                break;
                            OnContSample(sample);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                string detail = $"recorder='{_host.Name}' {ex.Message}";
                Components.ActivityLog.Error("continuous.error", detail);
                _host.OnContinuousError(detail);
            }

            // **空プルでも Ok を返す。** Eos を返すと枝が止まる
            // （1 コールバック 1 プルの例をドレイン形へ写してはいけない）。
            return FlowReturn.Ok;
        });
    }

    /// <summary>
    /// エンジンを止め、<b>現在のセグメントを確定させてから</b>返る。
    /// 待ちはすべて有界。<c>EventRecorder.CloseCore</c> はこれをイベント側の排出と
    /// <b>並行</b>に走らせる（直列にすると停止の予算を超える）。
    ///
    /// <para>
    /// <b>コールバックの退去を保証するのは <c>EventRecorder</c> の quiesce</b>
    /// （sink パイプラインの <c>Null</c> 化。実行中の <c>onNewSample</c> の復帰を待つ）で、
    /// このクラスではない。<c>CloseCore</c> は quiesce のあとにここを呼ぶので、
    /// 通常は誰もセグメントを触っていない。<b>quiesce が予算内に終わらなかったときだけ</b>
    /// コールバックがまだ走っていることがあり、そのまま確定させると進行中の
    /// <c>OnContSample</c> と <see cref="_writer"/> を奪い合う ── だから
    /// <see cref="_sampleLock"/> を<b>有界に</b>待ち、取れなければ確定を諦める
    /// （<c>EventRecorder</c> の <c>recorder.leak</c> と同じ「リークするが壊さない」規律）。
    /// </para>
    /// </summary>
    public void Close()
    {
        _isAlive = false;

        // 見張りは待ちに入る前に畳む。予算より前に閉じたときに、閉じたあとから
        // continuous.error が出ないようにする。
        StopFirstSampleWatch();

        bool entered = false;
        try
        {
            Monitor.TryEnter(_sampleLock, CallbackExitTimeoutMs, ref entered);
            if (entered)
            {
                // 最後のセグメントを確定させる（ここを飛ばすと moov が書かれず全損する）
                CloseSegment();
            }
            else
            {
                // コールバックがまだ書き出しパイプラインを触っている可能性がある。
                // SetState(Null) や pipeline.Dispose() は使用中のネイティブオブジェクトを
                // 壊すので行わず、参照だけ落とす（bus/src はインターンされた GObject
                // ラッパーで、もともと Dispose しない）。バスの購読も外さない
                // ── 解除は実行中のハンドラと競合しうる。
                Components.ActivityLog.Warn("continuous.leak",
                    $"recorder='{_host.Name}' a continuous sample callback was still running after "
                    + $"{CallbackExitTimeoutMs}ms; leaked the segment writer instead of finalizing it");
                _writer = null;
            }
        }
        finally
        {
            if (entered)
                Monitor.Exit(_sampleLock);
        }

        WaitForFinalizers(all: true);

        int suppressed = _pushWarnings.Flush() + _rewindWarnings.Flush();
        if (0 < suppressed)
            Components.ActivityLog.Warn("continuous.error",
                $"recorder='{_host.Name}' repeated={suppressed} (suppressed, final)");

        _host.OnContinuousStatus(running: false, currentFile: null, segmentCount: _segmentIndex);
        Components.ActivityLog.Info("continuous.stop",
            $"recorder='{_host.Name}' segments={_segmentIndex}");
    }

    /// <summary>
    /// サンプル 1 枚を処理する。<b>呼び出しは <see cref="_sampleLock"/> の下で行う</b>
    /// ── ここが触るセグメントの状態を <see cref="Close"/> の確定と奪い合わないため。
    /// </summary>
    private void OnContSample(Sample sample)
    {
        StopFirstSampleWatch();

        using var buffer = sample.GetBuffer();
        if (buffer is null)
            return;

        ulong pts = buffer.Pts.IsNone
            ? (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000)
            : buffer.Pts.Nanoseconds;
        bool keyframe = !buffer.HasFlags(BufferFlags.DeltaUnit);

        if (_writer is null)
        {
            // セグメントの先頭は必ずキーフレーム。h264parse config-interval=-1 が
            // 全 IDR の直前にパラメータセットを入れるので、ここから始めれば単体で再生できる。
            if (!keyframe)
                return;
            OpenSegment(sample, pts);
        }
        else if (pts < _segmentStartPts)
        {
            // PTS の巻き戻し（ソースの再起動、あるいは B フレームによる並べ替え）。
            // 符号なし減算のまま押し込むと約 2^64 ns の PTS が mux へ渡って
            // タイムスタンプが壊れる。現在のセグメントを確定させ、
            // 次のキーフレームから作り直す。
            //
            // **記録は畳む。** B フレームを出すエンコーダーを手書きで指定されると
            // 毎フレーム起こりうるので、素で書くと activity.log が埋まる
            // （EventRecorder の pts-rewind と同じ扱い）。
            var (emitRewind, rewindRepeated) = _rewindWarnings.Observe("pts-rewind");
            if (emitRewind)
            {
                string repeated = 0 < rewindRepeated ? $" repeated={rewindRepeated}" : "";
                Components.ActivityLog.Warn("continuous.error",
                    $"recorder='{_host.Name}' the source timestamp went backwards; "
                    + $"closed the segment and waiting for the next key frame{repeated}");
            }
            CloseSegment();
            if (!keyframe)
                return;
            OpenSegment(sample, pts);
        }
        else
        {
            long elapsed = (long)(pts - _segmentStartPts);
            if (SegmentRotationRules.ShouldRotate(elapsed, _segmentNs, keyframe))
            {
                CloseSegment();
                OpenSegment(sample, pts);
            }
            else if (!_overshootReported && SegmentRotationRules.IsOvershooting(elapsed, _segmentNs))
            {
                _overshootReported = true;
                string detail = $"recorder='{_host.Name}' file='{_currentPath}' "
                    + $"elapsedMs={elapsed / 1_000_000} segmentMs={_segmentNs / 1_000_000} "
                    + "no key frame arrived at the split point; pin the GOP length in "
                    + "ContinuousEncodingProperties to keep segments on time";
                Components.ActivityLog.Warn("continuous.overshoot", detail);
                _host.OnContinuousError(detail);
            }
        }

        // Push が buffer を消費する（PushBuffer がラッパーごと Dispose する）。
        // この行から先で buffer に触れてはならない。押し込まなかった経路では
        // 上の using が Dispose し、押し込んだ経路では using は空振りする
        // （Dispose は冪等）。
        Push(buffer, pts);
    }

    /// <summary>
    /// 最初のサンプルの見張りを畳む（サンプルが来た、あるいはエンジンを止めた）。
    /// フラグを先に立てた側だけが報告できるので、これが先ならタイマーは黙って降りる。
    /// </summary>
    private void StopFirstSampleWatch()
    {
        Interlocked.Exchange(ref _firstSampleReported, 1);
        Interlocked.Exchange(ref _firstSampleTimer, null)?.Dispose();
    }

    /// <summary>
    /// 予算を過ぎても最初のサンプルが来ないことを 1 回だけ報告する。
    /// <c>async=false</c> の枝は 1 フレームも出さなくても <c>PLAYING</c> になるので、
    /// これが無いと「常時録画 on なのに何も起きない」が無音の失敗になる。
    ///
    /// <para>
    /// <b>予算の切れ際の同時発火を弾く。</b> ここはプールスレッド、サンプルの到着は
    /// 枝のストリーミングスレッドなので、両者が同時にここへ来ることがある
    /// ── フラグを立てられた側だけが報告する。
    /// </para>
    /// <para>
    /// <b>例外を漏らさない。</b> タイマーのコールバックから抜けた例外は誰も受けず、
    /// プールスレッドの未処理例外としてプロセスを落とす。報告先
    /// （<see cref="IContinuousRecorderHost.OnContinuousError"/>）は購読側の
    /// <c>PropertyChanged</c> まで届くので、投げうる。
    /// </para>
    /// </summary>
    private void ReportMissingFirstSample()
    {
        try
        {
            if (Interlocked.Exchange(ref _firstSampleReported, 1) != 0)
                return;
            Interlocked.Exchange(ref _firstSampleTimer, null)?.Dispose();

            string detail = $"recorder='{_host.Name}' no encoded frame reached the continuous branch "
                + $"within {_firstSampleBudgetMs}ms; nothing is being recorded continuously";
            Components.ActivityLog.Error("continuous.error", detail);
            _host.OnContinuousError(detail);
        }
        catch (Exception ex)
        {
            DebugLogEx.Log(DebugLevel.Error,
                $"the continuous first-sample watchdog failed!\n{ex}");
        }
    }

    /// <summary>次のセグメントの書き出しパイプラインを作って <c>PLAYING</c> にする。</summary>
    private void OpenSegment(Sample sample, ulong pts)
    {
        string path = NextSegmentPath();

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // ParseLaunch は失敗すると Gst.GLib.GException を投げる
        // （サンプルのコールバックの catch が報告する）。
        var pipeline = (Pipeline)Gst.Global.ParseLaunch(ContinuousBranch.SegmentWriterPipeline);
        SegmentWriter? writer = null;
        try
        {
            pipeline.SetName($"continuous-writer-{_segmentIndex}");
            Bus? bus = pipeline.GetBus();
            var src = (GstApp.AppSrc)pipeline.GetByName("src")!;
            Element file = pipeline.GetByName("file")!;

            // **購読は PLAYING より前に済ませる。** まだ NULL 状態のバスには post が
            // 届かないので取り付けのゲートは要らず、再生開始の直後に出た Error も
            // 取りこぼさない。
            writer = new SegmentWriter(pipeline, bus, src, path);
            SubscribeSegment(writer);

            using (GObject.Value location = GObject.Value.New(GObject.GType.String))
            {
                location.SetString(path);
                file.SetProperty("location", in location);
            }

            // **ネゴシエート済みの caps をそのまま渡す。** 渡さないと h264parse が
            // stream-format / alignment を typefind で推測することになり、外れると
            // 全 NAL が黙って捨てられて中身の無い MP4 が残る（EventRecorder と同じ罠）。
            // GetCaps のラッパーは自前の参照を 1 本持つので必ず Dispose する
            // ── 解放されるのはラッパーの参照だけで、sample 側の caps は無傷のまま
            // （SetCaps もコピーを取る）。
            using var negotiated = sample.GetCaps();
            if (negotiated is not null)
                src.SetCaps(negotiated);

            if (pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
            {
                throw new InvalidOperationException(
                    $"the continuous segment writer for '{path}' did not want to play");
            }
        }
        catch
        {
            // **作りかけを必ず畳む。** 出力先が書けない等で毎キーフレームここへ来る場合、
            // 畳まないとネイティブのパイプラインがキーフレームごとに積み上がる。
            // bus/src/file はインターンされた GObject ラッパーなので Dispose しない
            // ── 自前で作ったパイプラインだけを Null に落としてから Dispose する。
            // 購読はパイプラインの Dispose より前に外す。
            writer?.Subscription?.Dispose();
            pipeline.SetState(State.Null);
            pipeline.Dispose();
            throw;
        }

        _writer = writer;
        _segmentStartPts = pts;
        _currentPath = path;
        _overshootReported = false;
        _segmentIndex++;

        _host.OnContinuousStatus(running: true, currentFile: path, segmentCount: _segmentIndex);
        Components.ActivityLog.Info("continuous.start",
            $"recorder='{_host.Name}' file='{path}' segment={_segmentIndex} "
            + $"segmentMs={_segmentNs / 1_000_000}");
    }

    /// <summary>
    /// 現在のセグメントを手放し、確定（EOS → バス待ち → Null → Dispose）を
    /// プールスレッドへ逃がす。<b>呼び出し側は待たない</b> ── 待つと切り替えのたびに
    /// フレームが落ちる。
    /// </summary>
    private void CloseSegment()
    {
        var writer = _writer;
        string? path = _currentPath;

        _writer = null;
        _previousPath = path;
        _currentPath = null;

        if (writer is null)
            return;

        // 在庫が上限なら、新しいセグメントを作る前にここで有界に待つ。
        WaitForFinalizers(all: false);

        var task = STTask.Run(() => FinalizeSegment(writer));
        lock (_finalizerLock)
            _finalizers.Add((task, path));
    }

    /// <summary>
    /// セグメントのバスを <c>Bus.SubscribeSyncDrop</c> で購読する。
    /// <b>ハンドラがするのは排出待ちの完了だけ</b>
    /// ── <c>continuous.error</c> の報告は <see cref="FinalizeSegment"/> 1 箇所に置く。
    ///
    /// <para>
    /// 購読より後に post されたメッセージはキューに入らない（配送も破棄もバインディングが
    /// 行う）。ここは <c>NULL</c> 状態のうちに購読するのでバックログも無く、汲み切りは要らない。
    /// </para>
    /// <para>
    /// <b>ハンドラから例外を漏らしてはいけない。</b> 漏れた例外はバインディングが捕捉して
    /// <c>Pass</c> に倒すので、そのメッセージは誰も汲まないキューへ積まれる。
    /// </para>
    /// </summary>
    private void SubscribeSegment(SegmentWriter writer)
    {
        if (writer.Bus is not { } bus)
            return;

        writer.Subscription = bus.SubscribeSyncDrop((_, message) =>
        {
            try
            {
                // メッセージのラッパーはハンドラの実行中だけ有効なので Dispose しない。
                if (message is not { } msg)
                    return;

                switch (msg.Type)
                {
                    case MessageType.Eos:
                        writer.Drain.TrySetResult(new(StopDrainSignal.Eos, null, null));
                        break;

                    case MessageType.Error:
                        {
                            // ParseError はネイティブ側のメモリをすべてバインディングが解放した上で
                            // GException（ただの managed 例外オブジェクト）を返すので、Dispose は不要。
                            var (gerror, debug) = msg.ParseError();
                            writer.Drain.TrySetResult(new(StopDrainSignal.Error, gerror.Message, debug));
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                // activity.log には出さない（報告は FinalizeSegment の担当）。
                DebugLogEx.Log(DebugLevel.Error,
                    $"the continuous segment writer bus handler failed!\n{ex}");
            }
        });
    }

    /// <summary>
    /// 1 本のセグメントを確定させる。<c>EventRecorder.StopDrainAndFinalize</c> と同じ
    /// 「有界待ち → 必ず Null」の形。<b>失敗してもエンジンは止めない</b>
    /// ── 1 本が壊れても常時録画そのものは続ける方が損失が小さい。
    /// </summary>
    private void FinalizeSegment(SegmentWriter writer)
    {
        string result = "ok";
        int timeoutMs = EventRecorder.StopFinalizeTimeoutMs;
        string path = writer.Path;
        try
        {
            writer.Src?.EndOfStream();

            // **同期の Wait にする（await にしない）。** 完了させるのはバスの同期
            // ハンドラ＝当のパイプラインのストリーミングスレッドなので、継続がそこで
            // インライン実行されると finally の SetState(Null) が自スレッドで走って固まる。
            SegmentDrainResult drained = writer.Drain.Task.Wait(Math.Max(0, timeoutMs))
                ? writer.Drain.Task.Result
                : new(StopDrainSignal.Timeout, null, null);

            if (drained.Signal == StopDrainSignal.Timeout)
            {
                result = "timeout";
                string detail = $"recorder='{_host.Name}' file='{path}' "
                    + $"the segment did not drain within {timeoutMs}ms; the file may be incomplete";
                Components.ActivityLog.Error("continuous.error", detail);
                _host.OnContinuousError(detail);
            }
            else if (drained.Signal == StopDrainSignal.Error)
            {
                result = "error";
                string detail = $"recorder='{_host.Name}' file='{path}' {drained.ErrorMessage} debug={drained.Debug}";
                Components.ActivityLog.Error("continuous.error", detail);
                _host.OnContinuousError(detail);
            }
        }
        catch (Exception ex)
        {
            result = "error";
            string detail = $"recorder='{_host.Name}' file='{path}' {ex.Message}";
            Components.ActivityLog.Error("continuous.error", detail);
            _host.OnContinuousError(detail);
        }
        finally
        {
            // bus/src はインターンされた GObject ラッパーなので Dispose しない。
            // 自前で作ったパイプラインだけは Null に落としてから Dispose する。
            // 購読はパイプラインの Dispose より前に外す。
            writer.Subscription?.Dispose();
            writer.Pipeline.SetState(State.Null);
            writer.Pipeline.Dispose();
            Components.ActivityLog.Info("continuous.finalize",
                $"recorder='{_host.Name}' file='{path}' result={result}");
        }
    }

    /// <summary>
    /// 排出の在庫を掃除する。<paramref name="all"/> が false のときは
    /// <see cref="MaxFinalizersInFlight"/> を超えている分だけ待つ。
    ///
    /// <para>
    /// <b>この待ちはサンプルのコールバック（＝枝のストリーミングスレッド）の中で起こる。</b>
    /// 待っている間フレームが溜まる先は枝の <c>queue</c>
    /// （<c>leaky=downstream max-size-buffers=8</c>）で、上限に達すれば古い方から捨てられる
    /// ── 待ちの長さに関わらず、この枝が抱えるメモリは有界である。
    /// </para>
    /// </summary>
    private void WaitForFinalizers(bool all)
    {
        STTask[] pending;
        lock (_finalizerLock)
        {
            _finalizers.RemoveAll(f => f.Task.IsCompleted);
            if (!all && _finalizers.Count < MaxFinalizersInFlight)
                return;
            pending = [.. _finalizers.Select(f => f.Task)];
        }
        if (pending.Length == 0)
            return;

        int budgetMs = EventRecorder.StopFinalizeTimeoutMs + EventRecorder.StopFinalizeSlackMs;
        if (!STTask.WaitAll(pending, budgetMs))
        {
            Components.ActivityLog.Warn("continuous.finalize backlog",
                $"recorder='{_host.Name}' {pending.Length} segment(s) were still draining after {budgetMs}ms");
        }
        lock (_finalizerLock)
            _finalizers.RemoveAll(f => f.Task.IsCompleted);
    }

    /// <summary>まだ排出中のセグメントのパス（比較は大文字小文字を無視する）。</summary>
    private HashSet<string> InFlightPaths()
    {
        lock (_finalizerLock)
        {
            return [.. _finalizers
                .Where(f => !f.Task.IsCompleted && f.Path is not null)
                .Select(f => f.Path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>
    /// 次のセグメントのパスを決める。テンプレートは<b>セグメントごとに展開し直す</b>ので
    /// <c>{Now}</c> は毎回変わる。それでも直前と同じになった場合（<c>{Now}</c> を含まない
    /// テンプレート等）は連番を足して上書きを防ぐ。
    /// </summary>
    private string NextSegmentPath()
    {
        string path = _host.ResolveSegmentPath(_template, _segmentIndex);

        // **直前だけでなく「まだ排出中のセグメント」とも突き合わせる。**
        // 排出は非同期なので、直前より前のセグメントがまだ書き終わっていないことがある
        // ── そこへ同じ名前で filesink を開くと、排出中のファイルを切り詰めてしまう。
        // （既定のテンプレートは {Segment} を含むので通常は起こらないが、
        //   利用者が {Now:HHmm} だけのテンプレートを書けば十分に起こりうる。）
        bool collides = string.Equals(path, _previousPath, StringComparison.OrdinalIgnoreCase)
            || InFlightPaths().Contains(path);
        if (!collides)
            return path;

        // 通し番号は単調増加なので、この形にすれば過去のどの名前とも一致しない。
        string directory = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string unique = $"{stem}_{_segmentIndex:00000}{extension}";
        return directory.Length == 0 ? unique : Path.Combine(directory, unique);
    }

    /// <summary>
    /// セグメント基準へ PTS を張り替えて書き出しパイプラインへ押し込む。
    /// <b>buffer はこの呼び出しが消費する</b>（<c>PushBuffer</c> がラッパーごと
    /// Dispose する）── 呼び出し側は以後 buffer に触れてはならない。
    /// </summary>
    private void Push(Gst.Buffer buffer, ulong pts)
    {
        if (_writer?.Src is not { } src)
            return;

        // 呼び出し元では sample がまだ同じネイティブバッファへの参照を握っているので、
        // この MakeWritable はコピーを作り、ラッパーの中身がそのコピーへ差し替わる
        // ── sample 側のバッファは無傷のまま、タイムスタンプはコピーにだけ書く。
        buffer.MakeWritable();
        buffer.SetPts(ClockTime.FromNanoseconds(pts - _segmentStartPts));
        buffer.SetDts(ClockTime.None);

        var flow = src.PushBuffer(buffer);
        if (flow == FlowReturn.Ok)
            return;

        // 拒否は黙って捨てない。同一内容が続くので畳む。
        var (emit, repeated) = _pushWarnings.Observe("push-rejected");
        if (emit)
        {
            string suffix = 0 < repeated ? $" repeated={repeated}" : "";
            Components.ActivityLog.Warn("continuous.error",
                $"recorder='{_host.Name}' file='{_currentPath}' "
                + $"the segment writer rejected a buffer: {flow}{suffix}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Close();
    }
}
