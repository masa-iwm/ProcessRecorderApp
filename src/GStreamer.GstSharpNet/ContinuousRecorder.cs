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
/// <b>スレッドの規律は <c>EventRecorder.PullSampleProc</c> と同じ。</b>
/// ループは <see cref="_isAlive"/>（volatile）だけで回し、
/// <c>EventRecorder._stateLock</c> は絶対に取らない
/// ── <c>CloseCore</c> はそのロックを保持したままこのエンジンの停止を待つ。
/// </para>
/// </summary>
internal sealed partial class ContinuousRecorder : IDisposable
{
    /// <summary>pull スレッドの停止を待つ上限(ms)。<c>EventRecorder</c> の Join と同じ。</summary>
    internal const int JoinTimeoutMs = 5000;

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
    private Thread? _thread;

    private Pipeline? _writer;
    private Bus? _writerBus;
    private GstApp.AppSrc? _writerSrc;

    private int _segmentIndex;
    private string? _currentPath;
    private string? _previousPath;
    private ulong _segmentStartPts;
    private bool _overshootReported;
    private bool _firstSampleReported;
    private bool _disposed;

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

    public void Start()
    {
        _isAlive = true;
        _thread = new Thread(Proc)
        {
            IsBackground = true,
            Name = $"continuous-{_host.Name}",
        };
        _thread.Start();
    }

