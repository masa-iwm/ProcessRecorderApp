using Gst;
using ProcessRecorderApp.Components;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using GstApp = Gst.App;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// <b>録画トランスコード 1 本ぶんのパイプライン。</b> 録画済みの <c>.mp4</c> を指定の位置から
/// 復号し直し、プリセットの解像度・fps で再エンコードして fMP4（<c>ftyp</c>＋<c>moov</c> →
/// <c>moof</c>＋<c>mdat</c>…）を <c>appsink</c> から流す。
///
/// <para>
/// <b>隔離は <see cref="DashPreviewStream"/> と同じ。</b> 自前の <see cref="Pipeline"/>・自前の
/// bus 購読を持ち、ここで起きた失敗は <see cref="Error"/> に畳んで返すだけで、
/// 録画にも DASH プレビューにも波及させない。
/// </para>
/// <para>
/// <b>引くのは呼び手のスレッドである</b>（<see cref="TryRead"/>）。<c>appsink</c> は
/// <c>max-buffers=16 drop=false</c> なので、引かれない間は appsink 自身が
/// ストリーミングスレッドを止める（＝バックプレッシャ）。コールバックは使わない。
/// </para>
/// <para>
/// <b>スレッドは 2 つ。</b> <see cref="Start"/> と <see cref="TryRead"/> を呼ぶ読み手のスレッドと、
/// bus の同期ハンドラ（フラグを立てるだけ）である。<see cref="Close"/> は読み手と別の
/// スレッドから来うる（切断・猶予失効・停止・<b>同じ <c>session</c> の次の要求</b>）。
/// </para>
/// <para>
/// <b>寿命の規律（<see cref="_readLock"/>）。</b> <see cref="Start"/> は組み立てから
/// 状態遷移までを、<see cref="Close"/> は <c>Dispose</c> を、どちらもこのロックの下で行う
/// ── <see cref="Close"/> は <see cref="Start"/> の途中に刺さりうるので、
/// 「組み立て中のパイプラインを解放しない」ことを場所で保証する。
/// <c>SetState(Null)</c> だけはロックの外で先に行う（待っている
/// <c>TryPullSample</c> と位置指定の再試行をそれで解く）。
/// <b>解放の権利は <see cref="ClaimPipeline"/> の交換 1 つで決まる</b>
/// ── <see cref="Close"/> と <see cref="Start"/> の後始末が同時に通っても、
/// 非 null を受け取った側だけが解放する。
/// </para>
/// </summary>
internal sealed class TranscodeSession
{
    /// <summary>
    /// fragment 1 つの長さ(ms)。<b><see cref="DashPreviewStream.FragmentDurationMs"/> とは
    /// 別の定数</b> ── あちらはライブ配信、こちらは録画済みファイルの変換で、
    /// 片方を動かしてももう片方に効かせてはいけない。
    /// </summary>
    public const int FragmentDurationMs = 1000;

    /// <summary>
    /// <c>qtdemux</c> が seek を受理するまで試し直す上限(ms)。
    /// <b><c>appsink async=false</c> では <c>PAUSED</c> が preroll を待たない</b>ので、
    /// 状態遷移が返った時点の <c>qtdemux</c> はまだ seek を受け付けない（実測 6〜14 回・
    /// 100〜230 ms で受理される）。超えたら <c>transcode.start-failed</c> で終わる。
    /// </summary>
    public const int SeekTimeoutMs = 5000;

    /// <summary>seek を試し直す間隔(ms)。</summary>
    public const int SeekRetryIntervalMs = 10;

    /// <summary>
    /// <see cref="Close"/> が「引いている最中の <see cref="TryRead"/> が抜ける」のを待つ上限(ms)。
    /// <see cref="DashPreviewStream.CallbackExitTimeoutMs"/> と同じ 5 秒。
    /// </summary>
    public const int ReaderExitTimeoutMs = 5000;

