using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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
internal static class EventsEndpoint
{
    /// <summary>
    /// 無通信のまま切られないようにする心拍の間隔。
    /// 途中の proxy は無通信の接続を落とすが、SSE には <c>event: ping</c> 以外に
    /// 「生きている」を伝える手段が無い。
    /// </summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    /// <summary>購読者ごとに保持する状態の数。</summary>
    public const int ChannelCapacity = 8;

    public static void Map(WebApplication app, IRemoteControlBackend backend)
    {
        app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            var channel = Channel.CreateBounded<RecordersSnapshot>(
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

            using IDisposable subscription = backend.SubscribeState(s => channel.Writer.TryWrite(s));

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
                await WriteStateAsync(ctx, initial);

                while (!ct.IsCancellationRequested)
                {
                    RecordersSnapshot? next = null;
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

                    if (next is null)
                        await WriteEventAsync(ctx, "ping", "{}");
                    else
                        await WriteStateAsync(ctx, next);
                }
            }
            catch (OperationCanceledException)
            {
                // クライアントが閉じた。購読は using が解く。
            }
        });
    }

    private static Task WriteStateAsync(HttpContext ctx, RecordersSnapshot snapshot)
        => WriteEventAsync(ctx, "state",
            JsonSerializer.Serialize(snapshot, RemoteApiJsonContext.Default.RecordersSnapshot));

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
