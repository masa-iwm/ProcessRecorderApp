using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ProcessRecorderApp.Components;

/// <summary>
/// ソース要素の 1 プロパティを検証するために要る情報だけを写した軽い記述。
///
/// <para>
/// <b>カタログの型（<c>SrcPropertyDef</c>）は写さない。</b> あちらは
/// <c>GStreamer.GstSharpNet</c> の型で、このプロジェクトからは参照できない
/// ── 参照できるようにすると、規則を L1 で固定するために GStreamer の初期化が要る。
/// </para>
/// </summary>
/// <param name="Kind"><see cref="SourcePresetRules.PropertyKinds"/> のいずれか。</param>
/// <param name="Choices">
/// <see cref="SourcePresetRules.KindEnum"/> のときに受け付ける値
/// （<b>動的に埋めた候補を含む</b> ── 写す側が解決してから渡す）。
/// </param>
public sealed record SourcePropertySpec(string Name, string Kind, IReadOnlyList<string>? Choices);

/// <summary>caps の 1 フィールドを検証するために要る情報だけを写した軽い記述。</summary>
/// <param name="IsResolution">値が <c>幅x高さ</c> でなければならないか。</param>
public sealed record SourceCapsSpec(string Name, bool IsResolution);

/// <summary>1 ソース要素ぶんの軽い記述（<see cref="SourcePropertySpec"/> の doc を参照）。</summary>
public sealed record SourceSpec(
    string Element,
    IReadOnlyList<SourcePropertySpec> Properties,
    IReadOnlyList<SourceCapsSpec> CapsFields);

/// <summary>
/// リモートから受け取ったソースのテンプレートを検証する<b>純粋な規則</b>。
///
/// <para>
/// <b>リモートが <c>SrcPipeline</c> を書ける唯一の口がここを通る。</b> 文字列そのものの
/// <c>PATCH</c> は <see cref="RemoteApiRules.RemoteDeniedRecorderSettings"/> が拒み続け、
/// 書けるのは「カタログに在る要素・在るプロパティ・在る caps フィールド」だけを
/// 組み立てた結果に限られる。検証を間違えると<b>LAN から任意の GStreamer パイプラインを
/// 実行できる</b>種類の欠陥になるので、<see cref="RemoteApiRules"/> と同じ形でここへ置いて
/// L1 で固定する。
/// </para>
/// </summary>
public static partial class SourcePresetRules
{
    /// <summary>真偽値。受け付けるのは <c>true</c> / <c>false</c> の 2 語だけ。</summary>
    public const string KindBool = "Bool";

    /// <summary>整数。<b>範囲は見ない</b> ── カタログが上下限を持っていない。</summary>
    public const string KindInt = "Int";

    /// <summary>選択肢のいずれか（<see cref="SourcePropertySpec.Choices"/>）。</summary>
    public const string KindEnum = "Enum";

    /// <summary>自由入力（<see cref="LeaksIntoPipelineSyntax"/> だけを見る）。</summary>
    public const string KindString = "String";

    /// <summary>
    /// 値の種別の全部。<b><c>SrcPropertyKind</c> の列挙子の名前と過不足なく一致する</b>
    /// （L1 の <c>SourcePresetRulesTests</c> が型で照合する）── 封筒に出る文字列が
    /// カタログの種別とずれると、画面は「文字列入力」として組んだ欄で列挙を送ることになる。
    /// </summary>
    public static readonly string[] PropertyKinds = [KindBool, KindInt, KindEnum, KindString];

    /// <summary>システムメモリの録画種別（<c>EventRecordingType.System</c> の名前）。</summary>
    public const string RecordingTypeSystem = "System";

    /// <summary>D3D12 の録画種別（<c>EventRecordingType.D3d12</c> の名前）。</summary>
    public const string RecordingTypeD3d12 = "D3d12";

    /// <summary>D3D12 の録画種別を要求する caps のメモリ機能。</summary>
    private const string D3d12MemoryFeature = "D3D12Memory";

    /// <summary>
    /// caps のメモリ機能から録画の種別を導く。
    ///
    /// <para>
    /// <b><c>memory:D3D11Memory</c> は <see cref="RecordingTypeSystem"/> 側に落ちる</b>
    /// ── D3D11 のメモリは CPU からマップできるので <c>videoconvert</c> が受ける
    /// （<c>SrcPipelineBuilder</c> のカタログのコメント）。
    /// 「null でなければ D3D12」にすると、画面キャプチャ（D3D11）を選んだだけで
    /// 種別が変わる。
    /// </para>
    /// </summary>
    public static string RecordingTypeFor(string? memoryFeature)
        => memoryFeature is not null && memoryFeature.Contains(D3d12MemoryFeature, StringComparison.Ordinal)
            ? RecordingTypeD3d12
            : RecordingTypeSystem;

    /// <summary>
    /// パイプラインの構文へ漏れる文字。<b><c>Assemble</c> の引用に頼らず先に落とす</b> ──
    /// 引用の実装が変わった日に、値がパイプラインの構造として解釈される形へ戻らないため。
    /// <c>'!'</c> は要素の区切り、改行と NUL は記録と <c>gst_parse_launch</c> の両方を壊す。
    /// </summary>
    private static readonly char[] ForbiddenValueChars = ['!', '\r', '\n', '\0'];

