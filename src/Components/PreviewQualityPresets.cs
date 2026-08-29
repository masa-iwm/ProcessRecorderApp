using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace ProcessRecorderApp.Components;

/// <summary>
/// ライブ画質プリセット 1 件。<b>高さ・fps・ビットレートだけを持ち、幅は持たない</b>
/// ── 幅はソースの縦横比から導くので、プリセットに書くと 16:9 以外のソースで歪む。
/// </summary>
/// <param name="Id">API とクライアントが使う識別子（序数比較）。</param>
/// <param name="Label">画面に出す表示名（英語リテラル。Web UI は英語のみ）。</param>
/// <param name="Height">目標の高さ(px)。ソースがこれより低ければソースに合わせて縮む。</param>
/// <param name="Fps">目標のフレームレート(fps)。ソースがこれより低ければソースに合わせる。</param>
/// <param name="BitrateKbps">エンコーダーへ指示するビットレート(kbit/sec)。</param>
public sealed record PreviewQualityPreset(string Id, string Label, int Height, int Fps, int BitrateKbps);

/// <summary>配信 1 本ぶんの解決済みの 4 値。</summary>
/// <param name="Width">幅(px)。</param>
/// <param name="Height">高さ(px)。</param>
/// <param name="Fps">フレームレート(fps)。</param>
/// <param name="BitrateKbps">ビットレート(kbit/sec)。</param>
public readonly record struct PreviewQuality(int Width, int Height, int Fps, int BitrateKbps);

/// <summary>
/// プレビュー枝に届いているソースの形。<b>0 は「読めていない」</b>
/// （<see cref="Width"/> か <see cref="Height"/> が 0 以下なら大きさが未知、
/// <see cref="Fps"/> が 0 以下ならフレームレートが未知）。
/// </summary>
/// <param name="Width">ソースの幅(px)。</param>
/// <param name="Height">ソースの高さ(px)。</param>
/// <param name="Fps">ソースのフレームレート(fps)。</param>
public readonly record struct PreviewSourceInfo(int Width, int Height, int Fps);

/// <summary>選べる画質 1 件（プリセットをソースに対して解決したもの）。</summary>
/// <param name="Id">識別子。<c>custom</c> は設定 4 値そのまま。</param>
/// <param name="Label">表示名。</param>
/// <param name="Quality">この項目を選んだときに配信される 4 値。</param>
public sealed record PreviewQualityOption(string Id, string Label, PreviewQuality Quality);

/// <summary>
/// 1 レコーダーのライブ画質の「いまの姿」。
/// <b><see cref="Current"/> は指示であり、<see cref="EffectiveId"/> は実際に動いているもの</b>
/// ── 指示を変えても mux を組み直すまで（数秒）は一致しない。
/// </summary>
/// <param name="Current">選ばれている id（override が無ければ <see cref="PreviewQualityPresets.Custom"/>）。</param>
/// <param name="Source">最後に読めたソースの形。読めていなければ <see langword="null"/>。</param>
/// <param name="EffectiveId">いま動いている mux の id。mux が無ければ <see langword="null"/>。</param>
/// <param name="Effective">いま動いている mux の 4 値。mux が無ければ <see langword="null"/>。</param>
/// <param name="Qualities">選べる項目（末尾は必ず <see cref="PreviewQualityPresets.Custom"/>）。</param>
public sealed record PreviewQualityState(
    string Current,
    PreviewSourceInfo? Source,
    string? EffectiveId,
    PreviewQuality? Effective,
    IReadOnlyList<PreviewQualityOption> Qualities);

/// <summary>
/// ライブ画質プリセットの正本（表と解決規則）。<b>純関数だけを置く</b>
/// ── 配信エンジンも API も画面も、同じ表・同じ算術を通す。
///
/// <para>
/// <b>プリセットの意味は「ソースに対する相対」である。</b> 絶対の 4 値として持つと、
/// ウィンドウ ソースのように実行中に解像度が変わる相手で縦横比が壊れる
/// ── 解決はソースの caps を読んだ側（配信エンジン）が毎回行う。
/// </para>
/// </summary>
public static class PreviewQualityPresets
{
    /// <summary>プリセットを使わない（レコーダー設定の 4 値をそのまま配信する）ことを表す id。</summary>
    public const string Custom = "custom";

    /// <summary>解決後の幅の下限(px)。<c>EventRecorderSettings.MinPreviewWidth</c> と一致させる。</summary>
    public const int MinWidth = 160;

    /// <summary>解決後の幅の上限(px)。<c>EventRecorderSettings.MaxPreviewWidth</c> と一致させる。</summary>
    public const int MaxWidth = 3840;

    /// <summary>解決後の高さの下限(px)。<c>EventRecorderSettings.MinPreviewHeight</c> と一致させる。</summary>
    public const int MinHeight = 120;

    /// <summary>解決後の高さの上限(px)。<c>EventRecorderSettings.MaxPreviewHeight</c> と一致させる。</summary>
    public const int MaxHeight = 2160;

    /// <summary>解決後の fps の下限。<c>EventRecorderSettings.MinPreviewFps</c> と一致させる。</summary>
    public const int MinFps = 1;

    /// <summary>解決後の fps の上限。<c>EventRecorderSettings.MaxPreviewFps</c> と一致させる。</summary>
    public const int MaxFps = 60;