    /// <summary>
    /// <c>transcode.*</c> のログイベント名の<b>後半（<c>reason</c>）が取りうる値の全部</b>。
    ///
    /// <para>
    /// <b>固定集合であることが約束である</b>（<c>src/README.md</c> の停止理由の表と対で保つ）
    /// ── 例外の <c>Message</c> のような自由文はここへ流さず、<c>transcode.error</c> の
    /// <c>detail=</c> へ出す。
    /// </para>
    /// </summary>
    private static class StopReason
    {
        /// <summary>変換元の末尾まで流し切った。</summary>
        public const string Eos = "eos";

        /// <summary>読み手が閉じた（切断・画質の切り替え・ページ遷移）。</summary>
        public const string ClientClosed = "client-closed";

        /// <summary>同じ <c>session</c> の新しい要求（＝シーク）に置き換えられた。</summary>
        public const string Replaced = "replaced";

        /// <summary>読み手が閉じた後、猶予のあいだに戻って来なかったので枠を返した。</summary>
        public const string LeaseExpired = "lease-expired";

        /// <summary>走行中の破綻（バスの ERROR・例外。内訳は <c>detail=</c>）。</summary>
        public const string Error = "error";

        /// <summary>組み立てか位置指定に失敗した（内訳は <c>detail=</c>）。</summary>
        public const string StartFailed = "start-failed";

        /// <summary>アプリの終了。</summary>
        public const string Shutdown = "shutdown";
    }

    /// <summary>
    /// <see cref="StopReason"/> の全要素。<b><c>transcode.&lt;理由&gt;</c> のログイベント名が
    /// 取りうる値の正本</b>で、L1 の <c>TranscodeStopReasonTests</c> が
    /// <c>src/README.md</c> の停止理由の表と過不足なく突き合わせる。
    /// </summary>
    internal static readonly string[] StopReasons =
    [
        StopReason.Eos,
        StopReason.ClientClosed,
        StopReason.Replaced,
        StopReason.LeaseExpired,
        StopReason.Error,
        StopReason.StartFailed,
        StopReason.Shutdown,
    ];

    /// <summary>
    /// 変換のパイプライン文字列。<b>ここだけが形の正本</b>。
    ///
    /// <para>
    /// <c>qtdemux</c> に <c>demux</c> と名前を付けるのは、<b>位置指定をこの要素へ直接送る</b>ため
    /// ── パイプラインへ送ると seek が <c>appsink</c> から <c>mp4mux</c> へ遡り、
    /// ソースパッドのセグメントが <c>BYTES</c> なので <c>gst_segment_do_seek</c> の
    /// CRITICAL を出して 0 バイトのまま固まる（実測）。
    /// </para>
    /// <para>
    /// <b>出力の fps は上限で書く</b>（<c>framerate=[1/1,{fps}/1]</c>）。<c>videorate
    /// drop-only=true</c> は落とすことしかできないので、<c>framerate={fps}/1</c> と固定すると
    /// <b>実 fps がそれ未満のファイルを変換できない</b> ── 失敗は capsfilter ではなく
    /// <c>qtdemux</c> → <c>h264parse</c> の delayed link に出て、<c>qtdemux</c> が
    /// <c>Internal data stream error</c>（debug は <c>streaming stopped, reason not-linked
    /// (-1)</c>）を出したまま preroll しない。sidecar の無い本と、実 fps が分数のカメラ
    /// （実測 <c>89/3</c>＝約 29.67）がここに当たる。範囲にすると実 fps がそのまま通り、
    /// 上回るときだけ落ちる。<b>上げ方向の複製（<c>drop-only</c> を外す）は採らない。</b>
    /// </para>
    /// <para>
    /// <c>videorate drop-only=true</c>・capsfilter の書式・<c>h264parse config-interval=-1</c>・
    /// <c>mp4mux fragment-mode=dash-or-mss</c> は
    /// <see cref="DashPreviewStream.BuildPipeline"/> と同じ理由で同じもの
    /// （<b>fps を範囲で書くのはここだけ</b> ── ライブ配信の入力はソースの実 fps そのもので、
    /// 落とせない上限は起こりえない）。
    /// <c>appsink</c> は <c>max-buffers=16 drop=false</c> ── <b>捨てない</b>ので、
    /// 引き手が遅ければ appsink が上流を止める（配信物に穴を開けない）。
    /// </para>
    /// <para>
    /// <c>location</c> は二重引用符で囲む（録画パスに空白が入りうる）。区切りは <c>/</c> へ直す。
    /// </para>
    /// </summary>
    public static string BuildPipeline(
        string filePath, int width, int height, int fps, string decoderLaunch, string encoderLaunch)
        => string.Create(CultureInfo.InvariantCulture,
            $"filesrc location=\"{filePath.Replace('\\', '/')}\" ! qtdemux name=demux ! "
            + $"h264parse ! {decoderLaunch} ! videoconvert ! videoscale ! videorate drop-only=true ! "
            + $"video/x-raw,width={width},height={height},framerate=[1/1,{fps}/1],pixel-aspect-ratio=1/1 ! "
            + $"videoconvert ! {encoderLaunch} ! h264parse config-interval=-1 ! "
            + $"mp4mux name=mux fragment-duration={FragmentDurationMs} fragment-mode=dash-or-mss ! "
            + $"appsink name=sink sync=false async=false max-buffers=16 drop=false");

