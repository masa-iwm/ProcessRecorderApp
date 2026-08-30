using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// <b>録画トランスコードの供給元。</b> <see cref="Controller"/> が 1 つ所有し、
/// <c>GET /api/recording-transcode/…</c> が要求ごとに引く。
///
/// <para>
/// <b>セッションはクライアントが名乗る <c>session</c> で識別する。</b> 同じ id の要求が来たら
/// 前のパイプラインを <c>transcode.replaced</c> で畳んで<b>枠を引き継ぐ</b>
/// ── シークのたびに枠を 1 つ余分に要ると、枠 1 つの機械では自分のシークで自分が busy になる。
/// </para>
/// <para>
/// <b>読み手が閉じたら枠は <see cref="TranscodeLimits.GraceMs"/> だけ id 付きで保持する。</b>
/// 「無通信で破棄」にはしない ── <c>appsink sync=false</c> ＋ クライアントの先読み抑制では
/// <b>転送の無い時間が正常状態</b>なので、無通信で破棄すると再生が死ぬ。猶予内の同じ id の要求は
/// 保持中の枠をそのまま取り、切れたら <c>transcode.lease-expired</c> で返す。
/// </para>
/// <para>
/// <b>ロックは <see cref="_gate"/> 1 つだけで、その下でパイプラインを触らない</b>
/// ── <c>SetState</c> と <see cref="TranscodeReader.TryRead"/> はロックの外である。
/// </para>
/// </summary>
internal sealed partial class TranscodeStreams : ITranscodeSource, IDisposable
{
    /// <summary>猶予の失効を見に行く間隔(ms)。</summary>
    public const int SweepIntervalMs = 1000;

    /// <summary>保持している枠 1 つ（<see cref="ExpiresAtTicks"/> は <c>TickCount64</c>）。</summary>
    private readonly record struct PendingLease(AuxiliaryEncoderLease Lease, long ExpiresAtTicks);

    private readonly object _gate = new();
    private readonly Dictionary<string, TranscodeSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingLease> _pending = new(StringComparer.Ordinal);
    private readonly Func<TranscodeCapability> _capability;
    private readonly AuxiliaryEncoderSlots _slots;
    private readonly Timer _sweep;

    private bool _shutdown;

    /// <param name="capability">
    /// 能力の読み取り。<b>要求ごとに読む</b> ── 生成の順序（<c>Controller.StaticInitialize</c> の
    /// プローブが済んでいるか）に依存させない。
    /// </param>
    /// <param name="slots">補助エンコーダー枠。</param>
    public TranscodeStreams(Func<TranscodeCapability> capability, AuxiliaryEncoderSlots slots)
    {
        _capability = capability;
        _slots = slots;
        _sweep = new Timer(_ => Sweep(), null, SweepIntervalMs, SweepIntervalMs);
    }

    /// <inheritdoc/>
    public TranscodeCapability Capability => _capability();

