using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// リモート操作 API が読む<b>アプリ側の状態</b>。実装はアプリ本体にある。
///
/// <para>
/// <b>この抽象の唯一の目的は、スレッド越境の責務を実装側へ置くこと。</b>
/// ここから見えるのは <see cref="Task"/> を返すメソッドだけで、どのスレッドで
/// 何が動くかは一切現れない ── 越境は実装側（アプリの Services）の責務である。
/// <b>この規律はコンパイルでは守られない</b>（UI 側の型は推移的な参照で書けてしまう）。
/// 守っているのは L1 の <c>RemoteControlIsolationTests</c> で、
/// このプロジェクトの .cs をテキストとして読み、UI 側の型名が現れたら落とす。
/// </para>
/// </summary>
public interface IRemoteControlBackend
{
    /// <summary>
    /// 全レコーダーの状態を読む。まだ録画エンジンが使えないときは
    /// <see cref="RemoteApiException"/>（終了コード 12）を投げる。
    /// </summary>
    Task<RecordersSnapshot> GetRecordersAsync(CancellationToken ct);

    /// <summary>
    /// アプリ設定のうち<b>リモートから編集してよいキーだけ</b>を返す
    /// （PascalCase。settings.json と同じ表現）。
    /// </summary>
    Task<JsonObject> GetAppSettingsAsync(CancellationToken ct);

    /// <summary>
    /// 状態変化の購読。<paramref name="onChange"/> は任意のスレッドで呼ばれる
    /// （200ms デバウンス済み）。戻り値の <see cref="IDisposable.Dispose"/> で解除する。
    /// </summary>
    IDisposable SubscribeState(Action<RecordersSnapshot> onChange);
}

/// <summary>
/// リモート操作 API の失敗。<see cref="ExitCode"/> は CLI と同じ番号で、
/// HTTP ステータスへは <c>RemoteApiRules.HttpStatusFor</c> が写す。
/// </summary>
public sealed class RemoteApiException(int exitCode, string message) : Exception(message)
{
    /// <summary>CLI と同じ終了コード。</summary>
    public int ExitCode { get; } = exitCode;
}
