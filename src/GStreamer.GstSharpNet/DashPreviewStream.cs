using Gst;
using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using GstApp = Gst.App;
using STTask = System.Threading.Tasks.Task;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// DASH プレビューの配信エンジンが宿主（<see cref="EventRecorder"/>）へ求める最小の口。
/// エンジンは宿主の内部状態も、宿主が持つどのロックも知らない。
///
/// <para>
/// <b>4 つの設定は毎サンプル読み直す。</b> 値が変わったら次のサンプルで組み直すのが
/// この機構の反映方法で、宿主に通知を出させない（通知を受けると、宿主のスレッドから
/// エンジンのロックへ入る経路が増える）。
/// </para>
/// </summary>
internal interface IDashPreviewHost
{
    /// <summary>ログ・診断に使うレコーダー名。</summary>
    string Name { get; }

    /// <summary>配信する幅(px)。</summary>
    int PreviewWidth { get; }

    /// <summary>配信する高さ(px)。</summary>
    int PreviewHeight { get; }

    /// <summary>配信するフレームレート(fps)。</summary>
    int PreviewFps { get; }

    /// <summary>配信のビットレート(kbit/sec)。</summary>
    int PreviewBitrateKbps { get; }
}

/// <summary>
/// <b>DASH プレビューの配信エンジン。</b> イベント録画の枝A（プレビュー用 <c>appsink</c>）へ
/// 届く<b>生フレーム</b>を、<b>レコーダーごとに 1 本</b>の第 2 パイプラインで
/// 縮小・低レート化して再エンコードし、fMP4 の fragment を DASH のセグメントへ集約して
/// 保持する。保持物は <c>Controller.DashPreviews</c> 経由で
/// <c>GET /api/recorders/{id}/dash/…</c> が引く。
///
/// <para>
/// <b>読まれなくなれば消える。</b> <see cref="TryGetSnapshot"/> が呼ばれるたびに
/// <see cref="DashPreviewLimits.LeaseMs"/> の貸出が延び、切れたら次のサンプルで畳む
/// ── 明示的な購読解除を持たない代わりに、これで第 2 パイプラインの寿命を有界にしている。
/// </para>
/// <para>
/// <b>非配信時のコストは volatile の 1 回読みだけ。</b> <see cref="OnRawSample"/> の先頭で
/// 抜けるので、誰も見ていないレコーダーの録画経路の費用はこの機能を足す前と同じである。
/// </para>
/// <para>
/// <b>スレッドは 3 種類ある。</b>
/// (1) <b>枝A のスレッド</b>＝ <see cref="OnRawSample"/>。mux の生成・破棄・押し込みは
/// ここの専有で、守るのは <see cref="_muxLock"/>。<b>取り方は
/// <see cref="Monitor.TryEnter(object, ref bool)"/> だけ</b> ── ここは録画と同じ
/// ストリーミングスレッドなので、待たせてよい区間が 1 つも無い。取れなければ
/// サンプルを 1 枚落として降りる。
/// (2) <b>mux スレッド</b>＝ 第 2 パイプラインの <c>appsink</c> と <c>bus</c> のコールバック。
/// 切り出しと集約とリングへの格納だけを行い、<see cref="_muxLock"/> は<b>取らない</b>
/// （取ると、畳んでいる最中の <c>SetState(Null)</c> ＝自スレッドの退去待ちと組んで詰む）。
/// (3) <b>HTTP / UI スレッド</b>＝ <see cref="TryGetSnapshot"/>。取るのは
/// <see cref="_ringLock"/> だけである。
/// </para>
/// <para>
/// <b>ロックの順序は <see cref="_muxLock"/> → <see cref="_ringLock"/> の一方向だけ。</b>
/// <see cref="_ringLock"/> の下で行うのは参照のコピーだけ（数十 µs）で、
/// <c>SetState</c> も IO も呼ばない。
/// </para>
/// <para>
/// <b>宿主のロックは 1 つも取らない。</b> 呼ばれるのは宿主のストリーミングスレッドなので、
/// 宿主側のロックへ手を出すと停止経路の待ちと輪を作る（<see cref="LivePreviewStream"/> と
/// <see cref="ContinuousRecorder"/> と同じ規律）。
/// </para>
/// </summary>
internal sealed partial class DashPreviewStream : IDisposable
{
    /// <summary>
    /// fragment 1 つの長さ(ms)。<b><see cref="LivePreviewStream.FragmentDurationMs"/> や
    /// <see cref="EventRecorder.FragmentDurationMs"/> とは別の定数</b> ── あちらは
    /// 録画済み H.264 をそのまま包む mux、こちらは再エンコードした第 2 パイプラインで、
    /// 片方を動かしてももう片方に効かせてはいけない。
    /// </summary>
    public const int FragmentDurationMs = 1000;

