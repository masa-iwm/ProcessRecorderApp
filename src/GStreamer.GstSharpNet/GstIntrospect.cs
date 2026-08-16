using Gst;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ProcessRecorderApp.GStreamer;

/// <summary>1 台のビデオ入力デバイスから得られた選択肢情報。</summary>
public sealed class VideoDeviceInfo
{
    public required string Name { get; init; }

    /// <summary>
    /// デバイスパス（<c>mfvideosrc</c> の <c>device-path</c> ＝ Media Foundation の
    /// シンボリックリンク）。読めなければ空。
    ///
    /// <para>
    /// <b>カメラ設定（<c>CameraControl</c>）はこの値でデバイスを開く。</b>
    /// 表示名や <c>device-index</c> からの逆引きには頼れない ── <c>mfdeviceprovider</c> の
    /// 並びと MF の列挙順が一致する保証はどこにも無い。
    /// </para>
    /// </summary>
    public string Path { get; init; } = "";

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
    /// 接続されているモニターの<b>物理ピクセル</b>での大きさ（例: <c>3840x2160</c>）を、
    /// <c>d3d12screencapturesrc</c> の <c>monitor-index</c> と<b>同じ並び</b>で返す
    /// （<c>d3d11screencapturesrc</c> も同じ並び ── 上流の
    /// <c>gst_d3d11_screen_capture_find_nth_monitor</c> は D3D12 側と同じ走査である）。
    ///
    /// <para>
    /// <b>並びは DXGI の <c>EnumAdapters1</c> × <c>EnumOutputs</c> を平坦化した順</b>である
    /// （上流 <c>gst_d3d12_screen_capture_find_nth_monitor</c> と同じ走査）。
    /// <c>EnumDisplayDevices</c> や <c>EnumDisplayMonitors</c> の順ではない ──
    /// アダプターが複数ある機械で番号がずれる。
    /// <b>読めなかったモニターも空文字で席を残す</b>こと ── 詰めると以降の番号が
    /// 1 つずつずれ、直そうとしている取り違えをそのまま作ってしまう。
    /// </para>
    /// <para>
    /// <b>大きさは <c>EnumDisplaySettings</c>（<c>ENUM_CURRENT_SETTINGS</c>）で取る。</b>
    /// キャプチャ側も同じ値を使っている（上流の <c>gst_d3d12_dxgi_capture_open</c> は
    /// <c>dmPelsWidth</c> / <c>dmPelsHeight</c> から大きさを決める）。
    /// <c>GetMonitorInfo</c> の <c>rcMonitor</c> や <c>DXGI_OUTPUT_DESC.DesktopCoordinates</c> で
    /// 代用してはいけない ── DPI 仮想化の影響を受け、キャプチャが実際に出す大きさと
    /// 食い違う（実測: 2194x1234 と 3840x2160。175% スケーリングの機械）。
    /// </para>
    /// <para>
    /// <b>GStreamer のデバイスプロバイダで取ってはいけない。</b> 一度そう実装したところ、
    /// <c>gst_device_provider_get_devices</c> の <c>GList</c> を gir-core で包む経路が
    /// メモリを壊し、ダイアログでソースを切り替えただけでアプリが落ちた。
    /// </para>
    /// <para>
    /// 取得できなければ空を返し、UI 側は自由入力へ倒れる（<c>CreateDXGIFactory1</c> が
    /// 失敗する機械では <c>d3d12screencapturesrc</c> 自体も動かない）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> GetMonitorResolutions()
    {
        var result = new List<string>();
        try
        {
            unsafe
            {
                Guid iid = IID_IDXGIFactory1;
                IntPtr factory;
                if (CreateDXGIFactory1(&iid, &factory) != 0 || factory == IntPtr.Zero)
                    return result;

                try
                {
                    for (uint adapterIndex = 0; ; adapterIndex++)
                    {
                        IntPtr adapter;
                        if (EnumAdapters1(factory, adapterIndex, &adapter) != 0 || adapter == IntPtr.Zero)
                            break;

                        try
                        {
                            for (uint outputIndex = 0; ; outputIndex++)
                            {
                                IntPtr output;
                                if (EnumOutputs(adapter, outputIndex, &output) != 0 || output == IntPtr.Zero)
                                    break;

                                try
                                {
                                    result.Add(ReadOutputSize(output));
                                }
                                finally
                                {
                                    ComRelease(output);
                                }
                            }
                        }
                        finally
                        {
                            ComRelease(adapter);
                        }
                    }
                }
                finally
                {
                    ComRelease(factory);
                }
            }
        }
        catch
        {
            // 取得できなければ空を返し、UI 側は自由入力へフォールバックする
        }
        return result;
    }

