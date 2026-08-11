using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ProcessRecorderApp.GStreamer;

/// <summary>ソース要素プロパティの値の種別(編集コントロールの選択に使う)。</summary>
public enum SrcPropertyKind
{
    Bool,
    Int,
    Enum,
    String,
}

/// <summary>ソース要素の 1 プロパティの定義(キュレート値)。</summary>
public sealed class SrcPropertyDef(
    string name,
    SrcPropertyKind kind,
    string? defaultValue = null,
    string[]? enumChoices = null,
    string? description = null,
    string? dynamicKey = null)
{
    /// <summary>GStreamer プロパティ名(例: is-live, monitor-index)。</summary>
    public string Name { get; } = name;
    public SrcPropertyKind Kind { get; } = kind;
    /// <summary>既定値(表示補助。未指定時の初期値として使う)。</summary>
    public string? DefaultValue { get; } = defaultValue;
    /// <summary>Enum のときの静的な選択肢。</summary>
    public string[]? EnumChoices { get; } = enumChoices;
    public string? Description { get; } = description;
    /// <summary>実行時に選択肢を動的取得するためのキー(例: monitor-index, mf-device-name)。null なら動的取得なし。</summary>
    public string? DynamicKey { get; } = dynamicKey;
}

/// <summary>caps の 1 フィールドの定義(format/framerate/resolution 等)。</summary>
public sealed class CapsFieldDef(
    string name,
    bool isResolution = false,
    string? defaultValue = null,
    string[]? choices = null,
    string? dynamicKey = null)
{
    /// <summary>caps フィールド名。resolution の場合は width/height に展開される合成名。</summary>
    public string Name { get; } = name;
    /// <summary>true の場合、値は "幅x高さ" 形式で width= / height= に展開する。</summary>
    public bool IsResolution { get; } = isResolution;
    public string? DefaultValue { get; } = defaultValue;
    /// <summary>静的な選択肢(null なら自由入力)。</summary>
    public string[]? Choices { get; } = choices;
    /// <summary>実行時に選択肢を動的取得するためのキー(例: mf-format)。null なら動的取得なし。</summary>
    public string? DynamicKey { get; } = dynamicKey;
}

/// <summary>ソース要素 1 種の定義(要素名・主要プロパティ・caps 構成)。</summary>
public sealed class SrcElementDef(
    string elementName,
    string displayName,
    SrcPropertyDef[] properties,
    CapsFieldDef[] capsFields,
    string? memoryFeature = null)
{
    /// <summary>GStreamer 要素名(例: d3d12screencapturesrc)。</summary>
    public string ElementName { get; } = elementName;
    public string DisplayName { get; } = displayName;
    public SrcPropertyDef[] Properties { get; } = properties;
    public CapsFieldDef[] CapsFields { get; } = capsFields;
    /// <summary>caps のメモリ機能(例: "memory:D3D12Memory")。null ならシステムメモリ(付与しない)。</summary>
    public string? MemoryFeature { get; } = memoryFeature;

    public override string ToString() => DisplayName;
}

/// <summary><see cref="SrcPipelineBuilder.Parse"/> の結果を保持する中間データ。</summary>
public sealed class ParsedSrcPipeline
{
    /// <summary>解析できたソース要素名。未知の場合は null。</summary>
    public string? SourceElement { get; init; }
    /// <summary>ソース要素に付いていた key=value 群。</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
    /// <summary>全 caps セグメントを統合した key=value 群(format/framerate/width/height 等)。</summary>
    public IReadOnlyDictionary<string, string> CapsFields { get; init; } = new Dictionary<string, string>();
    /// <summary>最初の caps のメモリ機能(例: "memory:D3D12Memory")。無しなら null。</summary>
    public string? MemoryFeature { get; init; }
    /// <summary>caps セグメントが 1 つ以上あったか。</summary>
    public bool HasCaps { get; init; }
    /// <summary>ソースより後ろに現れた要素名(例: videoconvert)。想定形では空。</summary>
    public IReadOnlyList<string> IntermediateElements { get; init; } = [];
}

