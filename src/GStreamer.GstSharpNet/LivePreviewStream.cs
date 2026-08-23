using Gst;
using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using GstApp = Gst.App;
using STTask = System.Threading.Tasks.Task;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// ライブプレビューの配信エンジンが宿主（<see cref="EventRecorder"/>）へ求める最小の口。
/// エンジンは宿主の内部状態も、宿主が持つどのロックも知らない。
/// </summary>
internal interface ILivePreviewHost
{
    /// <summary>ログ・診断に使うレコーダー名。</summary>
    string Name { get; }
}

/// <summary>
/// <b>ライブプレビューの配信エンジン。</b> イベント録画の枝（sink パイプラインの
/// 録画用 <c>appsink</c>）へ届く H.264 アクセスユニットを、<b>レコーダーごとに 1 本</b>の
/// 小さな mux パイプラインへ通して fMP4（<c>ftyp/moov</c> ＋ <c>moof/mdat</c>）に組み替え、
/// 購読者（HTTP の <c>preview.mp4</c>）へ配る。
///
/// <para>
/// <b>購読者が 0 人のときは何も作らない。</b> <see cref="OnEncodedSample"/> の先頭にある
/// volatile 1 回読みで抜けるだけで、mux パイプラインも <c>Fmp4SegmentSplitter</c> も
/// 存在しない ── 配信していないレコーダーの録画経路の費用は、この機能を足す前と同じである。
/// </para>
/// <para>
/// <b>再エンコードはしない。</b> 押し込むのは録画と同じ符号化済みバッファの複製で、
/// mux（<c>mp4mux fragment-mode=dash-or-mss</c>）だけが増える。
/// </para>
/// <para>
/// <b>スレッドは 3 種類ある。</b>
/// (1) <b>録画スレッド</b>＝ <see cref="OnEncodedSample"/>。mux の生成・破棄・押し込みと
/// リングの列挙はここの専有で、守るのは <see cref="_muxLock"/>。
/// (2) <b>mux スレッド</b>＝ 自前パイプラインの <c>appsink</c> コールバック。
/// 切り出しと購読者への配布だけを行い、<see cref="_muxLock"/> は<b>取らない</b>。
/// (3) <b>HTTP / UI スレッド</b>＝ <see cref="TrySubscribe"/> と購読の <c>Dispose</c>。
/// 取るのは <see cref="_subLock"/> だけ。
/// </para>
/// <para>
/// <b>ロックの順序は <see cref="_muxLock"/> → <see cref="_subLock"/> の一方向だけ。</b>
/// 逆順は禁止で、<c>SetState</c> は <see cref="_subLock"/> を保持したまま呼ばない ──
/// mux スレッドが配布のために <see cref="_subLock"/> を待っている間に
/// <c>SetState(Null)</c>（＝ mux スレッドの退去待ち）へ入ると、そこで詰む。
/// </para>
/// <para>
/// <b><c>SetState(Null)</c> を <see cref="_muxLock"/> 保持中に呼ぶ箇所が 1 つだけある</b>
/// ── <see cref="StartMux"/> の catch（作りかけの片付け）である。<b>そこは安全</b>:
/// そのパイプラインはまだ <c>PLAYING</c> に達しておらず、退去を待つべき
/// ストリーミングスレッドが存在しない。定常運転で退役させる経路
/// （<see cref="Teardown"/> の戻り値）は、必ずロックを抜けてから畳む。
/// </para>
/// <para>
/// <b>宿主のロックは 1 つも取らない。</b> 呼ばれるのは宿主のストリーミングスレッドなので、
/// 宿主側のロックへ手を出すと停止経路の待ちと輪を作る（<see cref="ContinuousRecorder"/> と同じ規律）。
/// 続けるかどうかを決めるのは <see cref="_isAlive"/>（volatile）だけである。
/// </para>
/// </summary>
internal sealed partial class LivePreviewStream : IDisposable
{
    /// <summary>
    /// <b>波 0 で実測した凍結文字列。</b> <c>appsrc</c> は詰まっても録画側を止めない
    /// （<c>block=false</c> ＋ <c>leaky-type=downstream</c>）。<c>appsink</c> は
    /// クロックに同期させない（<c>sync=false async=false</c>）── 1 本の枝の遅延申告で
    /// 配信が待たされる形を作らない。<c>faststart</c> は付けない（ライブでは決着しない）。
    /// </summary>
    public const string PreviewMuxPipeline =
        "appsrc name=src format=time block=false max-bytes=4194304 leaky-type=downstream ! h264parse ! " +
        "mp4mux name=mux fragment-duration=1000 fragment-mode=dash-or-mss ! appsink name=sink sync=false async=false";

