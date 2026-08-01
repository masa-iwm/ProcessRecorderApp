using ProcessRecorderApp.Components;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessRecorderApp.Services;

/// <summary>
/// 保存先の古い mp4 を定期的に削除する常駐タスク。
///
/// <para>
/// <b>起動直後に1回掃いてから、以後は N 時間ごと。</b> 起動時の1回があることで
/// 「数時間動かし続けないと効かない」を避けられる。同時にこれが
/// <b>L2 で検証できる形</b>でもある ── ファイルの更新時刻を過去に倒して起動するだけで、
/// 時間の細工もテスト専用の環境変数も要らない。
/// </para>
/// <para>
/// 形は <c>EventRecorder</c> の自動復帰の連鎖（<c>Task.Run</c> ＋
/// <c>Task.Delay(..., token)</c> ＋ <see cref="CancellationTokenSource"/>）に揃えてある。
/// このコードベースには <c>PeriodicTimer</c> も <c>DispatcherTimer</c> も無いので、
/// ここだけ別方式を持ち込まない。
/// </para>
/// <para>
/// <b>設定は毎回読み直す。</b> 保存先や日数を変えたら次の周回から効く
/// （再起動を要求しない）。<b>録画エンジンのロックには一切触れない</b>
/// ── 触れているのはファイルシステムと <see cref="ActivityLog"/> だけ。
/// </para>
/// </summary>
// partial なのは CsWinRT1028 のため（IDisposable は WinRT ABI 越しに渡りうるインターフェイスで、
// トリミング/AOT のために型を partial にしておく必要がある）。
public sealed partial class RecordingCleanupScheduler : IDisposable
{
    /// <summary>間隔の下限（時間）。0 や負の設定でビジーループにしないため。</summary>
    public const int MinimumIntervalHours = 1;

    private readonly Func<(string Directory, int RetentionDays, int IntervalHours)> _readSettings;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public RecordingCleanupScheduler(
        Func<(string Directory, int RetentionDays, int IntervalHours)> readSettings)
        => _readSettings = readSettings;

    /// <summary>周回を開始する。冪等。</summary>
    public void Start()
    {
        if (_loop is not null)
            return;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TimeSpan interval;
            try
            {
                var (directory, retentionDays, intervalHours) = _readSettings();
                interval = TimeSpan.FromHours(Math.Max(MinimumIntervalHours, intervalHours));

                var result = RecordingCleanup.Sweep(directory, retentionDays, DateTime.Now);

                // **何もしなかった周回はログに出さない。** 既定（削除しない）のまま
                // 使っている利用者の activity.log を 6 時間ごとに埋めない。
                if (result.DidSomething)
                {
                    ActivityLog.Info("cleanup.run", result.ToLogLine(directory));

                    // 成功と失敗はイベント名で分ける（`recording.stop` と
                    // `recording.stop timeout` と同じ規則。前方一致の照合で混同しないため）。
                    foreach (string failure in result.Failures)
                        ActivityLog.Warn("cleanup.error", failure);
                }
            }
            catch (Exception ex)
            {
                // 掃除の失敗で常駐ワーカーを落とさない。次の周回で再挑戦する。
                ActivityLog.Warn("cleanup.error", ex.ToString());
                interval = TimeSpan.FromHours(MinimumIntervalHours);
            }

            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            // 待つのは「掃除の途中でプロセスが消える」のを避けるため。
            // 触っているのはファイルシステムだけなので、待てなくても壊れるものは無い。
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Task.Delay のキャンセルは通常ここへ来ない（RunAsync が飲んでいる）。
        }
        _cts.Dispose();
    }
}
