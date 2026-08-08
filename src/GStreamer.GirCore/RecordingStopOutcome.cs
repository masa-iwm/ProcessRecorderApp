namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// 停止した録画が<b>ファイルとして何を残したか</b>。
/// CLI（<c>stop-recording</c> / <c>stop-recording-all</c>）の終了コードはこれで決まる。
///
/// <para>
/// <b>「停止処理が成功したか」ではなく「成果物が使えるか」を表す。</b>
/// 排出（EOS → バス待ち → <c>SetState(Null)</c>）は<b>1フレームも入っていなくても
/// 綺麗に終わる</b>ので、停止処理の成否だけを見ると
/// <b>終了コード 0 で使えないファイルを渡してしまう</b> ──
/// 呼び出し側のバッチは <c>%ERRORLEVEL%</c> で成否を判定し（両 README に記載）、
/// <c>stop-recording</c> の直後に <c>copy</c> する想定である。
/// </para>
/// <para>
/// <b>2つの失敗を分けているのは、呼び出し側の扱いが変わるからである。</b>
/// 終了コードを分ける基準は「値ごとに名前を付ける」ことではなく
/// <b>呼び出し側の判断が変わるかどうか</b>（<c>src/README.md</c>「終了コードの一覧」の
/// 2 と 5/6 ＝ 再試行の可否の説明と同じ規則）。
/// </para>
/// </summary>
public enum RecordingStopOutcome
{
    /// <summary>ファイルは確定しており、中身もある。</summary>
    Ok,

    /// <summary>
    /// 排出は綺麗に終わったが<b>1フレームも入っていない</b>。
    /// <b>捨ててよい</b> ── メディアデータが無いと断定できる。
    /// </summary>
    Empty,

    /// <summary>
    /// 排出が完了しなかった（打ち切り・バスのエラー・例外）。
    /// <b>捨てる前に救済を検討できる</b> ── <c>mdat</c> にはデータが入っている一方で
    /// <c>moov</c> が未確定なので、<b>「中身が無い」とは限らない</b>。
    /// <see cref="Empty"/> より優先する（中身の有無を断定できるのは、
    /// 排出が綺麗に終わった場合だけ）。
    /// </summary>
    NotFinalized,
}

/// <summary>
/// <see cref="RecordingStopOutcome"/> の判定規則（純粋関数）。
///
/// <para>
/// <b>ここに置いてあるのは L1 から参照できるようにするため。</b>
/// 規則を CLI 側（<c>ActivationCommands</c>・WinUI アプリのプロジェクト）に書くと、
/// L1 テストがそのプロジェクトを参照していないので<b>誰も守れない</b>
/// ── <c>RecordingCommandState</c> をここへ切り出したのとまったく同じ理由である。
/// </para>
/// <para>
/// <b>特に <see cref="Stronger"/> は E2E では守れない。</b>
/// 「空かつ未確定」を設定だけで同時に起こすことはできず
/// （押し込みが 0 なら排出するものが無いので排出は速く終わる）、
/// <b>排出の打ち切りそのものも決定論的に踏ませられない</b>
/// ── 待ち時間をどう較正しても機械の速度に負ける。
/// </para>
/// </summary>
public static class RecordingStopRules
{
    /// <summary>成果物として使えるか（＝CLI が成功として返してよいか）。</summary>
    public static bool IsUsableArtifact(RecordingStopOutcome outcome)
        => outcome == RecordingStopOutcome.Ok;

    /// <summary>
    /// 2つの結果のうち<b>強い方</b>（＝呼び出し側により強い制約を課す方）を返す。
    /// <c>-all</c> で複数のレコーダーの結果を1つの終了コードに畳むときに使う。
    ///
    /// <para>
    /// <b>弱い方を返してはいけない。</b> 「捨ててよい」（<see cref="RecordingStopOutcome.Empty"/>）と
    /// 「未確定なので触るな」（<see cref="RecordingStopOutcome.NotFinalized"/>）では、
    /// 前者を返すと<b>救済できたはずのデータを捨てさせる</b>ことになり、取り返しがつかない。
    /// </para>
    /// </summary>
    public static RecordingStopOutcome Stronger(RecordingStopOutcome a, RecordingStopOutcome b)
    {
        if (a == RecordingStopOutcome.NotFinalized || b == RecordingStopOutcome.NotFinalized)
            return RecordingStopOutcome.NotFinalized;
        if (a == RecordingStopOutcome.Empty || b == RecordingStopOutcome.Empty)
            return RecordingStopOutcome.Empty;
        return RecordingStopOutcome.Ok;
    }
}

/// <summary>排出待ち（EOS 送出後のバス待ち）が何で終わったか。</summary>
public enum StopDrainSignal
{
    /// <summary>EOS が返った（排出は綺麗に終わった）。</summary>
    Eos,
    /// <summary>バスがエラーを報告した（ディスク満杯・書込権限など）。</summary>
    Error,
    /// <summary>上限（<c>StopFinalizeTimeoutMs</c>）内に何も返らなかった。</summary>
    Timeout,
}

/// <summary>
/// 排出結果の分類（純粋関数）。<c>EventRecorder.StopDrainAndFinalize</c> が使う。
///
/// <para>
/// これが終了コード 16/17 の分岐の中核 ── <c>result=</c> は <c>recording.stop</c> の行に、
/// <see cref="RecordingStopOutcome"/> は CLI の終了コードに写る。
/// </para>
/// </summary>
public static class StopDrainRules
{
    /// <summary>
    /// (activity.log の <c>result=</c> に出す1語, 呼び出し側へ返す結果) を決める。
    ///
    /// <para>
    /// <b>「空」で「未確定」を潰さない。</b> 排出が完了していない
    /// （<see cref="StopDrainSignal.Timeout"/> / <see cref="StopDrainSignal.Error"/>）なら、
    /// たとえ押し込みが 0 でも「未確定なので触るな」（<see cref="RecordingStopOutcome.NotFinalized"/>）が
    /// 正しい答えで、「空だから捨てろ」ではない ── 中身が無いと断定できるのは
    /// 排出が綺麗に終わった場合だけである（終了コード表の「両方に該当する場合は 17」）。
    /// </para>
    /// </summary>
    public static (string Result, RecordingStopOutcome Outcome) Classify(StopDrainSignal signal, int samplesPushed)
        => signal switch
        {
            StopDrainSignal.Timeout => ("timeout", RecordingStopOutcome.NotFinalized),
            StopDrainSignal.Error => ("error", RecordingStopOutcome.NotFinalized),
            _ when samplesPushed == 0 => ("empty", RecordingStopOutcome.Empty),
            _ => ("ok", RecordingStopOutcome.Ok),
        };
}