    private readonly TranscodeOpen _open;
    private readonly PreviewQuality _quality;
    private readonly string _decoderLaunch;
    private readonly string _encoderLaunch;
    /// <summary>
    /// 停止の記録（第 1 引数は <c>transcode.&lt;理由&gt;</c> のイベント名、第 2 引数は内訳）。
    /// <b>書き手は <see cref="TranscodeStreams"/> 側に置く</b> ── スクラッチの
    /// 検証ハーネスからは同じセッションを別の記録先で回せるようにするため。
    /// </summary>
    private readonly Action<string, string> _log;

    /// <summary>
    /// <b>寿命の排他。</b> 組み立て（<see cref="Start"/>）・読み出し（<see cref="TryRead"/>）と、
    /// 解放（<see cref="ShutDown"/> の <c>Dispose</c>）が重ならないことを保証する。
    /// </summary>
    private readonly object _readLock = new();

    private Pipeline? _pipeline;
    private GstApp.AppSink? _sink;
    private IDisposable? _subscription;

    /// <summary>受け取ったサンプルの数（<c>IsEos()</c> を信用してよいかの判定に使う）。</summary>
    private int _received;

    /// <summary>破綻の内容（バスの ERROR・例外。<b>立てるのは bus スレッドと読み手</b>）。</summary>
    private volatile string? _error;

    /// <summary>バスが EOS を報せた（<b>立てるのは bus スレッド</b>）。</summary>
    private volatile bool _eos;

    /// <summary>
    /// <see cref="Close"/> 済み（0/1）。<b>遷移は <see cref="Interlocked"/> で 1 回だけ</b>
    /// ── 「見てから立てる」にすると、同時に来た 2 本の <see cref="Close"/> が
    /// どちらも通って同じパイプラインを 2 度解放する。
    /// </summary>
    private int _closed;

    public TranscodeSession(
        TranscodeOpen open,
        PreviewQuality quality,
        string decoderLaunch,
        string encoderLaunch,
        AuxiliaryEncoderLease lease,
        Action<string, string> log)
    {
        _open = open;
        _quality = quality;
        _decoderLaunch = decoderLaunch;
        _encoderLaunch = encoderLaunch;
        Lease = lease;
        _log = log;
    }

    /// <summary>この要求。</summary>
    public TranscodeOpen Open => _open;

    /// <summary>
    /// このセッションが握っている枠。<b>畳むのは <see cref="TranscodeStreams"/> の責務</b>
    /// ── <see cref="Close"/> は枠に触らない（同じ <c>session</c> の次の要求へ引き継ぐため）。
    /// </summary>
    public AuxiliaryEncoderLease Lease { get; }

    /// <summary>破綻の内容（無事なら null）。</summary>
    public string? Error => _error;

