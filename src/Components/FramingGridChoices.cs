using System;
using System.Collections.Generic;

namespace ProcessRecorderApp.Components;

/// <summary>
/// <see cref="FramingGridKind"/> の 1 件。表示するための資源キーと対にしてある。
/// </summary>
/// <param name="Kind">種類。</param>
/// <param name="ResourceKey">表示名の資源キー（アプリ側の resw）。</param>
public readonly record struct FramingGridChoice(FramingGridKind Kind, string ResourceKey);

/// <summary>
/// 構図補助線の選択肢の正本。設定画面のコンボボックスと、プレビューの右クリックメニューと、
/// 全画面中の上下キーが<b>同じ一覧・同じ並び</b>を使うための器。
///
/// <para>
/// <b>資源キーはここにリテラルで持つ。</b> L4（<c>LocalizationTests</c>）は
/// ソース中の文字列リテラルを資源キーの参照として拾うので、
/// 名前を組み立てて渡すと「参照されていないキー」として resw 側が落ちる。
/// </para>
/// <para>
/// <b>ここに WinUI もローカライズ実装も持ち込まない</b>（<c>Components</c> は L1 から直接
/// 呼ばれる）。文字列の解決は呼び出し側が <c>Localization.GetString</c> で行う。
/// </para>
/// </summary>
public static class FramingGridChoices
{
    /// <summary>
    /// 選択肢の並び。<b>この順序がそのまま上下キーの巡回順になる</b>ので、
    /// 並べ替えると操作感が変わる（設定ファイルの値は名前なので影響を受けない）。
    /// </summary>
    public static IReadOnlyList<FramingGridChoice> All { get; } = new[]
    {
        new FramingGridChoice(FramingGridKind.None, "Resources/FramingGrid_None"),
        new FramingGridChoice(FramingGridKind.Thirds, "Resources/FramingGrid_Thirds"),
        new FramingGridChoice(FramingGridKind.GoldenRatio, "Resources/FramingGrid_GoldenRatio"),
        new FramingGridChoice(FramingGridKind.Crosshair, "Resources/FramingGrid_Crosshair"),
        new FramingGridChoice(FramingGridKind.Square, "Resources/FramingGrid_Square"),
    };

    /// <summary>
    /// <paramref name="kind"/> から <paramref name="offset"/> 個ぶん進めた種類を返す（端は巻き取る）。
    ///
    /// <para>
    /// 一覧に無い種類（列挙型に値を足して <see cref="All"/> へ足し忘れた場合）は
    /// <b>先頭から数え直す</b> ── 例外にすると、キーを押しただけでアプリが落ちる。
    /// 足し忘れそのものは L1 が落とす。
    /// </para>
    /// </summary>
    public static FramingGridKind Next(FramingGridKind kind, int offset)
    {
        int count = All.Count;
        int current = 0;
        for (int i = 0; i < count; i++)
        {
            if (All[i].Kind == kind)
            {
                current = i;
                break;
            }
        }

        // 剰余は負になりうるので、足してから取り直す。
        int next = (((current + offset) % count) + count) % count;
        return All[next].Kind;
    }

    /// <summary>
    /// <paramref name="kind"/> の表示名の資源キー。一覧に無ければ <see langword="null"/>。
    /// </summary>
    public static string? ResourceKeyFor(FramingGridKind kind)
    {
        foreach (var choice in All)
        {
            if (choice.Kind == kind)
                return choice.ResourceKey;
        }
        return null;
    }
}