    /// <summary>
    /// fragment 1 つの長さ(ms)。<see cref="PreviewMuxPipeline"/> の
    /// <c>fragment-duration</c> と同じ値でなければならない（L1 が双方向で固定している）。
    /// </summary>
    public const int FragmentDurationMs = 1000;

    /// <summary>
    /// <see cref="Close"/> が「進行中のサンプル処理が抜ける」のを待つ上限(ms)。
    /// <see cref="ContinuousRecorder.CallbackExitTimeoutMs"/> と同じ 5 秒。
    /// </summary>
    public const int CallbackExitTimeoutMs = 5000;

    /// <summary>
    /// プロセス全体の購読者数（<see cref="PreviewStreamLimits.MaxTotalSubscribers"/> の会計）。
    /// レコーダーをまたぐので静的に持つ。
    /// </summary>
    private static int s_totalSubscribers;

    private readonly ILivePreviewHost _host;

    /// <summary>
    /// mux（<see cref="_mux"/>）の生成・破棄に触ってよい者を 1 人に絞るロック。
    /// 取るのは録画スレッドと <see cref="Close"/> だけ。
    ///
    /// <para>
    /// <b>保持中に無期限の待ちを行わない。</b> 中で呼ぶ <c>SetState</c> は
    /// 自前で作った mux パイプラインの <c>PLAYING</c> 化だけで、宿主のパイプラインには
    /// 触らない ── 宿主の停止経路（実行中のコールバックの復帰待ち）と組んだ輪はできない。
    /// <c>Null</c> 化はこのロックを抜けてから行う。
    /// </para>
    /// </summary>
    private readonly object _muxLock = new();

    /// <summary>
    /// 購読者の一覧と「最後に配ったもの」に触ってよい者を 1 人に絞るロック。
    ///
    /// <para>
    /// <b><see cref="PreviewSubscription.Publish"/> の呼び出しは全部この下にある。</b>
    /// 呼ぶのは mux スレッドと HTTP / UI スレッドの 2 種類だが、ここで直列化されるので
    /// 「単一の供給スレッドから呼ぶ」という契約は満たされる。
    /// </para>
    /// </summary>
    private readonly object _subLock = new();

    /// <summary>購読者（<see cref="_subLock"/>）。席を返すのは HTTP 側の <c>Dispose</c>。</summary>
    private readonly List<PreviewSubscription> _subs = [];

    /// <summary>エンジンが生きているか。<see cref="Close"/> が倒す。</summary>
    private volatile bool _isAlive;

    /// <summary>購読者が 1 人以上いるか（mux を作る唯一の条件）。</summary>
    private volatile bool _wantMux;

    /// <summary>
    /// mux を作り直すべきか。<b>立てるのは mux スレッド、消すのは録画スレッド</b>
    /// ── mux スレッドから <c>SetState</c> を呼ぶと自スレッドの退去を待つことになる。
    /// </summary>
    private volatile bool _wantRestart;

    /// <summary>いま動いている mux（<see cref="_muxLock"/>）。購読者が 0 人なら null。</summary>
    private MuxEngine? _mux;

    /// <summary>
    /// 直近の Init（<see cref="_subLock"/>）。新しい購読者へはこれを最初に配る。
    /// </summary>
    private PreviewSegment? _lastInit;

    /// <summary>
    /// 直近の Media（<see cref="_subLock"/>）。<b>同期始まりのときだけ</b>保持し、
    /// 非同期始まりが来たら null に戻す ── 飛び飛びのセグメントを先渡しすると
    /// 参照が切れて崩れた絵になる。
    /// </summary>
    private PreviewSegment? _lastSyncMedia;

    /// <summary>直前に押し込んだ PTS（録画スレッド専有）。</summary>
    private ulong _lastPushedPts;

    private bool _disposed;

    public LivePreviewStream(ILivePreviewHost host)
    {
        _host = host;

        // 生成は宿主の初期化（sink パイプラインが PLAYING に達した段）で行われるので、
        // ここで開けてよい。倒すのは Close だけ。
        _isAlive = true;
    }