    /// <summary>
    /// <see cref="Close"/> が「進行中のサンプル処理が抜ける」のを待つ上限(ms)。
    /// <see cref="LivePreviewStream.CallbackExitTimeoutMs"/> と同じ 5 秒。
    /// </summary>
    public const int CallbackExitTimeoutMs = 5000;

    /// <summary>
    /// <c>dash.stream-stop</c> の <c>reason=</c> が取りうる値の<b>全部</b>。
    ///
    /// <para>
    /// <b>固定集合であることが約束である。</b> 切り出し器・集約器の自由文や
    /// 例外の <c>Message</c> をここへ流すと、運用側は「起きうる理由」を数え上げられなくなる
    /// ── それらは <c>dash.stream-error</c> の <c>detail=</c> へ出す
    /// （<c>src/README.md</c> の停止理由の表と対で保つこと）。
    /// </para>
    /// <para>
    /// <c>parse_launch</c> と <c>PLAYING</c> の失敗はここに現れない ── まだ
    /// <see cref="_mux"/> が無いので <see cref="Teardown"/> は何もせず、
    /// 記録は <c>dash.stream-error</c> の 1 行だけになる。
    /// </para>
    /// </summary>
    private static class StopReason
    {
        /// <summary>貸出が切れた（読み手が居なくなった）。</summary>
        public const string LeaseExpired = "lease expired";

        /// <summary>幅・高さ・fps・ビットレートのどれかが変わった。</summary>
        public const string SettingsChanged = "settings changed";

        /// <summary>枝A の caps が変わった（init も変わる）。</summary>
        public const string CapsChanged = "caps changed";

        /// <summary>候補のエンコーダーで組めなかった／バスの ERROR。</summary>
        public const string EncoderFailed = "encoder failed";

        /// <summary>集約器が見つけた時刻の巻き戻し。</summary>
        public const string PtsRewind = DashSegmentAssembler.PtsRewindFault;

        /// <summary>集約器の安全網（IDR が来ないまま fragment が溜まった）。</summary>
        public const string GopTooLong = DashSegmentAssembler.GopTooLongFault;

        /// <summary>Init から timescale / codecs を読めなかった。</summary>
        public const string InitUnparsable = "init unparsable";

        /// <summary>切り出し器が壊れた入力を見つけた（内訳は <c>detail=</c>）。</summary>
        public const string SplitterFault = "splitter fault";

        /// <summary>それ以外の破綻（例外・想定外の理由。内訳は <c>detail=</c>）。</summary>
        public const string StreamError = "stream error";

        /// <summary>レコーダーの停止。</summary>
        public const string Close = "close";
    }

    /// <summary>
    /// <see cref="StopReason"/> の全要素。<b><c>dash.stream-stop</c> の <c>reason=</c> が
    /// 取りうる値の正本</b>で、L1 の <c>DashStopReasonTests</c> が
    /// <c>src/README.md</c> の停止理由の表と過不足なく突き合わせる。
    /// </summary>
    internal static readonly string[] StopReasons =
    [
        StopReason.LeaseExpired,
        StopReason.SettingsChanged,
        StopReason.CapsChanged,
        StopReason.EncoderFailed,
        StopReason.PtsRewind,
        StopReason.GopTooLong,
        StopReason.InitUnparsable,
        StopReason.SplitterFault,
        StopReason.StreamError,
        StopReason.Close,
    ];

    /// <summary>
    /// 第 2 パイプラインの文字列。<b>ここだけが形の正本</b>で、L1 がトークンを固定する。
    ///
    /// <para>
    /// <c>appsrc</c> は詰まっても録画側を止めない（<c>block=false</c> ＋
    /// <c>leaky-type=downstream</c>）。<b>上限はバイトではなく枚数</b>
    /// （<c>max-buffers=2</c> ＋ <c>max-bytes=0</c>）── 生フレームは 1 枚が MB 級で、
    /// バイト上限では「何枚溜まるか」が解像度によって変わってしまう。
    /// </para>
    /// <para>
    /// <c>videorate drop-only=true</c> は<b>増やさない</b> ── 入力より高い fps を
    /// 指定されたときにフレームを複製すると、エンコーダーに無駄な仕事をさせるだけになる。
    /// </para>
    /// <para>
    /// <c>h264parse config-interval=-1</c> で SPS/PPS を各 IDR へ付ける
    /// ── DASH のセグメントは途中から取得されるので、init だけに置くと復帰できない。
    /// <c>appsink</c> はクロックに同期させない（<c>sync=false async=false</c>）。
    /// <c>faststart</c> は付けない（EOS まで 1 バイトも出なくなる）。
    /// </para>
    /// </summary>
    public static string BuildPipeline(int width, int height, int fps, string encoderLaunch)
        => string.Create(CultureInfo.InvariantCulture,
            $"appsrc name=src format=time block=false max-buffers=2 max-bytes=0 leaky-type=downstream ! "
            + $"videorate drop-only=true ! videoscale ! "
            + $"video/x-raw,width={width},height={height},framerate={fps}/1,pixel-aspect-ratio=1/1 ! "
            + $"videoconvert ! {encoderLaunch} ! h264parse config-interval=-1 ! "
            + $"mp4mux name=mux fragment-duration={FragmentDurationMs} fragment-mode=dash-or-mss ! "
            + $"appsink name=sink sync=false async=false");

