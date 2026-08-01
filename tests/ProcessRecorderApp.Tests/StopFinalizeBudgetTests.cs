using ProcessRecorderApp.GStreamer;
using ProcessRecorderApp.SingleInstance;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>停止の排出予算とランチャーの結果待ちの関係を固定する。</b>
///
/// <para>
/// <c>StopFinalizeTimeoutMs</c> は PropertyGrid から利用者が変更できる公開ノブで、
/// 設定画面（<c>PropDesc_StopFinalizeTimeout</c>・en/ja）と <c>src/README.md</c> が
/// <b>「50000 以下にしてください」と案内している</b>。一方 <c>stop-recording</c> は
/// <c>StopFinalizeTimeoutMs + StopFinalizeSlackMs</c> まで専有しうるので、
/// ランチャーの結果待ち（<see cref="SingleInstanceManager.WorkerAcceptTimeoutMs"/>）が
/// それを下回ると、<b>案内どおりに設定した利用者の <c>stop-recording</c> が
/// 終了コード 2 を返し始める</b> ── 画面が「50000 以下で大丈夫」と言いながら、
/// その値で壊れる状態になる。
/// </para>
/// <para>
/// <b>3つの数字（60000 / 50000 / 5000）は別々のファイル・別々のアセンブリに散らばっており、
/// このテストが無ければどれか1つを動かしても何も落ちない。</b>
/// 「結果待ちは 15〜20 秒で足りる」のような見積もりは
/// <c>ReadyWaitTimeout</c>(8秒) だけを数えて、こちらの 55 秒を見落としやすい。
/// </para>
/// </summary>
public class StopFinalizeBudgetTests
{
    /// <summary>
    /// 案内している上限どおりに設定した利用者が、結果待ちのタイムアウトを踏まないこと。
    /// </summary>
    [Fact]
    public void TheAdvisedStopFinalizeCeiling_FitsInsideTheLauncherResultWait()
    {
        int worstCaseStop = EventRecorder.MaxAdvisedStopFinalizeTimeoutMs
                          + EventRecorder.StopFinalizeSlackMs;

        Assert.True(worstCaseStop < SingleInstanceManager.WorkerAcceptTimeoutMs,
            $"案内している上限で停止が {worstCaseStop}ms 専有しうるのに、"
            + $"ランチャーの結果待ちは {SingleInstanceManager.WorkerAcceptTimeoutMs}ms しかない。"
            + Environment.NewLine
            + "この状態だと、設定画面の案内どおりに設定した利用者の stop-recording が"
            + Environment.NewLine
            + "終了コード 2（結果待ちタイムアウト）を返し始める。"
            + Environment.NewLine
            + "ランチャー側を縮めたなら、resw の PropDesc_StopFinalizeTimeout（en/ja）と"
            + Environment.NewLine
            + "src/README.md の案内、そして MaxAdvisedStopFinalizeTimeoutMs も一緒に下げること。");
    }

    /// <summary>
    /// 既定値は案内の上限の内側にあること（既定のまま使う利用者が最も多い）。
    /// </summary>
    [Fact]
    public void TheDefaultStopFinalizeTimeout_IsWithinTheAdvisedCeiling()
    {
        Assert.InRange(
            EventRecorder.DefaultStopFinalizeTimeoutMs, 0, EventRecorder.MaxAdvisedStopFinalizeTimeoutMs);
    }

    /// <summary>
    /// <b>案内の数字が利用者に見えている文言と一致すること。</b>
    /// 定数だけ直して resw を直し忘れると、<b>画面が古い上限を案内し続ける</b>
    /// ── 利用者から見える面がずれる方が、定数がずれるより悪い。
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void TheAdvisedCeiling_AppearsInTheSettingsDescription(string locale)
    {
        string text = File.ReadAllText(
            RepositoryFiles.At("src", "ProcessRecorderApp", "Strings", locale, "Resources.resw"));

        int start = text.IndexOf("PropDesc_StopFinalizeTimeout", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{locale} に PropDesc_StopFinalizeTimeout が無い。");

        int end = text.IndexOf("</data>", start, StringComparison.Ordinal);
        string entry = text[start..end];

        Assert.Contains(
            EventRecorder.MaxAdvisedStopFinalizeTimeoutMs.ToString(),
            entry,
            StringComparison.Ordinal);
    }
}
