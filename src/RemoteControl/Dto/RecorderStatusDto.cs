using System.Collections.Generic;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// 1 レコーダーぶんの状態。<c>status</c> コマンドの 8 列と 1:1 で対応する。
/// </summary>
/// <param name="IsRecording">
/// 表示用に畳んだ値ではなく<b>実体</b>（復帰待ちは
/// <paramref name="IsAwaitingRecoveryResume"/> に独立して出る）。
/// </param>
/// <param name="ContinuousState">常時録画の状態（1 語）。</param>
/// <param name="LastError">
/// <b>1 行へ潰していない生の値。</b> 改行は JSON が運ぶので、TAB 区切りの CLI 出力と
/// 違ってここでは潰す理由が無い。
/// </param>
public sealed record RecorderStatusDto(
    string Name,
    bool IsInitialized,
    bool IsRecording,
    bool IsAwaitingRecoveryResume,
    string? LastFilename,
    string ContinuousState,
    string? ContinuousLastFilename,
    string? LastError);

/// <summary>
/// 全レコーダーの状態と、全体に対して行える操作。
///
/// <para>
/// <b>補助エンコーダー枠の 2 つはレコーダー単位ではなくプロセス全体の値である。</b>
/// ここへ載せているのは、値が変わる契機（ライブ DASH の起動・退役、トランスコードの
/// 開始・終了、上限の設定変更）が SSE の <c>state</c> と同じ 1 本で足りるからで、
/// これ以外に空き枠の変化を押し出す経路は無い。
/// </para>
/// </summary>
/// <param name="AuxiliaryEncoderLimit">補助エンコーダー枠の上限（<c>RemoteAuxiliaryEncoderLimit</c>）。</param>
/// <param name="AuxiliaryEncodersFree">
/// いま取れる枠の数。<b>0 なら新しいライブ DASH もトランスコードも
/// <c>auxiliary encoder busy</c> で断られる</b>（既に走っているものは続く）。
/// </param>
public sealed record RecordersSnapshot(
    IReadOnlyList<RecorderStatusDto> Recorders,
    bool CanStartAll,
    bool CanStopAll,
    bool IsIdleAll,
    int AuxiliaryEncoderLimit,
    int AuxiliaryEncodersFree);