/// <summary>
/// SrcPipeline 文字列の編集支援(キュレート方式)。
///
/// サポートするソース要素について主要プロパティ・caps 構成をコード内で定義し、
/// 既存文字列の解析(<see cref="Parse"/>)と再生成(<see cref="Assemble"/>)を提供する。
/// 一部の選択肢(モニター数・デバイス一覧・デバイス caps)は実行時に
/// <see cref="GstIntrospect"/> で動的取得し、ビルダー UI 側で補完する。
/// </summary>
public static partial class SrcPipelineBuilder
{
    /// <summary>caps のメディアタイプ(現状すべて video/x-raw)。</summary>
    public const string CapsMediaType = "video/x-raw";

    // format の代表的な選択肢(自由入力も可)
    private static readonly string[] FormatChoices =
        ["NV12", "I420", "RGBA", "BGRA", "RGB", "BGR", "YUY2", "P010_10LE"];

    // テストソースの pattern 代表的な選択肢
    private static readonly string[] TestPatterns =
        ["smpte", "snow", "black", "white", "red", "green", "blue", "ball", "smpte75", "circular"];

    /// <summary>
    /// 編集対象としてサポートするソース要素のカタログ。
    /// 表示名・説明はローカライズリソースから解決するため、静的フィールド初期化子ではなく
    /// <see cref="Lazy{T}"/> で遅延生成する（<see cref="Parse"/>/<see cref="Assemble"/> だけを
    /// 使う経路でリソース基盤の初期化を強制しないため）。
    /// </summary>
    public static SrcElementDef[] Sources => _sources.Value;

