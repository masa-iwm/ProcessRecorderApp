using ProcessRecorderApp.SingleInstance;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <see cref="CommandOutcome"/> のファクトリと不変条件。
///
/// <c>ExitCode</c> は常駐ワーカーからランチャープロセスの終了コードへそのまま伝播するため、
/// ランチャー自身が予約している 1（ワーカー起動失敗）/ 2（結果待ちタイムアウト）と
/// 衝突してはならない。この境界は CLI 契約そのもの（ルート README のバッチ例が依存する）。
/// </summary>
public class CommandOutcomeTests
{
    [Fact]
    public void Activate_ShowsTheWindowWithoutToastAndSucceeds()
    {
        var outcome = CommandOutcome.Activate();

        Assert.True(outcome.ShowWindow);
        Assert.False(outcome.ShowsToast);
        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public void Silent_ShowsNothingAndSucceeds()
    {
        var outcome = CommandOutcome.Silent();

        Assert.False(outcome.ShowWindow);
        Assert.False(outcome.ShowsToast);
        Assert.Null(outcome.ConsoleOutput);
        Assert.Null(outcome.ConsoleError);
        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public void SilentWithToast_NotifiesWithoutShowingTheWindow()
    {
        var outcome = CommandOutcome.SilentWithToast("Title", "Message");

        Assert.False(outcome.ShowWindow);
        Assert.True(outcome.ShowsToast);
        Assert.Equal("Title", outcome.ToastTitle);
        Assert.Equal("Message", outcome.ToastMessage);
        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public void ActivateWithToast_ShowsTheWindowAndNotifies()
    {
        var outcome = CommandOutcome.ActivateWithToast("Title", "Message");

        Assert.True(outcome.ShowWindow);
        Assert.True(outcome.ShowsToast);
    }

    [Fact]
    public void Failure_WithoutArguments_UsesTheDefaultFailureExitCodeAndDoesNotNotify()
    {
        var outcome = CommandOutcome.Failure();

        Assert.False(outcome.ShowWindow);
        Assert.False(outcome.ShowsToast);
        Assert.Equal(CommandOutcome.DefaultFailureExitCode, outcome.ExitCode);
    }

    [Fact]
    public void Failure_WithAnExplicitExitCode_KeepsIt()
        => Assert.Equal(13, CommandOutcome.Failure(exitCode: 13).ExitCode);

    [Fact]
    public void Failure_WithTitleAndMessage_Notifies()
    {
        var outcome = CommandOutcome.Failure("Title", "Message");

        Assert.True(outcome.ShowsToast);
        Assert.Equal(CommandOutcome.DefaultFailureExitCode, outcome.ExitCode);
    }

    /// <summary>
    /// ランチャーが予約している 1 / 2 と衝突しないための下限。
    /// これを下げると「ワーカー起動失敗」と「コマンドの失敗」が区別できなくなる。
    /// </summary>
    [Fact]
    public void DefaultFailureExitCode_DoesNotCollideWithTheLauncherReservedCodes()
    {
        Assert.True(CommandOutcome.DefaultFailureExitCode >= 10);
        Assert.NotEqual(SingleInstanceManager.ExitCode_WorkerStartFailed, CommandOutcome.DefaultFailureExitCode);
        Assert.NotEqual(SingleInstanceManager.ExitCode_WorkerResultTimeout, CommandOutcome.DefaultFailureExitCode);
    }

    /// <summary>
    /// 単一インスタンス層が予約している終了コードが互いに衝突しないこと。
    ///
    /// <para>
    /// <b><see cref="SingleInstanceManager.ExitCode_WorkerShuttingDown"/> が
    /// <see cref="SingleInstanceManager.ExitCode_WorkerResultTimeout"/> と別値であることが
    /// この表明の要点。</b> 前者は「一度も実行していない＝安全に再試行できる」、
    /// 後者は「成否不明」で、<b>呼び出し側の再試行の判断が真逆になる</b>。
    /// 同じ値に潰すと、バッチは副作用のあるコマンドを再試行してよいか判断できなくなる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheSingleInstanceReservedExitCodes_AreAllDistinct()
    {
        int[] reserved =
        [
            SingleInstanceManager.ExitCode_WorkerStartFailed,
            SingleInstanceManager.ExitCode_WorkerResultTimeout,
            SingleInstanceManager.ExitCode_WorkerAlreadyRunning,
            SingleInstanceManager.ExitCode_WorkerShuttingDown,
            SingleInstanceManager.ExitCode_LauncherBusy,
            SingleInstanceManager.UnexpectedErrorExitCode,
        ];

        Assert.Equal(reserved.Length, reserved.Distinct().Count());
        Assert.DoesNotContain(0, reserved); // 0 は成功。予約コードに混ぜてはいけない
    }

    // ---- 部分的な指定（record struct の with 式） ----

    [Fact]
    public void ShowsToast_RequiresBothTitleAndMessage()
    {
        Assert.False((CommandOutcome.Silent() with { ToastTitle = "Title" }).ShowsToast);
        Assert.False((CommandOutcome.Silent() with { ToastMessage = "Message" }).ShowsToast);
        Assert.True((CommandOutcome.Silent() with { ToastTitle = "T", ToastMessage = "M" }).ShowsToast);
    }

    [Fact]
    public void ConsoleOutput_CanBeAttachedToAnyOutcome()
    {
        var outcome = CommandOutcome.Silent() with { ConsoleOutput = "value" + Environment.NewLine };

        Assert.Equal("value" + Environment.NewLine, outcome.ConsoleOutput);
        Assert.Equal(0, outcome.ExitCode);
    }
}