    private readonly IDashPreviewHost _host;

    /// <summary>
    /// mux（<see cref="_mux"/>）の生成・破棄に触ってよい者を 1 人に絞るロック。
    /// <b><c>lock</c> で取らない</b> ── 取るのは録画と同じストリーミングスレッドで、
    /// 待たせてよい区間が 1 つも無い。
    /// </summary>
    private readonly object _muxLock = new();

    /// <summary>
    /// 読み出し用の保持物（init・timescale・codecs・セグメントのリング）を守るロック。
    /// <b>下で行うのは参照のコピーだけ</b>で、<c>SetState</c> も IO も呼ばない。
    /// </summary>
    private readonly object _ringLock = new();

    /// <summary>
    /// 組めなかったエンコーダーのファクトリ名（<see cref="_muxLock"/>）。
    /// <b>この stream の寿命内で保持する</b> ── 毎サンプル同じ候補で失敗し続けると、
    /// 録画スレッドで <c>parse_launch</c> を回すことになる。
    /// </summary>
    private readonly HashSet<string> _rejectedEncoders = new(StringComparer.Ordinal);

    /// <summary>セグメントのリング（<see cref="_ringLock"/>）。古いものから捨てる。</summary>
    private readonly Queue<DashMediaSegment> _ring = new();

    /// <summary>エンジンが生きているか。<see cref="Close"/> が倒す。</summary>
    private volatile bool _isAlive;

    /// <summary>読み手が居るか（mux を作る唯一の条件）。lease が切れたら倒れる。</summary>
    private volatile bool _wantMux;

    /// <summary>候補が尽きた。<b>以後この寿命では二度と組まない</b>。</summary>
    private volatile bool _faultedEncoder;

    /// <summary>
    /// mux スレッドが見つけた破綻。<b>立てるのは mux スレッド、畳むのは枝A のスレッド</b>
    /// ── mux スレッドから <c>SetState</c> を呼ぶと自スレッドの退去を待つことになる。
    /// </summary>
    private volatile bool _faulted;

    /// <summary>破綻の理由（<see cref="_faulted"/> と対）。</summary>
    private volatile string? _faultReason;

    /// <summary>いま動いている mux（<see cref="_muxLock"/>）。読み手が居なければ null。</summary>
    private MuxEngine? _mux;

    /// <summary>最後に <see cref="TryGetSnapshot"/> が呼ばれた時刻（<c>TickCount64</c>）。</summary>
    private long _lastTouchTicks;

    /// <summary>連続体の通し番号（<see cref="_muxLock"/> で増やす）。</summary>
    private int _generation;

    // ---- 読み出し用の保持物（すべて _ringLock） ----

    private byte[]? _init;
    private uint _timescale;
    private string? _codecs;
    private int _ringGeneration;
    private int _ringWidth;
    private int _ringHeight;
    private int _ringFps;
    private int _ringBitrateKbps;
    private DateTimeOffset _ringAvailabilityStartUtc;
    private ulong _presentationTimeOffset;
    private bool _hasPresentationTimeOffset;

    private bool _disposed;

    public DashPreviewStream(IDashPreviewHost host)
    {
        _host = host;

        // 生成は宿主の初期化（sink パイプラインが PLAYING に達した段）で行われるので、
        // ここで開けてよい。倒すのは Close だけ。
        _isAlive = true;
    }