    /// <summary>
    /// 1 つの DXGI 出力から物理ピクセルの大きさを読む。読めなければ空文字
    /// （<b>席は残す</b> ── 上流の走査は <c>GetDesc</c> / <c>GetMonitorInfo</c> の
    /// 失敗だけを数えずに飛ばすので、そこは呼び出し側でも数えない）。
    /// </summary>
    private static unsafe string ReadOutputSize(IntPtr output)
    {
        DXGI_OUTPUT_DESC desc;
        if (GetOutputDesc(output, &desc) != 0)
            return "";

        MONITORINFOEXW info = default;
        info.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (!GetMonitorInfo(desc.Monitor, &info))
            return "";

        DEVMODE mode = default;
        mode.dmSize = (ushort)sizeof(DEVMODE);
        return EnumDisplaySettings(info.szDevice, ENUM_CURRENT_SETTINGS, &mode)
            && 0 < mode.dmPelsWidth && 0 < mode.dmPelsHeight
            ? $"{mode.dmPelsWidth}x{mode.dmPelsHeight}"
            : "";
    }

    private const int ENUM_CURRENT_SETTINGS = -1;

    // {770aae78-f26f-4dba-a829-253c83d1b387}（Windows SDK の dxgi.h と一致）
    private static readonly Guid IID_IDXGIFactory1 =
        new(0x770aae78, 0xf26f, 0x4dba, 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87);

    // COM の vtable のスロット番号。**Windows SDK の dxgi.h の Vtbl 構造体から数えた値**で、
    // 間違えると別の関数を呼んでアクセス違反になる。NativeStructSizes と同じく検査で固定する。
    private const int SlotRelease = 2;              // IUnknown::Release
    private const int SlotEnumAdapters1 = 12;       // IDXGIFactory1::EnumAdapters1
    private const int SlotEnumOutputs = 7;          // IDXGIAdapter::EnumOutputs
    private const int SlotGetOutputDesc = 7;        // IDXGIOutput::GetDesc

