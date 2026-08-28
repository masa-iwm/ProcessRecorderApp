using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="RecordingIndexChangeKind"/> ⇔ SSE の <c>recording</c> イベントの
/// <c>kind</c> 文字列の<b>双方向</b>照合（<c>EventsEndpoint.KindName</c>）。
///
/// <para>
/// <b>写像はソーステキストとして読む。</b> <c>KindName</c> は既定の腕で投げるので、
/// 種別が増えてもコンパイルは通り、増えた種別が配られた瞬間に初めて 500 になる
/// ── コンパイラの代わりにここが落ちる。
/// </para>
/// <para>
/// <b>L1 から <c>RemoteControl.csproj</c> は参照できない。</b> 参照した瞬間に
/// ASP.NET Core の共有フレームワークが L1 のテストホストへ降りてくる
/// （<c>RemoteControlIsolationTests</c> の境界 ③）。だからテキストで読む。
/// </para>
/// <para>
/// <b>収集件数を先に assert する。</b> 正規表現が空振りしたまま「全部の値が在る」を
/// 通すと検査そのものが消える（docs/test-harness.md「テストの有効性検証の原則」）。
/// </para>
/// </summary>
public sealed class RecordingChangeKindDriftTests
{
    /// <summary><c>RecordingIndexChangeKind.Added =&gt; "added",</c> の 1 本。</summary>
    private static readonly Regex ArmRegex =
        new(@"RecordingIndexChangeKind\.(\w+)\s*=>\s*""([a-z]+)""\s*,", RegexOptions.Compiled);

    /// <summary>Web UI と API の約束として固定してある綴り。</summary>
    private static readonly string[] WireNames = ["added", "completed", "removed", "updated"];

    private static Dictionary<string, string> Arms()
    {
        string path = RepositoryFiles.At("src", "RemoteControl", "Endpoints", "EventsEndpoint.cs");
        string text = File.ReadAllText(path);

        var arms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in ArmRegex.Matches(text))
        {
            if (SourceReferences.IsCommentLine(text, match.Index))
                continue;

            arms[match.Groups[1].Value] = match.Groups[2].Value;
        }

        Assert.NotEmpty(arms);
        return arms;
    }

    [Fact]
    public void EveryChangeKindHasAWireName()
    {
        var arms = Arms();

        foreach (RecordingIndexChangeKind kind in Enum.GetValues<RecordingIndexChangeKind>())
        {
            Assert.True(arms.TryGetValue(kind.ToString(), out string? name),
                $"RecordingIndexChangeKind.{kind} に対応する腕が EventsEndpoint.KindName に無い。"
                + Environment.NewLine
                + "腕を足さないと、その種別が起きた瞬間に /api/events が 500 で切れる"
                + "（既定の腕は投げる）。");

            Assert.Contains(name, WireNames);
        }
    }

    [Fact]
    public void EveryWireNameIsProducedByExactlyOneChangeKind()
    {
        var arms = Arms();

        // 逆方向。綴りを取り違えて 2 つの種別が同じ文字列を名乗ると、
        // 受け手（Web UI）はどちらが起きたのか区別できない。
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string kind, string name) in arms)
        {
            Assert.True(Enum.TryParse<RecordingIndexChangeKind>(kind, out _),
                $"EventsEndpoint.KindName に RecordingIndexChangeKind.{kind} の腕があるが、"
                + "その名前の値は列挙に無い。");

            Assert.False(seen.TryGetValue(name, out string? other),
                $"kind='{name}' を {kind} と {other} の 2 つが名乗っている。");

            seen[name] = kind;
        }

        Assert.Equal(WireNames.Length, seen.Count);
        foreach (string name in WireNames)
            Assert.True(seen.ContainsKey(name), $"kind='{name}' を作る腕が無い。");
    }
}