    /// <summary>
    /// mux 1 本ぶんの状態。<b>切り出し器はここに持つ</b> ── 作り直しのたびに
    /// 新しくなるので、前の mux の途中の箱が次の Init へ混ざらない。
    /// </summary>
    private sealed class MuxEngine(Pipeline pipeline, GstApp.AppSrc src, GstApp.AppSink sink, string capsText)
    {
        public Pipeline Pipeline { get; } = pipeline;

        public GstApp.AppSrc Src { get; } = src;

        public GstApp.AppSink Sink { get; } = sink;

        /// <summary>
        /// <c>appsrc</c> へ設定した caps の文字列表現。<b>ラッパーは保持しない</b>
        /// ── 比較に要るのは値だけで、ネイティブの参照を抱える理由が無い。
        /// </summary>
        public string CapsText { get; } = capsText;

        public Fmp4SegmentSplitter Splitter { get; } = new();

        /// <summary>
        /// 退役済み。<b>mux スレッドはこれを見てから配る</b> ── <c>SetState(Null)</c> は
        /// ロックの外で走るので、退役の決定とコールバックの停止には時間差がある。
        /// </summary>
        public volatile bool Retired;

        /// <summary>次に振る通し番号（mux スレッド専有）。</summary>
        public long NextSequence;
    }

    // ---- 録画スレッド ----

    /// <summary>
    /// 録画の枝に届いた符号化済みサンプル 1 枚を mux へ回す。
    /// <b>呼び出しは宿主のストリーミングスレッド</b>で、<paramref name="ring"/> の列挙も
    /// このスレッドの専有である（<see cref="RecordingRingBuffer{T}"/> の契約）。
    ///
    /// <para>
    /// <b>所有権: <paramref name="sample"/> も <paramref name="buffer"/> も消費しない。</b>
    /// 押し込むのはここで作る複製で、<c>PushBuffer</c> がそれを消費する。
    /// </para>
    /// </summary>
    /// <param name="sample">枝から取り出したサンプル（caps の取得にだけ使う）。</param>
    /// <param name="buffer"><paramref name="sample"/> のバッファ（借り物）。</param>
    /// <param name="pts">このバッファの PTS(ns)。</param>
    /// <param name="ring">事前バッファのリング（mux 起動時の流し込み元）。</param>
    public void OnEncodedSample(Sample sample, Gst.Buffer buffer, ulong pts, RecordingRingBuffer<Gst.Buffer> ring)
    {
        // **非配信時はここで終わる。** volatile の 1 回読みと参照の比較だけ。
        if (!_wantMux && Volatile.Read(ref _mux) is null)
            return;

        Pipeline? retired = null;
        try
        {
            // 切り出し器が壊れたことを mux スレッドが立てている。作り直しはここで行う
            // （SetState を呼んでよいのは録画スレッドと Close だけ）。
            if (_wantRestart)
            {
                _wantRestart = false;
                Pipeline? faulted;
                lock (_muxLock)
                    faulted = Teardown("splitter fault");
                RetireAsync(faulted);
            }

            lock (_muxLock)
                retired = Advance(sample, buffer, pts, ring);
        }
        catch (Exception ex)
        {
            // **1 枚の失敗で録画を殺さない。** 購読者は Teardown が完了させるので、
            // クライアントは再接続し、そこで作り直しが試みられる。
            Components.ActivityLog.Error("preview.stream-error", $"recorder='{_host.Name}' {ex.Message}");
            lock (_muxLock)
                retired ??= Teardown("stream error");
        }

        // **Null 化と Dispose はロックの外で。** 進行中の mux スレッドの退去を待つので、
        // 録画スレッドを止めないようプールへ逃がす（ContinuousRecorder の排出と同じ理由）。
        RetireAsync(retired);
    }

    /// <summary>
    /// <see cref="_muxLock"/> の下で行う本体。戻り値は<b>ロックの外で畳むべき</b>
    /// パイプライン（無ければ null）。
    /// </summary>
    private Pipeline? Advance(Sample sample, Gst.Buffer buffer, ulong pts, RecordingRingBuffer<Gst.Buffer> ring)
    {
        if (!_isAlive)
            return null;

        if (!_wantMux)
            return _mux is null ? null : Teardown("no subscribers");

        if (_mux is null)
        {
            // 起動と流し込みは 1 度のロックの中で済ませる（ContinuousRecorder.OpenSegment と同じ）。
            StartMux(sample, ring);
            _lastPushedPts = pts;
            return null;
        }

        // PTS の巻き戻し（ソースの再起動、あるいは並べ替え）。符号なし減算のまま
        // 押し込むと mux のタイムスタンプが壊れるので、作り直して仕切り直す。
        if (pts < _lastPushedPts)
            return Teardown("pts rewind");

        // caps が変わったら 2 つ目の Init が要る ── 契約でそれは禁止なので、
        // 作り直して購読者に再接続させる。
        using (var negotiated = sample.GetCaps())
        {
            if (negotiated is null || !string.Equals(negotiated.ToString(), _mux.CapsText, StringComparison.Ordinal))
                return Teardown("caps changed");
        }

        Push(_mux, buffer);
        _lastPushedPts = pts;
        return null;
    }