    /// <inheritdoc/>
    public bool TryOpen(
        TranscodeOpen open,
        [NotNullWhen(true)] out TranscodeReader? reader,
        [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(open);
        reader = null;

        var capability = Capability;
        if (!capability.Transcode || capability.Decoder is not { } decoder)
        {
            reason = TranscodeReasons.Unavailable;
            return false;
        }

        // **検査済みの前提**（HTTP 層が IsValidId かつ custom でないことを見ている）。
        if (!PreviewQualityPresets.TryFind(open.QualityId, out var preset))
            throw new ArgumentException($"'{open.QualityId}' is not a transcode quality id", nameof(open));

        var quality = PreviewQualityPresets.Resolve(preset, open.Source);

        if (ResolveEncoder(quality) is not { } encoder)
        {
            ActivityLog.Error("transcode.error",
                $"session='{open.SessionId}' {DashPreviewReasons.EncoderUnavailable}");
            reason = TranscodeReasons.StartFailed;
            return false;
        }

        TranscodeSession? replaced = null;
        TranscodeSession session;
        lock (_gate)
        {
            if (_shutdown)
            {
                reason = TranscodeReasons.Unavailable;
                return false;
            }

            AuxiliaryEncoderLease lease;
            if (_sessions.Remove(open.SessionId, out var running))
            {
                // 同じ session の作り直し（＝シーク）。枠はそのまま引き継ぐ。
                replaced = running;
                lease = running.Lease;
            }
            else if (_pending.Remove(open.SessionId, out var held))
            {
                lease = held.Lease;
            }
            else if (!_slots.TryAcquire("transcode:" + open.SessionId, out var acquired))
            {
                reason = TranscodeReasons.Busy;
                return false;
            }
            else
            {
                lease = acquired;
            }

            session = new TranscodeSession(open, quality, decoder, encoder, lease, Log);
            _sessions[open.SessionId] = session;
        }

        // **ロックの外で畳み、ロックの外で組む。** どちらも状態遷移を待つ。
        if (replaced is not null)
            CloseQuietly(replaced, replaced.CloseAsReplaced);

        session.Start();

        // **閉じも見る。** 同じ session の次の要求が、この Start の最中に
        // CloseAsReplaced を掛けてくることがある ── そのとき Start は SeekToStart が
        // Closed を見て降りるので、Error は null のまま「畳んだ session の reader」を
        // 返してしまう（そして transcode.start を記録する）。**畳まれていたら断る。**
        // 記録は足さない ── 置き換えた側が transcode.replaced を出している。
        if (session.Error is not null || session.Closed)
        {
            // **失敗しても枠は猶予へ戻す。** すぐ返すと、同じ相手の再試行が他人に枠を取られる。
            // 置き換えられていた場合は Forget が何もしない（枠は置き換えた側のもの）。
            // **記録はここでは足さない** ── 内訳（file= と例外の文言）は
            // TranscodeSession.Start が transcode.start-failed の detail= へ 1 行で出している。
            Forget(session);
            reason = TranscodeReasons.StartFailed;
            return false;
        }

        ActivityLog.Info("transcode.start",
            string.Create(CultureInfo.InvariantCulture,
                $"session='{open.SessionId}' file='{open.FilePath}' start={open.StartSeconds:0.###} "
                + $"quality={open.QualityId} size={quality.Width}x{quality.Height} fps={quality.Fps} "
                + $"decoder='{decoder}' encoder='{encoder}'"));

        reader = new SessionReader(this, session);
        reason = null;
        return true;
    }

    /// <summary>
    /// 使うエンコーダーの launch 文字列。<b>候補列の先頭 1 つだけ</b>で、巡回はしない
    /// ── 要求 1 本ごとに候補列を舐めると、失敗のたびにその回数ぶん
    /// <c>parse_launch</c> と状態遷移を回すことになる（<c>DashPreviewStream</c> と同じ判断だが、
    /// あちらは次のサンプルで次の候補へ進めるのに対し、こちらは 1 要求で終わる）。
    /// </summary>
    private static string? ResolveEncoder(PreviewQuality quality)
    {
        var candidates = EncoderCatalog.Resolve(
            EventRecordingType.System, EventRecorder.PreferredH264Encoder,
            EncoderCatalog.ProbeWithGStreamer, gop: quality.Fps);

        if (candidates.Count == 0)
            return null;

        try
        {
            return candidates[0].WithBitrateKbps(quality.BitrateKbps).LaunchString;
        }
        catch (InvalidOperationException)
        {
            // ビットレートを当てられない定義（単位が確認できていない）は使わない。
            return null;
        }
    }

    /// <summary>
    /// セッションを一覧から外して畳み、枠を猶予へ移す。
    /// <b>既に別の要求へ置き換えられていれば何もしない</b>（枠はそちらのもの）。
    /// </summary>
    private void Forget(TranscodeSession session)
    {
        string id = session.Open.SessionId;
        bool owned;
        bool grace;

        lock (_gate)
        {
            owned = _sessions.TryGetValue(id, out var current) && ReferenceEquals(current, session);
            if (owned)
            {
                _sessions.Remove(id);

                // **id の被覆を切らさない。** ここで空白を作ると、畳んでいる最中に届いた
                // 同じ id の要求が枠をもう 1 つ取る。
                grace = !_shutdown;
                if (grace)
                {
                    _pending[id] = new PendingLease(
                        session.Lease, Environment.TickCount64 + TranscodeLimits.GraceMs);
                }
            }
            else
            {
                grace = false;
            }
        }

        if (!owned)
            return;

        CloseQuietly(session, session.CloseAsReader);

        if (!grace)
            session.Lease.Dispose();
    }

    /// <summary>
    /// セッションを畳む。<b>例外を外へ出さない</b> ── ここで抜けると
    /// その先の枠の返却（<see cref="Forget"/>）や残りのセッション（<see cref="CloseAll"/>）が
    /// 丸ごと落ち、席が空かないまま <see cref="Controller"/> の破棄へ上がる。
    /// </summary>
    private static void CloseQuietly(TranscodeSession session, Action close)
    {
        try
        {
            close();
        }
        catch (Exception ex)
        {
            ActivityLog.Error("transcode.error",
                $"session='{session.Open.SessionId}' the transcode session did not close cleanly: {ex.Message}");
        }
    }

    /// <summary>猶予の切れた枠を返す。<b>1 秒ごと</b>。</summary>
    private void Sweep()
    {
        List<KeyValuePair<string, PendingLease>>? expired = null;

        lock (_gate)
        {
            long now = Environment.TickCount64;
            foreach (var pair in _pending)
            {
                if (pair.Value.ExpiresAtTicks <= now)
                    (expired ??= []).Add(pair);
            }

            if (expired is null)
                return;

            foreach (var pair in expired)
                _pending.Remove(pair.Key);
        }

        foreach (var pair in expired)
        {
            pair.Value.Lease.Dispose();
            ActivityLog.Info("transcode." + TranscodeSession.LeaseExpiredReason, $"session='{pair.Key}'");
        }
    }

    /// <summary>
    /// 全部畳む（<see cref="Controller"/> の破棄）。<b>猶予の途中の枠も返す</b>
    /// ── 終了後に席が残らない。
    /// </summary>
    public void CloseAll()
    {
        TranscodeSession[] sessions;
        List<AuxiliaryEncoderLease> leases = [];
        lock (_gate)
        {
            _shutdown = true;
            sessions = [.. _sessions.Values];
            foreach (var pending in _pending.Values)
                leases.Add(pending.Lease);
            _sessions.Clear();
            _pending.Clear();
        }

        foreach (var session in sessions)
        {
            CloseQuietly(session, session.CloseAsShutdown);
            session.Lease.Dispose();
        }

        foreach (var lease in leases)
            lease.Dispose();
    }

    public void Dispose()
    {
        _sweep.Dispose();
        CloseAll();
    }

    /// <summary>停止 1 件の記録（イベント名は <c>transcode.&lt;理由&gt;</c>）。</summary>
    private static void Log(string eventName, string detail) => ActivityLog.Info(eventName, detail);

    /// <summary>
    /// HTTP 層へ渡す読み出し口。<b><see cref="Dispose"/> が「読み手が閉じた」</b>で、
    /// そこでパイプラインを畳んで枠を猶予へ移す。
    /// </summary>
    private sealed partial class SessionReader(TranscodeStreams owner, TranscodeSession session) : TranscodeReader
    {
        private int _disposed;

        public override bool TryRead(int timeoutMs, [NotNullWhen(true)] out byte[]? chunk)
        {
            bool read = session.TryRead(timeoutMs, out chunk);
            return read && chunk is not null;
        }

        public override bool Ended => session.Ended;

        public override string? Error => session.Error;

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            owner.Forget(session);
        }
    }
}
