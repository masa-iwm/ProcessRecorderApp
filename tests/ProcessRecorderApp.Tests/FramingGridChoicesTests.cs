using System;
using System.Linq;
using ProcessRecorderApp.Components;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 構図補助線の選択肢カタログ（<see cref="FramingGridChoices"/>）。
///
/// <para>
/// 設定画面のコンボボックス・右クリックメニュー・全画面中の上下キーが同じ一覧を使うので、
/// <b>ここが列挙型と食い違うと 3 面が同時に壊れる</b>。特に「列挙型に値を足して
/// <c>All</c> へ足し忘れる」は、画面上は選択肢が 1 つ減るだけで気付きにくい。
/// </para>
/// </summary>
public class FramingGridChoicesTests
{
    [Fact]
    public void EveryKindHasAnEntry()
    {
        var listed = FramingGridChoices.All.Select(c => c.Kind).ToArray();

        Assert.Equal(Enum.GetValues<FramingGridKind>().OrderBy(k => k), listed.OrderBy(k => k));
    }

    [Fact]
    public void EntriesAreUnique()
    {
        Assert.Equal(FramingGridChoices.All.Count, FramingGridChoices.All.Select(c => c.Kind).Distinct().Count());
        Assert.Equal(FramingGridChoices.All.Count, FramingGridChoices.All.Select(c => c.ResourceKey).Distinct().Count());
    }

    /// <summary>
    /// 資源キーは L4 が拾える形（<c>Resources/</c> 配下のリテラル）でなければならない。
    /// </summary>
    [Fact]
    public void ResourceKeysAreQualified()
    {
        foreach (var choice in FramingGridChoices.All)
            Assert.StartsWith("Resources/FramingGrid_", choice.ResourceKey, StringComparison.Ordinal);
    }

    [Fact]
    public void NoneIsFirstSoTheCycleStartsFromOff()
    {
        Assert.Equal(FramingGridKind.None, FramingGridChoices.All[0].Kind);
    }

    [Fact]
    public void NextAdvancesInCatalogOrder()
    {
        Assert.Equal(FramingGridKind.Thirds, FramingGridChoices.Next(FramingGridKind.None, 1));
        Assert.Equal(FramingGridKind.GoldenRatio, FramingGridChoices.Next(FramingGridKind.Thirds, 1));
    }

    [Fact]
    public void NextWrapsForward()
    {
        var last = FramingGridChoices.All[^1].Kind;

        Assert.Equal(FramingGridChoices.All[0].Kind, FramingGridChoices.Next(last, 1));
    }

    [Fact]
    public void NextWrapsBackward()
    {
        var last = FramingGridChoices.All[^1].Kind;

        Assert.Equal(last, FramingGridChoices.Next(FramingGridChoices.All[0].Kind, -1));
    }

    [Fact]
    public void NextWithZeroKeepsTheKind()
    {
        Assert.Equal(FramingGridKind.Crosshair, FramingGridChoices.Next(FramingGridKind.Crosshair, 0));
    }

    /// <summary>
    /// 一覧に無い値でも例外にしない ── キーを押しただけで落ちる方が有害。
    /// </summary>
    [Fact]
    public void NextTreatsAnUnknownKindAsTheFirstEntry()
    {
        var unknown = (FramingGridKind)999;

        Assert.Equal(FramingGridChoices.All[1].Kind, FramingGridChoices.Next(unknown, 1));
    }

    [Fact]
    public void ResourceKeyForReturnsNullForAnUnknownKind()
    {
        Assert.Equal("Resources/FramingGrid_Square", FramingGridChoices.ResourceKeyFor(FramingGridKind.Square));
        Assert.Null(FramingGridChoices.ResourceKeyFor((FramingGridKind)999));
    }
}
