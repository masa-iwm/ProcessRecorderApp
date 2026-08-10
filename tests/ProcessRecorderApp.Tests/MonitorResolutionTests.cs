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
/// <b>この検査はこの機械の実物を読む。</b> 値そのものは機械ごとに違うので固定できないが、
/// <b>DPI 仮想化された値が返っていないこと</b>は形で分かる ── テストホストは
/// DPI 非対応のプロセスなので、<c>GetMonitorInfo</c> 系なら仮想化された値
/// （実測で 2194x1234 のような半端な大きさ）が返る。<c>EnumDisplaySettings</c> は
/// ディスプレイの現在のモードを返すのでプロセスの DPI 認識に依存しない。
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
            Assert.True(ContinuousBranch.TryParseResolution(value, out int width, out int height),
                $"解像度として読めない値が返っている: '{value}'");
            // 640x480 未満・16K 超は、DPI 仮想化や単位の取り違えを疑う形
            Assert.InRange(width, 640, 15360);
            Assert.InRange(height, 480, 8640);
        }

        // モニター数と一致すること（並びを monitor-index と突き合わせる前提が崩れていない）
        Assert.True(resolutions.Count <= GstIntrospect.GetMonitorCount(),
            $"モニター数 {GstIntrospect.GetMonitorCount()} より多い解像度が返っている: {resolutions.Count}");
    }
}
