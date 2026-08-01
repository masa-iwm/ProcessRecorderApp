using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 録画の開始／終了の実行可否（<see cref="RecordingCommandState"/>）。
///
/// 停止は「受付（同期）」と「排出（プール）」に分かれているため、
/// <c>IsRecording=false</c> かつ <c>IsStopping=true</c> という**排出中**の状態が存在する。
/// ここで守るのはその状態での実行可否で、特に
/// 「排出中は開始できない」が外れると UI スレッドが最大 5 秒固まる
/// （<c>EventRecorder.Start</c> が排出の完了を待つため）。
///
/// 規則を WinUI アプリのプロジェクト（<c>GstEventRecorderViewModel</c>）に置くと
/// L1 から参照できないので、純粋関数としてここへ切り出してある。
/// </summary>
public class RecordingCommandStateTests
{
    [Fact]
    public void Idle_CanStartButCannotStop()
    {
        Assert.True(RecordingCommandState.CanStart(isInitialized: true, isRecording: false, isStopping: false));
        Assert.False(RecordingCommandState.CanStop(isInitialized: true, isRecording: false));
    }

    [Fact]
    public void Recording_CanStopButCannotStart()
    {
        Assert.False(RecordingCommandState.CanStart(isInitialized: true, isRecording: true, isStopping: false));
        Assert.True(RecordingCommandState.CanStop(isInitialized: true, isRecording: true));
    }

    /// <summary>
    /// このスイートの中核。停止を受け付けた直後、排出が終わるまでの窓。
    /// <c>IsRecording</c> は既に false なので、<c>IsStopping</c> を見ていないと
    /// 「開始できる」と誤判定する。
    /// </summary>
    [Fact]
    public void WhileDraining_CanNeitherStartNorStop()
    {
        Assert.False(RecordingCommandState.CanStart(isInitialized: true, isRecording: false, isStopping: true),
            "排出中に開始を通すと Start が排出の完了を待ち、UI スレッドが最大 StopFinalizeTimeoutMs 固まる");
        Assert.False(RecordingCommandState.CanStop(isInitialized: true, isRecording: false),
            "二重停止は弾かれること");
    }

    /// <summary>
    /// 停止の受付は同期なので、<c>IsRecording=true</c> と <c>IsStopping=true</c> が
    /// 同時に立つことはない。仮にそう見えても開始は許さない。
    /// </summary>
    [Fact]
    public void StoppingAlwaysWinsOverStart()
    {
        Assert.False(RecordingCommandState.CanStart(isInitialized: true, isRecording: true, isStopping: true));
    }

    [Fact]
    public void Uninitialized_CanDoNothing()
    {
        // 初期化に失敗したレコーダー（エンコーダーが1つも見つからない等）。
        Assert.False(RecordingCommandState.CanStart(isInitialized: false, isRecording: false, isStopping: false));
        Assert.False(RecordingCommandState.CanStop(isInitialized: false, isRecording: true));
        Assert.False(RecordingCommandState.CanStart(isInitialized: false, isRecording: false, isStopping: true));
    }
}
