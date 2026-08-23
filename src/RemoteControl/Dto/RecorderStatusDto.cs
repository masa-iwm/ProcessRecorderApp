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

/// <summary>全レコーダーの状態と、全体に対して行える操作。</summary>
public sealed record RecordersSnapshot(
    IReadOnlyList<RecorderStatusDto> Recorders,
    bool CanStartAll,
    bool CanStopAll,
    bool IsIdleAll);