    /// <summary>
    /// mux 1 本ぶんの状態。<b>切り出し器と集約器はここに持つ</b> ── 作り直しのたびに
    /// 新しくなるので、前の mux の途中の箱や保留中のセグメントが次の連続体へ混ざらない。
    /// </summary>
    private sealed class MuxEngine(
        Pipeline pipeline,
        GstApp.AppSrc src,
        GstApp.AppSink sink,
        string capsText,
        string factoryName,
        int width,
        int height,
        int fps,
        int bitrateKbps)
    {
        public Pipeline Pipeline { get; } = pipeline;

        public GstApp.AppSrc Src { get; } = src;

        public GstApp.AppSink Sink { get; } = sink;

        /// <summary>
        /// <c>appsrc</c> へ設定した caps の文字列表現。<b>ラッパーは保持しない</b>
        /// ── 比較に要るのは値だけで、ネイティブの参照を抱える理由が無い。
        /// </summary>
        public string CapsText { get; } = capsText;

        /// <summary>この mux が使っているエンコーダーのファクトリ名（不採用にするときの鍵）。</summary>
        public string FactoryName { get; } = factoryName;

        public int Width { get; } = width;

        public int Height { get; } = height;

        public int Fps { get; } = fps;

        public int BitrateKbps { get; } = bitrateKbps;

        public Fmp4SegmentSplitter Splitter { get; } = new();

        public DashSegmentAssembler Assembler { get; } = new();

        /// <summary>バスの購読（パイプラインを畳む前に外す）。</summary>
        public IDisposable? Subscription { get; set; }

        /// <summary>
        /// バスが ERROR を出した（<c>not-negotiated</c> 等）。<b>畳むのは枝A のスレッド</b>。
        /// </summary>
        public volatile bool EncoderFailed;

        /// <summary>
        /// 退役済み。<b>mux スレッドはこれを見てから仕事をする</b> ──
        /// <c>SetState(Null)</c> はロックの外で走るので、退役の決定とコールバックの
        /// 停止には時間差がある。
        /// </summary>
        public volatile bool Retired;
    }

    // ---- 枝A のスレッド ----

    /// <summary>
    /// プレビュー枝に届いた生サンプル 1 枚を第 2 パイプラインへ回す。
    /// <b>呼び出しは宿主のストリーミングスレッド</b>で、<paramref name="sample"/> は
    /// このハンドラを抜けた時点で破棄される（＝同期で消費し切る）。
    ///
    /// <para>
    /// <b>所有権: <paramref name="sample"/> は消費しない。</b> 押し込むのはここで作る
    /// 複製で、<c>PushBuffer</c> がそれを消費する。
    /// </para>
    /// </summary>
    internal void OnRawSample(Sample sample)
    {
        // **非配信時はここで終わる。** volatile の 1 回読みと参照の比較だけ。
        if (!_wantMux && Volatile.Read(ref _mux) is null)
            return;

        // **待たない。** ここは録画と同じストリーミングスレッドなので、取れなければ
        // サンプルを 1 枚落として降りる（プレビューの 1 枚は捨ててよい）。
        bool entered = false;
        Pipeline? retired = null;
        try
        {
            Monitor.TryEnter(_muxLock, ref entered);
            if (!entered)
                return;

            retired = Advance(sample);
        }
        catch (Exception ex)
        {
            // **1 枚の失敗で録画を殺さない。** 次のサンプルで組み直しが試みられる。
            Components.ActivityLog.Error("dash.stream-error",
                $"recorder='{_host.Name}' reason={StopReason.StreamError} detail={ex.Message}");
            retired ??= Teardown(StopReason.StreamError);
        }
        finally
        {
            if (entered)
                Monitor.Exit(_muxLock);
        }

        // **Null 化と Dispose はロックの外で。** 進行中の mux スレッドの退去を待つので、
        // 録画スレッドを止めないようプールへ逃がす。
        RetireAsync(retired);
    }

    /// <summary>
    /// <see cref="_muxLock"/> の下で行う本体。戻り値は<b>ロックの外で畳むべき</b>
    /// パイプライン（無ければ null）。
    /// </summary>
    private Pipeline? Advance(Sample sample)
    {
        if (!_isAlive)
            return null;

        // 読み手が居なくなった。明示的な解除は無いので、貸出の期限だけが畳む条件になる。
        if (DashPreviewLimits.LeaseMs < Environment.TickCount64 - Volatile.Read(ref _lastTouchTicks))
        {
            _wantMux = false;
            return Teardown(StopReason.LeaseExpired);
        }

        if (_mux is not { } engine)
        {
            if (_wantMux)
            {
                StartMux(sample);

                // **起こしたきっかけの 1 枚もそのまま押し込む。** ここには
                // LivePreviewStream のようなリングからの流し込みが無いので、
                // 落とすと連続体の先頭が 1 枚欠ける。caps はいまこの sample から
                // 設定した直後なので、必ず一致している。
                if (_mux is { } started)
                    Push(started, sample);
            }

            return null;
        }

        // mux スレッドが見つけた破綻（切り出し・集約）。畳むのはここ。
        if (_faulted)
        {
            _faulted = false;
            return Teardown(_faultReason ?? StopReason.StreamError);
        }

        // バスの ERROR。この候補は使えないので、次のサンプルで次の候補から組み直す。
        if (engine.EncoderFailed)
        {
            _rejectedEncoders.Add(engine.FactoryName);
            return Teardown(StopReason.EncoderFailed);
        }

        // 4 設定は毎サンプル読み直す（宿主から通知を受けない）。
        if (engine.Width != _host.PreviewWidth
            || engine.Height != _host.PreviewHeight
            || engine.Fps != _host.PreviewFps
            || engine.BitrateKbps != _host.PreviewBitrateKbps)
        {
            return Teardown(StopReason.SettingsChanged);
        }

        // caps が変わったら init も変わる。連続体を切り直す。
        using (var negotiated = sample.GetCaps())
        {
            if (negotiated is null || !string.Equals(negotiated.ToString(), engine.CapsText, StringComparison.Ordinal))
                return Teardown(StopReason.CapsChanged);
        }

        Push(engine, sample);
        return null;
    }