    /// <summary>
    /// mux パイプラインを作って <c>PLAYING</c> にし、リング末尾のキーフレーム以降を流し込む。
    /// <b><see cref="_muxLock"/> を保持したまま呼ぶこと。</b>
    /// </summary>
    private void StartMux(Sample sample, RecordingRingBuffer<Gst.Buffer> ring)
    {
        // ParseLaunch は失敗すると Gst.GLib.GException を投げる（呼び出し側の catch が報告する）。
        var pipeline = (Pipeline)Gst.Global.ParseLaunch(PreviewMuxPipeline);
        MuxEngine engine;
        try
        {
            pipeline.SetName($"live-preview-{_host.Name}");
            var src = (GstApp.AppSrc)pipeline.GetByName("src")!;
            var sink = (GstApp.AppSink)pipeline.GetByName("sink")!;

            // **ネゴシエート済みの caps をそのまま渡す。** 渡さないと h264parse が
            // stream-format / alignment を推測することになり、外れると NAL が黙って
            // 捨てられて中身の無い fMP4 が出る。GetCaps のラッパーは自前の参照を
            // 1 本持つので必ず Dispose する（SetCaps はコピーを取る）。
            using var negotiated = sample.GetCaps()
                ?? throw new InvalidOperationException("the encoded sample carried no caps");
            src.SetCaps(negotiated);

            engine = new MuxEngine(pipeline, src, sink, negotiated.ToString());
            sink.SetSimpleCallbacks(onNewSample: s => OnMuxSample(engine, s));

            if (pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
                throw new InvalidOperationException("the live preview muxer did not want to play");
        }
        catch
        {
            // **作りかけを必ず畳む。** 要素・バスはインターンされた GObject ラッパーなので
            // Dispose しない ── 自前で作ったパイプラインだけを Null に落としてから Dispose する。
            pipeline.SetState(State.Null);
            pipeline.Dispose();
            throw;
        }

        _mux = engine;

        int subscribers;
        lock (_subLock)
            subscribers = _subs.Count;
        Components.ActivityLog.Info("preview.stream-start",
            $"recorder='{_host.Name}' subscribers={subscribers} fragmentMs={FragmentDurationMs}");

        PrimeFromRing(engine, ring);
    }

    /// <summary>
    /// リングの<b>末尾のキーフレームから末尾まで</b>を順に押し込む。初画までの待ちを
    /// 1 GOP ぶん短くするのがねらいで、キーフレームが 1 枚も無ければ何もしない
    /// （次の IDR まで mux は待つ）。
    ///
    /// <para>
    /// <b>現在のバッファを二重に押さない。</b> 呼び出し元の <c>ProcessRecordSample</c> は
    /// リングへ複製を積んだ<b>後</b>にここへ来るので、当のバッファは既に末尾に居る。
    /// </para>
    /// </summary>
    private static void PrimeFromRing(MuxEngine engine, RecordingRingBuffer<Gst.Buffer> ring)
    {
        int index = 0;
        int keyIndex = -1;
        foreach (var (_, item) in ring)
        {
            if (!item.HasFlags(BufferFlags.DeltaUnit))
                keyIndex = index;
            index++;
        }

        if (keyIndex < 0)
            return;

        index = 0;
        foreach (var (_, item) in ring)
        {
            if (keyIndex <= index)
                Push(engine, item);
            index++;
        }
    }

    /// <summary>
    /// バッファ 1 本の複製を mux の <c>appsrc</c> へ押し込む。
    /// <b><paramref name="buffer"/> は消費しない</b>（複製の方を <c>PushBuffer</c> が消費する）。
    ///
    /// <para>
    /// <b>拒否は記録しない。</b> <c>appsrc</c> は <c>leaky-type=downstream</c> なので、
    /// 溢れたときに古い方が落ちるのは設計どおりであり、録画には一切影響しない。
    /// </para>
    /// </summary>
    private static void Push(MuxEngine engine, Gst.Buffer buffer)
    {
        // gst_buffer_copy 相当（GST_BUFFER_COPY_ALL・全域）。null は複製の失敗。
        var copy = buffer.CopyRegion(BufferCopy.All, 0, nuint.MaxValue);
        if (copy is null)
            return;

        _ = engine.Src.PushBuffer(copy);
    }

    /// <summary>
    /// mux を退役させ、購読者を完了させる。<b><see cref="_muxLock"/> を保持したまま呼ぶこと。</b>
    /// 戻り値は<b>ロックの外で</b> <c>Null</c> → <c>Dispose</c> すべきパイプライン。
    ///
    /// <para>
    /// <b>購読者は全員完了させる（席は消さない）。</b> 2 つ目の Init は契約で禁止なので、
    /// 作り直したストリームは<b>新しい接続</b>で受けてもらうしかない。席が返るのは
    /// HTTP 側の <c>Dispose</c> のときである。
    /// </para>
    /// </summary>
    private Pipeline? Teardown(string reason)
    {
        if (_mux is not { } engine)
            return null;

        engine.Retired = true;
        _mux = null;

        lock (_subLock)
        {
            _lastInit = null;
            _lastSyncMedia = null;
            foreach (var sub in _subs)
                sub.Complete();
        }

        Components.ActivityLog.Info("preview.stream-stop", $"recorder='{_host.Name}' reason={reason}");
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
            Components.ActivityLog.Error("preview.stream-error",
                $"recorder='{_host.Name}' the preview muxer did not shut down cleanly: {ex.Message}");
        }
    }

