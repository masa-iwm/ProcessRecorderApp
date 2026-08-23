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
    /// 録画の配信 root（解決済みの絶対パス）。<b>アプリ全体で 1 つ</b>で、
    /// 録画の書き込み先と同じ値である。
    ///
    /// <para>
    /// <b>返すのは文字列だけで、列挙も開封もしない。</b> ディスクを読むのは呼び出し側
    /// （<c>RecordingEndpoints</c>）の仕事である ── 実装は UI スレッドの上に居るので、
    /// 件数に比例する同期 IO をここへ載せると画面が止まる。
    /// </para>
    /// </summary>
    Task<string> GetRecordingsRootAsync(CancellationToken ct);

    /// <summary>
    /// 状態変化の購読。<paramref name="onChange"/> は任意のスレッドで呼ばれる
    /// （200ms デバウンス済み）。戻り値の <see cref="IDisposable.Dispose"/> で解除する。
    /// </summary>
    IDisposable SubscribeState(Action<RecordersSnapshot> onChange);

    // ---- 書き込み ----
    //
    // **CancellationToken を取らない。** 開始・停止・設定変更は、要求元が切っても
    // 完遂させる ── 途中で畳むと「録画は始まったが誰も知らない」状態が残る。
    // 書けなくなるのは応答だけで、それは捨ててよい。
    //
    // **{id} は文字列のまま渡す。** 数値ならインデックス・それ以外は名前という規則は
    // CLI（RecorderCliRules.ResolveTargetIndex）が正本であり、実装側がそれを呼ぶ。
    // ここで先に解決すると規則が 2 か所になる。

    /// <summary>
    /// 1 レコーダーの録画を開始する。
    /// 12（未準備）/ 13（対象なし）/ 14（開始できない状態）は
    /// <see cref="RemoteApiException"/>。
    /// </summary>
    Task<RecorderActionResult> StartAsync(string id);

    /// <summary>
    /// 1 レコーダーの録画を停止し、<b>ファイルの確定まで待つ</b>。
    /// 12 / 13 / 14 は <see cref="RemoteApiException"/>。
    /// 停止自体は成功したが成果物が使えない場合（16 / 17）も
    /// <see cref="RemoteApiException"/> で、<see cref="RemoteApiException.Filename"/> に
    /// そのファイルのパスが載る。
    /// </summary>
    Task<RecorderActionResult> StopAsync(string id);

    /// <summary>全レコーダーの録画を開始する。12 / 14 は <see cref="RemoteApiException"/>。</summary>
    Task<StartAllResult> StartAllAsync();

    /// <summary>
    /// 全レコーダーの録画を停止する。12 / 14 は <see cref="RemoteApiException"/>。
    /// 1 本でも使えない成果物があれば<b>強い方</b>の 16 / 17 で
    /// <see cref="RemoteApiException"/>（<see cref="RemoteApiException.Filename"/> は
    /// その終了コードに該当する最初のレコーダーのファイル）。
    /// </summary>
    Task<StopAllResult> StopAllAsync();

    /// <summary>テンプレート変数の全件（キーの序数順）。</summary>
    Task<VariablesDto> GetVariablesAsync();

    /// <summary>
    /// テンプレート変数を書く。<paramref name="value"/> が非 null なら値を設定し、
    /// <paramref name="persist"/> が非 null なら保存するかどうかを設定する
    /// （順序は値 → 保存。CLI の <c>--set</c> → <c>--persist</c> と同じ）。
    /// 未定義のキーに保存だけを指定した場合は 11（変数が未定義）で
    /// <see cref="RemoteApiException"/>。
    /// </summary>
    Task<VariableDto> PutVariableAsync(string key, string? value, bool? persist);

    /// <summary>
    /// 1 レコーダーの設定と項目の説明。12 / 13 は <see cref="RemoteApiException"/>。
    /// </summary>
    Task<RecorderSettingsDto> GetRecorderSettingsAsync(string id);

    /// <summary>
    /// 1 レコーダーの設定を部分更新する。12 / 13 は <see cref="RemoteApiException"/>。
    /// 未知のキー・型の合わない値は 4（引数が不正）。
    /// </summary>
    Task<PatchResultDto> PatchRecorderSettingsAsync(string id, JsonObject patch);

    /// <summary>
    /// アプリ設定を部分更新する。<b>編集を許していないキー</b>と型の合わない値は
    /// 4（引数が不正）で <see cref="RemoteApiException"/>。
    /// </summary>
    Task<PatchResultDto> PatchAppSettingsAsync(JsonObject patch);
}

/// <summary>
/// リモート操作 API の失敗。<see cref="ExitCode"/> は CLI と同じ番号で、
/// HTTP ステータスへは <c>RemoteApiRules.HttpStatusFor</c> が写す。
/// </summary>
public sealed class RemoteApiException(int exitCode, string message) : Exception(message)
{
    /// <summary>CLI と同じ終了コード。</summary>
    public int ExitCode { get; } = exitCode;

    /// <summary>
    /// 失敗しても残っているファイルのパス（<b>停止の 16 / 17 のときだけ</b>）。
    /// 応答本文の <c>ErrorDto.Filename</c> になる。
    /// </summary>
    public string? Filename { get; init; }
}