    private static readonly Lazy<SrcElementDef[]> _sources = new(() =>
    [
        // 画面キャプチャ: caps は framerate のみ(memory:D3D12Memory)。
        // format/解像度は指定しない(D3d12 種別では録画側で d3d12convert により NV12 化される)。
        new SrcElementDef(
            elementName: "d3d12screencapturesrc",
            displayName: Localization.GetString("Resources/Src_ScreenCapture_DisplayName"),
            properties:
            [
                new SrcPropertyDef("monitor-index", SrcPropertyKind.Int, "0",
                    description: Localization.GetString("Resources/Src_MonitorIndex_Desc"), dynamicKey: "monitor-index"),
                new SrcPropertyDef("show-cursor", SrcPropertyKind.Bool, "false",
                    description: Localization.GetString("Resources/Src_ShowCursor_Desc")),
            ],
            capsFields:
            [
                // **解像度も選べるようにしてある。** 常時録画のフレームレート／解像度の
                // 上書きは、ソースの caps がこれらを固定しているときにしか効かせられない
                // ── 枝の要求は tee を越えて上流へ伝播するので、固定していないと
                // プレビューとイベント録画まで一緒に縮む（src/README.md「解像度の上書きには
                // 上流の固定が要る」）。選択肢はモニターが実際に出す大きさを
                // GstIntrospect.GetScreenCaptureMonitors から動的に取る。
                new CapsFieldDef("resolution", isResolution: true, defaultValue: "", dynamicKey: "monitor-resolution"),
                new CapsFieldDef("framerate", defaultValue: "30/1"),
            ],
            memoryFeature: "memory:D3D12Memory"),

        // 画面キャプチャ(D3D11): 上の D3D12 版と同じ形で、メモリ機能だけが D3D11Memory。
        // **D3D12 版は show-cursor=true でプロセスごと落ちうる**（上流の欠陥。同梱ランタイムは
        // 直したビルドを積むが、非同梱配布では利用者の GStreamer 次第 ──
        // docs/environment-facts.md）。**上流の D3D11 側は同じ処理が元から正しい**ので、
        // カーソルを写したい構成の逃げ道として置いてある。
        //
        // **memory:D3D11Memory は Type=D3d12 でも Type=System でも通る**（実測）──
        // 前者は d3d12upload が D3D11Memory を受け、後者は D3D11 のメモリが CPU から
        // マップできるので videoconvert が受ける。したがって D3D12 版と違い、
        // 種別との組み合わせで初期化に失敗する形にはならない。
        //
        // **この要素は拡縮できない。** caps でモニターの実寸以外を要求すると
        // `Internal data stream error` になり 1 フレームも流れない（実測: 3840x2160 の画面へ
        // 1024x768 を要求）。解像度の選択肢は GstIntrospect.GetMonitorResolutions が返す
        // 実寸そのものなので通る ── **任意の大きさを名乗る d3d12screencapturesrc とはここが違う**。
        // monitor-index の並びは D3D12 版と同一（上流 gst_d3d11_screen_capture_find_nth_monitor は
        // EnumAdapters1 × EnumOutputs の平坦化で、D3D12 側と同じ走査）。
        new SrcElementDef(
            elementName: "d3d11screencapturesrc",
            displayName: Localization.GetString("Resources/Src_ScreenCaptureD3d11_DisplayName"),
            properties:
            [
                new SrcPropertyDef("monitor-index", SrcPropertyKind.Int, "0",
                    description: Localization.GetString("Resources/Src_MonitorIndex_Desc"), dynamicKey: "monitor-index"),
                new SrcPropertyDef("show-cursor", SrcPropertyKind.Bool, "false",
                    description: Localization.GetString("Resources/Src_ShowCursor_Desc")),
            ],
            capsFields:
            [
                new CapsFieldDef("resolution", isResolution: true, defaultValue: "", dynamicKey: "monitor-resolution"),
                new CapsFieldDef("framerate", defaultValue: "30/1"),
            ],
            memoryFeature: "memory:D3D11Memory"),

        // カメラ: format/解像度/framerate はデバイス caps から動的取得する。
        new SrcElementDef(
            elementName: "mfvideosrc",
            displayName: Localization.GetString("Resources/Src_Camera_DisplayName"),
            properties:
            [
                new SrcPropertyDef("device-index", SrcPropertyKind.Int, "0",
                    description: Localization.GetString("Resources/Src_DeviceIndex_Desc"), dynamicKey: "mf-device-index"),
                new SrcPropertyDef("device-name", SrcPropertyKind.String, null,
                    description: Localization.GetString("Resources/Src_DeviceName_Desc"), dynamicKey: "mf-device-name"),
            ],
            capsFields:
            [
                new CapsFieldDef("format", defaultValue: "NV12", dynamicKey: "mf-format"),
                new CapsFieldDef("resolution", isResolution: true, defaultValue: "1920x1080", dynamicKey: "mf-resolution"),
                new CapsFieldDef("framerate", defaultValue: "30/1", dynamicKey: "mf-framerate"),
            ]),

        // テストパターン(D3D12)
        new SrcElementDef(
            elementName: "d3d12testsrc",
            displayName: Localization.GetString("Resources/Src_TestPatternD3d12_DisplayName"),
            properties:
            [
                new SrcPropertyDef("is-live", SrcPropertyKind.Bool, "true", description: Localization.GetString("Resources/Src_IsLive_Desc")),
                new SrcPropertyDef("do-timestamp", SrcPropertyKind.Bool, "true", description: Localization.GetString("Resources/Src_DoTimestamp_Desc")),
                new SrcPropertyDef("pattern", SrcPropertyKind.Enum, "smpte", TestPatterns, Localization.GetString("Resources/Src_Pattern_Desc")),
            ],
            capsFields:
            [
                new CapsFieldDef("format", defaultValue: "NV12", choices: FormatChoices),
                new CapsFieldDef("resolution", isResolution: true, defaultValue: "1280x720"),
                new CapsFieldDef("framerate", defaultValue: "30/1"),
            ],
            memoryFeature: "memory:D3D12Memory"),

        // テストパターン(システムメモリ)
        new SrcElementDef(
            elementName: "videotestsrc",
            displayName: Localization.GetString("Resources/Src_TestPatternSystem_DisplayName"),
            properties:
            [
                new SrcPropertyDef("is-live", SrcPropertyKind.Bool, "true", description: Localization.GetString("Resources/Src_IsLive_Desc")),
                new SrcPropertyDef("do-timestamp", SrcPropertyKind.Bool, "true", description: Localization.GetString("Resources/Src_DoTimestamp_Desc")),
                new SrcPropertyDef("pattern", SrcPropertyKind.Enum, "smpte", TestPatterns, Localization.GetString("Resources/Src_Pattern_Desc")),
            ],
            capsFields:
            [
                new CapsFieldDef("format", defaultValue: "I420", choices: FormatChoices),
                new CapsFieldDef("resolution", isResolution: true, defaultValue: "1280x720"),
                new CapsFieldDef("framerate", defaultValue: "30/1"),
            ]),
    ]);


