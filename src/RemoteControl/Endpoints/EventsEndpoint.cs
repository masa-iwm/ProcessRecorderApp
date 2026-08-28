using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProcessRecorderApp.Components;

namespace ProcessRecorderApp.RemoteControl.Endpoints;

/// <summary>
/// 状態変化の push（Server-Sent Events）。
///
/// <para>
/// <b>購読者ごとに容量 8 の bounded channel を持ち、溢れたら古い方から捨てる。</b>
/// 運ぶのは「今の状態の全体」なので、遅い購読者に配りきれなかった途中経過には
/// 意味が無い ── 最新が届けば追いつく。ここを unbounded にすると、
/// 応答しないクライアント 1 つでメモリが伸び続ける。
/// </para>
/// <para>
/// <b>購読のコールバックは <see cref="System.Threading.Channels.ChannelWriter{T}.TryWrite"/> だけを行う。</b>
/// コールバックは状態を持っている側のスレッドで呼ばれるので、
/// ここで HTTP を書くと配信の遅さがそのままアプリの応答性になる。
/// </para>
/// </summary>
internal static partial class EventsEndpoint
{
    /// <summary>
    /// 無通信のまま切られないようにする心拍の間隔。
    /// 途中の proxy は無通信の接続を落とすが、SSE には <c>event: ping</c> 以外に
    /// 「生きている」を伝える手段が無い。
    /// </summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    /// <summary>購読者ごとに保持する状態の数。</summary>
    public const int ChannelCapacity = 8;

    /// <summary>
    /// 配るのを待っている 1 イベント。<b>直列化はここへ入れる時点で済ませる</b>
    /// ── 変換をこのスレッド（状態を持っている側／索引の作り直しスレッド）で
    /// 終えておかないと、遅い購読者が居るだけで元の持ち主が待たされる。
    /// </summary>
    private readonly record struct SseEvent(string Name, string Data);

    public static void Map(
        WebApplication app, IRemoteControlBackend backend, RemoteAuth auth, RecordingIndex recordings)
    {
        app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            if (!await AuthGate.AllowAsync(ctx, auth, RemoteRole.Viewer, write: false))
                return;

            var channel = Channel.CreateBounded<SseEvent>(
                new BoundedChannelOptions(ChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    AllowSynchronousContinuations = false,
                });

            // **最初の 1 件を取ってから購読する。** 取得の側は「エンジンが使えるようになるまで」
            // 待つ規則を持っており、購読の側は待たない ── 逆順にすると、起動直後の接続が
            // 何も監視していない購読を掴んだまま心拍だけを送り続ける。
            // 取得と購読の間に起きた変化は落ちるが、その窓は要求 1 つぶんの往復より短い。
            var initial = await backend.GetRecordersAsync(ctx.RequestAborted);

            // **ここでも配信 root を解決して索引へ渡す。** 一覧を一度も読んでいない
            // クライアント（SSE だけ張るもの）が居ると、索引は空の root を見たままで
            // <c>recording</c> が永久に出ない。
            string root = await backend.GetRecordingsRootAsync(ctx.RequestAborted);
            await Task.Run(() => recordings.Rebind(root), ctx.RequestAborted);

            using IDisposable subscription = backend.SubscribeState(
                s => channel.Writer.TryWrite(new SseEvent("state", SerializeState(s))));

            using IDisposable recordingSubscription = SubscribeRecordings(
                recordings, c => channel.Writer.TryWrite(new SseEvent("recording", SerializeChange(c))));

            // 応答ヘッダーはここで初めて確定する。ここより前で失敗すれば、
            // 例外の受け口が 500 / 503 を返せる（HasStarted が false のうち）。
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            // 途中の proxy が SSE を溜め込まないようにする（この経路は必ず即時に届ける）。
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var ct = ctx.RequestAborted;
            try
            {
                await WriteEventAsync(ctx, "state", SerializeState(initial));

                while (!ct.IsCancellationRequested)
                {
                    SseEvent? next = null;
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        wait.CancelAfter(PingInterval);
                        try
                        {
                            next = await channel.Reader.ReadAsync(wait.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // 心拍の時刻か、クライアントの切断。下で区別する。
                        }
                    }

                    if (ct.IsCancellationRequested)
                        break;

                    if (next is SseEvent value)
                        await WriteEventAsync(ctx, value.Name, value.Data);
                    else
                        await WriteEventAsync(ctx, "ping", "{}");
                }
            }
            catch (OperationCanceledException)
            {
                // クライアントが閉じた。購読は using が解く。
            }
        });
    }

    /// <summary>
    /// 索引の差分の購読。<see cref="RecordingIndex.Changed"/> は event なので、
    /// 他の購読と同じ <c>using</c> の形にするためにここで包む。
    /// </summary>
    private static IDisposable SubscribeRecordings(
        RecordingIndex recordings, Action<RecordingIndexChange> handler)
    {
        recordings.Changed += handler;
        return new Unsubscriber(() => recordings.Changed -= handler);
    }

    /// <summary>
    /// <b><c>partial</c> は外せない。</b> <see cref="IDisposable"/> は WinRT の
    /// <c>IClosable</c> に写るので、外すと <c>CsWinRT1028</c>（trimming / AOT）で落ちる。
    /// </summary>
    private sealed partial class Unsubscriber(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private static string SerializeState(RecordersSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, RemoteApiJsonContext.Default.RecordersSnapshot);

    private static string SerializeChange(RecordingIndexChange change)
        => JsonSerializer.Serialize(
            new RecordingChangeDto(KindName(change.Kind), RemoteApiRules.ToUrlPath(change.RelativePath)),
            RemoteApiJsonContext.Default.RecordingChangeDto);

    /// <summary>
    /// 種別の綴り。<b>列挙の名前をそのまま小文字にしない</b>
    /// ── 型の名前を変えただけで配線上の値が変わるのは、API の約束の壊し方として一番静かである。
    ///
    /// <para>
    /// <b>既定の腕は投げる。</b> 名前の無い値（<c>(RecordingIndexChangeKind)4</c> の
    /// ようなキャスト）まで網羅しろという <c>CS8524</c> は、既定の腕を置く以外に
    /// 消す手が無い ── ただし既定を <c>"updated"</c> のような綴りにすると、
    /// 新しい種別が黙って別物と名乗って配られる。
    /// </para>
    /// <para>
    /// <b>種別が増えたことに気付く仕掛けはコンパイラではなく L1 にある</b>
    /// （<c>RecordingChangeKindDriftTests</c>：列挙の全値がここの腕に在ることを
    /// ソーステキストで照合する）。値の出どころは <c>RecordingIndex</c> 1 つだけである。
    /// </para>
    /// </summary>
    private static string KindName(RecordingIndexChangeKind kind) => kind switch
    {
        RecordingIndexChangeKind.Added => "added",
        RecordingIndexChangeKind.Completed => "completed",
        RecordingIndexChangeKind.Removed => "removed",
        RecordingIndexChangeKind.Updated => "updated",
        _ => throw new InvalidOperationException($"unknown recording index change kind: {(int)kind}"),
    };

    /// <summary>
    /// SSE の 1 イベント。<b>書いたら必ず flush する</b> ── 溜められると
    /// 「変化が push される」という約束そのものが成立しない。
    /// </summary>
    private static async Task WriteEventAsync(HttpContext ctx, string name, string data)
    {
        await ctx.Response.WriteAsync($"event: {name}\ndata: {data}\n\n", Encoding.UTF8, ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }
}
