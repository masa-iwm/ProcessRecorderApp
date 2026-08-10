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

    /// <summary>
    /// 接続されているモニターの<b>物理ピクセル</b>での大きさ（例: <c>3840x2160</c>）を
    /// <c>EnumDisplayDevices</c> の並びで返す。
    ///
    /// <para>
    /// <b>GStreamer のデバイスプロバイダを使ってはいけない。</b> 一度そう実装したところ、
    /// <c>gst_device_provider_get_devices</c> の GList を辿る経路でメモリを壊し、
    /// <b>パイプライン編集ダイアログでソースを切り替えただけでアプリが落ちた</b>
    /// （<c>Microsoft.UI.Xaml.dll</c> の 0xc0000005 と、
    /// <c>GetDynamicChoices → ToArray</c> でのアクセス違反を実測）。
    /// ここは Win32 だけで済ませる。
    /// </para>
    /// <para>
    /// <b><c>GetMonitorInfo</c> でも代用してはいけない。</b> あちらは DPI 仮想化の影響を
    /// 受け、キャプチャが実際に出す大きさと食い違う（実測: 2194x1234 と 3840x2160。
    /// 175% スケーリングの機械）。<c>EnumDisplaySettings</c> が返すのは<b>ディスプレイの
    /// 現在のモード</b>で、プロセスの DPI 認識に関係なく物理ピクセルである。
    /// </para>
    /// <para>
    /// 並びが <c>d3d12screencapturesrc</c> の <c>monitor-index</c>（DXGI の出力列挙）と
    /// 完全に一致する保証は無い。呼び出し側は「その番号の値」だけに依存せず、
    /// <b>全モニターの大きさを選択肢として出す</b>こと。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> GetMonitorResolutions()
    {
        var result = new List<string>();
        try
        {
            unsafe
            {
                for (uint i = 0; ; i++)
                {
                    DISPLAY_DEVICE device = default;
                    device.cb = sizeof(DISPLAY_DEVICE);
                    if (!EnumDisplayDevices(null, i, &device, 0))
                        break;

                    // 画面に出ていないアダプターと、ミラードライバ（画面を複製するだけの
                    // 仮想デバイス。desktop に付いた状態で列挙されうる）は飛ばす
                    if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0
                        || (device.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                        continue;

                    DEVMODE mode = default;
                    mode.dmSize = (ushort)sizeof(DEVMODE);
                    if (EnumDisplaySettings(device.DeviceName, ENUM_CURRENT_SETTINGS, &mode)
                        && 0 < mode.dmPelsWidth && 0 < mode.dmPelsHeight)
                    {
                        result.Add($"{mode.dmPelsWidth}x{mode.dmPelsHeight}");
                    }
                }
            }
        }
        catch
        {
            // 取得できなければ空を返し、UI 側は自由入力へフォールバックする
        }
        return result;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

    // **文字列は fixed バッファで持つ（ByValTStr ではない）。** ソース生成の P/Invoke
    // （LibraryImport）は実行時マーシャリングを必要とする型を扱えず、SYSLIB1051 になる。
    // blittable にしておけば AOT でもそのまま渡せる。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DISPLAY_DEVICE
    {
        public int cb;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public uint StateFlags;
        public fixed char DeviceID[128];
        public fixed char DeviceKey[128];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DEVMODE
    {
        public fixed char dmDeviceName[32];
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        public fixed char dmFormName[32];
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumDisplayDevices(string? lpDevice, uint iDevNum, DISPLAY_DEVICE* lpDisplayDevice, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumDisplaySettings(char* lpszDeviceName, int iModeNum, DEVMODE* lpDevMode);

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
