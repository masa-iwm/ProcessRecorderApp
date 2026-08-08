using Gst;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ProcessRecorderApp.GStreamer;

/// <summary>1 台のビデオ入力デバイスから得られた選択肢情報。</summary>
public sealed class VideoDeviceInfo
{
    public required string Name { get; init; }
    /// <summary>デバイスが提供する format 値(例: NV12)。</summary>
    public IReadOnlyList<string> Formats { get; init; } = Array.Empty<string>();
    /// <summary>デバイスが提供する解像度(例: 1920x1080)。</summary>
    public IReadOnlyList<string> Resolutions { get; init; } = Array.Empty<string>();
    /// <summary>デバイスが提供するフレームレート(例: 30/1)。</summary>
    public IReadOnlyList<string> Framerates { get; init; } = Array.Empty<string>();
}

/// <summary>
/// GStreamer / OS の実行時情報を取得するヘルパー。
/// モニター数や Media Foundation ビデオ入力デバイスの一覧・対応 caps を、パイプラインビルダー UI の
/// 動的な選択肢として提供する。GStreamer 初期化(<see cref="Controller.StaticInitialize"/>)後に呼ぶこと。
/// すべて失敗時は安全な既定値を返す(例外は投げない)。
/// </summary>
public static partial class GstIntrospect
{
    private const int SM_CMONITORS = 80; // GetSystemMetrics: number of display monitors

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    /// <summary>接続されているモニター数を返す(取得できない場合は 1)。</summary>
    public static int GetMonitorCount()
    {
        try
        {
            int count = GetSystemMetrics(SM_CMONITORS);
            return count > 0 ? count : 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>mfvideosrc 対応のビデオ入力デバイス一覧と、それぞれの対応 caps を取得する。</summary>
    public static IReadOnlyList<VideoDeviceInfo> GetVideoSourceDevices()
    {
        var result = new List<VideoDeviceInfo>();
        try
        {
            // Media Foundation のデバイスプロバイダのみを使う。
            // (DeviceMonitor は ksvideosrc など他プロバイダも列挙し、同一カメラが重複するため。
            //  mfdeviceprovider を直接使うことで mfvideosrc の device-index 並びとも一致する)
            using var provider = DeviceProviderFactory.GetByName("mfdeviceprovider");
            if (provider is null)
                return result;

            provider.Start();
            try
            {
                var list = provider.GetDevices();
                if (list is not null)
                {
                    try
                    {
                        uint n = GLib.List.Length(list);
                        for (uint i = 0; i < n; i++)
                        {
                            IntPtr ptr = GLib.List.NthData(list, i);
                            if (ptr == IntPtr.Zero)
                                continue;

                            // 1台の異常（表示名・caps のマーシャリング失敗）で列挙全体を
                            // 空にしない ── その1台だけ飛ばして続行する。
                            // owned: true の参照は using が解放する。
                            try
                            {
                                using var device = Device.NewFromPointer(ptr, true);
                                result.Add(ReadDevice(device));
                            }
                            catch (Exception ex)
                            {
                                DebugLogEx.Log(DebugLevel.Warning,
                                    $"introspection failed for one device; skipped\n{ex}");
                            }
                        }
                    }
                    finally
                    {
                        // 例外で抜けてもリストノードは解放する（消費済みの要素は using が解放済み）。
                        GLib.List.Free(list);
                    }
                }
            }
            finally
            {
                provider.Stop();
            }
        }
        catch
        {
            // 内省に失敗した場合は空一覧を返し、UI 側は自由入力へフォールバックする
        }
        return result;
    }

    // 1 デバイスの表示名と caps 構造から format/解像度/framerate を収集する
    private static VideoDeviceInfo ReadDevice(Device device)
    {
        string name = device.GetDisplayName() ?? "";
        // 挿入順を保ちつつ重複排除する
        var formats = new List<string>();
        var resolutions = new List<string>();
        var framerates = new List<string>();

        void AddUnique(List<string> to, string value)
        {
            if (!string.IsNullOrEmpty(value) && !to.Contains(value))
                to.Add(value);
        }

        try
        {
            using var caps = device.GetCaps();
            if (caps is not null)
            {
                uint size = caps.GetSize();
                for (uint s = 0; s < size; s++)
                {
                    // GetStructure は caps 所有の借用参照のため Dispose しない
                    var st = caps.GetStructure(s);
                    if (st is null)
                        continue;

                    string? fmt = st.GetString("format");
                    if (fmt is not null)
                        AddUnique(formats, fmt);

                    if (st.GetInt("width", out int w) && st.GetInt("height", out int h))
                        AddUnique(resolutions, $"{w}x{h}");

                    if (st.GetFraction("framerate", out int num, out int den) && den != 0)
                        AddUnique(framerates, $"{num}/{den}");
                }
            }
        }
        catch
        {
            // caps 読み取り失敗時は取得済み分のみ返す
        }

        return new VideoDeviceInfo
        {
            Name = name,
            Formats = formats,
            Resolutions = resolutions,
            Framerates = framerates,
        };
    }
}
