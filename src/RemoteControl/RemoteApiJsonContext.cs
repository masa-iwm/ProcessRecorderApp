using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// リモート操作 API の JSON（ソース生成）。
///
/// <para>
/// <b>ここに載っている型だけを手でシリアライズする。</b> 最小 API の推論経路
/// （<c>Results.Json</c> やハンドラの戻り値）へ渡すと、リフレクション前提の
/// オーバーロードに束縛されて Native AOT の警告（IL2026 / IL3050）が出る。
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(RecordersSnapshot))]
[JsonSerializable(typeof(RecorderStatusDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(OkDto))]
[JsonSerializable(typeof(JsonObject))]
internal partial class RemoteApiJsonContext : JsonSerializerContext
{
}