    // ---- mux スレッド ----

    /// <summary>
    /// 自前パイプラインの <c>appsink</c> コールバック。切り出して購読者へ配るだけで、
    /// <see cref="_muxLock"/> も <c>SetState</c> も触らない。
    ///
    /// <para>
    /// <b>例外を漏らさない。</b> トランポリンは抜けた例外を <c>FlowReturn.Error</c> へ
    /// 変換するので、漏らすと mux が黙って止まる（配信だけが無音で死ぬ）。
    /// 捕まえたら作り直しを要求して降りる。
    /// </para>
    /// </summary>
    private FlowReturn OnMuxSample(MuxEngine engine, GstApp.AppSink sink)
    {
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
                Publish(engine, segment);

            if (engine.Splitter.IsFaulted && !engine.Retired)
            {
                Components.ActivityLog.Error("preview.stream-error",
                    $"recorder='{_host.Name}' the fMP4 stream could not be split: {engine.Splitter.Fault}");

                // 作り直しの実行は録画スレッドへ渡す（ここから SetState を呼ぶと自スレッドを待つ）。
                _wantRestart = true;
            }
        }
        catch (Exception ex) when (!engine.Retired)
        {
            // **退役済みの mux の例外は握り潰す。** 畳んだ後に遅れて届いた 1 件で
            // 作り直しを要求すると、いま動いている新しい mux が巻き添えで落ちる。
            Components.ActivityLog.Error("preview.stream-error", $"recorder='{_host.Name}' {ex.Message}");
            _wantRestart = true;
        }
        catch (Exception ex)
        {
            DebugLogEx.Log(DebugLevel.Error,
                $"a retired live preview mux callback failed!\n{ex}");
        }

