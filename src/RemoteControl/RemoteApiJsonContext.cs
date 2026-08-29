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
[JsonSerializable(typeof(RecorderActionResult))]
[JsonSerializable(typeof(StartAllResult))]
[JsonSerializable(typeof(StopAllResult))]
[JsonSerializable(typeof(StopItemResult))]
[JsonSerializable(typeof(VariableDto))]
[JsonSerializable(typeof(VariablesDto))]
[JsonSerializable(typeof(VariablePutRequest))]
[JsonSerializable(typeof(RecorderSettingsDto))]
[JsonSerializable(typeof(SettingPropertyDto))]
[JsonSerializable(typeof(PatchResultDto))]
[JsonSerializable(typeof(RecordingFileDto))]
[JsonSerializable(typeof(RecordingsDto))]
[JsonSerializable(typeof(RecordingFragmentDto))]
[JsonSerializable(typeof(RecordingFragmentsDto))]
[JsonSerializable(typeof(RecordingDaysDto))]
[JsonSerializable(typeof(RecordingChangeDto))]
[JsonSerializable(typeof(LoginRequestDto))]
[JsonSerializable(typeof(LoginResultDto))]
[JsonSerializable(typeof(MeDto))]
[JsonSerializable(typeof(SourcesDto))]
[JsonSerializable(typeof(SourceDefDto))]
[JsonSerializable(typeof(SourcePropertyDto))]
[JsonSerializable(typeof(CapsFieldDto))]
[JsonSerializable(typeof(SourcePresetDto))]
[JsonSerializable(typeof(SourceApplyResultDto))]
[JsonSerializable(typeof(PreviewQualityStateDto))]
[JsonSerializable(typeof(PreviewSourceDto))]
[JsonSerializable(typeof(PreviewQualityOptionDto))]
[JsonSerializable(typeof(PreviewEffectiveQualityDto))]
[JsonSerializable(typeof(PreviewQualityRequestDto))]
[JsonSerializable(typeof(CapabilitiesDto))]
internal partial class RemoteApiJsonContext : JsonSerializerContext
{
}
