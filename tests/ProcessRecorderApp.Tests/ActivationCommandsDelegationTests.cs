using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>CLI の実処理が <c>ActivationCommands</c> へ戻ってこないことを固定する。</b>
///
/// <para>
/// <c>ActivationCommands</c> が持つのは引数解析・文言（Localization）・
/// <c>CommandOutcome</c> の整形だけで、レコーダーの操作とテンプレート変数の読み書きは
/// <c>RecorderControlService</c> にある。この境界が崩れると、CLI 以外の呼び出し面
/// （同じ終了コードを返す必要がある）から同じ実処理を呼べなくなる
/// ── しかも<b>製品の挙動は何も変わらないので、実行では検出できない</b>。
/// </para>
/// <para>
/// <b>両方向を見る。</b> 「片側に無い」だけを見ると、実処理をどこにも書かずに消しても緑になる。
/// 移した先に在ることも併せて見ることで、検査そのものが空振りしていないことを担保する
/// （docs/test-harness.md「テストの有効性検証の原則」）。
/// </para>
/// </summary>
public class ActivationCommandsDelegationTests
{
    /// <summary>
    /// <c>ActivationCommands.cs</c> のコード行に出てはいけない識別子
    /// （ビューモデルへの直接アクセス・レコーダー操作・テンプレート変数の読み書き）。
    /// 部分一致で見るので <c>CanStartRecording</c> は <c>CanStartRecordingAll</c> も捕まえる。
    /// </summary>
    private static readonly string[] BannedInActivationCommands =
    [
        "GstControllerViewModel",
        ".Recorders",
        ".Model.",
        "StartRecording(",
        "StopRecordingAsync(",
        "StartRecordingAllAndReport",
        "StopRecordingAllAsync",
        "CanStartRecording",
        "CanStopRecording",
        "TemplateVariables",
        "SetTemplateVariable",
        "SetTemplateVariablePersistent",
        "ResolveTargetIndex",
        "AppSettings.Default",
    ];

    /// <summary>
    /// <c>RecorderControlService.cs</c> のコード行に在るべき識別子（実処理を移した先）。
    /// </summary>
    private static readonly string[] RequiredInService =
    [
        "StartRecording(",
        "StopRecordingAsync(",
        "StartRecordingAllAndReport",
        "StopRecordingAllAsync",
        "ResolveTargetIndex",
        "SetTemplateVariablePersistent",
    ];

    [Fact]
    public void ActivationCommands_DoesNotDriveTheRecordersItself()
    {
        string path = RepositoryFiles.At("src", "ProcessRecorderApp", "ActivationCommands.cs");
        string text = File.ReadAllText(path);

        var found = BannedInActivationCommands
            .SelectMany(token => CodeOccurrences(text, token)
                .Select(index => $"{token} ({RepositoryFiles.Relative(path)}:{LineNumber(text, index)})"))
            .ToList();

        Assert.True(found.Count == 0,
            "ActivationCommands.cs のコード行に実処理の識別子が現れている:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, found)
            + Environment.NewLine
            + "レコーダーの操作とテンプレート変数の読み書きは RecorderControlService に置くこと"
            + "（ここに残すのは引数解析・文言・CommandOutcome の整形だけ）。");
    }

    [Fact]
    public void RecorderControlService_OwnsTheMovedCalls()
    {
        string path = RepositoryFiles.At("src", "ProcessRecorderApp", "RecorderControlService.cs");
        string text = File.ReadAllText(path);

        var missing = RequiredInService
            .Where(token => CodeOccurrences(text, token).Count == 0)
            .ToList();

        Assert.True(missing.Count == 0,
            "RecorderControlService.cs のコード行に実処理が見つからない: "
            + string.Join(", ", missing)
            + Environment.NewLine
            + "移動先が空になると、ActivationCommands 側の禁止検査だけが残って"
            + Environment.NewLine
            + "「どこにも実処理が無い」状態でも緑になる。");
    }

    /// <summary>
    /// コメント行の出現（doc コメントがこれらの名前を何度も出す）を除いた出現位置。
    /// 判定は <see cref="SourceReferences.IsCommentLine"/> と同じもの
    /// ── 素の <c>IndexOf</c> でコメント行を拾って「注入したのに落ちない」を起こした前例がある。
    /// </summary>
    private static List<int> CodeOccurrences(string text, string token)
    {
        var found = new List<int>();
        for (int index = text.IndexOf(token, StringComparison.Ordinal);
             0 <= index;
             index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal))
        {
            if (!SourceReferences.IsCommentLine(text, index))
                found.Add(index);
        }
        return found;
    }

    private static int LineNumber(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;
}