    /// <summary>
    /// プリセットの一覧。<b>この順序がそのまま API とメニューの並びになる</b>
    /// （高い方が先）。<see cref="Offered"/> が「1 つも残らない」ときに使う最小は
    /// 高さが最も小さいものである。
    /// </summary>
    public static IReadOnlyList<PreviewQualityPreset> All { get; } = new[]
    {
        new PreviewQualityPreset("1080p", "1080p", 1080, 30, 6000),
        new PreviewQualityPreset("720p", "720p", 720, 30, 3000),
        new PreviewQualityPreset("480p", "480p", 480, 30, 1500),
        new PreviewQualityPreset("360p", "360p", 360, 15, 800),
    };

    /// <summary><paramref name="id"/>（序数完全一致）のプリセットを探す。</summary>
    public static bool TryFind(string id, [NotNullWhen(true)] out PreviewQualityPreset? preset)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
            {
                preset = candidate;
                return true;
            }
        }

        preset = null;
        return false;
    }

    /// <summary>
    /// <see cref="All"/> の id か <see cref="Custom"/> であること。
    /// <b>API はこれを通ったものだけを配信側へ渡す</b>（供給側は検査済みを前提にする）。
    /// </summary>
    public static bool IsValidId(string? id)
        => id is not null && (string.Equals(id, Custom, StringComparison.Ordinal) || TryFind(id, out _));

    /// <summary>
    /// <paramref name="preset"/> を <paramref name="source"/> に対して解決する。
    ///
    /// <para>
    /// 高さはソースを超えない（ソースが偶数でなければ 1 落とす）。幅はその高さと
    /// ソースの縦横比（未知なら 16:9）から最近接の偶数へ、最後に上下限で丸める。
    /// fps はソースを超えない。<b>ビットレートは縮めない</b>
    /// ── 小さく符号化した方が余裕が出るだけで、上限として害が無い。
    /// </para>
    /// </summary>
    public static PreviewQuality Resolve(PreviewQualityPreset preset, PreviewSourceInfo? source)
    {
        var known = Known(source);

        int height = known is { } size ? Math.Min(preset.Height, EvenDown(size.Height)) : preset.Height;
        double aspect = known is { } forAspect ? (double)forAspect.Width / forAspect.Height : 16.0 / 9.0;
        int width = EvenNearest(height * aspect);

        width = Math.Clamp(width, MinWidth, MaxWidth);
        height = Math.Clamp(height, MinHeight, MaxHeight);

        int fps = source is { Fps: > 0 } withFps ? Math.Min(preset.Fps, withFps.Fps) : preset.Fps;
        fps = Math.Clamp(fps, MinFps, MaxFps);

        return new PreviewQuality(width, height, fps, preset.BitrateKbps);
    }

    /// <summary>
    /// <paramref name="source"/> で意味のあるプリセットだけを <see cref="All"/> の順で返す。
    /// <b>ソースより高いプリセットは出さない</b>（拡大しても情報は増えず帯域だけ増える）。
    /// 1 つも残らなければ<b>最小の 1 つだけ</b>を返す ── 選択肢が空のメニューを出さない。
    /// </summary>
    public static IReadOnlyList<PreviewQualityPreset> Offered(PreviewSourceInfo? source)
    {
        if (Known(source) is not { } size)
            return All;

        int cap = EvenDown(size.Height);
        var offered = new List<PreviewQualityPreset>(All.Count);
        foreach (var preset in All)
        {
            if (preset.Height <= cap)
                offered.Add(preset);
        }

        if (offered.Count == 0)
            offered.Add(Smallest());

        return offered;
    }

    /// <summary>
    /// API とクライアントが読む姿を 1 つ組む。<paramref name="presetId"/> が
    /// <see langword="null"/> なら <see cref="PreviewQualityState.Current"/> は
    /// <see cref="Custom"/> になる。
    /// </summary>
    /// <param name="presetId">選ばれている override の id（無ければ null）。</param>
    /// <param name="source">最後に読めたソースの形（読めていなければ null）。</param>
    /// <param name="custom">レコーダー設定の 4 値（<see cref="Custom"/> はこれをクランプしない）。</param>
    /// <param name="effectiveId">いま動いている mux の id（無ければ null）。</param>
    /// <param name="effective">いま動いている mux の 4 値（無ければ null）。</param>
    public static PreviewQualityState BuildState(
        string? presetId,
        PreviewSourceInfo? source,
        PreviewQuality custom,
        string? effectiveId,
        PreviewQuality? effective)
    {
        var known = Known(source);

        var offered = Offered(known);
        var qualities = new List<PreviewQualityOption>(offered.Count + 1);
        foreach (var preset in offered)
            qualities.Add(new PreviewQualityOption(preset.Id, preset.Label, Resolve(preset, known)));

        // **末尾は必ず custom。** 「設定どおりに配る」に戻す道が常に要る。
        qualities.Add(new PreviewQualityOption(Custom, "Custom", custom));

        return new PreviewQualityState(presetId ?? Custom, known, effectiveId, effective, qualities);
    }

    /// <summary>大きさが読めているものだけを返す（読めていなければ null）。</summary>
    private static PreviewSourceInfo? Known(PreviewSourceInfo? source)
        => source is { Width: > 0, Height: > 0 } known ? known : null;

    /// <summary>高さが最も小さいプリセット（<see cref="All"/> は空にならない）。</summary>
    private static PreviewQualityPreset Smallest()
    {
        var smallest = All[0];
        foreach (var preset in All)
        {
            if (preset.Height < smallest.Height)
                smallest = preset;
        }

        return smallest;
    }

    /// <summary>奇数なら 1 落として偶数にする（負にはしない）。</summary>
    private static int EvenDown(int value) => value <= 0 ? 0 : value - (value % 2);

    /// <summary>最も近い偶数（0.5 は大きい方へ）。</summary>
    private static int EvenNearest(double value)
        => (int)Math.Round(value / 2.0, MidpointRounding.AwayFromZero) * 2;
}
