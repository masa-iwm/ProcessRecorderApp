using System;
using System.Collections.Generic;

namespace ProcessRecorderApp.GStreamer;

/// <summary>レコーダーの映像源が「戻ってくることのあるデバイス」かどうかの区分。</summary>
internal enum DeviceKind
{
    /// <summary>デバイスに紐づかない（テストソース・未知の要素）。到着の監視をしない。</summary>
    None,

    /// <summary>カメラ（<c>mfvideosrc</c>）。</summary>
    Camera,

    /// <summary>モニター（画面キャプチャ）。</summary>
    Monitor,
}

/// <summary>
/// <c>SrcPipeline</c> の映像源が、どの種類のデバイス到着で復帰しうるかを決める規則。
///
/// <para>
/// <b><see cref="SrcPipelineBuilder.Sources"/> は引かない。</b> あちらは表示名を
/// ローカライズリソースから取る <c>Lazy</c> なので、引くとリソース基盤の初期化を
/// GStreamer のスレッドから強制することになる。ここは要素名だけの静的な表にして、
/// <b>カタログと過不足が無いことは L1（<c>DeviceKindRulesTests</c>）が縛る</b>。
/// </para>
/// <para>
/// <b>モニターの監視は、要素が D3D11 でも D3D12 のプロバイダで行う。</b>
/// 見ているのは <c>WM_DISPLAYCHANGE</c>（ディスプレイ構成の変化）であって
/// src 要素ではない ── そして <c>d3d11screencapturedeviceprovider</c> は
/// <c>probe</c> しか実装しておらず、通知を一切出さない
/// （上流 <c>sys/d3d11/gstd3d11screencapturedevice.cpp</c>。詳細は docs/environment-facts.md）。
/// </para>
/// </summary>
internal static class DeviceKindRules
{
    /// <summary>カメラの到着を知らせるデバイスプロバイダ。</summary>
    public const string CameraProviderName = "mfdeviceprovider";

    /// <summary>モニターの到着を知らせるデバイスプロバイダ。</summary>
    public const string MonitorProviderName = "d3d12screencapturedeviceprovider";

    private static readonly Dictionary<string, DeviceKind> _byElement = new(StringComparer.Ordinal)
    {
        ["mfvideosrc"] = DeviceKind.Camera,
        ["d3d12screencapturesrc"] = DeviceKind.Monitor,
        ["d3d11screencapturesrc"] = DeviceKind.Monitor,
        ["d3d12testsrc"] = DeviceKind.None,
        ["videotestsrc"] = DeviceKind.None,
    };

    /// <summary>要素名から種別を引く。表に無い要素は <see cref="DeviceKind.None"/>。</summary>
    public static DeviceKind ForElement(string? elementName)
        => elementName is not null && _byElement.TryGetValue(elementName, out DeviceKind kind)
            ? kind
            : DeviceKind.None;

    /// <summary>
    /// その要素名が種別表に<b>明示的に</b>載っているか。
    /// カタログ（<see cref="SrcPipelineBuilder.Sources"/>）との過不足を L1 が縛るために要る
    /// ── <see cref="ForElement"/> は未知の要素も <see cref="DeviceKind.None"/> を返すので、
    /// それだけでは「決め忘れ」と「監視しないと決めた」を区別できない。
    /// </summary>
    public static bool IsKnownElement(string? elementName)
        => elementName is not null && _byElement.ContainsKey(elementName);

    /// <summary>
    /// <c>SrcPipeline</c> 文字列から種別を引く。
    /// 解析できない・空・未知の要素はいずれも <see cref="DeviceKind.None"/>。
    /// </summary>
    public static DeviceKind Classify(string? srcPipeline)
        => ForElement(SrcPipelineBuilder.Parse(srcPipeline).SourceElement);

    /// <summary>その種別を監視するデバイスプロバイダの名前。<see cref="DeviceKind.None"/> は null。</summary>
    public static string? ProviderName(DeviceKind kind) => kind switch
    {
        DeviceKind.Camera => CameraProviderName,
        DeviceKind.Monitor => MonitorProviderName,
        _ => null,
    };

    /// <summary>activity.log に出す種別の綴り（ローカライズしない）。</summary>
    public static string LogName(DeviceKind kind) => kind switch
    {
        DeviceKind.Camera => "camera",
        DeviceKind.Monitor => "monitor",
        _ => "none",
    };
}
