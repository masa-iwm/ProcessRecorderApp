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
/// 画面キャプチャの対象となるモニター 1 台の情報。
///
/// <para>
/// <b><see cref="Path"/> だけが再起動・抜き差しをまたいで安定している。</b>
/// <see cref="Index"/> は並びの中の位置なので前のモニターを抜くと詰まり、
/// <see cref="Handle"/> は実行時の <c>HMONITOR</c> なので保存できない。
/// 設定に書けるのはパス、要素へ渡せるのはハンドル ── その橋渡しが
/// <see cref="MonitorSelection"/> である。
/// </para>
/// </summary>
public sealed class MonitorInfo
{
    /// <summary>
    /// <c>d3d12screencapturesrc</c> / <c>d3d11screencapturesrc</c> の <c>monitor-index</c> と
    /// 同じ並びでの位置（0 始まり）。
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// デバイスパス（<c>\\?\DISPLAY#...</c> の形）。物理モニター＋端子ごとに安定しており、
    /// 上流自身が再 probe 後の同一性判定に使っている。読めなければ空。
    /// </summary>
    public string Path { get; init; } = "";

    /// <summary>
    /// <c>HMONITOR</c>（要素の <c>monitor-handle</c> に渡す値）。読めなければ 0。
    /// <b>実行時のハンドルなので保存してはいけない</b> ── 保存するのは <see cref="Path"/> の方。
    /// </summary>
    public ulong Handle { get; init; }

    /// <summary>物理ピクセルでの大きさ（例 <c>3840x2160</c>）。読めなければ空。</summary>
    public string Resolution { get; init; } = "";
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

    /// <summary>
    /// <see cref="ElementHasProperty"/> の結果（要素名＋プロパティ名 → 有無）。
    /// 問い合わせは要素を1つ作るので、ソースを切り替えるたびに走らせない。
    /// </summary>
    private static readonly Dictionary<string, bool> _propertyProbeCache = new(StringComparer.Ordinal);