    /// <summary>
    /// 第 2 パイプラインを作って <c>PLAYING</c> にする。
    /// <b><see cref="_muxLock"/> を保持したまま呼ぶこと。</b>
    ///
    /// <para>
    /// <b>候補は先頭 1 つだけ試す。</b> ここは録画のストリーミングスレッドなので、
    /// 1 回のサンプルで候補列を舐めると、失敗のたびにその回数ぶん
    /// <c>parse_launch</c> と状態遷移を回すことになる。落ちた候補は
    /// <see cref="_rejectedEncoders"/> へ入れて、<b>次のサンプルで次の候補</b>へ進む。
    /// </para>
    /// </summary>
    private void StartMux(Sample sample)
    {
        if (_faultedEncoder)
            return;

        int width = _host.PreviewWidth;
        int height = _host.PreviewHeight;
        int fps = _host.PreviewFps;
        int bitrateKbps = _host.PreviewBitrateKbps;

        if (NextCandidate(fps, bitrateKbps) is not { } encoder)
        {
            // **記録は 1 回だけ。** 毎サンプル出すと activity.log がこれで埋まる。
            _faultedEncoder = true;
            Components.ActivityLog.Error("dash.stream-error",
                $"recorder='{_host.Name}' {DashPreviewReasons.EncoderUnavailable}");
            return;
        }

        Pipeline? pipeline = null;
        MuxEngine engine;
        IDisposable? subscription = null;
        try
        {
            // **ParseLaunch も try の内側で。** 失敗すると Gst.GLib.GException を投げるので、
            // 外に出すとこの候補が不採用にならず、毎サンプル同じ候補で parse_launch を
            // 回すことになる（要素やプロパティが版によって無い候補はこの形で落ちる）。
            pipeline = (Pipeline)Gst.Global.ParseLaunch(BuildPipeline(width, height, fps, encoder.LaunchString));

            pipeline.SetName($"dash-preview-{_host.Name}");
            var src = (GstApp.AppSrc)pipeline.GetByName("src")!;
            var sink = (GstApp.AppSink)pipeline.GetByName("sink")!;

            // **ネゴシエート済みの caps をそのまま渡す**（ContinuousRecorder と同じ）。
            // 渡さないと下流が typefind で推測することになり、外れると全フレームが
            // 黙って捨てられる。GetCaps のラッパーは自前の参照を 1 本持つので必ず
            // Dispose する（SetCaps はコピーを取る）。
            using var negotiated = sample.GetCaps()
                ?? throw new InvalidOperationException("the preview sample carried no caps");
            src.SetCaps(negotiated);

            engine = new MuxEngine(
                pipeline, src, sink, negotiated.ToString(), encoder.FactoryName,
                width, height, fps, bitrateKbps);

            sink.SetSimpleCallbacks(onNewSample: s => OnMuxSample(engine, s));

            // **購読は PLAYING より前に済ませる**（ContinuousRecorder と同じ理由）──
            // まだ NULL 状態のバスには post が届かないので、再生開始の直後に出た
            // not-negotiated も取りこぼさない。
            if (pipeline.GetBus() is { } bus)
                subscription = engine.Subscription = bus.SubscribeSyncDrop((_, m) => OnBusMessage(engine, m));

            if (pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
                throw new InvalidOperationException("the dash preview pipeline did not want to play");
        }
        catch (Exception ex)
        {
            // **作りかけを必ず畳む。** 要素・バスはインターンされた GObject ラッパーなので
            // Dispose しない ── 自前で作ったパイプラインだけを Null に落としてから Dispose する。
            _rejectedEncoders.Add(encoder.FactoryName);
            Components.ActivityLog.Error("dash.stream-error",
                $"recorder='{_host.Name}' encoder='{encoder.FactoryName}' {ex.Message}");

            // 購読はパイプラインの Dispose より前に外す。
            subscription?.Dispose();
            if (pipeline is not null)
            {
                pipeline.SetState(State.Null);
                pipeline.Dispose();
            }

            _ = Teardown(StopReason.EncoderFailed);
            return;
        }

        _mux = engine;
        _generation++;

        var startedAt = DateTimeOffset.UtcNow;
        lock (_ringLock)
        {
            _ringGeneration = _generation;
            _ringWidth = width;
            _ringHeight = height;
            _ringFps = fps;
            _ringBitrateKbps = bitrateKbps;
            _ringAvailabilityStartUtc = startedAt;
        }

        Components.ActivityLog.Info("dash.stream-start",
            $"recorder='{_host.Name}' encoder='{encoder.FactoryName}' size={width}x{height} "
            + $"fps={fps} kbps={bitrateKbps} generation={_generation}");
    }

    /// <summary>
    /// まだ試していない先頭の候補。<b>ビットレートを当てられない定義は不採用</b>にする
    /// ── <c>bitrate=</c> を持たない候補は <c>WithBitrateKbps</c> が素通しするだけなので
    /// 使えなくはないが、単位が確認できていない定義（<c>BitrateUnitPerKbps</c> が
    /// 設定されているのにトークンが無い）は設計違反として投げてくる。
    /// </summary>
    private H264EncoderDef? NextCandidate(int fps, int bitrateKbps)
    {
        var candidates = EncoderCatalog.Resolve(
            EventRecordingType.System, EventRecorder.PreferredH264Encoder,
            EncoderCatalog.ProbeWithGStreamer, gop: fps);

        foreach (var candidate in candidates)
        {
            if (_rejectedEncoders.Contains(candidate.FactoryName))
                continue;

            try
            {
                return candidate.WithBitrateKbps(bitrateKbps);
            }
            catch (InvalidOperationException)
            {
                _rejectedEncoders.Add(candidate.FactoryName);
            }
        }

        return null;
    }

    /// <summary>
    /// サンプル 1 枚の複製を <c>appsrc</c> へ押し込む。
    /// <b><paramref name="sample"/> は消費しない</b>（複製の方を <c>PushBuffer</c> が消費する）。
    ///
    /// <para>
    /// <b>拒否は記録しない。</b> <c>appsrc</c> は <c>leaky-type=downstream</c> なので、
    /// 溢れたときに古い方が落ちるのは設計どおりであり、録画には一切影響しない。
    /// </para>
    /// </summary>
    private static void Push(MuxEngine engine, Sample sample)
    {
        using var buffer = sample.GetBuffer();
        if (buffer is null)
            return;

        // gst_buffer_copy 相当（GST_BUFFER_COPY_ALL・全域）。null は複製の失敗。
        var copy = buffer.CopyRegion(BufferCopy.All, 0, nuint.MaxValue);
        if (copy is null)
            return;

        _ = engine.Src.PushBuffer(copy);
    }

    /// <summary>
    /// mux を退役させ、保持物を捨てる。<b><see cref="_muxLock"/> を保持したまま呼ぶこと。</b>
    /// 戻り値は<b>ロックの外で</b> <c>Null</c> → <c>Dispose</c> すべきパイプライン。
    ///
    /// <para>
    /// <b>リングは空にする。</b> 次の連続体は新しい init で始まるので、
    /// 前の init に紐づいたセグメントを残すと、読み手は復号できない列を掴む。
    /// </para>
    /// </summary>
    private Pipeline? Teardown(string reason)
    {
        if (_mux is not { } engine)
            return null;

        engine.Retired = true;
        _mux = null;
        _faulted = false;
        _faultReason = null;

        // 購読はパイプラインの Dispose より前に外す（ContinuousRecorder と同じ）。
        engine.Subscription?.Dispose();
        engine.Subscription = null;

        lock (_ringLock)
        {
            _ring.Clear();
            _init = null;
            _codecs = null;
            _timescale = 0;
            _presentationTimeOffset = 0;
            _hasPresentationTimeOffset = false;
        }

        Components.ActivityLog.Info("dash.stream-stop", $"recorder='{_host.Name}' reason={reason}");
        return engine.Pipeline;
    }

    /// <summary>退役したパイプラインの後始末をプールスレッドへ逃がす。</summary>
    private void RetireAsync(Pipeline? pipeline)
    {
        if (pipeline is null)
            return;

        _ = STTask.Run(() => Retire(pipeline));
    }

    /// <summary>
    /// 退役したパイプラインを畳む。<b>要素はインターンされた GObject ラッパーなので
    /// Dispose しない</b> ── 自前で作ったパイプラインだけを <c>Null</c> に落としてから Dispose する。
    /// </summary>
    private void Retire(Pipeline? pipeline)
    {
        if (pipeline is null)
            return;

        try
        {
            pipeline.SetState(State.Null);
            pipeline.Dispose();
        }
        catch (Exception ex)
        {
            Components.ActivityLog.Error("dash.stream-error",
                $"recorder='{_host.Name}' the dash preview pipeline did not shut down cleanly: {ex.Message}");
        }
    }

    // ---- mux スレッド ----

    /// <summary>
    /// 第 2 パイプラインの <c>appsink</c> コールバック。切り出して集約してリングへ積むだけで、
    /// <see cref="_muxLock"/> も <c>SetState</c> も触らない。
    ///
    /// <para>
    /// <b>例外を漏らさない。</b> トランポリンは抜けた例外を <c>FlowReturn.Error</c> へ
    /// 変換するので、漏らすと mux が黙って止まる（配信だけが無音で死ぬ）。
    /// </para>
    /// </summary>
    private FlowReturn OnMuxSample(MuxEngine engine, GstApp.AppSink sink)
    {
        if (engine.Retired)
            return FlowReturn.Ok;

        try
        {
            // **1 プルでは足りない。** appsink は 1 render につき 1 回しか呼ばないので、
            // 取り付け前に溜まった分は初回に吸い切る。
            while (sink.TryPullSample(ClockTime.Zero) is { } sample)
            {
                using (sample)
                {
                    // 退役後に届いた分は捨てる（作り直しの Init と混ざらない）。
                    if (engine.Retired)
                        continue;

                    using var buffer = sample.GetBuffer();
                    if (buffer is null)
                        continue;

                    using var map = buffer.Map(MapFlags.Read);
                    engine.Splitter.Push(map.Span);
                }
            }

            while (engine.Splitter.TryDequeue(out var segment))
                Consume(engine, segment);

            // **理由は固定集合へ畳む。** 切り出し器の自由文と、集約器が返しうる
            // 想定外の文字列は detail 側へ回す（StopReason の doc）。
            if (engine.Splitter.IsFaulted)
            {
                Fault(engine, StopReason.SplitterFault, engine.Splitter.Fault);
            }
            else if (engine.Assembler.IsFaulted)
            {
                string? fault = engine.Assembler.Fault;
                if (fault is StopReason.PtsRewind or StopReason.GopTooLong)
                    Fault(engine, fault);
                else
                    Fault(engine, StopReason.StreamError, fault);
            }
        }
        catch (Exception ex) when (!engine.Retired)
        {
            Fault(engine, StopReason.StreamError, ex.Message);
        }
        catch (Exception ex)
        {
            // 畳んだ後に遅れて届いた 1 件で作り直しを要求すると、
            // いま動いている新しい mux が巻き添えで落ちる。
            DebugLogEx.Log(DebugLevel.Error, $"a retired dash preview mux callback failed!\n{ex}");
        }

        // 空プルでも Ok を返す（Eos を返すと枝が止まる）。
        return FlowReturn.Ok;
    }

    /// <summary>切り出した 1 件を保持物へ取り込む。<b>mux スレッド専有。</b></summary>
    private void Consume(MuxEngine engine, Fmp4Segment segment)
    {
        if (segment.Kind == PreviewSegmentKind.Init)
        {
            if (!Fmp4InitInfo.TryParse(segment.Bytes, out var info))
            {
                Fault(engine, StopReason.InitUnparsable);
                return;
            }

            lock (_ringLock)
            {
                // **退役の判定は保持物を守るロックの内側で。** Teardown は Retired を
                // 立ててから同じロックでリングと Init を消すので、ここで見れば
                // 遅れて届いた旧世代の Init を書き戻すことはない。
                if (engine.Retired)
                    return;

                _init = segment.Bytes;
                _timescale = info.Timescale;
                _codecs = info.Codecs;
            }
            return;
        }

        engine.Assembler.Push(segment);

        while (engine.Assembler.TryDequeue(out var media))
        {
            lock (_ringLock)
            {
                // 旧世代のセグメントを新しいリングへ混ぜない（Init も同じ理由で守る）。
                if (engine.Retired)
                    return;

                if (!_hasPresentationTimeOffset)
                {
                    _presentationTimeOffset = media.Time;
                    _hasPresentationTimeOffset = true;
                }

                _ring.Enqueue(media);
                while (DashPreviewLimits.RingDepth < _ring.Count)
                    _ring.Dequeue();
            }
        }
    }

    /// <summary>
    /// 破綻を記録して、畳むことを枝A のスレッドへ頼む。
    /// <b>ここから <see cref="_muxLock"/> は取らない</b> ── 取ると、畳んでいる最中の
    /// <c>SetState(Null)</c>（＝このスレッドの退去待ち）と組んで詰む。
    /// </summary>
    private void Fault(MuxEngine engine, string reason, string? detail = null)
    {
        if (engine.Retired)
            return;

        Components.ActivityLog.Error("dash.stream-error",
            detail is null
                ? $"recorder='{_host.Name}' reason={reason}"
                : $"recorder='{_host.Name}' reason={reason} detail={detail}");
        _faultReason = reason;
        _faulted = true;
    }

    /// <summary>
    /// バスの同期ハンドラ。<b>立てるだけで畳まない</b>（畳むのは枝A のスレッド）。
    /// メッセージのラッパーはハンドラの実行中だけ有効なので Dispose しない。
    /// </summary>
    private void OnBusMessage(MuxEngine engine, Message message)
    {
        try
        {
            if (message.Type != MessageType.Error || engine.Retired)
                return;

            // ParseError はネイティブ側のメモリをすべてバインディングが解放した上で
            // GException（ただの managed 例外オブジェクト）を返すので、Dispose は不要。
            var (gerror, _) = message.ParseError();
            Components.ActivityLog.Error("dash.stream-error",
                $"recorder='{_host.Name}' encoder='{engine.FactoryName}' {gerror.Message}");
            engine.EncoderFailed = true;
        }
        catch (Exception ex)
        {
            DebugLogEx.Log(DebugLevel.Error, $"the dash preview bus handler failed!\n{ex}");
        }
    }

    // ---- HTTP / UI スレッド ----

    /// <summary>
    /// いまの姿を 1 枚取る。<b>呼ぶこと自体が貸出を延ばす</b>ので、読み手が居なくなれば
    /// 第 2 パイプラインは <see cref="DashPreviewLimits.LeaseMs"/> 後に畳まれる。
    ///
    /// <para>
    /// <b><see cref="_muxLock"/> は取らない。</b> あれは録画スレッドの専有で、
    /// HTTP のスレッドがそこで待つと、遅い読み手が録画の枝を止めることになる。
    /// </para>
    /// </summary>
    internal bool TryGetSnapshot(out DashPreviewSnapshot? snapshot, out string? reason)
    {
        snapshot = null;

        // **候補が尽きているなら貸出を延ばさない。** 延ばすと、二度と組まない mux の
        // ために枝A のスレッドが毎サンプル lease を判定し続けることになる。
        if (_faultedEncoder)
        {
            reason = DashPreviewReasons.EncoderUnavailable;
            return false;
        }

        Volatile.Write(ref _lastTouchTicks, Environment.TickCount64);
        _wantMux = true;

        lock (_ringLock)
        {
            if (_init is not { } init || _codecs is not { } codecs || _ring.Count == 0)
            {
                reason = DashPreviewReasons.Starting;
                return false;
            }

            snapshot = new DashPreviewSnapshot(
                _ringGeneration, init, _timescale, codecs,
                _ringWidth, _ringHeight, _ringFps, _ringBitrateKbps,
                _ringAvailabilityStartUtc, _presentationTimeOffset, (DashMediaSegment[])[.. _ring]);
        }

        reason = null;
        return true;
    }

    // ---- 停止 ----

    /// <summary>
    /// エンジンを止める。宿主は<b>録画の枝を静止させてから</b>同期で呼ぶ。
    ///
    /// <para>
    /// <b>取れなければ諦める。</b> 静止が予算内に終わらなかった場合だけ、枝A のスレッドが
    /// まだ mux を触っていることがある ── そのまま <c>SetState(Null)</c> / <c>Dispose</c> すると
    /// 使用中のネイティブオブジェクトを壊すので、参照だけ落として記録に残す
    /// （<c>recorder.leak</c> と同じ「リークするが壊さない」規律）。
    /// </para>
    /// </summary>
    public void Close()
    {
        _isAlive = false;

        bool entered = false;
        Pipeline? retired = null;
        try
        {
            Monitor.TryEnter(_muxLock, CallbackExitTimeoutMs, ref entered);
            if (entered)
            {
                retired = Teardown(StopReason.Close);
            }
            else
            {
                Components.ActivityLog.Warn("dash.leak",
                    $"recorder='{_host.Name}' a dash preview sample callback was still running after "
                    + $"{CallbackExitTimeoutMs}ms; leaked the muxer instead of stopping it");
            }
        }
        finally
        {
            if (entered)
                Monitor.Exit(_muxLock);
        }

        // **同期で畳む。** 呼び出し元はこの直後に宿主のパイプラインを解放しうるので、
        // プールへ逃がすと解放済みのものを触りにいく。
        Retire(retired);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Close();
    }
}
