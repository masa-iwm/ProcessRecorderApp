using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>終了処理に入った常駐ワーカーが、届いたリダイレクトを黙って捨てないこと。</b>
///
/// <para>
/// 終了経路（トレイの「終了」／Ctrl+閉じる）が
/// <c>Activated -= OnActivationRedirected</c> を <c>UnregisterKey()</c> より<b>先に</b>
/// 実行すると、キー解除の直前にキーの持ち主を見終わっていたランチャーの
/// リダイレクトは<b>購読者不在で痕跡ゼロに消え</b>、そのランチャーは結果通知を
/// 上限（最大60秒）まで待って「成否不明」の 2 を返す（実際にあった欠陥）。
/// 終了コード 5 の即答で塞げるのは「発火した後に <c>DispatcherQueue</c> が止まった場合」だけで、
/// <b>購読解除の後に届く分はここでしか塞げない</b>。
/// </para>
/// <para>
/// <b>実行では守れない。</b> 購読解除とキー解除の間（隣り合う2文）へリダイレクトを
/// 差し込むのはプロセス間のレースそのもので、狙って起こせない。
/// <c>WorkerAcceptingEventOrderTests</c> と同じく<b>ソースをテキストとして</b>突き合わせる
/// ── 実行はしないが、「ハンドラを外す1行が戻ってきた」「順序が入れ替わった」という
/// 失敗モードは確実に捕まえられる。
/// </para>
/// </summary>
public class ShutdownRedirectHandlingTests
{
    private static string Source =>
        File.ReadAllText(RepositoryFiles.At("src", "SingleInstance", "SingleInstanceManager.cs"));

    private const string BeginShutdownSignature = "private void BeginShutdown()";
    private const string ExitApplicationSignature = "private void ExitApplication()";
    private const string ClosingSignature =
        "private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)";

    /// <summary>
    /// 終了経路は<b>2つとも</b>同じ入口を通ること
    /// （片方だけ直すと、そちらでしか塞がらない ── 実際にそうなっていたことがある）。
    /// </summary>
    [Theory]
    [InlineData(ExitApplicationSignature)]
    [InlineData(ClosingSignature)]
    public void BothShutdownPaths_GoThroughBeginShutdown(string signature)
    {
        string body = SourceMethodBody.Extract(Source, signature);

        Assert.True(SourceMethodBody.ContainsCode(body, "BeginShutdown()"),
            $"{signature} が BeginShutdown() を通っていない。"
            + Environment.NewLine
            + "終了経路はトレイの「終了」と Ctrl+閉じる の2つあり、片方だけ直すと"
            + "もう片方では従来どおり「60 秒待って終了コード 2」になる。");
    }

    /// <summary>
    /// <b>「終了を決めた」旗は、キー解除より前に立てること。</b>
    /// 逆だと、キーが空いてから旗が立つまでの間に届いたリダイレクトへ
    /// <c>ExitCode_WorkerShuttingDown</c> を返せない。
    /// </summary>
    [Fact]
    public void TheShuttingDownFlag_IsRaisedBeforeTheKeyIsUnregistered()
    {
        string body = SourceMethodBody.Extract(Source, BeginShutdownSignature);

        int flag = SourceMethodBody.IndexOfCode(body, "_shuttingDown = true");
        Assert.True(flag >= 0,
            "BeginShutdown から _shuttingDown の設定が消えている。"
            + "改名したなら、この検査も一緒に直すこと（この旗が唯一の防護）。");

        int unregister = SourceMethodBody.IndexOfCode(body, "UnregisterKey()");
        Assert.True(unregister >= 0, "BeginShutdown からキー解除が消えている。");

        Assert.True(flag < unregister,
            "_shuttingDown を立てる前にキーを解除している。"
            + Environment.NewLine
            + "その間に届いたリダイレクトは DispatcherQueue へ積まれてしまい、"
            + "実行される保証が無いまま「委譲できた」ことになる ── "
            + "ランチャーは結果が来ないまま 60 秒待って終了コード 2（成否不明）を返す。");
    }

    /// <summary>
    /// <b>終了経路で <c>Activated</c> の購読を外さないこと。</b>
    ///
    /// <para>
    /// 外すと、そのリダイレクトは<b>誰にも届かず痕跡も残らない</b>。
    /// 残しておけば「一度も実行していない」5 を即答できる
    /// （5 と 2 の違いは再試行の可否そのもの ── <c>CommandOutcomeTests</c> 参照）。
    /// </para>
    /// <para>
    /// 検査は<b>コメントを除外して</b>行う ── <c>BeginShutdown</c> の doc は
    /// 「なぜこの1行が<i>無い</i>のか」を説明するために <c>Activated</c> を含んでおり、
    /// 素の一致では自分のコメントで赤くなる。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(BeginShutdownSignature)]
    [InlineData(ExitApplicationSignature)]
    [InlineData(ClosingSignature)]
    public void TheShutdownPaths_NeverDetachTheActivationHandler(string signature)
    {
        string body = SourceMethodBody.Extract(Source, signature);

        Assert.False(SourceMethodBody.ContainsCode(body, "Activated -="),
            $"{signature} が Activated の購読を外している。"
            + Environment.NewLine
            + "終了処理に入った後も、届いたリダイレクトには答えられなければならない。"
            + "購読を外すと、キー解除の直前にキーの持ち主を見終わっていたランチャーの"
            + Environment.NewLine
            + "リダイレクトが痕跡ゼロで消え、そのランチャーは 60 秒待って"
            + "「成否不明」の 2 を返す（利用者から見れば固まってから曖昧に失敗する）。"
            + Environment.NewLine
            + "購読は残したまま _shuttingDown で即答すること（BeginShutdown の doc を参照）。");
    }
}