    /// <summary>
    /// <b>その要素が実際にそのプロパティを持っているかを実行時に確かめる。</b>
    ///
    /// <para>
    /// GStreamer には「ビルド構成によっては登録されないプロパティ」がある
    /// （<c>gst-inspect-1.0</c> の <c>conditionally available</c>）。
    /// <c>d3d12screencapturesrc</c> の <c>capture-api</c> がまさにそれで、
    /// <b>Windows Graphics Capture を組み込んだビルドにしか生えない</b>
    /// ── 同梱ランタイムの MSVC 版には在り（<c>gstwinrt-1.0-0.dll</c> がある）、
    /// MinGW 版には無い（実測）。
    /// </para>
    /// <para>
    /// <b>形態（MinGW / MSVC）で判定しないこと。</b> 判定材料としては近そうに見えるが、
    /// 実際に効くのは「そのビルドがその機能を持つか」であって、
    /// 利用者が別途入れた GStreamer は同じ形態でも構成が違いうる。
    /// 要素そのものに訊けば、どの経路で解決されたランタイムでも正しい答えになる。
    /// </para>
    /// <para>
    /// GStreamer 初期化前・要素を作れない・その他どんな失敗でも
    /// <see langword="false"/>（＝出さない）へ倒す。
    /// </para>
    /// </summary>
    public static bool ElementHasProperty(string factoryName, string propertyName)
    {
        string key = factoryName + "\u0000" + propertyName;
        lock (_propertyProbeCache)
        {
            if (_propertyProbeCache.TryGetValue(key, out bool cached))
                return cached;
        }

        bool has = Probe();

        lock (_propertyProbeCache)
        {
            _propertyProbeCache[key] = has;
        }
        return has;

        bool Probe()
        {
            if (!DebugLogEx.IsGstInitialized)
                return false;
            try
            {
                // 要素は GObject ラッパーなので Dispose しない（解放はファイナライザ経由）。
                Element? element = ElementFactory.Make(factoryName, name: null);
                if (element is null)
                    return false;

                // 無いプロパティは ArgumentException。返る Value も所有物なので破棄する。
                using Gst.GObject.Value value = element.GetProperty(propertyName);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch
            {
                // 要素が作れない機械（D3D12 が無い等）でも UI は成立させる。
                return false;
            }
        }
    }

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
    /// <b>並びは <c>d3d12screencapturedeviceprovider</c> が返す順</b>をそのまま使う。
    /// プラグイン自身が <c>EnumAdapters1</c> × <c>EnumOutputs</c> を平坦化して並べるので、
    /// <c>monitor-index</c>（<c>gst_d3d12_screen_capture_find_nth_monitor</c>）と同じ順に
    /// なるはずである ── ただし<b>複数アダプター構成での並び順一致は実機未確認</b>で、
    /// 確認が取れるまでは要注意。
    /// <b>読めなかったモニターも空文字で席を残す</b>こと ── 詰めると以降の番号が
    /// 1 つずつずれ、直そうとしている取り違えをそのまま作ってしまう。
    /// </para>
    /// <para>
    /// <b>大きさは caps が運ぶ</b>（<c>width</c> / <c>height</c>）。デバイスの properties の
    /// <c>desktop.coordinates</c> で代用してはいけない ── あちらは DPI 仮想化の値で、
    /// キャプチャが実際に出す大きさと食い違う（実測: 2194x1234 と 3840x2160。
    /// 175% スケーリングの機械。物理ピクセルを持つのは <c>display.coordinates</c> の方）。
    /// </para>
    /// <para>
    /// プロバイダーが無い・起動に失敗した・デバイスが 0 台のいずれでも空を返し、
    /// UI 側は自由入力へ倒れる（そういう機械では <c>d3d12screencapturesrc</c> 自体も動かない）。
    /// </para>
    /// <para>
    /// <b>列挙そのものは <see cref="GetMonitors"/> が 1 本で持ち、これはその射影である。</b>
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> GetMonitorResolutions()
    {
        var monitors = GetMonitors();
        var result = new List<string>(monitors.Count);
        foreach (MonitorInfo monitor in monitors)
            result.Add(monitor.Resolution);
        return result;
    }

    /// <summary>
    /// 接続されているモニターを <c>d3d12screencapturedeviceprovider</c> の並びで列挙する。
    ///
    /// <para>
    /// <b>列挙の経路はここ 1 本だけにしてある。</b> <see cref="GetMonitorResolutions"/> は
    /// この結果の射影である ── プロバイダーを 2 か所から Start/Stop すると、
    /// 並び（＝ <c>monitor-index</c> との対応）が経路ごとにずれうる。
    /// </para>
    /// <para>
    /// <b>読めなかったモニターも席を残す</b>（パスも大きさも空、ハンドルは 0）──
    /// 詰めると以降の <c>monitor-index</c> が 1 つずつずれ、直そうとしている
    /// 取り違えをそのまま作ってしまう。
    /// </para>
    /// <para>
    /// プロバイダーが無い・起動に失敗した・デバイスが 0 台のいずれでも空を返す
    /// （そういう機械では <c>d3d12screencapturesrc</c> 自体も動かない）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        try
        {
            // プロバイダーもデバイスも interned な GObject（GStreamer 側が保持し続ける）なので
            // Dispose しない。
            DeviceProvider? provider = DeviceProviderFactory.GetByName("d3d12screencapturedeviceprovider");
            if (provider is not null && provider.Start())
            {
                try
                {
                    int index = 0;
                    foreach (Device device in provider.GetDevices())
                    {
                        result.Add(new MonitorInfo
                        {
                            Index = index++,
                            Path = ReadDevicePath(device),
                            Handle = ReadMonitorHandle(device),
                            Resolution = ReadMonitorSize(device),
                        });
                    }
                }
                finally
                {
                    provider.Stop();
                }
            }
        }
        catch
        {
            // 取得できなければ空を返し、UI 側は自由入力へフォールバックする
        }

        // **DebugLogEx では見えない**（GST_DEBUG が未設定だと 1 行も出ない）ので
        // activity.log へ出す ── カメラの `camera.devices` と同じ形。
        // **0 台でも 1 行出す。** 「パスで指定したのに解決できない」ときに、
        // 列挙そのものが空だったのか一致しなかっただけなのかは、この行でしか分からない。
        int withPath = 0;
        foreach (MonitorInfo monitor in result)
        {
            if (!string.IsNullOrEmpty(monitor.Path))
                withPath++;
        }
        Components.ActivityLog.Info("monitor.devices", $"count={result.Count} withPath={withPath}");
        return result;
    }

