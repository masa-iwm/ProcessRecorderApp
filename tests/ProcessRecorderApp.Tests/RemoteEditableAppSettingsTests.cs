using ProcessRecorderApp.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <c>AppSettings</c> の public プロパティ ⇔
/// <c>RemoteApiRules.RemoteEditableAppSettings</c> / <c>RemoteDeniedAppSettings</c> の
/// <b>過不足なき一致</b>。
///
/// <para>
/// <b>「拒否リストに無ければ許可」にしないための検査である。</b> 後から増えた
/// プロパティが黙ってリモートから書き換え可能になると、<c>OutputDirectory</c> のような
/// 「配信 root そのもの」を動かせる穴が、誰も気付かないまま開く。
/// 2 本のリストを明示し、ここで全プロパティと突き合わせておけば、
/// プロパティを増やした時点でどちらに入れるかを<b>決めさせられる</b>。
/// </para>
/// <para>
/// <c>AppSettings</c> は WinUI アプリプロジェクトにあり L1 からは参照できないので、
/// <c>AppSettingsReloadTests</c> と同じくソースをテキストとして読む。
/// </para>
/// </summary>
public sealed class RemoteEditableAppSettingsTests
{
    /// <summary>
    /// public なインスタンスプロパティの宣言。
    /// <c>public partial ObservableCollection&lt;T&gt; Recorders { get; internal set; }</c> のような
    /// 修飾子・総称・完全修飾名つきの形をまとめて拾う。
    /// </summary>
    private static readonly Regex PropertyRegex =
        new(@"public\s+[\w<>\[\]?.,\s]+?\s+(\w+)\s*\{\s*get", RegexOptions.Compiled);

    private static List<string> DeclaredProperties()
    {
        var found = new List<string>();

        // JsonSettingsBase の public プロパティは AppSettings に継承され、
        // settings.json にも出る（IsFirstRun）。片方だけ見ると取りこぼす。
        string[][] files =
        [
            ["src", "ProcessRecorderApp", "Settings", "AppSettings.cs"],
            ["src", "Components", "JsonSettingsBase.cs"],
        ];

        foreach (string[] segments in files)
        {
            string text = File.ReadAllText(RepositoryFiles.At(segments));
            foreach (Match match in PropertyRegex.Matches(text))
            {
                if (SourceReferences.IsCommentLine(text, match.Index))
                    continue;

                // static は設定の項目ではない（既定値やファイル名の定数）。
                if (match.Value.Contains("static", StringComparison.Ordinal))
                    continue;

                found.Add(match.Groups[1].Value);
            }
        }

        return found;
    }

    [Fact]
    public void TheScanFindsTheSettingsProperties()
    {
        var declared = DeclaredProperties();

        Assert.True(declared.Count >= 20,
            $"AppSettings の public プロパティが {declared.Count} 件しか読めない（20 件以上あるはず）。"
            + Environment.NewLine
            + "宣言の書き方かファイルの置き場所が変わったのなら PropertyRegex も一緒に直すこと"
            + Environment.NewLine
            + "（空振りのまま緑にすると、許可リストの検査そのものが消える）。");

        Assert.Contains("OutputDirectory", declared);
        Assert.Contains("IsFirstRun", declared);
    }

    [Fact]
    public void EveryPropertyIsEitherEditableOrDenied()
    {
        var declared = DeclaredProperties().ToHashSet(StringComparer.Ordinal);
        var classified = RemoteApiRules.RemoteEditableAppSettings
            .Concat(RemoteApiRules.RemoteDeniedAppSettings)
            .ToHashSet(StringComparer.Ordinal);

        string[] unclassified = [.. declared.Except(classified).OrderBy(n => n, StringComparer.Ordinal)];
        Assert.True(unclassified.Length == 0,
            "AppSettings に、許可にも拒否にも入っていないプロパティがある: "
            + string.Join(", ", unclassified)
            + Environment.NewLine
            + "リモートから書き換えてよいなら RemoteEditableAppSettings へ、"
            + "そうでないなら RemoteDeniedAppSettings へ入れること。");

        string[] stale = [.. classified.Except(declared).OrderBy(n => n, StringComparer.Ordinal)];
        Assert.True(stale.Length == 0,
            "許可/拒否リストに、AppSettings に存在しない名前がある: "
            + string.Join(", ", stale)
            + Environment.NewLine
            + "改名・削除したなら、リストも一緒に直すこと"
            + "（残しておくと、次に同名のプロパティが生まれたとき意図しない分類が付く）。");
    }

    [Fact]
    public void TheTwoListsAreDisjoint()
    {
        string[] both =
        [
            .. RemoteApiRules.RemoteEditableAppSettings
                .Intersect(RemoteApiRules.RemoteDeniedAppSettings, StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
        ];

        Assert.True(both.Length == 0, "許可と拒否の両方に載っている: " + string.Join(", ", both));
    }

    [Fact]
    public void ThePathAndCollectionPropertiesAreDenied()
    {
        // 意図の表明。OutputDirectory は配信 root そのもので、動かせると
        // 配信範囲ごと別の場所へ移る。コレクションの差し替えは録画中の
        // パイプラインを落とす（RecorderSettingsMirrorTests の注意と同じ理由）。
        foreach (string name in new[] { "OutputDirectory", "GstDebugDumpDotDir", "DebugLogFile", "Recorders", "UiaTriggers" })
            Assert.False(RemoteApiRules.IsRemoteEditable(name), $"{name} はリモートから書き換えられてはいけない。");
    }
}
