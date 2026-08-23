using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// 1 レコーダーに対する開始／停止の結果。
/// </summary>
/// <param name="Filename">
/// 実際に使われたファイルのパス。<b>未展開のテンプレートではない</b>
/// ── CLI の <c>start-recording</c> / <c>stop-recording</c> が標準出力へ出すものと同じ値で、
/// 呼び出し側はこれをそのまま後処理へ渡せる。配信（<c>RecordingEndpoints</c> の
/// <c>GET /api/recordings/{path}</c>）が要るのは配信 root からの相対パスなので、
/// 渡すときは root ぶんを落とすこと。
/// </param>
public sealed record RecorderActionResult(string Name, string? Filename);

/// <summary>
/// 全レコーダーの開始結果。
/// </summary>
/// <param name="Started">
/// <b>今回開始した分だけ</b>。全件を返すと、開始しなかったレコーダーの
/// 前回の録画が今回の成果物として運ばれる（CLI の <c>start-recording-all</c> と同じ規則）。
/// </param>
/// <param name="Failed">
/// <b>開始できるはずだったのに落ちた分だけ</b>の名前。既に録画中のレコーダーは含まない。
/// </param>
public sealed record StartAllResult(IReadOnlyList<RecorderActionResult> Started, IReadOnlyList<string> Failed);

/// <summary>
/// 全レコーダーの停止結果（<b>今回停止した分だけ</b>を停止順で）。
/// </summary>
public sealed record StopAllResult(IReadOnlyList<StopItemResult> Stopped);

/// <summary>
/// 1 レコーダーぶんの停止結果。
/// </summary>
/// <param name="ExitCode">
/// そのレコーダー<b>単体</b>の判定（0 / 16 / 17）。全体の成否は HTTP ステータスが表す
/// ── 200 でも <b>ここが非 0 の行は使えない成果物</b>である
/// （全体が非 0 になるのは畳み込みが失敗したときで、そのときは本文が <c>ErrorDto</c> になる）。
/// </param>
public sealed record StopItemResult(string Name, string? Filename, int ExitCode);

/// <summary>
/// テンプレート変数 1 件。
/// </summary>
/// <param name="Persistent">settings.json に残るか（セッション限りなら <see langword="false"/>）。</param>
public sealed record VariableDto(string Key, string Value, bool Persistent);

/// <summary>テンプレート変数の全件（<b>キーの序数順</b>）。</summary>
public sealed record VariablesDto(IReadOnlyList<VariableDto> Variables);

/// <summary>
/// <c>PUT /api/variables/{key}</c> の本文。
///
/// <para>
/// <b>両方とも省略可で、意味が別。</b> <see cref="Value"/> は値の設定、
/// <see cref="Persist"/> は保存するかどうかの設定で、CLI の <c>--set</c> と
/// <c>--persist</c> にそれぞれ対応する。両方 <see langword="null"/>（＝どちらも
/// 指定していない）要求は何もしないので断る。
/// </para>
/// </summary>
public sealed record VariablePutRequest(string? Value, bool? Persist);

/// <summary>
/// 1 レコーダーの設定と、その項目の説明。
/// </summary>
/// <param name="Values">
/// 現在値。<b>キーは settings.json と同じ PascalCase</b>
/// （封筒側の camelCase とは別 ── ここは設定ファイルの写しであり、
/// <c>PATCH</c> の本文もこの表記で受ける）。
/// </param>
public sealed record RecorderSettingsDto(JsonObject Values, IReadOnlyList<SettingPropertyDto> Properties);

/// <summary>
/// 設定項目 1 つの説明（画面を組むために要る情報）。
/// </summary>
/// <param name="Type"><c>string</c> / <c>int</c> / <c>bool</c> / <c>enum</c> のいずれか。</param>
/// <param name="Category">
/// <b>解決済みの表示文字列</b>（リソースキーではない）。翻訳を持っているのはアプリ側だけなので、
/// ここで解決してから配る。
/// </param>
/// <param name="Description"><paramref name="Category"/> と同じく解決済み。説明が無ければ <see langword="null"/>。</param>
/// <param name="Choices">列挙型のメンバー名。列挙型でなければ <see langword="null"/>。</param>
/// <param name="Min">下限（丸めのある数値だけ）。</param>
/// <param name="Max">上限（丸めのある数値だけ）。</param>
/// <param name="RequiresReinitialize">変更しても初期化をやり直すまで効かないか。</param>
public sealed record SettingPropertyDto(
    string Name,
    string Type,
    string Category,
    string? Description,
    IReadOnlyList<string>? Choices,
    long? Min,
    long? Max,
    bool RequiresReinitialize);

/// <summary>
/// <c>PATCH</c> の結果。
/// </summary>
/// <param name="Applied">実際に書き込んだキー（要求の本文に現れた順）。</param>
/// <param name="Clamped">
/// <b>要求した値と、書き込んだあとの値が違うキー。</b> 設定側の setter が範囲へ丸めるため、
/// 200 を返しながら値が変わっていることがある ── 黙って丸めると
/// 呼び出し側は「効かなかった」ことに気付けない。
/// </param>
/// <param name="RequiresReinitialize">
/// 書き込んだキーのうち、<b>初期化をやり直すまで効かない</b>もの。
/// </param>
public sealed record PatchResultDto(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Clamped,
    IReadOnlyList<string> RequiresReinitialize);
