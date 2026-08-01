using ProcessRecorderApp.SingleInstance;
using Windows.UI.Core;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// 閉じるボタン(X)の分岐規則（<see cref="SingleInstanceManager.ShouldExitOnClose"/>）。
///
/// 契約は「X だけならトレイへ格納してプロセスは生存、Ctrl+X のときだけ終了」。
/// これはアプリの中核価値（トレイ常駐中の常時バッファリング）を直接支えているのに、
/// 判定が <c>!= CoreVirtualKeyStates.None</c> と書かれていたため
/// <c>Locked</c> にも一致し、Ctrl に触れていなくても終了していた
/// （切断中の RDP セッションで Ctrl が <c>Locked</c> と報告されることを実測）。
///
/// <see cref="CoreVirtualKeyStates"/> は <c>[Flags]</c>（None=0 / Down=1 / Locked=2）。
/// </summary>
public class CloseToTrayTests
{
    [Fact]
    public void NoKeyPressed_HidesToTray()
    {
        Assert.False(SingleInstanceManager.ShouldExitOnClose(CoreVirtualKeyStates.None));
    }

    [Fact]
    public void LockedWithoutDown_HidesToTray()
    {
        // この1件が本命の回帰テスト。`!= None` に戻すとここだけが落ちる。
        // Locked はトグル状態でモディファイアキーには意味が無く、「押されている」ではない。
        Assert.False(SingleInstanceManager.ShouldExitOnClose(CoreVirtualKeyStates.Locked));
    }

    [Fact]
    public void ControlHeld_ExitsTheApplication()
    {
        Assert.True(SingleInstanceManager.ShouldExitOnClose(CoreVirtualKeyStates.Down));
    }

    [Fact]
    public void ControlHeldWhileLocked_ExitsTheApplication()
    {
        Assert.True(SingleInstanceManager.ShouldExitOnClose(
            CoreVirtualKeyStates.Down | CoreVirtualKeyStates.Locked));
    }
}