    /// <summary>
    /// エンジンを止め、<b>現在のセグメントを確定させてから</b>返る。
    /// 待ちはすべて有界。<c>EventRecorder.CloseCore</c> はこれをイベント側の排出と
    /// <b>並行</b>に走らせる（直列にすると停止の予算を超える）。
    /// </summary>
    public void Close()
    {
        _isAlive = false;
        bool stopped = _thread is null || _thread.Join(JoinTimeoutMs);
        _thread = null;

        if (stopped)
        {
            // 最後のセグメントを確定させる（ここを飛ばすと moov が書かれず全損する）
            CloseSegment();
        }
        else
        {
            // pull スレッドがまだ書き出しパイプラインを触っている可能性がある。
            // SetState(Null) や pipeline.Dispose() は使用中のネイティブオブジェクトを
            // 壊すので行わず、参照だけ落とす（bus/src はインターンされた GObject
            // ラッパーで、もともと Dispose しない）── EventRecorder の recorder.leak
            // と同じ規律。
            Components.ActivityLog.Warn("continuous.leak",
                $"recorder='{_host.Name}' the continuous pull thread did not stop in time; "
                + "leaked the segment writer instead of disposing it");
            _writerSrc = null;
            _writerBus = null;
            _writer = null;
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

    private void Proc()
    {
        long startedAt = Environment.TickCount64;

        while (_isAlive)
        {
            try
            {
                // GMainLoop を回していないアプリなので、ファイナライザーが積んだ
                // GObject の解放はこのループで消化する（1 イテレーションに 1 回）。
                GstSharp.DrainPendingReleases();

                using var sample = _source.TryPullSample(ClockTime.FromMilliseconds(100));
                if (sample is null)
                {
                    ReportMissingFirstSample(startedAt);
                    continue;
                }
                _firstSampleReported = true;

                using var buffer = sample.GetBuffer();
                if (buffer is null)
                    continue;

                ulong pts = buffer.Pts.IsNone
                    ? (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000)
                    : buffer.Pts.Nanoseconds;
                bool keyframe = !buffer.HasFlags(BufferFlags.DeltaUnit);

                if (_writer is null)
                {
                    // セグメントの先頭は必ずキーフレーム。h264parse config-interval=-1 が
                    // 全 IDR の直前にパラメータセットを入れるので、ここから始めれば単体で再生できる。
                    if (!keyframe)
                        continue;
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
                        continue;
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
            catch (Exception ex)
            {
                string detail = $"recorder='{_host.Name}' {ex.Message}";
                Components.ActivityLog.Error("continuous.error", detail);
                _host.OnContinuousError(detail);
            }
        }
    }

    /// <summary>
    /// 予算を過ぎても最初のサンプルが来ないことを 1 回だけ報告する。
    /// <c>async=false</c> の枝は 1 フレームも出さなくても <c>PLAYING</c> になるので、
    /// これが無いと「常時録画 on なのに何も起きない」が無音の失敗になる。
    /// </summary>
    private void ReportMissingFirstSample(long startedAt)
    {
        if (_firstSampleReported || Environment.TickCount64 - startedAt < _firstSampleBudgetMs)
            return;

        _firstSampleReported = true;
        string detail = $"recorder='{_host.Name}' no encoded frame reached the continuous branch "
            + $"within {_firstSampleBudgetMs}ms; nothing is being recorded continuously";
        Components.ActivityLog.Error("continuous.error", detail);
        _host.OnContinuousError(detail);
    }

    /// <summary>次のセグメントの書き出しパイプラインを作って <c>PLAYING</c> にする。</summary>
    private void OpenSegment(Sample sample, ulong pts)
    {
        string path = NextSegmentPath();

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // ParseLaunch は失敗すると Gst.GLib.GException を投げる（Proc の catch が報告する）。
        var pipeline = (Pipeline)Gst.Global.ParseLaunch(ContinuousBranch.SegmentWriterPipeline);
        Bus? bus = null;
        GstApp.AppSrc? src = null;
        try
        {
            pipeline.SetName($"continuous-writer-{_segmentIndex}");
            bus = pipeline.GetBus();
            src = (GstApp.AppSrc)pipeline.GetByName("src")!;
            Element file = pipeline.GetByName("file")!;

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
            pipeline.SetState(State.Null);
            pipeline.Dispose();
            throw;
        }

        _writer = pipeline;
        _writerBus = bus;
        _writerSrc = src;
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
        var pipeline = _writer;
        var bus = _writerBus;
        var src = _writerSrc;
        string? path = _currentPath;

        _writer = null;
        _writerBus = null;
        _writerSrc = null;
        _previousPath = path;
        _currentPath = null;

        if (pipeline is null)
            return;

        // 在庫が上限なら、新しいセグメントを作る前にここで有界に待つ。
        WaitForFinalizers(all: false);

        var task = STTask.Run(() => FinalizeSegment(pipeline, bus, src, path));
        lock (_finalizerLock)
            _finalizers.Add((task, path));
    }

    /// <summary>
    /// 1 本のセグメントを確定させる。<c>EventRecorder.StopDrainAndFinalize</c> と同じ
    /// 「有界待ち → 必ず Null」の形。<b>失敗してもエンジンは止めない</b>
    /// ── 1 本が壊れても常時録画そのものは続ける方が損失が小さい。
    /// </summary>
    private void FinalizeSegment(Pipeline pipeline, Bus? bus, GstApp.AppSrc? src, string? path)
    {
        string result = "ok";
        int timeoutMs = EventRecorder.StopFinalizeTimeoutMs;
        try
        {
            src?.EndOfStream();

            using var msg = bus?.TimedPopFiltered(
                ClockTime.FromMilliseconds(Math.Max(0, timeoutMs)),
                MessageType.Eos | MessageType.Error);

            if (msg is null)
            {
                result = "timeout";
                string detail = $"recorder='{_host.Name}' file='{path}' "
                    + $"the segment did not drain within {timeoutMs}ms; the file may be incomplete";
                Components.ActivityLog.Error("continuous.error", detail);
                _host.OnContinuousError(detail);
            }
            else if (msg.Type == MessageType.Error)
            {
                result = "error";
                // ParseError はネイティブ側のメモリをすべてバインディングが解放した上で
                // GException（ただの managed 例外オブジェクト）を返すので、Dispose は不要。
                var (gerror, debug) = msg.ParseError();
                string detail = $"recorder='{_host.Name}' file='{path}' {gerror.Message} debug={debug}";
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
            pipeline.SetState(State.Null);
            pipeline.Dispose();
            Components.ActivityLog.Info("continuous.finalize",
                $"recorder='{_host.Name}' file='{path}' result={result}");
        }
    }

    /// <summary>
    /// 排出の在庫を掃除する。<paramref name="all"/> が false のときは
    /// <see cref="MaxFinalizersInFlight"/> を超えている分だけ待つ。
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
        if (_writerSrc is null)
            return;

        // 呼び出し元では sample がまだ同じネイティブバッファへの参照を握っているので、
        // この MakeWritable はコピーを作り、ラッパーの中身がそのコピーへ差し替わる
        // ── sample 側のバッファは無傷のまま、タイムスタンプはコピーにだけ書く。
        buffer.MakeWritable();
        buffer.SetPts(ClockTime.FromNanoseconds(pts - _segmentStartPts));
        buffer.SetDts(ClockTime.None);

        var flow = _writerSrc.PushBuffer(buffer);
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