    /// <summary>
    /// <see cref="Close"/> 済みか。<b><see cref="Start"/> の後に外から見る</b>
    /// ── 組み立ての最中に同じ <c>session</c> の次の要求へ置き換えられると、
    /// <see cref="Error"/> は null のまま畳まれたパイプラインが残る。
    /// </summary>
    internal bool Closed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// もう続きが来ないか。<b>2 つの判定を掛け合わせる。</b>
    ///
    /// <para>
    /// <c>IsEos()</c> は sink が <c>PAUSED</c> 未満でも true を返すので、
    /// 走り出す前は信用できない ── 「バスが EOS を報せた」か
    /// 「1 サンプル以上受け取った」のどちらかが立ってから見る。
    /// </para>
    /// <para>
    /// 逆に<b>バスの EOS だけでも終端にしない</b> ── EOS のメッセージは
    /// <c>appsink</c> の待ち行列に最大 16 個残ったまま出るので、それだけで畳むと
    /// 末尾を捨てる。<c>IsEos()</c> は待ち行列が空になるまで false を返す
    /// （こちらが引き切ったことの判定である）。
    /// </para>
    /// </summary>
    public bool Ended
    {
        get
        {
            if (_error is not null || Closed)
                return true;

            return (_eos || 0 < Volatile.Read(ref _received)) && SinkIsEos();
        }
    }

