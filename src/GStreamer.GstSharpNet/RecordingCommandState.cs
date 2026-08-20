namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// 「今このレコーダーは録画を開始／終了できるか」の判定規則。
///
/// <para>
/// 状態は3つの独立したフラグで表される。<c>IsRecording</c> と <c>IsStopping</c> が
/// <b>同時に false になる期間がある</b>のが要点で、これが排出中
/// （EOS 送出 → バス待ち → <c>SetState(Null)</c>）にあたる。
/// </para>
///
/// <para>
/// <b>なぜ純粋関数として切り出すか</b> ── この規則は
/// <c>GstEventRecorderViewModel</c>（WinUI アプリプロジェクト）にあり、
/// L1 テストプロジェクトから参照できない。同じ理由で見逃されていた不具合が既に2件ある
/// （<c>ShouldExitOnClose</c> の <c>Locked</c> 誤判定、<c>NeedsSystemMemory</c> の手動指定経路）。
/// 規則そのものをここへ置けば L1 が守れる。
/// </para>
/// </summary>
public static class RecordingCommandState
{
    /// <summary>
    /// 録画を開始できるか。
    ///
    /// <para>
    /// <b><paramref name="isStopping"/> を見ることが必須。</b> 排出中に開始を通すと
    /// <c>EventRecorder.Start</c> が排出の完了を待つため、呼び出しスレッド（UI スレッド）が
    /// 最大 <c>StopFinalizeTimeoutMs</c>（既定 5 秒）固まる ── 排出をプールへ移して
    /// 剥がしたはずの UI ブロックが、停止直後の連打という一番踏みやすい操作で戻ってくる。
    /// </para>
    /// </summary>
    public static bool CanStart(bool isInitialized, bool isRecording, bool isStopping)
        => isInitialized && !isRecording && !isStopping;

    /// <summary>
    /// 録画を終了できるか。
    ///
    /// <para>
    /// <b>排出中(<c>IsStopping</c>)は false。</b> <paramref name="isRecording"/> は
    /// 停止を受け付けた時点で同期的に false になるので、この式だけで二重停止を弾ける
    /// ── 逆に言えば、<c>IsRecording=false</c> をプールスレッドへ逃がすと
    /// ここに窓が開いて二重停止が通る。
    /// </para>
    /// </summary>
    /// <param name="resumePending">
    /// 自動復帰が終わったら録り直す予定があるか（<c>EventRecorder.IsAwaitingRecoveryResume</c>）。
    /// <b>これを見ないと、抜けているあいだの停止がどこにも届かない。</b>
    /// 作り直しのあいだ <c>isRecording</c> も <c>isInitialized</c> も false になるので、
    /// その窓で利用者が止めても、UiaTrigger の停止条件が立っても、
    /// <b>復帰した瞬間に録画が勝手に再開する</b>。
    /// </param>
    public static bool CanStop(bool isInitialized, bool isRecording, bool resumePending)
        => resumePending || (isInitialized && isRecording);

    /// <summary>
    /// 画面と外部から見て「録画セッション中」か。
    ///
    /// <para>
    /// <b>復帰待ちも録画中として見せる。</b> 見せないと、トグルは切れた状態で表示され、
    /// <b>利用者には切る手段が無くなる</b>（切れているものは切れない）。
    /// 実体の <c>_IsRecording</c> は false のままで、こちらは表示と可否の判断にだけ使う
    /// ── コールバックが読むのはあくまで実体の方である。
    /// </para>
    /// </summary>
    public static bool ShowsAsRecording(bool isRecording, bool resumePending)
        => isRecording || resumePending;
}