    /// <summary>
    /// ソースを切り替えたときに、いまの入力のうち<b>新しいソースにも在る項目だけ</b>を引き継ぐ。
    ///
    /// <para>
    /// <c>d3d12screencapturesrc</c> と <c>d3d11screencapturesrc</c> のように
    /// <b>プロパティも caps も同じ顔ぶれ</b>のソースが並んでいるため、切り替えのたびに
    /// 既定値へ戻されると、モニター番号や解像度を毎回入れ直すことになる。
    /// </para>
    /// <para>
    /// <b>名前が一致するものだけを運ぶ。</b> 引き継ぎ先のカタログに無い項目は捨てる ──
    /// カメラの <c>device-name</c> を画面キャプチャへ運ぶような、
    /// <b>その要素では意味を成さない値</b>が残ると、パイプラインが黙って壊れる。
    /// </para>
    /// <para>
    /// 戻り値は <see cref="Parse"/> と同じ形にしてある。呼び出し側（ビルダー UI）は
    /// 「既存文字列を開いたとき」と同じ経路で行を組み直せる ── 分岐が増えない。
    /// </para>
    /// </summary>
    /// <param name="target">切り替え先のソース定義。</param>
    /// <param name="properties">いま入力されているプロパティ（要素プロパティ名 → 値）。</param>
    /// <param name="capsValues">
    /// いま入力されている caps（<c>CapsFieldDef.Name</c> → 値。解像度は "幅x高さ" のまま渡す）。
    /// </param>
    /// <param name="capsEnabled">caps を出力する設定になっているか。</param>
    public static ParsedSrcPipeline CarryOver(
        SrcElementDef target,
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyDictionary<string, string> capsValues,
        bool capsEnabled)
    {
        ArgumentNullException.ThrowIfNull(target);

        var carriedProperties = new Dictionary<string, string>();
        foreach (var def in target.Properties)
        {
            if (properties is not null
                && properties.TryGetValue(def.Name, out string? value)
                && !string.IsNullOrEmpty(value))
            {
                carriedProperties[def.Name] = value;
            }
        }

        var carriedCaps = new Dictionary<string, string>();
        foreach (var def in target.CapsFields)
        {
            if (capsValues is null
                || !capsValues.TryGetValue(def.Name, out string? value)
                || string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (def.IsResolution)
            {
                // 解析結果と同じ形（width / height）で返す ── 呼び出し側は Parse の結果と
                // 同じ読み方ができる。読めない文字列は運ばない（黙って幅だけ入るのを避ける）。
                var (w, h) = SplitResolution(value);
                if (w is not null && h is not null)
                {
                    carriedCaps["width"] = w;
                    carriedCaps["height"] = h;
                }
            }
            else
            {
                carriedCaps[def.Name] = value;
            }
        }

        return new ParsedSrcPipeline
        {
            SourceElement = target.ElementName,
            Properties = carriedProperties,
            CapsFields = carriedCaps,
            MemoryFeature = target.MemoryFeature,
            HasCaps = capsEnabled,
        };
    }

    /// <summary>要素名からカタログ定義を取得する(未登録なら null)。</summary>
    public static SrcElementDef? FindSource(string? elementName)
        => elementName is null ? null : Sources.FirstOrDefault(s => s.ElementName == elementName);

    // 要素/caps セグメント内の "key=value"(値は "..." で括られる場合あり)を切り出す。
    // 引用値の中は \" / \\ のエスケープを1単位として読む（Assemble の QuoteIfNeeded が
    // 生成する形。エスケープを知らない "[^"]*" だと、値に '"' を含むラウンドトリップが
    // 引用の途中で切れる）
    [GeneratedRegex("""(?<key>[\w.-]+)\s*=\s*(?<value>"(?:\\.|[^"\\])*"|[^\s,]+)""")]
    private static partial Regex KeyValueRegex();

    // 先頭が "video/x-raw" のようなメディアタイプで始まるか(caps セグメントの判定)
    [GeneratedRegex(@"^[a-zA-Z][\w-]*/[\w+-]")]
    private static partial Regex MediaTypeRegex();

    /// <summary>
    /// SrcPipeline 文字列を解析し、ソース要素・プロパティ・caps・中間要素に分解する。
    /// caps が複数現れる場合はフィールドを統合する。
    /// </summary>
    public static ParsedSrcPipeline Parse(string? pipeline)
    {
        if (string.IsNullOrWhiteSpace(pipeline))
            return new ParsedSrcPipeline();

        // '!' は要素/caps の区切りだが、**二重引用符の中には値として現れる** ──
        // mfvideosrc の device-name には実在するデバイス表示名（例: "Live! Cam Sync HD"）が
        // そのまま入る。gst_parse_launch は引用内の '!' を値の一部として扱うので、
        // 単純 Split だと「録画は通るのにビルダーで開き直すと設定が壊れる」形になる。
        var segments = SplitOutsideQuotes(pipeline)
                       .Select(s => s.Trim())
                       .Where(s => s.Length > 0)
                       .ToList();
        if (segments.Count == 0)
            return new ParsedSrcPipeline();

        string? sourceElement = null;
        var properties = new Dictionary<string, string>();
        var capsFields = new Dictionary<string, string>();
        string? memoryFeature = null;
        bool hasCaps = false;
        var intermediateElements = new List<string>();
        bool sourceSeen = false;

        foreach (var seg in segments)
        {
            if (MediaTypeRegex().IsMatch(seg))
            {
                // caps セグメント。統合してフィールドを取り込む。
                hasCaps = true;
                if (memoryFeature is null)
                {
                    int comma = seg.IndexOf(',');
                    string head = comma >= 0 ? seg[..comma].Trim() : seg.Trim();
                    var memMatch = MemRegex().Match(head);
                    if (memMatch.Success)
                        memoryFeature = memMatch.Groups[1].Value.Trim();
                }
                foreach (Match m in KeyValueRegex().Matches(seg))
                    capsFields[m.Groups["key"].Value] = Unquote(m.Groups["value"].Value);
            }
            else
            {
                // 要素セグメント。先頭トークン=要素名。
                var firstToken = seg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (firstToken is null)
                    continue;

                if (!sourceSeen)
                {
                    sourceSeen = true;
                    sourceElement = firstToken;
                    foreach (Match m in KeyValueRegex().Matches(seg))
                        properties[m.Groups["key"].Value] = Unquote(m.Groups["value"].Value);
                }
                else
                {
                    intermediateElements.Add(firstToken);
                }
            }
        }

        return new ParsedSrcPipeline
        {
            SourceElement = sourceElement,
            Properties = properties,
            CapsFields = capsFields,
            MemoryFeature = string.IsNullOrEmpty(memoryFeature) ? null : memoryFeature,
            HasCaps = hasCaps,
            IntermediateElements = intermediateElements,
        };
    }

    /// <summary>
    /// カタログ定義と各値から SrcPipeline 文字列を生成する(ソース [プロパティ] ! caps)。
    /// resolution フィールドは width/height に展開する。
    /// </summary>
    /// <param name="def">対象ソースのカタログ定義。</param>
    /// <param name="capsEnabled">caps を付与するか。false の場合はソース+プロパティのみ。</param>
    /// <param name="properties">有効化された要素プロパティの (名前, 値)。</param>
    /// <param name="capsValues">有効化された caps フィールドの 名前→値(resolution は "幅x高さ")。</param>
    public static string Assemble(
        SrcElementDef def,
        bool capsEnabled,
        IEnumerable<(string Name, string Value)> properties,
        IReadOnlyDictionary<string, string> capsValues)
    {
        var sb = new StringBuilder();
        sb.Append(def.ElementName);
        foreach (var (name, value) in properties)
        {
            if (string.IsNullOrEmpty(value))
                continue;
            sb.Append(' ').Append(name).Append('=').Append(QuoteIfNeeded(value));
        }

        if (capsEnabled)
        {
            bool anyField = def.CapsFields.Any(c => capsValues.ContainsKey(c.Name));
            // 出力すべきフィールドが無くても、メモリ機能がある場合は機能付き caps を出す
            if (anyField || def.MemoryFeature is not null)
            {
                sb.Append(" ! ").Append(CapsMediaType);
                if (def.MemoryFeature is not null)
                    sb.Append('(').Append(def.MemoryFeature).Append(')');

                foreach (var field in def.CapsFields)
                {
                    if (!capsValues.TryGetValue(field.Name, out string? value) || string.IsNullOrEmpty(value))
                        continue;

                    if (field.IsResolution)
                    {
                        var (w, h) = SplitResolution(value);
                        if (w is not null && h is not null)
                            sb.Append(", width=").Append(w).Append(", height=").Append(h);
                    }
                    else
                    {
                        // caps 値も要素プロパティと同じ規則で引用する。無引用のまま
                        // 区切り文字（空白・カンマ・'!'）を含む値を連結すると、以降の
                        // トークンの意味が変わってパイプラインの構造そのものが壊れる。
                        // 数値・分数（15/1）・列挙は引用対象にならないので、
                        // 通常の出力は従来と1バイトも変わらない。
                        sb.Append(", ").Append(field.Name).Append('=').Append(QuoteIfNeeded(value));
                    }
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>"幅x高さ" を (幅, 高さ) に分解する。解釈できない場合は (null, null)。</summary>
    public static (string? Width, string? Height) SplitResolution(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);
        var m = ResolutionRegex().Match(value.Trim());
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (null, null);
    }

    /// <summary>width/height から "幅x高さ" を組み立てる。両方揃わない場合は null。</summary>
    public static string? JoinResolution(string? width, string? height)
        => string.IsNullOrEmpty(width) || string.IsNullOrEmpty(height) ? null : $"{width}x{height}";

    /// <summary>
    /// '!' で要素/caps のセグメントに分割する。二重引用符の中の '!' は区切りにせず、
    /// 引用内の <c>\"</c> / <c>\\</c> エスケープは1単位として読み飛ばす
    /// （<see cref="QuoteIfNeeded"/> が生成する形と対）。
    /// </summary>
    private static List<string> SplitOutsideQuotes(string pipeline)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < pipeline.Length; i++)
        {
            char c = pipeline[i];
            if (inQuotes && c == '\\' && i + 1 < pipeline.Length)
            {
                current.Append(c).Append(pipeline[++i]);
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == '!' && !inQuotes)
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        segments.Add(current.ToString());
        return segments;
    }

    /// <summary>
    /// 引用が必要な文字（区切り・引用・エスケープ文字そのもの）。
    /// タブ・改行も含める ── <see cref="KeyValueRegex"/> の無引用値は <c>\s</c> で
    /// 終わるので、空白類のうち ' ' だけを引用対象にするとラウンドトリップが崩れる。
    /// </summary>
    private static readonly char[] QuoteTriggerChars = [' ', '\t', '\r', '\n', ',', '!', '"', '\\'];

    // 値に区切りとして解釈されうる文字を含む場合は二重引用符で括り、内部の '"' と '\' は
    // '\' でエスケープする（gst_parse_launch は引用内のエスケープを解釈する）。
    // 「先頭が '"' なら引用済み」というヒューリスティックは持たない ── 打ちかけの引用や
    // 値自体に '"' を含むケースで不平衡な引用を生成し、以降のトークンが引用へ吸い込まれて
    // 値がパイプライン構造として解釈されてしまう。
    private static string QuoteIfNeeded(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (value.IndexOfAny(QuoteTriggerChars) < 0)
            return value;
        // GStreamer の caps のリスト `{ NV12, I420 }` とレンジ `[ 1, 30 ]` は引用しない
        // ── 引用すると列挙ではなく1個の文字列値になり、ネゴシエーションが落ちる。
        if ((value[0] == '{' && value[^1] == '}') || (value[0] == '[' && value[^1] == ']'))
            return value;

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            if (c is '"' or '\\')
                sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    // 先頭末尾の二重引用符を外し、引用内の '\' エスケープを復元する
    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            return value;
        string inner = value[1..^1];
        if (!inner.Contains('\\'))
            return inner;

        var sb = new StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
                i++;
            sb.Append(inner[i]);
        }
        return sb.ToString();
    }
    [GeneratedRegex(@"\(([^)]*)\)")]
    private static partial Regex MemRegex();
    [GeneratedRegex(@"^(\d+)\s*[xX×]\s*(\d+)$")]
    private static partial Regex ResolutionRegex();
}