    private static unsafe void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    private static unsafe int EnumAdapters1(IntPtr factory, uint index, IntPtr* adapter)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, SlotEnumAdapters1))(
            factory, index, adapter);

    private static unsafe int EnumOutputs(IntPtr adapter, uint index, IntPtr* output)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(adapter, SlotEnumOutputs))(
            adapter, index, output);

    private static unsafe int GetOutputDesc(IntPtr output, DXGI_OUTPUT_DESC* desc)
        => ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)Slot(output, SlotGetOutputDesc))(
            output, desc);

    private static unsafe uint ComRelease(IntPtr obj)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, SlotRelease))(obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
    }

    // **文字列は fixed バッファで持つ（ByValTStr ではない）。** ソース生成の P/Invoke
    // （LibraryImport）は実行時マーシャリングを必要とする型を扱えず、SYSLIB1051 になる。
    // blittable にしておけば AOT でもそのまま渡せる。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
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

    [LibraryImport("dxgi.dll")]
    private static unsafe partial int CreateDXGIFactory1(Guid* riid, IntPtr* ppFactory);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetMonitorInfo(IntPtr monitor, MONITORINFOEXW* info);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumDisplaySettings(char* lpszDeviceName, int iModeNum, DEVMODE* lpDevMode);

    /// <summary>
    /// <c>mfvideosrc</c> 対応のビデオ入力デバイス一覧と、それぞれの対応 caps を取得する。
    ///
    /// <para>
    /// <b>マネージドのオブジェクト束縛を通してはいけない。</b> gir-core では
    /// <c>gst_device_provider_get_devices</c> が返す <c>GList</c> を <c>Device.NewFromPointer</c>
    /// で包む書き方がマネージドヒープを壊し、
    /// <b>あとから無関係な場所でアクセス違反になる</b>（実測: <c>ucrtbase.dll</c> の
    /// 0xc0000409 と、CoreCLR の FailFast が示す <c>DispatchResolve.FindImplSlotInSimpleMap</c>
    /// でのアクセス違反）。
    /// <b>カメラが 1 台も無い機械では起きない</b> ── <c>GList</c> が空で走査に入らないので、
    /// カメラの無い CI と開発機は緑のまま、利用者の機械でだけ落ちる。
    /// </para>
    /// <para>
    /// そのためここは C の API を直接呼ぶ。GstSharp.Net preview1 にデバイス列挙の
    /// バインディングは無い（preview2 受け入れの筆頭）。所有権は
    /// <c>gst_device_provider_get_devices</c> が transfer full ── 各要素を
    /// <c>gst_object_unref</c> し、リスト自体を <c>g_list_free</c> する。
    /// </para>
    /// </summary>
    public static IReadOnlyList<VideoDeviceInfo> GetVideoSourceDevices()
    {
        var result = new List<VideoDeviceInfo>();

        // Media Foundation のデバイスプロバイダのみを使う。
        // (DeviceMonitor は ksvideosrc など他プロバイダも列挙し、同一カメラが重複するため。
        //  mfdeviceprovider を直接使うことで mfvideosrc の device-index 並びとも一致する)
        // **gst_device_provider_factory_get_by_name が返すのは GstDeviceProvider であって
        // factory ではない**（transfer full）。factory と取り違えて
        // gst_device_provider_factory_get へ渡すと NULL が返り、**選択肢が黙って空になる**。
        IntPtr provider = gst_device_provider_factory_get_by_name("mfdeviceprovider");
        if (provider == IntPtr.Zero)
        {
            DebugLogEx.Log(DebugLevel.Warning, "mfdeviceprovider is not available; no camera choices");
            return result;
        }

        try
        {
            if (!gst_device_provider_start(provider))
                return result;

            IntPtr list = gst_device_provider_get_devices(provider);
            try
            {
                // GList は { data, next, prev }。next は 1 ポインタぶん先。
                for (IntPtr node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                {
                    IntPtr device = Marshal.ReadIntPtr(node, 0);
                    if (device == IntPtr.Zero)
                        continue;

                    try
                    {
                        result.Add(ReadDevice(device));
                    }
                    catch (Exception ex)
                    {
                        // 1台の異常で列挙全体を空にしない ── その1台だけ飛ばして続行する。
                        DebugLogEx.Log(DebugLevel.Warning,
                            $"introspection failed for one device; skipped\n{ex}");
                    }

                    gst_object_unref(device);
                }
            }
            finally
            {
                if (list != IntPtr.Zero)
                    g_list_free(list);
            }
        }
        finally
        {
            gst_device_provider_stop(provider);
            gst_object_unref(provider);
        }

        // **DebugLogEx では見えない。** あちらは gst_debug_log 経由なので
        // GST_DEBUG が未設定だと 1 行も出ない ── カメラの確認手順が
        // 「debug.log に count= が出る」を前提にしていたのに、既定の起動では
        // どこにも出ていなかった（実機で指摘された）。
        // activity.log なら Log 画面へも複写されるので、押したその場で見える。
        int withPath = 0;
        foreach (var device in result)
        {
            if (!string.IsNullOrWhiteSpace(device.Path))
                withPath++;
        }
        Components.ActivityLog.Info("camera.devices", $"count={result.Count} withPath={withPath}");
        return result;
    }

    private static VideoDeviceInfo ReadDevice(IntPtr device)
    {
        string name = TakeString(gst_device_get_display_name(device));

        // 挿入順を保ちつつ重複排除する
        var formats = new List<string>();
        var resolutions = new List<string>();
        var framerates = new List<string>();

        static void AddUnique(List<string> to, string? value)
        {
            if (!string.IsNullOrEmpty(value) && !to.Contains(value))
                to.Add(value);
        }

        IntPtr caps = gst_device_get_caps(device);
        if (caps != IntPtr.Zero)
        {
            try
            {
                uint size = gst_caps_get_size(caps);
                for (uint s = 0; s < size; s++)
                {
                    // caps 所有の借用参照（transfer none）なので解放しない
                    IntPtr st = gst_caps_get_structure(caps, s);
                    if (st == IntPtr.Zero)
                        continue;

                    AddUnique(formats, Marshal.PtrToStringUTF8(gst_structure_get_string(st, "format")));

                    if (gst_structure_get_int(st, "width", out int w)
                        && gst_structure_get_int(st, "height", out int h))
                        AddUnique(resolutions, $"{w}x{h}");

                    if (gst_structure_get_fraction(st, "framerate", out int num, out int den) && den != 0)
                        AddUnique(framerates, $"{num}/{den}");
                }
            }
            finally
            {
                gst_mini_object_unref(caps);
            }
        }

        return new VideoDeviceInfo
        {
            Name = name,
            Path = ReadDevicePath(device),
            Formats = formats,
            Resolutions = resolutions,
            Framerates = framerates,
        };
    }

    /// <summary>
    /// デバイスパス（MF のシンボリックリンク）を読む。読めなければ空文字。
    ///
    /// <para>
    /// <b><c>gst_device_get_properties</c> が返す <c>GstStructure*</c> は transfer full で、
    /// 解放は <c>gst_structure_free</c>。</b> <c>gst_mini_object_unref</c> ではない ──
    /// <c>GstStructure</c> は <c>GstMiniObject</c> ではないので、取り違えると
    /// <b>カメラのある機械でだけヒープが壊れる</b>（この周辺は既に 2 回やられている。
    /// <c>docs/coverage-gaps.md</c>）。
    /// </para>
    /// <para>
    /// キー名は <c>mfdeviceprovider</c> が付ける <c>device.path</c>。
    /// 読めなくても致命的ではない（カメラ設定が使えないだけ）ので、静かに空を返す。
    /// </para>
    /// </summary>
    private static string ReadDevicePath(IntPtr device)
    {
        IntPtr properties = gst_device_get_properties(device);
        if (properties == IntPtr.Zero)
            return "";

        try
        {
            return Marshal.PtrToStringUTF8(gst_structure_get_string(properties, "device.path")) ?? "";
        }
        finally
        {
            gst_structure_free(properties);
        }
    }

    /// <summary>transfer full の <c>gchar*</c> を文字列にして解放する。</summary>
    private static string TakeString(IntPtr utf8)
    {
        if (utf8 == IntPtr.Zero)
            return "";
        try
        {
            return Marshal.PtrToStringUTF8(utf8) ?? "";
        }
        finally
        {
            g_free(utf8);
        }
    }

    // "Gst" / "GLib" は論理名。Controller.StaticInitialize が
    // Gst.Interop.NativeLoader.EnsureRegistered で登録するリゾルバが、バインディング本体と
    // 同じ固定ディレクトリの DLL に解決する（フレーバー混在は起きない）。未知の名前
    // (dxgi.dll / user32.dll) には関与しない。
    [LibraryImport("Gst", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr gst_device_provider_factory_get_by_name(string factoryname);

    [LibraryImport("Gst")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool gst_device_provider_start(IntPtr provider);

    [LibraryImport("Gst")]
    private static partial void gst_device_provider_stop(IntPtr provider);

    [LibraryImport("Gst")]
    private static partial IntPtr gst_device_provider_get_devices(IntPtr provider);

    [LibraryImport("Gst")]
    private static partial IntPtr gst_device_get_display_name(IntPtr device);

    /// <summary>transfer full の <c>GstStructure*</c>（解放は gst_structure_free）。</summary>
    [LibraryImport("Gst")]
    private static partial IntPtr gst_device_get_properties(IntPtr device);

    /// <summary><c>GstStructure</c> は MiniObject ではないので専用の解放関数を使う。</summary>
    [LibraryImport("Gst")]
    private static partial void gst_structure_free(IntPtr structure);

    [LibraryImport("Gst")]
    private static partial IntPtr gst_device_get_caps(IntPtr device);

    [LibraryImport("Gst")]
    private static partial uint gst_caps_get_size(IntPtr caps);

    [LibraryImport("Gst")]
    private static partial IntPtr gst_caps_get_structure(IntPtr caps, uint index);

    [LibraryImport("Gst", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr gst_structure_get_string(IntPtr structure, string fieldname);

    [LibraryImport("Gst", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool gst_structure_get_int(IntPtr structure, string fieldname, out int value);

    [LibraryImport("Gst", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool gst_structure_get_fraction(IntPtr structure, string fieldname,
        out int numerator, out int denominator);

    [LibraryImport("Gst")]
    private static partial void gst_object_unref(IntPtr obj);

    [LibraryImport("Gst")]
    private static partial void gst_mini_object_unref(IntPtr obj);

    [LibraryImport("GLib")]
    private static partial void g_free(IntPtr mem);

    [LibraryImport("GLib")]
    private static partial void g_list_free(IntPtr list);

    /// <summary>
    /// P/Invoke で使う構造体の大きさ。Windows の定義とずれると、
    /// <b>スタックの隣を書き潰してあとから無関係な場所で落ちる</b>ので検査で固定する。
    /// </summary>
    internal static unsafe (int OutputDesc, int MonitorInfo, int Devmode) NativeStructSizes()
        => (sizeof(DXGI_OUTPUT_DESC), sizeof(MONITORINFOEXW), sizeof(DEVMODE));

    /// <summary>COM の vtable のスロット番号。dxgi.h の Vtbl 構造体から数えた値。</summary>
    internal static (int Release, int EnumAdapters1, int EnumOutputs, int GetOutputDesc) NativeVtableSlots()
        => (SlotRelease, SlotEnumAdapters1, SlotEnumOutputs, SlotGetOutputDesc);
}
