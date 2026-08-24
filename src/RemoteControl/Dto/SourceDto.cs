using System.Collections.Generic;

namespace ProcessRecorderApp.RemoteControl;

/// <summary>
/// 編集対象としてサポートするソース要素の一覧（<c>GET /api/sources</c>）。
/// <b>並びはカタログの並びそのまま</b>（画面の選択肢の順序が要求ごとに変わらない）。
/// </summary>
public sealed record SourcesDto(IReadOnlyList<SourceDefDto> Sources);

/// <summary>
/// ソース要素 1 種の候補。
/// </summary>
/// <param name="DisplayName">
/// <b>解決済みの表示文字列</b>（リソースキーではない）。翻訳を持っているのはアプリ側だけなので、
/// ここで解決してから配る（<c>SettingPropertyDto.Category</c> と同じ流儀）。
/// </param>
/// <param name="MemoryFeature">caps のメモリ機能（例: <c>memory:D3D12Memory</c>）。無ければ null。</param>
/// <param name="RecordingType">
/// <see cref="Components.SourcePresetRules.RecordingTypeFor"/> が
/// <paramref name="MemoryFeature"/> から導いた録画種別（<c>System</c> / <c>D3d12</c>）。
/// <b>この要素を適用すると <c>Type</c> はこの値になる</b>。
/// </param>
public sealed record SourceDefDto(
    string Element,
    string DisplayName,
    string? MemoryFeature,
    string RecordingType,
    IReadOnlyList<SourcePropertyDto> Properties,
    IReadOnlyList<CapsFieldDto> CapsFields);

/// <summary>
/// ソース要素の 1 プロパティ。
/// </summary>
/// <param name="Kind">
/// <c>Bool</c> / <c>Int</c> / <c>Enum</c> / <c>String</c>
/// （<see cref="Components.SourcePresetRules.PropertyKinds"/>。カタログの
/// <c>SrcPropertyKind</c> の名前と一致する）。
/// </param>
/// <param name="Choices">
/// <c>Enum</c> のときに選べる値。<b>実行時に取れる候補（モニター・カメラ）は解決済み</b>。
/// <b>取れなければ null で、そのプロパティは<u>どの値も通らない</u></b>
/// （<c>SourcePresetRules.Validate</c> は候補の無い <c>Enum</c> を全て断る）
/// ── 自由入力にはならないので、画面は入力欄を出さずに諦めること。
/// </param>
/// <param name="Description"><see cref="SourceDefDto.DisplayName"/> と同じく解決済み。無ければ null。</param>
/// <param name="ConditionallyAvailable">
/// <b>ビルド構成によっては要素に登録されないプロパティ</b>（GStreamer の
/// <c>conditionally available</c>）。付いている値を送ると、その要素に無い機械では
/// パイプラインの構築そのものが失敗する。
/// </param>
public sealed record SourcePropertyDto(
    string Name,
    string Kind,
    string? DefaultValue,
    IReadOnlyList<string>? Choices,
    string? Description,
    bool ConditionallyAvailable);

/// <summary>
/// caps の 1 フィールド。
/// </summary>
/// <param name="IsResolution">値が <c>幅x高さ</c>（例 <c>1280x720</c>）でなければならないか。</param>
public sealed record CapsFieldDto(
    string Name,
    bool IsResolution,
    string? DefaultValue,
    IReadOnlyList<string>? Choices);

/// <summary>
/// <c>PUT /api/recorders/{id}/source</c> の本文。
/// </summary>
/// <param name="Properties">要素プロパティの 名前→値（<b>すべて文字列</b>）。</param>
/// <param name="Caps">
/// caps フィールドの 名前→値。<b>null または空なら caps を出さない</b>
/// （<c>SrcPipelineBuilder.Assemble</c> の <c>capsEnabled=false</c>）。
/// </param>
public sealed record SourcePresetDto(
    string? Element,
    IReadOnlyDictionary<string, string>? Properties,
    IReadOnlyDictionary<string, string>? Caps);

/// <summary>
/// テンプレートの適用結果。<b><see cref="PatchResultDto"/> と同じ 3 つ ＋ 組み立てた文字列</b>。
///
/// <para>
/// <b><see cref="PatchResultDto"/> にフィールドを足していない。</b> 封筒の
/// <c>DefaultIgnoreCondition</c> は <c>Never</c> なので、足すと既存の <c>PATCH</c> の応答すべてに
/// <c>"srcPipeline":null</c> が現れる ── 既存 API の JSON の形を変えないために別の型にする。
/// </para>
/// </summary>
/// <param name="SrcPipeline">実際に書き込んだ <c>SrcPipeline</c>（逆パースの必要が無いように返す）。</param>
public sealed record SourceApplyResultDto(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Clamped,
    IReadOnlyList<string> RequiresReinitialize,
    string SrcPipeline);
