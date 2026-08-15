using System.Text.RegularExpressions;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// レコーダー設定の三重ミラー（<c>EventRecorderSettings</c> ⇔ <c>EventRecorder</c> ⇔
/// <c>GstEventRecorderViewModel</c>）の<b>追記漏れ検出器</b>。
///
/// <para>
/// レコーダーのプロパティを 1 つ増やすと、手書きの同期が 4 箇所
/// （<c>EventRecorder.Settings_PropertyChanged</c> の case・<c>EventRecorder</c> ctor のコピー・
/// <c>GstEventRecorderViewModel.ApplyModelChange</c> の case・VM ctor のコピー）に要る。
/// 1 箇所でも漏れると「UI が古い値を表示し続ける」「settings.json に反映されない」という形で
/// 黙って壊れる ── コンパイルもテストも通ったまま。
/// </para>
/// <para>
/// <b>L1 からは実行できない</b>（VM は WinUI アプリプロジェクトにあり参照できない）ので、
/// <c>AppSettingsReloadTests</c> と同じ方式でソースを<b>テキストとして</b>突き合わせる。
/// 実行はしないが、「宣言したのに写していない」という唯一の失敗モードは確実に捕まえられる。
/// </para>
/// </summary>
public class RecorderSettingsMirrorTests
{
    private static string RecorderSourcePath =>
        RepositoryFiles.At("src", "GStreamer.GstSharpBundle", "EventRecorder.cs");

    private static string ViewModelSourcePath =>
        RepositoryFiles.At("src", "ProcessRecorderApp", "ViewModels", "GstEventRecorderViewModel.cs");

    /// <summary>
    /// <c>EventRecorderSettings</c>（永続化される設定）の public プロパティ名。
    /// 行単位の走査で拾う ── 自動プロパティ（1 行）と手書きプロパティ
    /// （<c>BufferDuration</c> のように <c>{</c> が次行に来る形）の両方に対応するため、
    /// 1 本の正規表現にはしない。
    /// </summary>
    private static string[] SettingsPropertyNames(string source)
    {
        string classBody = SourceMethodBody.Extract(source, "public partial class EventRecorderSettings");

        var names = new List<string>();
        foreach (string raw in classBody.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("public ", StringComparison.Ordinal))
                continue;
            // const（定数）・static（純粋関数）・'(' を含む行（メソッド）はプロパティではない
            if (line.Contains("const ", StringComparison.Ordinal)
                || line.Contains("static ", StringComparison.Ordinal)
                || line.Contains('('))
                continue;

            string declaration = line;
            int brace = declaration.IndexOf('{');
            if (brace >= 0)
                declaration = declaration[..brace];
            int assign = declaration.IndexOf('=');
            if (assign >= 0)
                declaration = declaration[..assign];

            // "public [partial] <型> <名前>" ── 最後のトークンがプロパティ名
            string[] tokens = declaration.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3)
                names.Add(tokens[^1]);
        }
        return [.. names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    public static TheoryData<string, string> MirrorSites()
    {
        // (対象ファイル, メソッドのシグネチャ)。シグネチャは SourceMethodBody.Extract が
        // 本体を切り出せる程度に一意であればよい。
        var data = new TheoryData<string, string>();
        foreach (var signature in new[]
                 {
                     "private void Settings_PropertyChanged",
                     "public EventRecorder(EventRecorderSettings settings)",
                 })
            data.Add(RecorderSourcePath, signature);
        foreach (var signature in new[]
                 {
                     "private void ApplyModelChange",
                     "public GstEventRecorderViewModel(EventRecorder model",
                 })
            data.Add(ViewModelSourcePath, signature);
        return data;
    }

    [Theory]
    [MemberData(nameof(MirrorSites))]
    public void EverySettingsProperty_AppearsInTheMirrorSite(string path, string signature)
    {
        string[] properties = SettingsPropertyNames(File.ReadAllText(RecorderSourcePath));

        // 走査が壊れると 0 件になり「1件も無いので全部通る」になる。件数の下限で守る
        // （現在 6 件。増える分には問題ない）。
        Assert.True(properties.Length >= 5,
            $"EventRecorderSettings のプロパティ走査が壊れている（{properties.Length} 件しか見つからない）: "
            + string.Join(", ", properties));

        string body = SourceMethodBody.Extract(File.ReadAllText(path), signature);
        var missing = properties
            .Where(name => !ContainsWord(body, name))
            .ToArray();

        Assert.True(missing.Length == 0,
            $"{Path.GetFileName(path)} の '{signature}' が写していないプロパティ: {string.Join(", ", missing)}"
            + Environment.NewLine
            + "追記を忘れると、そのプロパティだけ「UI が古い値のまま」「settings.json に反映されない」形で黙って壊れる。");
    }

    /// <summary>
    /// <b>逆シリアライズで代入されたコレクションの既存要素も購読される</b>ことのピン。
    /// <c>OnRecordersChanging</c> / <c>OnUiaTriggerAssignmentsChanging</c> が要素の購読を
    /// 張り替えないと、settings.json から読み込んだレコーダー（＝実運用の通常ケース）の
    /// 子プロパティ編集がデバウンス自動保存に届かなくなる ── DEBUG 初回起動
    /// （<c>Recorders.Add</c> 経由）では見えない欠陥なので、ソースで固定する。
    /// </summary>
    /// <summary>
    /// 語境界つき・コメント除外の出現検査。素の部分文字列一致だと <c>Type</c> が
    /// <c>ActualType</c> に埋もれ、case を消しても緑のまま通ってしまう。
    /// </summary>
    private static bool ContainsWord(string body, string name)
    {
        foreach (Match m in Regex.Matches(body, $@"\b{Regex.Escape(name)}\b"))
        {
            if (!SourceReferences.IsCommentLine(body, m.Index))
                return true;
        }
        return false;
    }

    [Theory]
    [InlineData("partial void OnRecordersChanging", "SubscribeRecorder")]
    [InlineData("partial void OnUiaTriggerAssignmentsChanging", "SubscribeAssignment")]
    public void CollectionReplacement_ResubscribesTheElements(string signature, string subscribeCall)
    {
        string source = File.ReadAllText(
            RepositoryFiles.At("src", "ProcessRecorderApp", "Settings", "AppSettings.cs"));

        string body = SourceMethodBody.Extract(source, signature);

        Assert.True(SourceMethodBody.ContainsCode(body, subscribeCall),
            $"AppSettings の '{signature}' が {subscribeCall} を呼んでいない。"
            + Environment.NewLine
            + "逆シリアライズは「要素を詰め終えたコレクションを setter へ代入する」ため、"
            + "ここで張り替えないと既存要素の編集がデバウンス自動保存の契機にならない。");
    }
}