    /// <summary>
    /// モニターの <c>HMONITOR</c> を読む。読めなければ 0（＝ハンドルでは指定できない）。
    ///
    /// <para>
    /// 大きさやパスと違い、これはデバイスの properties ではなく<b>デバイスの GObject
    /// プロパティ</b>（<c>monitor-handle</c>、<c>guint64</c>）に在る ── 要素側の
    /// <c>monitor-handle</c> と同じ綴り・同じ型で、プロバイダーが作った要素へ
    /// 渡しているのもこの値である。
    /// </para>
    /// <para>
    /// <see cref="Gst.GObject.Object.GetProperty"/> が返す <see cref="Gst.GObject.Value"/> は
    /// 呼び出し側の所有物なので破棄する。プロパティを持たないビルドでは
    /// <see cref="ArgumentException"/> になるが、そこは静かに 0 へ倒す
    /// ── 呼び出し側（<see cref="MonitorSelection"/>）が縮退の道を持っている。
    /// </para>
    /// </summary>
    private static ulong ReadMonitorHandle(Device device)
    {
        try
        {
            using Gst.GObject.Value value = device.GetProperty("monitor-handle");
            return value.GetUInt64();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 画面キャプチャデバイス 1 台の大きさを caps から読む。読めなければ空文字
    /// （<b>席は残す</b> ── 詰めると <c>monitor-index</c> がずれる）。
    /// </summary>
    private static string ReadMonitorSize(Device device)
    {
        using Caps? caps = device.GetCaps();
        if (caps is null || caps.GetSize() == 0)
            return "";

        // 画面キャプチャのデバイスは 1 台 = 1 構造体で、大きさはその画面の実寸に固定されている。
        using Structure structure = caps.GetStructure(0);
        return structure.GetInt("width", out int width)
            && structure.GetInt("height", out int height)
            && 0 < width && 0 < height
            ? $"{width}x{height}"
            : "";
    }

    /// <summary>
    /// <c>mfvideosrc</c> 対応のビデオ入力デバイス一覧と、それぞれの対応 caps を取得する。
    ///
    /// <para>
    /// <c>gst_device_provider_get_devices</c> が返す <c>GList</c> は、バインディングが
    /// <b>要素ポインタを全部控えてからスパイン（リスト本体）を解放し、そのあとで包む</b>
    /// 経路で扱う（<c>GListMarshal.CollectAndFreeSpine</c>）。GirCore ではこの経路が
    /// マネージドヒープを壊し、カメラのある機械でだけ後から無関係な場所で落ちていたが、
    /// GstSharp.Net preview2 で解消済み（実測: 再列挙 200 回で安定）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<VideoDeviceInfo> GetVideoSourceDevices()
    {
        var result = new List<VideoDeviceInfo>();

        // Media Foundation のデバイスプロバイダのみを使う。
        // (DeviceMonitor は ksvideosrc など他プロバイダも列挙し、同一カメラが重複するため。
        //  mfdeviceprovider を直接使うことで mfvideosrc の device-index 並びとも一致する)
        // **DeviceProviderFactory.GetByName が返すのは GstDeviceProvider であって
        // factory ではない**（factory 自体が要るときは DeviceProviderFactory.Find）。
        // プロバイダーもデバイスも interned な GObject なので Dispose しない。
        DeviceProvider? provider = DeviceProviderFactory.GetByName("mfdeviceprovider");
        if (provider is null)
        {
            DebugLogEx.Log(DebugLevel.Warning, "mfdeviceprovider is not available; no camera choices");
            return result;
        }

        if (!provider.Start())
            return result;

        try
        {
            foreach (Device device in provider.GetDevices())
            {
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
            }
        }
        finally
        {
            provider.Stop();
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

    private static VideoDeviceInfo ReadDevice(Device device)
    {
        string name = device.GetDisplayName();

        // 挿入順を保ちつつ重複排除する
        var formats = new List<string>();
        var resolutions = new List<string>();
        var framerates = new List<string>();

        static void AddUnique(List<string> to, string? value)
        {
            if (!string.IsNullOrEmpty(value) && !to.Contains(value))
                to.Add(value);
        }

        // Caps は MiniObject、GetStructure(index) は Boxed の**所有コピー**を返すので、
        // どちらも破棄する（GST0001 が見張っている）。
        using Caps? caps = device.GetCaps();
        if (caps is not null)
        {
            uint size = caps.GetSize();
            for (uint s = 0; s < size; s++)
            {
                using Structure structure = caps.GetStructure(s);

                AddUnique(formats, structure.GetString("format"));

                if (structure.GetInt("width", out int w)
                    && structure.GetInt("height", out int h))
                    AddUnique(resolutions, $"{w}x{h}");

                if (structure.GetFraction("framerate", out int num, out int den) && den != 0)
                    AddUnique(framerates, $"{num}/{den}");
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
    /// キー名は <c>mfdeviceprovider</c> が付ける <c>device.path</c>。
    /// 読めなくても致命的ではない（カメラ設定が使えないだけ）ので、静かに空を返す。
    /// </para>
    /// </summary>
    private static string ReadDevicePath(Device device)
    {
        // GetProperties は所有する GstStructure を返す（Boxed なので破棄する）。
        using Structure? properties = device.GetProperties();
        return properties?.GetString("device.path") ?? "";
    }
}
