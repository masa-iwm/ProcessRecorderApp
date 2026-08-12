using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ProcessRecorderApp.GStreamer;

/// <summary>カメラ制御の系統（Windows のインターフェイスが 2 つに分かれている）。</summary>
public enum CameraControlDomain
{
    /// <summary><c>IAMVideoProcAmp</c>（明るさ・コントラスト・ホワイトバランス等）。</summary>
    VideoProcAmp,

    /// <summary><c>IAMCameraControl</c>（フォーカス・露出・ズーム等）。</summary>
    CameraControl,
}

/// <summary>
/// 扱えるカメラ制御項目 1 つ分の定義。
/// </summary>
/// <param name="Key">設定文字列でのキー（安定した識別子。表示名ではない）。</param>
/// <param name="Domain">どちらのインターフェイスで設定するか。</param>
/// <param name="PropertyId">そのインターフェイスでのプロパティ番号（DirectShow の定義値）。</param>
/// <param name="ResourceKey">
/// 表示名の完全修飾リソースキー。
/// <b>補間ではなくリテラルで持つ</b> ── L4 の <c>SourceReferences</c> は
/// <c>"Resources/..."</c> の形をした文字列リテラルを拾って「参照されているキー」を数えるので、
/// <c>$"Resources/CameraItem_{key}"</c> のように組み立てると
/// <b>17 個のキーが「どこからも参照されていない」と報告され続ける</b>
/// （削除候補の検出が形骸化する）。解決は表示側で行う（この型は resw を読まない）。
/// </param>
public sealed record CameraControlDef(string Key, CameraControlDomain Domain, int PropertyId, string ResourceKey);

/// <summary>
/// 扱えるカメラ制御項目の一覧。
///
/// <para>
/// <b>番号は DirectShow の <c>VideoProcAmpProperty</c> / <c>CameraControlProperty</c> の定義値。</b>
/// Media Foundation のデバイスソースはこの 2 つのインターフェイスを（直接か
/// <c>IMFGetService</c> 経由で）出すので、GStreamer の <c>mfvideosrc</c> が
/// カメラ制御のプロパティを 1 つも持たない（実測）現状ではこれが唯一の道である。
/// </para>
/// <para>
/// 実際にどの項目が使えるかは<b>ドライバ次第</b>で、<c>GetRange</c> が失敗した項目は
/// UI に出さない。ここに並べてあるのは「聞いてみる候補」にすぎない。
/// </para>
/// </summary>
public static class CameraControlCatalog
{
    /// <summary>自動（auto）を表すキーの接尾辞。</summary>
    public const string AutoSuffix = "-auto";

    /// <summary>候補の一覧（設定文字列へ書き出すときの順序でもある）。</summary>
    public static IReadOnlyList<CameraControlDef> All { get; } =
        // 具象型へ明示的にする（AOT/トリミングのため。CsWinRT1032）
        (CameraControlDef[])
    [
        // IAMVideoProcAmp
        new("brightness", CameraControlDomain.VideoProcAmp, 0, "Resources/CameraItem_brightness"),
        new("contrast", CameraControlDomain.VideoProcAmp, 1, "Resources/CameraItem_contrast"),
        new("hue", CameraControlDomain.VideoProcAmp, 2, "Resources/CameraItem_hue"),
        new("saturation", CameraControlDomain.VideoProcAmp, 3, "Resources/CameraItem_saturation"),
        new("sharpness", CameraControlDomain.VideoProcAmp, 4, "Resources/CameraItem_sharpness"),
        new("gamma", CameraControlDomain.VideoProcAmp, 5, "Resources/CameraItem_gamma"),
        new("color-enable", CameraControlDomain.VideoProcAmp, 6, "Resources/CameraItem_color-enable"),
        new("white-balance", CameraControlDomain.VideoProcAmp, 7, "Resources/CameraItem_white-balance"),
        new("backlight-compensation", CameraControlDomain.VideoProcAmp, 8, "Resources/CameraItem_backlight-compensation"),
        new("gain", CameraControlDomain.VideoProcAmp, 9, "Resources/CameraItem_gain"),
        // IAMCameraControl
        new("pan", CameraControlDomain.CameraControl, 0, "Resources/CameraItem_pan"),
        new("tilt", CameraControlDomain.CameraControl, 1, "Resources/CameraItem_tilt"),
        new("roll", CameraControlDomain.CameraControl, 2, "Resources/CameraItem_roll"),
        new("zoom", CameraControlDomain.CameraControl, 3, "Resources/CameraItem_zoom"),
        new("exposure", CameraControlDomain.CameraControl, 4, "Resources/CameraItem_exposure"),
        new("iris", CameraControlDomain.CameraControl, 5, "Resources/CameraItem_iris"),
        new("focus", CameraControlDomain.CameraControl, 6, "Resources/CameraItem_focus"),
    ];