    /// <summary>
    /// パイプラインを組んで、指定の位置から流し始める。<b>例外は投げない</b>
    /// ── 失敗は <see cref="Error"/> に文言を置き、<c>transcode.start-failed</c> を記録する。
    ///
    /// <para>
    /// 手順は <c>PAUSED</c> →（<c>qtdemux</c> が受理するまで seek を試し直す）→ <c>PLAYING</c>。
    /// <b>seek は最初のバッファが <c>mp4mux</c> へ届く前でなければならない</b>
    /// ── 一度データが通った後の flush では <c>moov</c> が出し直されず、
    /// 最初の <c>moof</c> に seek 前のサンプルが残る（実測）。<c>start=0</c> でも同じ手順を通る。
    /// </para>
    /// <para>
    /// <b>全体を <see cref="_readLock"/> の下で行う</b>（寿命の規律）。同じ <c>session</c> の
    /// 次の要求は、これが走っている最中に <see cref="Close"/> を掛けてくる
    /// ── 組み立ての途中で解放されないことをロックで保証し、
    /// <b>閉じられていたら自分で畳んでから戻る</b>（そうしないと、
    /// <see cref="_pipeline"/> がまだ null の窓を通った <see cref="Close"/> の後で
    /// 誰も畳まないパイプラインが <c>PLAYING</c> のまま残る）。
    /// </para>
    /// </summary>
    public void Start()
    {
        lock (_readLock)
        {
            if (Closed)
                return;

            try
            {
                string desc = BuildPipeline(
                    _open.FilePath, _quality.Width, _quality.Height, _quality.Fps,
                    _decoderLaunch, _encoderLaunch);

                // **ParseLaunch も try の内側で**（要素やプロパティが版によって無ければ
                // Gst.GLib.GException を投げる）。
                var pipeline = (Pipeline)Gst.Global.ParseLaunch(desc);
                _pipeline = pipeline;
                pipeline.SetName($"transcode-{_open.SessionId}");

                _sink = (GstApp.AppSink?)pipeline.GetByName("sink")
                    ?? throw new InvalidOperationException("the transcode pipeline has no appsink");
                var demux = pipeline.GetByName("demux")
                    ?? throw new InvalidOperationException("the transcode pipeline has no qtdemux");

                // 購読は状態遷移より前に済ませる（NULL のバスには post が届かない）。
                if (pipeline.GetBus() is { } bus)
                    _subscription = bus.SubscribeSyncDrop((_, m) => OnBusMessage(m));

                if (pipeline.SetState(State.Paused) == StateChangeReturn.Failure)
                    throw new InvalidOperationException("the transcode pipeline did not want to pause");

                // 位置指定の途中で閉じられたら PLAYING へは進めない。
                if (SeekToStart(demux)
                    && pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
                {
                    throw new InvalidOperationException("the transcode pipeline did not want to play");
                }
            }
            catch (Exception ex)
            {
                _error = ex.Message;

                // 単票にするため、失敗の記録はこの 1 行だけにする（file= も detail= へ入れる）。
                // ここで return しない ── 組み立ての途中で別スレッドの Close が先に通っていると
                // （その時点の _pipeline は null）この Close は no-op になり、直前に格納した
                // パイプラインを畳む者が誰も居なくなる。末尾の後始末に必ず落とす。
                Close(StopReason.StartFailed, $"file='{_open.FilePath}' {ex.Message}");
            }

            // **柵を張ってから閉じを読む。** 素の読みだと、_pipeline の格納と
            // この読みが入れ替わって「Close は null を見た・こちらは未閉を見た」の
            // 両取りこぼしが起きうる（交換は全体の柵になる）。
            if (Interlocked.CompareExchange(ref _closed, 0, 0) != 0)
                ShutDown(ClaimPipeline());
        }
    }

    /// <summary>
    /// <c>qtdemux</c> へ位置指定を送る。<b>受理されるまで試し直す</b>
    /// （<see cref="SeekTimeoutMs"/> を超えたら失敗）。
    /// <b>毎回 <see cref="Closed"/> を見て抜ける</b> ── <see cref="Close"/> は
    /// <c>SetState(Null)</c> をこのロックの外で行うので、見ないと畳まれた後の要素へ
    /// 5 秒ぶん <c>Seek</c> を送り続けることになる。
    ///
    /// <para>
    /// <b><see cref="_error"/> も毎回見る。</b> preroll しないまま破綻したパイプライン
    /// （リンクに失敗した <c>qtdemux</c> など）は seek を永久に受理しないので、
    /// 見ないと <see cref="SeekTimeoutMs"/> を回り切るまで待ったうえで、
    /// バスが既に報せている真因の代わりに「位置指定が受理されなかった」だけが残る。
    /// 抜け方は打ち切りと同じ例外にする ── <see cref="Start"/> の <c>catch</c> が
    /// <c>start-failed</c> の記録とパイプラインの後始末をまとめて行う唯一の場所である。
    /// </para>
    /// </summary>
    /// <returns>受理されたら true、閉じられて降りたら false。</returns>
    private bool SeekToStart(Element demux)
    {
        long startNs = (long)(_open.StartSeconds * 1_000_000_000.0);
        const SeekFlags Flags = SeekFlags.Flush | SeekFlags.KeyUnit | SeekFlags.SnapBefore;

        var spin = Stopwatch.StartNew();
        while (true)
        {
            if (Closed)
                return false;

            if (_error is { } failure)
            {
                throw new InvalidOperationException(
                    "the transcode pipeline failed before it accepted the start position: " + failure);
            }

            if (demux.Seek(1.0, Format.Time, Flags, SeekType.Set, startNs, SeekType.None, -1))
                return true;

            if (SeekTimeoutMs <= spin.ElapsedMilliseconds)
            {
                throw new InvalidOperationException(
                    "the transcode pipeline did not accept the start position within "
                    + $"{SeekTimeoutMs}ms");
            }

            Thread.Sleep(SeekRetryIntervalMs);
        }
    }

    /// <summary>
    /// チャンクを 1 つ引く。<b>呼び手のスレッドが最大 <paramref name="timeoutMs"/> だけ待つ。</b>
    /// 取れなかったときは <see cref="Ended"/> で「終わったのか、まだ来ていないだけか」を分ける。
    /// </summary>
    public bool TryRead(int timeoutMs, out byte[]? chunk)
    {
        chunk = null;

        // **閉じた後は引かない。** ネイティブのパイプラインは既に解放されうる。
        if (Closed)
            return false;

        lock (_readLock)
        {
            if (Closed || _sink is not { } sink)
                return false;

            try
            {
                using var sample = sink.TryPullSample(ClockTime.FromMilliseconds(timeoutMs));
                if (sample is null)
                    return false;

                using var buffer = sample.GetBuffer();
                if (buffer is null)
                    return false;

                using var map = buffer.Map(MapFlags.Read);
                chunk = map.Span.ToArray();
            }
            catch (Exception ex)
            {
                // **1 回の失敗を例外で外へ出さない**（HTTP 層はこれを Ended として畳む）。
                _error ??= ex.Message;
                return false;
            }
        }

        Interlocked.Increment(ref _received);
        return true;
    }

    /// <summary>
    /// パイプラインを畳む。<b>冪等</b>で、<b>枠には触らない</b>
    /// （同じ <c>session</c> の次の要求へ引き継ぐのは <see cref="TranscodeStreams"/> の仕事）。
    /// <b>例外は投げない</b> ── 呼び手（<c>CloseAll</c>）は他のセッションの後始末を続ける。
    ///
    /// <para>
    /// 冪等の実体は <see cref="_closed"/> の <see cref="Interlocked"/> 遷移で、
    /// <b>通れるのは 1 本だけ</b>である。実際に畳むのは <see cref="ShutDown"/>。
    /// </para>
    /// </summary>
    internal void Close(string reason, string? detail = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        ShutDown(ClaimPipeline());

        _log("transcode." + reason,
            detail is null
                ? $"session='{_open.SessionId}'"
                : $"session='{_open.SessionId}' detail={detail}");
    }

    /// <summary>
    /// 解放するパイプラインを取り出す。<b>非 null を受け取れるのは 1 度だけ</b>で、
    /// それが解放の権利である（<see cref="Close"/> と <see cref="Start"/> の後始末が
    /// 同時に通っても二重解放にならない）。
    /// </summary>
    private Pipeline? ClaimPipeline() => Interlocked.Exchange(ref _pipeline, null);

    /// <summary>
    /// 畳んで解放する。<b>受け取ったパイプラインの解放権はこの呼び出しにある。</b>
    ///
    /// <para>
    /// <c>SetState(Null)</c> は引いている最中でも安全で、待っている
    /// <c>TryPullSample</c> と位置指定の再試行をその場で解く。だから<b>ロックの外で先に</b>
    /// 落とす ── そうしないと下のロックが取れない。<b>解放（<c>Dispose</c>）だけは</b>
    /// 引き手・組み手が抜けてからでなければネイティブを壊すので、
    /// <see cref="_readLock"/> を <see cref="ReaderExitTimeoutMs"/> で待ち、
    /// 取れなければ <c>transcode.leak</c> を残して意図的にリークする
    /// （<c>dash.leak</c> と同じ「リークするが壊さない」規律）。
    /// </para>
    /// <para>
    /// <b>ロックの下でもう一度 <c>Null</c> へ落とす。</b> 外で落とした後に
    /// <see cref="Start"/> が <c>PAUSED</c>／<c>PLAYING</c> まで進めていることがあり、
    /// 解放は <c>NULL</c> でなければならない。<b>購読を外すのもロックの下</b>
    /// ── 外で外すと、<see cref="Start"/> がまだ購読していない窓を通ったときに
    /// 誰も持たない購読が残る。
    /// </para>
    /// </summary>
    private void ShutDown(Pipeline? pipeline)
    {
        if (pipeline is null)
            return;

        try
        {
            pipeline.SetState(State.Null);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("transcode.error",
                $"session='{_open.SessionId}' the transcode pipeline did not shut down cleanly: {ex.Message}");
        }

        bool entered = false;
        try
        {
            Monitor.TryEnter(_readLock, ReaderExitTimeoutMs, ref entered);
            if (!entered)
            {
                ActivityLog.Warn("transcode.leak",
                    $"session='{_open.SessionId}' a transcode read was still running after "
                    + $"{ReaderExitTimeoutMs}ms; leaked the pipeline instead of freeing it");
                return;
            }

            _sink = null;
            _subscription?.Dispose();
            _subscription = null;

            pipeline.SetState(State.Null);
            pipeline.Dispose();
        }
        catch (Exception ex)
        {
            ActivityLog.Error("transcode.error",
                $"session='{_open.SessionId}' the transcode pipeline did not shut down cleanly: {ex.Message}");
        }
        finally
        {
            if (entered)
                Monitor.Exit(_readLock);
        }
    }

    /// <summary>
    /// 読み手が閉じたときの理由。<b>破綻していたら <c>error</c> が優先である</b>
    /// ── バスの ERROR で畳んだものを <c>client-closed</c> として記録すると、
    /// 運用側からは正常な切断と区別が付かない（<see cref="Ended"/> は
    /// 破綻でも true になるので、先に <see cref="Error"/> を見る）。
    /// </summary>
    internal void CloseAsReader()
    {
        if (_error is { } error)
            Close(StopReason.Error, error);
        else
            Close(Ended ? StopReason.Eos : StopReason.ClientClosed);
    }

    /// <summary>置き換え（同じ <c>session</c> のシーク）。</summary>
    internal void CloseAsReplaced() => Close(StopReason.Replaced);

    /// <summary>アプリの終了。</summary>
    internal void CloseAsShutdown() => Close(StopReason.Shutdown);

    /// <summary>貸出の猶予が切れた（セッションは既に閉じている ── 記録のためだけに通る）。</summary>
    internal static string LeaseExpiredReason => StopReason.LeaseExpired;

    /// <summary>
    /// バスの同期ハンドラ。<b>拾うのは ERROR と EOS だけ</b>で、
    /// <b>フラグを立てるだけで畳まない</b>（畳むのは読み手のスレッド）。
    /// メッセージのラッパーはハンドラの実行中だけ有効なので Dispose しない。
    /// </summary>
    private void OnBusMessage(Message message)
    {
        try
        {
            if (Closed)
                return;

            if (message.Type == MessageType.Eos)
            {
                // **1 サンプルも来ないまま終わる要求がある**（start が末尾以降）。
                // これを立てておかないと、その要求は誰も終端と判定できない。
                _eos = true;
                return;
            }

            if (message.Type != MessageType.Error)
                return;

            // **debug を捨てない。** 真因はそこにしか出ない ── qtdemux の
            // `Internal data stream error.` は message だけ読むと原因が分からず、
            // debug の `streaming stopped, reason not-linked (-1)` で初めてリンクの
            // 失敗と分かる。
            var (gerror, debug) = message.ParseError();

            // 発信元は要素名で出す。**`encoder=` は出さない** ── 失敗の発信元が
            // エンコーダーとは限らず（実測では qtdemux）、読み手をエンコーダーへ誘導する。
            // 発信元のラッパーはインターンされた GObject なので Dispose しない。
            string source = message.Src?.Name ?? "?";
            string detail = string.IsNullOrEmpty(debug) ? gerror.Message : $"{gerror.Message}; {debug}";

            // 組み立て中（位置指定の再試行）はこれを見て降りる（SeekToStart）ので、
            // detail を丸ごと入れる ── start-failed の detail= がそのまま真因になる。
            _error ??= detail;
            ActivityLog.Error("transcode.error",
                $"session='{_open.SessionId}' src='{source}' detail='{detail}'");
        }
        catch (Exception ex)
        {
            DebugLogEx.Log(DebugLevel.Error, $"the transcode bus handler failed!\n{ex}");
        }
    }

    /// <summary>
    /// <c>appsink</c> が EOS に達しているか。<b>問い合わせは <see cref="_readLock"/> の下で行う</b>
    /// ── <see cref="ShutDown"/> はこのロックを取ってからネイティブのパイプラインを解放するので、
    /// 外で呼ぶと解放済みのオブジェクトへネイティブ呼び出しを出す窓が残る
    /// （管理された例外にならないので <c>try</c> では止められない）。
    /// <b>取れなければ「まだ終わっていない」を返す</b> ── 引いている最中なので終端ではない。
    /// </summary>
    private bool SinkIsEos()
    {
        bool entered = false;
        try
        {
            Monitor.TryEnter(_readLock, ref entered);
            if (!entered || Closed || _sink is not { } sink)
                return false;

            return sink.IsEos();
        }
        catch
        {
            return true;
        }
        finally
        {
            if (entered)
                Monitor.Exit(_readLock);
        }
    }
}