        // 空プルでも Ok を返す（Eos を返すと枝が止まる）。
        return FlowReturn.Ok;
    }

    /// <summary>切り出した 1 件を全購読者へ配る。</summary>
    private void Publish(MuxEngine engine, Fmp4Segment segment)
    {
        var published = new PreviewSegment(
            segment.Kind, segment.Bytes, engine.NextSequence++, segment.StartsWithSync);

        lock (_subLock)
        {
            if (engine.Retired)
                return;

            if (published.Kind == PreviewSegmentKind.Init)
                _lastInit = published;
            else
                _lastSyncMedia = segment.StartsWithSync ? published : null;

            foreach (var sub in _subs)
                Deliver(sub, published);
        }
    }

    /// <summary>
    /// 1 人へ配る。<b><see cref="_subLock"/> を保持したまま呼ぶこと。</b>
    /// まだ Init を受けていない購読者には Init を先に渡す ── 順序違反は
    /// <see cref="PreviewSubscription.Publish"/> が例外にする契約である。
    /// </summary>
    private void Deliver(PreviewSubscription sub, PreviewSegment segment)
    {
        if (!sub.HasReceivedInit && segment.Kind != PreviewSegmentKind.Init)
        {
            // Init をまだ持っていなければ何も流さない（次の Init を待つ）。
            if (_lastInit is not { } init || !sub.Publish(init))
                return;
        }

        sub.Publish(segment);
    }

    // ---- HTTP / UI スレッド ----

    /// <summary>
    /// 購読する。失敗の理由は英語（そのまま HTTP の本文になる）。
    ///
    /// <para>
    /// <b>上限は供給側だけが数える。</b> 1 レコーダーあたりと、プロセス全体の 2 段。
    /// </para>
    /// </summary>
    public bool TrySubscribe(
        [NotNullWhen(true)] out PreviewSubscription? subscription,
        [NotNullWhen(false)] out string? reason)
    {
        subscription = null;

        int subscribers;
        lock (_subLock)
        {
            if (!_isAlive)
            {
                reason = "recorder is not running";
                return false;
            }

            if (PreviewStreamLimits.MaxSubscribersPerRecorder <= _subs.Count)
            {
                reason = "too many preview subscribers for this recorder";
                return false;
            }

            // **超えたら必ず戻す。** 戻し損ねると席が二度と空かない。
            if (PreviewStreamLimits.MaxTotalSubscribers < Interlocked.Increment(ref s_totalSubscribers))
            {
                Interlocked.Decrement(ref s_totalSubscribers);
                reason = "too many preview subscribers";
                return false;
            }

            var sub = new PreviewSubscription(_host.Name, Unsubscribe);
            _subs.Add(sub);
            _wantMux = true;

            // **先渡しは同じロックの下で。** これを外に出すと、mux スレッドが先に
            // Media を配って「Init より前に Media」になる。
            if (_lastInit is { } init && sub.Publish(init) && _lastSyncMedia is { } media)
                sub.Publish(media);

            subscribers = _subs.Count;
            subscription = sub;
            reason = null;
        }

        // 記録はロックの外で（Unsubscribe と同じ形）── ActivityLog の書き出しは
        // ファイル IO で、配布の直列化に必要な区間ではない。
        Components.ActivityLog.Info("preview.subscribe",
            $"recorder='{_host.Name}' subscribers={subscribers}");
        return true;
    }

    /// <summary>
    /// 席を返す（<see cref="PreviewSubscription.Dispose"/> がちょうど 1 回呼ぶ）。
    /// <b>mux はここでは止めない</b> ── 止めるのは録画スレッドの次のサンプルである。
    /// サンプルが来なければ <see cref="Close"/> まで残るが、購読者が居ない mux は
    /// 誰にも配らないので害は無い。
    /// </summary>
    private void Unsubscribe(PreviewSubscription sub)
    {
        int remaining;
        lock (_subLock)
        {
            if (!_subs.Remove(sub))
                return;

            Interlocked.Decrement(ref s_totalSubscribers);
            remaining = _subs.Count;
            if (remaining == 0)
                _wantMux = false;
        }

        Components.ActivityLog.Info("preview.unsubscribe",
            $"recorder='{_host.Name}' subscribers={remaining}");
    }

    // ---- 停止 ----

    /// <summary>
    /// エンジンを止める。宿主は<b>録画の枝を静止させてから</b>同期で呼ぶ。
    ///
    /// <para>
    /// <b>取れなければ諦める。</b> 静止が予算内に終わらなかった場合だけ、録画スレッドが
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
                retired = Teardown("close");
            }
            else
            {
                Components.ActivityLog.Warn("preview.leak",
                    $"recorder='{_host.Name}' a preview sample callback was still running after "
                    + $"{CallbackExitTimeoutMs}ms; leaked the muxer instead of stopping it");
            }
        }
        finally
        {
            if (entered)
                Monitor.Exit(_muxLock);
        }

        // **同期で畳む。** 呼び出し元はこの直後に宿主のパイプラインを解放しうるので、
        // プールへ逃がすと解放済みのものを触りにいく。ここで待つのは mux スレッドの
        // 退去だけで、その相手が要求しうるロック（_subLock）はもう手放している。
        Retire(retired);

        lock (_subLock)
        {
            foreach (var sub in _subs)
                sub.Complete();
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