    /// <summary>キーから定義を引く（未知なら null）。比較は序数・大文字小文字を区別しない。</summary>
    public static CameraControlDef? Find(string key)
        => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>1 項目の設定値。値と自動フラグは独立に指定できる（片方だけでもよい）。</summary>
/// <param name="Value">手動値。指定しないなら null。</param>
/// <param name="Auto">自動にするか。指定しないなら null（ドライバの現状に任せる）。</param>
public readonly record struct CameraControlValue(int? Value, bool? Auto)
{
    /// <summary>何も指定していない（書き出す意味が無い）か。</summary>
    public bool IsEmpty => Value is null && Auto is null;
}

/// <summary>
/// レコーダー設定 <c>CameraControls</c> の文字列書式。
///
/// <para>
/// 形は <c>brightness=128;focus=30;focus-auto=false</c>。
/// <b>17 項目をそれぞれ設定プロパティにしない</b>のは、レコーダー設定を 1 つ増やすたびに
/// 手書きのミラーが 4 箇所（<c>RecorderSettingsMirrorTests</c> が縛る）＋ E2E の
/// <c>RecorderSpec</c> に要るため ── 17 個なら 85 箇所になる。
/// <c>SrcPipeline</c> と同じ「そのまま持つ文字列」にして、編集は専用ダイアログに任せる。
/// </para>
/// <para>
/// <b>解析も生成もここ（純粋関数）に閉じる。</b> COM には触れない ──
/// L1 は GStreamer もカメラも初期化できないので、テストできるのはこの層だけである。
/// </para>
/// <para>
/// <b>知らないキーは捨てずに持ち越す。</b> 新しい版で増えた項目を古い版で開いて保存し直しても
/// 消えないようにするため（<c>AppSettings.ExtensionData</c> と同じ判断）。
/// </para>
/// </summary>
public sealed class CameraControlSettings
{
    private readonly Dictionary<string, CameraControlValue> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析できなかった／知らないキーの原文（順序を保って持ち越す）。</summary>
    private readonly List<string> _passthrough = [];

    /// <summary>1 項目も指定されていないか（このとき適用は何もしない）。</summary>
    public bool IsEmpty => _values.Count == 0;

    /// <summary>指定のあるキー（カタログの順）。</summary>
    public IEnumerable<CameraControlDef> SpecifiedItems
        => CameraControlCatalog.All.Where(d => _values.ContainsKey(d.Key));

    /// <summary>指定値を取り出す（未指定なら既定の空値）。</summary>
    public CameraControlValue Get(string key)
        => _values.TryGetValue(key, out var value) ? value : default;

    /// <summary>指定値を設定する。空の値を渡すとその項目の指定を消す。</summary>
    public void Set(string key, CameraControlValue value)
    {
        if (value.IsEmpty)
            _values.Remove(key);
        else
            _values[key] = value;
    }

    /// <summary>
    /// 設定文字列を解析する。区切りは <c>;</c>、値は <c>キー=値</c>。
    /// 空白は無視する。読めない断片は捨てずに <see cref="Format"/> で書き戻す。
    /// </summary>
    public static CameraControlSettings Parse(string? text)
    {
        var result = new CameraControlSettings();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (string raw in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = raw.Trim();
            if (entry.Length == 0)
                continue;

            int separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                result._passthrough.Add(entry);
                continue;
            }

            string key = entry[..separator].Trim();
            string value = entry[(separator + 1)..].Trim();

            bool isAuto = key.EndsWith(CameraControlCatalog.AutoSuffix, StringComparison.OrdinalIgnoreCase);
            string baseKey = isAuto ? key[..^CameraControlCatalog.AutoSuffix.Length] : key;

            if (CameraControlCatalog.Find(baseKey) is not { } def)
            {
                // 知らないキーは持ち越す（将来の版で増えた項目を消さない）
                result._passthrough.Add(entry);
                continue;
            }

            var current = result.Get(def.Key);
            if (isAuto)
            {
                if (bool.TryParse(value, out bool auto))
                    result._values[def.Key] = current with { Auto = auto };
                else
                    result._passthrough.Add(entry);   // 読めない値も捨てない
            }
            else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                result._values[def.Key] = current with { Value = number };
            }
            else
            {
                result._passthrough.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// 設定文字列へ書き出す（カタログの順 → 持ち越した断片の順）。
    /// 1 項目も無ければ空文字を返す。
    /// </summary>
    public string Format()
    {
        var parts = new List<string>();

        foreach (var def in CameraControlCatalog.All)
        {
            if (!_values.TryGetValue(def.Key, out var value))
                continue;
            if (value.Value is { } number)
                parts.Add($"{def.Key}={number.ToString(CultureInfo.InvariantCulture)}");
            if (value.Auto is { } auto)
                parts.Add($"{def.Key}{CameraControlCatalog.AutoSuffix}={(auto ? "true" : "false")}");
        }

        parts.AddRange(_passthrough);
        return string.Join(";", parts);
    }

    /// <summary>
    /// 既存の設定文字列へ、編集した項目だけを重ねて書き戻す。
    ///
    /// <para>
    /// <b>編集 UI はこれを使うこと（ゼロから組み立てない）。</b> 新しい
    /// <see cref="CameraControlSettings"/> から作ると、<see cref="Parse"/> が意図的に
    /// 持ち越している<b>未知キー</b>と、<b>そのカメラが申告しない項目の指定</b>が黙って消える。
    /// これは <c>UnknownOrUnreadableEntries_SurviveTheRoundTrip</c> が縛っている不変条件
    /// そのものだが、その表明は <see cref="Parse"/>/<see cref="Format"/> しか通らないので、
    /// <b>合成をここへ置かないと UI 経路の取りこぼしを L1 から縛れない</b>
    /// （実際にダイアログが 1 度これを破っていた）。
    /// </para>
    /// </summary>
    /// <param name="originalText">開いたときの設定文字列。</param>
    /// <param name="updates">編集した項目（キー・値・自動。自動を扱えない項目は null）。</param>
    public static string Merge(
        string? originalText, IEnumerable<(string Key, int Value, bool? Auto)> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var settings = Parse(originalText);
        foreach (var (key, value, auto) in updates)
            settings.Set(key, new CameraControlValue(value, auto));
        return settings.Format();
    }

    /// <inheritdoc/>
    public override string ToString() => Format();
}
