using ProcessRecorderApp.GStreamer;
using Xunit;

namespace ProcessRecorderApp.Tests;

/// <summary>
/// <b>モニターの物理ピクセルの取得。</b>
///
/// <para>
/// パイプライン編集ダイアログが <c>d3d12screencapturesrc</c> の解像度欄の選択肢に使う。
/// 常時録画の解像度・フレームレートの上書きは<b>ソースの caps が固定されていないと
/// 効かせられない</b>ので、その値を利用者が手で調べなくて済むようにするための経路である。
/// </para>
/// <para>
/// <b>大きさは <c>d3d12screencapturedeviceprovider</c> のデバイス caps が運ぶ</b>
/// （<c>width</c> / <c>height</c>）。キャプチャ側がこれから出す大きさそのものなので、
/// <b>プロセスの DPI 認識に依存しない</b> ── 生の Win32（<c>GetMonitorInfo</c> の
/// <c>rcMonitor</c> や <c>DXGI_OUTPUT_DESC.DesktopCoordinates</c>）で取っていた頃の
/// 「DPI 非対応プロセスでは仮想化された半端な値が返る」罠は、経路ごと無くなっている。
/// </para>
/// <para>
/// <b>この検査はこの機械の実物を読む。</b> 値そのものは機械ごとに違うので固定できないが、
/// <b>実寸としてありえない値でないこと</b>は形で確かめられる。
/// </para>
/// </summary>
public class MonitorResolutionTests
{
    [Fact]
    public void EveryConnectedMonitor_ReportsAPlausiblePhysicalSize()
    {
        var resolutions = GstIntrospect.GetMonitorResolutions();

        // 画面のある機械なら1つ以上。無い環境（サービス等）では空でよい ──
        // 呼び出し側は空なら自由入力へ倒れる。
        if (resolutions.Count == 0)
            return;

        foreach (string value in resolutions)
        {
            // 読めなかったモニターは**空文字で席が残る**（意図的。詰めると以降の
            // monitor-index が1つずつずれ、直そうとしている取り違えを作ってしまう）。
            if (value.Length == 0)
                continue;

            Assert.True(ContinuousBranch.TryParseResolution(value, out int width, out int height),
                $"解像度として読めない値が返っている: '{value}'");
            // 640x480 未満・16K 超は、単位の取り違えや caps の読み違いを疑う形
            Assert.InRange(width, 640, 15360);
            Assert.InRange(height, 480, 8640);
        }

        // モニター数と一致すること（並びを monitor-index と突き合わせる前提が崩れていない）
        Assert.True(resolutions.Count <= GstIntrospect.GetMonitorCount(),
            $"モニター数 {GstIntrospect.GetMonitorCount()} より多い解像度が返っている: {resolutions.Count}");
    }
}