    /// <summary>
    /// 値が<b>引用を免れる形</b>か（先頭が <c>'{'</c> または <c>'['</c>）。
    ///
    /// <para>
    /// <c>Assemble</c> の引用は caps のリスト <c>{ NV12, I420 }</c> とレンジ <c>[ 1, 30 ]</c> を
    /// <b>引用せずに素通しする</b>（引用すると列挙ではなく 1 個の文字列値になるため）。
    /// この形の値は空白ごとパイプラインの構文へ出るので、
    /// <c>device-name={a} fakesink name=z}</c> のように<b>要素を足せる</b>。
    /// テンプレートで送るのは 1 つの具体値だけなので、この形は丸ごと断る。
    /// </para>
    /// <para>
    /// <b>先頭の空白を落としてから見る。</b> 引用するかどうかの判定が前後の空白を
    /// 無視する形へ変わっても、通す値の集合が広がらないため。
    /// </para>
    /// </summary>
    private static bool IsQuoteExemptForm(string value)
    {
        string trimmed = value.TrimStart();
        return 0 < trimmed.Length && (trimmed[0] == '{' || trimmed[0] == '[');
    }

    /// <summary>
    /// 値がパイプラインの構文へ漏れるか ── 漏れる文字を含むか
    /// （<see cref="ForbiddenValueChars"/>）、引用を免れる形か（<see cref="IsQuoteExemptForm"/>）。
    /// </summary>
    public static bool LeaksIntoPipelineSyntax(string? value)
        => value is not null
           && (value.IndexOfAny(ForbiddenValueChars) >= 0 || IsQuoteExemptForm(value));

    /// <summary>
    /// 解像度の値（<c>幅x高さ</c>）として読めるか。
    ///
    /// <para>
    /// <b><c>SrcPipelineBuilder.SplitResolution</c> と同じ形を受ける</b>（前後の空白を落として
    /// <c>数値 x 数値</c>）。あちらは <c>GStreamer.GstSharpNet</c> にあり、このプロジェクトからは
    /// 参照できないので写してある ── 一致は L1 が両方を呼んで表で照合する。
    /// </para>
    /// </summary>
    public static bool IsResolutionValue(string? value)
        => value is not null && ResolutionRegex().IsMatch(value.Trim());

    [GeneratedRegex(@"^(\d+)\s*[xX×]\s*(\d+)$")]
    private static partial Regex ResolutionRegex();

    /// <summary>要素がカタログに無いときの理由。</summary>
    public const string UnknownElementError = "unknown source element";

    /// <summary>
    /// テンプレートを検証する。受け付けられなければ false で、
    /// <paramref name="error"/> に<b>何が駄目だったか</b>が入る（応答の <c>error</c> になる）。
    ///
    /// <para>
    /// <b>先に文字を見る。</b> 種別の判定より前にパイプラインの構文へ漏れる文字を落とすので、
    /// 自由入力（<see cref="KindString"/>）にも同じ規律が掛かる。
    /// </para>
    /// </summary>
    /// <param name="source">
    /// 対象のソース（カタログに無ければ <see langword="null"/> を渡す ──
    /// <see cref="UnknownElementError"/> で断る）。
    /// </param>
    /// <param name="properties">要素プロパティの 名前→値。</param>
    /// <param name="caps">caps フィールドの 名前→値（null または空なら caps を出さない）。</param>
    public static bool Validate(
        SourceSpec? source,
        IReadOnlyDictionary<string, string>? properties,
        IReadOnlyDictionary<string, string>? caps,
        [NotNullWhen(false)] out string? error)
    {
        if (source is null)
        {
            error = UnknownElementError;
            return false;
        }

        if (properties is not null)
        {
            foreach (var pair in properties)
            {
                if (!ValidateProperty(source, pair.Key, pair.Value, out error))
                    return false;
            }
        }

        if (caps is not null)
        {
            foreach (var pair in caps)
            {
                if (!ValidateCapsField(source, pair.Key, pair.Value, out error))
                    return false;
            }
        }

        error = null;
        return true;
    }

    private static bool ValidateProperty(
        SourceSpec source, string name, string? value, [NotNullWhen(false)] out string? error)
    {
        SourcePropertySpec? def = null;
        foreach (var candidate in source.Properties)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                def = candidate;
                break;
            }
        }

        if (def is null)
        {
            error = "unknown property: " + name;
            return false;
        }

        if (LeaksIntoPipelineSyntax(value))
        {
            error = "invalid character in value for property: " + name;
            return false;
        }

        bool ok = def.Kind switch
        {
            KindBool => string.Equals(value, "true", StringComparison.Ordinal)
                        || string.Equals(value, "false", StringComparison.Ordinal),
            KindInt => value is not null
                       && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            KindEnum => IsChoice(def.Choices, value),
            _ => true,
        };

        if (!ok)
        {
            error = "invalid value for property: " + name;
            return false;
        }

        error = null;
        return true;
    }

    private static bool ValidateCapsField(
        SourceSpec source, string name, string? value, [NotNullWhen(false)] out string? error)
    {
        SourceCapsSpec? def = null;
        foreach (var candidate in source.CapsFields)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                def = candidate;
                break;
            }
        }

        if (def is null)
        {
            error = "unknown caps field: " + name;
            return false;
        }

        if (LeaksIntoPipelineSyntax(value))
        {
            error = "invalid character in value for caps field: " + name;
            return false;
        }

        if (def.IsResolution && !IsResolutionValue(value))
        {
            error = "invalid value for caps field: " + name;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>選択肢のいずれかか（序数一致。選択肢が無ければ<b>どの値も通さない</b>）。</summary>
    private static bool IsChoice(IReadOnlyList<string>? choices, string? value)
    {
        if (choices is null || value is null)
            return false;

        foreach (string choice in choices)
        {
            if (string.Equals(choice, value, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
