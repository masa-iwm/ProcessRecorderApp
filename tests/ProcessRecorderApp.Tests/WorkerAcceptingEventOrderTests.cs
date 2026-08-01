using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <c>StartResidentWorker</c> の中で、<b>キー登録に負けたワーカーが
/// <c>WorkerAcceptingEvent</c> に一切触れずに帰る</b>ことを固定する。
///
/// <para>
/// <b>これは load-bearing な順序である。</b> このイベントは<b>手動リセット</b>で、
/// 「今どのワーカーがコマンドを受け取れるか」を表す<b>プロセス間で共有された1個の旗</b>。
/// リセットをキー登録より前に置くと、<b>登録に負けた（＝別のワーカーが既に動いている）
/// プロセスが、生きているワーカーのシグナルを消してしまう</b> ──
/// 以後そのワーカー宛てのリダイレクトは、ランチャー側の
/// <c>WaitUntilWorkerAcceptsCommands</c> で毎回 60 秒待たされる。
/// 「初回リダイレクトの喪失」と同じ症状が、別の形で戻ってくる。
/// </para>
/// <para>
/// <b>実行では守れない。</b> 再現には「キー登録に負けるワーカー」を狙って作る必要があり、
/// これはプロセス起動のレースそのもの。<c>AppSettingsReloadTests</c> と同じく
/// <b>ソースをテキストとして</b>突き合わせる ── 実行はしないが、
/// 「リセットが登録より前に来た」という唯一の失敗モードは確実に捕まえられる。
/// </para>
/// </summary>
public class WorkerAcceptingEventOrderTests
{
    private static string SourcePath =>
        RepositoryFiles.At("src", "SingleInstance", "SingleInstanceManager.Launcher.cs");

    private const string Signature =
        "public static int StartResidentWorker(string keyPrefix, Func<Application> appFactory, Action? initializing = null)";

    [Fact]
    public void AWorkerThatLosesTheKeyRace_NeverTouchesTheSharedAcceptingEvent()
    {
        string body = SourceMethodBody.Extract(File.ReadAllText(SourcePath), Signature);

        int losingReturn = IndexOfCode(body, "return ExitCode_WorkerAlreadyRunning;");
        Assert.True(losingReturn >= 0,
            "キー登録に負けたときの早期 return が見つからない。"
            + "改名したなら、この検査も一緒に直すこと（この return が唯一の防護）。");

        int firstEventUse = IndexOfCode(body, "_workerAcceptingEvent");
        Assert.True(firstEventUse >= 0,
            "_workerAcceptingEvent の初期化が StartResidentWorker から消えている。"
            + "受理待ちの旗を立てる場所が変わったなら、この検査も一緒に直すこと。");

        Assert.True(losingReturn < firstEventUse,
            "キー登録に負けたワーカーが WorkerAcceptingEvent に触れてしまう順序になっている。"
            + Environment.NewLine
            + "この旗は手動リセットでプロセス間に1個しかないので、負けた側がリセットすると"
            + Environment.NewLine
            + "**生きているワーカーのシグナルを消す** ── 以後そのワーカー宛てのリダイレクトは"
            + Environment.NewLine
            + "毎回 60 秒待たされる。早期 return を _workerAcceptingEvent より前に戻すこと。");
    }

    /// <summary>
    /// キー登録そのものも、当然リセットより前でなければならない
    /// （上の表明は「負けた場合」を守るが、こちらは登録行が丸ごと後ろへ動く事故を守る）。
    /// </summary>
    [Fact]
    public void TheKeyIsRegisteredBeforeTheAcceptingEventIsReset()
    {
        string body = SourceMethodBody.Extract(File.ReadAllText(SourcePath), Signature);

        int register = IndexOfCode(body, "FindOrRegisterForKey");
        Assert.True(register >= 0, "StartResidentWorker からキー登録が消えている。");

        int reset = IndexOfCode(body, ".Reset()");
        Assert.True(reset >= 0, "WorkerAcceptingEvent のリセットが見つからない。");

        Assert.True(register < reset,
            "キー登録より前に WorkerAcceptingEvent をリセットしている。"
            + "登録に負けた場合に、生きているワーカーのシグナルを消す。");
    }

    /// <summary>
    /// <b>コメント行を数えない</b>最初の出現位置（<see cref="SourceMethodBody.IndexOfCode"/>）。
    /// このメソッドの直前のコメントが「後続の…コンストラクターでの
    /// <c>FindOrRegisterForKey</c> は…」と<b>本文と同じ語を含んでいる</b>ため、
    /// 素の <c>IndexOf</c> では検査が黙って無効になる
    /// （docs/test-harness.md「テストの有効性検証の原則」）。
    /// </summary>
    private static int IndexOfCode(string body, string needle) => SourceMethodBody.IndexOfCode(body, needle);
}
