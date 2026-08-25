using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>プレビューの 2 本のパイプライン（fMP4 の mux と DASH の第 2 パイプライン）が使う
/// GStreamer 要素と、同梱ランタイムの内容を突き合わせる。</b>
/// <see cref="ContinuousRuntimeDependencyTests"/> と同型で、狙う事故も同じ ──
/// 開発機も CI もフル構成の GStreamer なので、同梱に無い要素を書いても<b>両方緑になり</b>、
/// 壊れるのは同梱配布の実行時だけである。
///
/// <para>
/// <b>対応表に無い要素が文字列に現れたら赤にする。</b> パイプラインへ要素を足すたびに
/// 台帳の確認を強制するのが目的で、「知らない要素は素通り」にすると検査が消える。
/// </para>
/// </summary>
public sealed class PreviewRuntimeDependencyTests
{
    /// <summary>要素名 → それを提供するプラグイン（MinGW 命名）。</summary>
    private static readonly Dictionary<string, string> PluginOf = new(StringComparer.Ordinal)
    {
        ["appsrc"] = "libgstapp.dll",
        ["appsink"] = "libgstapp.dll",
        ["h264parse"] = "libgstvideoparsersbad.dll",
        ["mp4mux"] = "libgstisomp4.dll",

        // DASH の第 2 パイプラインだけが使うもの。
        ["videorate"] = "libgstvideorate.dll",
        ["videoscale"] = "libgstvideoconvertscale.dll",
        ["videoconvert"] = "libgstvideoconvertscale.dll",
        ["capsfilter"] = "libgstcoreelements.dll",
        ["mfh264enc"] = "libgstmediafoundation.dll",
    };

    /// <summary>
    /// DASH の第 2 パイプラインで実際に使う文字列。<b>エンコーダーは <c>mfh264enc</c> で
    /// 代表させる</b> ── 候補は実機の顔ぶれで変わるが、同梱に在ることを確かめられるのは
    /// 台帳に載っているものだけである。
    /// </summary>
    private static string DashPipeline
        => DashPreviewStream.BuildPipeline(1280, 720, 15, "mfh264enc bitrate=2000 gop-size=15 low-latency=true");

    /// <summary>
    /// <c>!</c> で切った各段の要素名。
    ///
    /// <para>
    /// <b>caps だけの段は <c>capsfilter</c> に読み替える。</b> <c>video/x-raw,...</c> の
    /// 先頭トークンをそのまま取ると <c>video</c> という要素名になり、対応表に無いので
    /// 「知らない要素がある」と報告されるか、素通りして<b>検査が消える</b>。
    /// </para>
    /// </summary>
    private static string[] Elements(string pipeline)
    {
        string[] names = [.. pipeline
            .Split('!', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(stage => stage.Contains('/', StringComparison.Ordinal)
                ? "capsfilter"
                : Regex.Match(stage, @"^[A-Za-z0-9_]+").Value)
            .Where(name => 0 < name.Length)];

        // 空振りで緑にしない（分け方を変えたならこの検査も一緒に直すこと）。
        Assert.True(3 <= names.Length,
            $"パイプラインから取り出せた要素が {names.Length} 件しかない: " + pipeline);
        return names;
    }

    /// <summary>検査対象の 2 本。</summary>
    private static string[] Pipelines() => [LivePreviewStream.PreviewMuxPipeline, DashPipeline];

    /// <summary>2 本に現れる要素をまとめたもの。</summary>
    private static string[] AllElements()
        => [.. Pipelines().SelectMany(Elements).Distinct(StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal)];

    /// <summary>
    /// 同梱ランタイムの台帳（形態ごとに1つ）。<b>MinGW 版と MSVC 版の両方を見る</b>
    /// ── MSVC 版は同じプラグインを <c>lib</c> 接頭辞なしで配る。
    /// </summary>
    private static string[] Ledgers()
    {
        string[] paths = [.. Directory.EnumerateFiles(
            RepositoryFiles.At("licenses", "third-party"), "COMPONENTS*.tsv")];
        Assert.NotEmpty(paths);
        return paths;
    }

    private static string[] BundledPlugins(string ledger)
    {
        string[] paths = [.. File.ReadAllLines(ledger)
            .Where(l => !l.StartsWith('#') && 0 < l.Trim().Length)
            .Skip(1)
            .Select(l => l.Split('\t')[0])
            .Where(p => p.StartsWith("lib/gstreamer-1.0/", StringComparison.Ordinal))];

        Assert.NotEmpty(paths);
        return paths;
    }

    /// <summary>引数は MinGW 命名。MSVC 命名（<c>gstX.dll</c>）はここで導く。</summary>
    private static string[] LedgersMissing(string mingwPluginFileName)
    {
        string msvcName = mingwPluginFileName.StartsWith("lib", StringComparison.Ordinal)
            ? mingwPluginFileName[3..]
            : mingwPluginFileName;

        return [.. Ledgers()
            .Where(ledger => !BundledPlugins(ledger).Any(p =>
                p.EndsWith("/" + mingwPluginFileName, StringComparison.OrdinalIgnoreCase)
                || p.EndsWith("/" + msvcName, StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFileName)!];
    }

    /// <summary>
    /// パイプラインに現れる要素はすべて対応表に載っていること
    /// （＝台帳の確認をすり抜けた要素が無いこと）。
    /// </summary>
    [Fact]
    public void EveryElementInThePreviewPipelineIsMappedToAPlugin()
    {
        string[] unknown = [.. AllElements().Where(e => !PluginOf.ContainsKey(e))];

        Assert.True(unknown.Length == 0,
            $"プレビューのパイプラインに対応表の無い要素がある: {string.Join(" / ", unknown)}。"
            + "PluginOf へ足し、同梱の台帳（licenses/third-party/COMPONENTS*.tsv）に"
            + "そのプラグインが在ることを確かめること。");
    }

    /// <summary>
    /// 使う要素のプラグインが<b>どの形態の台帳にも</b>在ること。
    /// ここが崩れると、ライブプレビューは同梱配布で一切動かない。
    /// </summary>
    [Fact]
    public void EveryElementThePreviewMuxUses_IsBundled()
    {
        var violations = new List<string>();

        foreach (string element in AllElements())
        {
            if (!PluginOf.TryGetValue(element, out string? plugin))
                continue;   // 対応表の欠落は上のテストが報告する

            string[] missing = LedgersMissing(plugin);
            if (0 < missing.Length)
                violations.Add($"{element} ({plugin}) が {string.Join(" / ", missing)} に無い");
        }

        Assert.True(violations.Count == 0,
            "プレビューのパイプラインが同梱配布で動かなくなる:" + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }
}
