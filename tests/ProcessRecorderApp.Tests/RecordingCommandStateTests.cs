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
        Assert.False(RecordingCommandState.CanStop(isInitialized: true, isRecording: false, resumePending: false));
    }

    [Fact]
    public void Recording_CanStopButCannotStart()
    {
        Assert.False(RecordingCommandState.CanStart(isInitialized: true, isRecording: true, isStopping: false));
        Assert.True(RecordingCommandState.CanStop(isInitialized: true, isRecording: true, resumePending: false));
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
        Assert.False(RecordingCommandState.CanStop(isInitialized: true, isRecording: false, resumePending: false),
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

    // ---- 自動復帰による録り直し待ち ----

    /// <summary>
    /// <b>このスイートで 2 番目に重要な窓。</b> 作り直しのあいだは
    /// <c>IsRecording</c> も <c>IsInitialized</c> も false になる。ここで停止を
    /// 受け付けられないと、利用者が止めても UiaTrigger の停止条件が立っても
    /// <b>どこにも届かないまま、復帰した瞬間に録画が再開する</b>。
    /// </summary>
    [Fact]
    public void WhileAwaitingTheResume_CanStopEvenThoughNothingIsRecording()
    {
        Assert.True(RecordingCommandState.CanStop(isInitialized: false, isRecording: false, resumePending: true),
            "デバイスが抜けているあいだ（未初期化）の停止が届くこと");
        Assert.True(RecordingCommandState.CanStop(isInitialized: true, isRecording: false, resumePending: true),
            "作り直しに成功して録り直す直前でも、停止が届くこと");
    }

    /// <summary>
    /// 復帰待ちは<b>画面からは録画中に見える</b>こと。見えないとトグルは
    /// 切れた状態で表示され、切る手段が無くなる（切れているものは切れない）。
    /// 機械可読な <c>status</c> は畳まず、実体と復帰待ちを別の列で出す。
    /// </summary>
    [Fact]
    public void AwaitingTheResume_ShowsAsRecording()
    {
        Assert.True(RecordingCommandState.ShowsAsRecording(isRecording: false, resumePending: true));
        Assert.True(RecordingCommandState.ShowsAsRecording(isRecording: true, resumePending: false));
        Assert.True(RecordingCommandState.ShowsAsRecording(isRecording: true, resumePending: true));
        Assert.False(RecordingCommandState.ShowsAsRecording(isRecording: false, resumePending: false));
    }

    /// <summary>
    /// 復帰待ちは開始の可否を変えない ── 二重開始は
    /// <c>StartCore</c> 側（意図を消してから開始する）で防いでおり、
    /// ここで塞ぐと「作り直し直後に利用者が開始する」正当な操作まで弾く。
    /// </summary>
    [Fact]
    public void AwaitingTheResume_DoesNotBlockAManualStart()
        => Assert.True(RecordingCommandState.CanStart(isInitialized: true, isRecording: false, isStopping: false));

    [Fact]
    public void Uninitialized_CanDoNothing()
    {
        // 初期化に失敗したレコーダー（エンコーダーが1つも見つからない等）。
        Assert.False(RecordingCommandState.CanStart(isInitialized: false, isRecording: false, isStopping: false));
        Assert.False(RecordingCommandState.CanStop(isInitialized: false, isRecording: true, resumePending: false));
        Assert.False(RecordingCommandState.CanStart(isInitialized: false, isRecording: false, isStopping: true));
    }
}
