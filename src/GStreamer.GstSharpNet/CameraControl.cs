using System;
using System.Collections.Generic;
using System.Text;
using Windows.Win32;
using Windows.Win32.Media.DirectShow;
using Windows.Win32.Media.MediaFoundation;

namespace ProcessRecorderApp.GStreamer;

/// <summary>
/// 1 項目の「範囲＋現在値」の写し。<b>COM に触れない素のデータ</b>なので、
/// 専用スレッドから UI スレッドへ安全に渡せる。
/// </summary>
/// <param name="Def">項目の定義。</param>
/// <param name="Range">ドライバが申告した可動範囲。</param>
/// <param name="Value">読み取り時点の値（読めなければ既定値）。</param>
/// <param name="Auto">読み取り時点で自動だったか。</param>
public sealed record CameraControlSnapshot(
    CameraControlDef Def, CameraControlRange Range, int Value, bool Auto);

/// <summary>ドライバが申告した 1 項目の可動範囲。</summary>
/// <param name="Minimum">最小値。</param>
/// <param name="Maximum">最大値。</param>
/// <param name="Step">刻み（これ未満は動かせない）。</param>
/// <param name="Default">既定値。</param>
/// <param name="SupportsAuto">自動（auto）に対応しているか。</param>
public sealed record CameraControlRange(int Minimum, int Maximum, int Step, int Default, bool SupportsAuto);

/// <summary>
/// カメラ（<c>mfvideosrc</c>）のフォーカス・明るさ等を Windows のカメラ制御 COM から操作する。
///
/// <para>
/// <b>GStreamer 側に手段が無い。</b> 実機の 1.28.6 で確認したところ、<c>mfvideosrc</c> の
/// 公開プロパティは <c>device-index</c> / <c>device-name</c> / <c>device-path</c> などだけで、
/// カメラ制御のプロパティは 1 つも無く、<c>GstColorBalance</c> も <c>GstPhotography</c> も
/// 実装していない（<c>gst-inspect-1.0</c> に <c>Implemented Interfaces:</c> の節が出ない）。
/// <c>ksvideosrc</c> も同じで、しかも <c>libgstwinks.dll</c> は同梱ランタイムに無い。
/// したがって Media Foundation のデバイスソースから
/// <c>IAMVideoProcAmp</c> / <c>IAMCameraControl</c> を取り出して直接叩くしかない。
/// </para>
/// <para>
/// <b>COM の呼び出しは CsWin32 の生成に任せる</b>（<c>NativeMethods.json</c> の
/// <c>allowMarshaling: false</c> により、インターフェイスは <c>lpVtbl</c> を持つ構造体として
/// 生成される ＝ AOT 安全で、しかも<b>スロット番号も引数幅もメタデータから正しく決まる</b>）。
/// 手で書くと危ない ── <c>GetRange</c> / <c>Set</c> / <c>Get</c> の C の <c>long</c> は
/// <b>4 バイト（C# の <c>int</c>）</b>で、<c>nint</c> や <c>long</c> にすると
/// スタックの隣を書き潰して後から無関係な場所で落ちる。IID も同様で、
/// <c>IAMVideoProcAmp</c> は <c>C6E13360-…</c>、<c>IAMCameraControl</c> は <c>C6E13370-…</c> と
/// 1 文字違いである。
/// </para>
/// <para>
/// <b>ポインタをスレッドをまたいで持ち回らない。</b> 1 回の呼び出し（開く → 設定する → 解放する）
/// に閉じるか、<see cref="CameraControlSession"/> を作ったスレッドだけで使うこと。
/// </para>
/// <para>
/// <b>この経路は開発機では検証できない</b>（カメラが無い）。
/// 手順は <c>docs/coverage-gaps.md</c> の「カメラ」の節にある。
/// </para>
/// </summary>
/// <summary><see cref="CameraControl.ResolveDevicePath"/> の結果。</summary>
public enum CameraDeviceResolution
{
    /// <summary>デバイスパスが取れた。</summary>
    Ok,

    /// <summary>ソースがカメラ（<c>mfvideosrc</c>）ではない ── カメラ設定は関係しない。</summary>
    NotACamera,

    /// <summary>
    /// カメラだが、どのカメラかがパイプラインに書かれていない
    /// （<c>device-path</c> も <c>device-name</c> も <c>device-index</c> も無い）。
    /// </summary>
    NoDevicePath,

    /// <summary>
    /// どのカメラかは書かれているが、その機械で見つからない
    /// （取り外されている・名前が変わった・番号が範囲外）。
    ///
    /// <para>
    /// <b><see cref="NoDevicePath"/> と分ける。</b> 一緒にすると、カメラを抜いただけなのに
    /// 「パイプライン編集ダイアログで選び直してください」と案内することになり、
    /// <b>原因と対処が噛み合わない</b>（実機で指摘された）。
    /// </para>
    /// </summary>
    DeviceNotFound,
}

public static unsafe class CameraControl
{
    /// <summary>カメラ制御を当てられるソース要素（これ以外では何もしない）。</summary>
    public const string SourceElement = "mfvideosrc";

    /// <summary>
    /// パイプライン文字列からカメラのデバイスパス（MF のシンボリックリンク）を求める。
    ///
    /// <para>
    /// <b>解決規則をここ 1 箇所に置く。</b> 「どのカメラを開くか」の判断が
    /// 適用側（<c>EventRecorder.ApplyCameraControls</c>）と編集ダイアログ側
    /// （<c>MainPage.BuildCameraControlsAsync</c>）に分かれていると、
    /// <b>ダイアログで見ている値と実際に適用される値が別のデバイスを指しうる</b>
    /// ── 実際に、入力（<c>ActualSrcPipeline</c> を見るか否か）と
    /// カメラ判定の有無が食い違っていた。
    /// </para>
    /// <para>
    /// <b>優先順は <c>device-path</c> → <c>device-name</c> → <c>device-index</c>。</b>
    /// パスが書いてあればそれが最も確実だが、<b>実際の設定はたいてい名前か番号で書かれている</b>
    /// ── パイプライン編集ダイアログがその 2 つを選ばせるからである。
    /// パスだけを受け付ける実装にしていたため、<b>通常の構成ではカメラ設定が
    /// まったく使えなかった</b>（実機で発覚）。
    /// </para>
    /// <para>
    /// 名前・番号からの逆引きには <c>GstIntrospect.GetVideoSourceDevices()</c> を使う。
    /// これは <c>mfdeviceprovider</c> を直接列挙したもので、<b><c>mfvideosrc</c> の
    /// <c>device-index</c> と同じ並び</b>である（<c>GstIntrospect</c> がそのために
    /// <c>GstDeviceMonitor</c> を避けている）。同名のカメラが複数ある場合は先勝ちなので、
    /// 確実に指したいときはパイプライン編集ダイアログで <c>device-path</c> を選ぶこと。
    /// </para>
    /// </summary>
    public static CameraDeviceResolution ResolveDevicePath(string? srcPipeline, out string? devicePath)
    {
        devicePath = null;

        var parsed = SrcPipelineBuilder.Parse(srcPipeline);
        if (!string.Equals(parsed.SourceElement, SourceElement, StringComparison.Ordinal))
            return CameraDeviceResolution.NotACamera;

        if (parsed.Properties.TryGetValue("device-path", out string? path) && !string.IsNullOrWhiteSpace(path))
        {
            devicePath = path;
            return CameraDeviceResolution.Ok;
        }

        bool hasName = parsed.Properties.TryGetValue("device-name", out string? name)
                       && !string.IsNullOrWhiteSpace(name);
        bool hasIndex = parsed.Properties.TryGetValue("device-index", out string? indexText)
                        && int.TryParse(indexText, out _);
        if (!hasName && !hasIndex)
            return CameraDeviceResolution.NoDevicePath;   // どのカメラかが書かれていない

        // 列挙はここで初めて行う（デバイスを開くわけではないが、それなりに時間がかかる）。
        IReadOnlyList<VideoDeviceInfo> devices;
        try
        {
            devices = GstIntrospect.GetVideoSourceDevices();
        }
        catch (Exception)
        {
            return CameraDeviceResolution.NoDevicePath;
        }

        if (hasName)
        {
            // 表示名は完全一致・先勝ち（CLI のレコーダー解決と同じ規則）。
            foreach (var device in devices)
            {
                if (string.Equals(device.Name, name, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(device.Path))
                {
                    devicePath = device.Path;
                    return CameraDeviceResolution.Ok;
                }
            }
        }

        if (hasIndex && int.TryParse(indexText, out int index)
            && 0 <= index && index < devices.Count
            && !string.IsNullOrWhiteSpace(devices[index].Path))
        {
            devicePath = devices[index].Path;
            return CameraDeviceResolution.Ok;
        }

        // 名前も番号も書かれているのに引けなかった ＝ そのカメラが今この機械に無い。
        return CameraDeviceResolution.DeviceNotFound;
    }

    private static readonly Lazy<bool> _mediaFoundationStarted = new(() =>
    {
        // プロセスで 1 回。MFShutdown は呼ばない（プロセス終了に任せる ──
        // 参照カウントを跨いで解放順を管理するより、落とさないことを優先する）。
        return PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_LITE).Succeeded;
    });

    /// <summary>
    /// デバイスのシンボリックリンク（<c>mfvideosrc</c> の <c>device-path</c>）を開いて
    /// 制御インターフェイスを得る。得られなければ null（カメラが無い・占有されている・
    /// ドライバが制御に対応していない）。
    /// </summary>
    public static CameraControlSession? TryOpen(string? symbolicLink)
    {
        if (string.IsNullOrWhiteSpace(symbolicLink) || !_mediaFoundationStarted.Value)
            return null;

        IMFAttributes* attributes = null;
        IMFMediaSource* source = null;
        try
        {
            if (PInvoke.MFCreateAttributes(&attributes, 2).Failed || attributes is null)
                return null;

            Guid sourceTypeKey = PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
            Guid vidcap = PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
            attributes->SetGUID(&sourceTypeKey, &vidcap);

            Guid linkKey = PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK;
            fixed (char* link = symbolicLink)
                attributes->SetString(&linkKey, link);

            if (PInvoke.MFCreateDeviceSource(attributes, &source).Failed || source is null)
                return null;

            // **メディアソースの所有権はセッションへ渡す。** Media Foundation は
            // 解放の前に IMFMediaSource::Shutdown を呼ぶことを求めており、ここで
            // Release だけして手放すと、ワーカーキューと Frame Server のハンドルが
            // プロセス終了まで残る（初期化のたびに走るので、自動復帰が繰り返される
            // 環境では積み上がり、カメラが「使用中」のままになりうる）。
            // かといってこの場で Shutdown すると、取り出した制御インターフェイスが死ぬ
            // ── だからセッションが持ち、Dispose で Shutdown → Release する。
            var session = new CameraControlSession(
                source,
                (IAMVideoProcAmp*)QueryControl(source, IAMVideoProcAmp.IID_Guid),
                (IAMCameraControl*)QueryControl(source, IAMCameraControl.IID_Guid));
            source = null;   // 所有権を渡したので finally では触らない

            return session.IsUsable ? session : DisposeAndReturnNull(session);
        }
        catch (Exception)
        {
            // COM の失敗はここで握る ── カメラ設定が使えないことで録画を止めない。
            return null;
        }
        finally
        {
            // セッションへ渡せなかった場合だけ、ここで畳む。
            if (source is not null)
            {
                source->Shutdown();
                source->Release();
            }
            if (attributes is not null)
                attributes->Release();
        }
    }

    private static CameraControlSession? DisposeAndReturnNull(CameraControlSession session)
    {
        session.Dispose();
        return null;
    }

    /// <summary>
    /// 制御インターフェイスを取り出す。
    ///
    /// <para>
    /// <b>直接の <c>QueryInterface</c> が通らない実装がある。</b> Windows の作法は
    /// <c>IMFGetService::GetService(MF_MEDIASOURCE_SERVICE, …)</c>（KSPROXY 経由）なので、
    /// <b>両方試す</b>。片方だけにすると、通るカメラと通らないカメラで挙動が割れる。
    /// </para>
    /// </summary>
    private static void* QueryControl(IMFMediaSource* source, Guid interfaceId)
    {
        void* result = null;
        Guid iid = interfaceId;

        if (source->QueryInterface(&iid, &result).Succeeded && result is not null)
            return result;

        result = null;
        Guid getServiceId = IMFGetService.IID_Guid;
        void* getService = null;
        if (source->QueryInterface(&getServiceId, &getService).Failed || getService is null)
            return null;

        try
        {
            Guid service = PInvoke.MF_MEDIASOURCE_SERVICE;
            iid = interfaceId;
            ((IMFGetService*)getService)->GetService(&service, &iid, &result);
            return result;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            ((IMFGetService*)getService)->Release();
        }
    }

    /// <summary>
    /// 設定文字列（<c>EventRecorderSettings.CameraControls</c>）をカメラへ適用する。
    /// 成功なら null、駄目だった項目があれば人が読める理由を返す（呼び出し側が記録する）。
    ///
    /// <para>
    /// <b>失敗しても例外にしない。</b> 呼び出し元は <c>EventRecorder.InitializeCore</c> の末尾で、
    /// ここで投げると<b>カメラ設定の不備で録画そのものが止まる</b>
    /// （常時録画の隔離契約と同じ判断）。
    /// </para>
    /// </summary>
    public static string? Apply(string? symbolicLink, string? cameraControls)
    {
        var settings = CameraControlSettings.Parse(cameraControls);
        if (settings.IsEmpty)
            return null;   // 指定が無いなら何もしない（デバイスも開かない）

        using var session = TryOpen(symbolicLink);
        if (session is null)
            return $"could not open the camera controls for device '{symbolicLink}'";

        var failures = new List<string>();
        foreach (var def in settings.SpecifiedItems)
        {
            var value = settings.Get(def.Key);
            if (!session.TrySet(def, value.Value, value.Auto))
                failures.Add(def.Key);
        }

        return failures.Count == 0 ? null : $"could not set: {string.Join(", ", failures)}";
    }
}

/// <summary>
/// 開いたカメラの制御インターフェイス。<b>作ったスレッドだけで使うこと。</b>
/// </summary>
public sealed unsafe partial class CameraControlSession : IDisposable
{
    private IMFMediaSource* _source;
    private IAMVideoProcAmp* _videoProcAmp;
    private IAMCameraControl* _cameraControl;

    internal CameraControlSession(
        IMFMediaSource* source, IAMVideoProcAmp* videoProcAmp, IAMCameraControl* cameraControl)
    {
        _source = source;
        _videoProcAmp = videoProcAmp;
        _cameraControl = cameraControl;
    }

    /// <summary>どちらか一方でも取れていれば使える。</summary>
    public bool IsUsable => _videoProcAmp is not null || _cameraControl is not null;

    /// <summary>
    /// ドライバが申告する可動範囲。<b>対応していない項目は失敗する</b>ので、
    /// これが false の項目は UI に出さない（出すと「動かせないつまみ」になる）。
    /// </summary>
    public bool TryGetRange(CameraControlDef def, out CameraControlRange range)
    {
        range = null!;
        int min = 0, max = 0, step = 0, @default = 0, caps = 0;
        try
        {
            switch (def.Domain)
            {
                case CameraControlDomain.VideoProcAmp when _videoProcAmp is not null:
                    _videoProcAmp->GetRange(def.PropertyId, &min, &max, &step, &@default, &caps);
                    break;
                case CameraControlDomain.CameraControl when _cameraControl is not null:
                    _cameraControl->GetRange(def.PropertyId, &min, &max, &step, &@default, &caps);
                    break;
                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;   // 未対応の項目（HRESULT 失敗）
        }

        // Auto フラグは両インターフェイスで同じ値（1 = Auto / 2 = Manual）。
        range = new CameraControlRange(min, max, step, @default,
            (caps & (int)CameraControlFlags.CameraControl_Flags_Auto) != 0);
        return true;
    }

    /// <summary>現在値と自動フラグ。取れなければ false。</summary>
    public bool TryGet(CameraControlDef def, out int value, out bool auto)
    {
        value = 0;
        auto = false;
        int raw = 0, flags = 0;
        try
        {
            switch (def.Domain)
            {
                case CameraControlDomain.VideoProcAmp when _videoProcAmp is not null:
                    _videoProcAmp->Get(def.PropertyId, &raw, &flags);
                    break;
                case CameraControlDomain.CameraControl when _cameraControl is not null:
                    _cameraControl->Get(def.PropertyId, &raw, &flags);
                    break;
                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        value = raw;
        auto = (flags & (int)CameraControlFlags.CameraControl_Flags_Auto) != 0;
        return true;
    }

    /// <summary>
    /// 値を設定する。<paramref name="auto"/> が true なら値は無視される（ドライバの仕様）。
    /// <paramref name="value"/> が null で <paramref name="auto"/> も null なら何もしない。
    /// </summary>
    public bool TrySet(CameraControlDef def, int? value, bool? auto)
    {
        if (value is null && auto is null)
            return true;

        // 値だけ指定なら手動、auto だけ指定ならその指示に従う。
        bool useAuto = auto ?? false;
        int flags = useAuto
            ? (int)CameraControlFlags.CameraControl_Flags_Auto
            : (int)CameraControlFlags.CameraControl_Flags_Manual;

        // 自動にするときも値の欄は埋める必要がある（無視されるが引数は必須）。
        // 手動値の指定が無ければ現在値を使う ── 0 を押し込むと範囲外になりうる。
        int number;
        if (value is { } specified)
            number = specified;
        else if (TryGet(def, out int current, out _))
            number = current;
        else
            number = 0;

        try
        {
            switch (def.Domain)
            {
                case CameraControlDomain.VideoProcAmp when _videoProcAmp is not null:
                    _videoProcAmp->Set(def.PropertyId, number, flags);
                    return true;
                case CameraControlDomain.CameraControl when _cameraControl is not null:
                    _cameraControl->Set(def.PropertyId, number, flags);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>ドライバが実際に対応している項目だけを列挙する（範囲つき）。</summary>
    public IReadOnlyList<(CameraControlDef Def, CameraControlRange Range)> SupportedItems()
    {
        var items = new List<(CameraControlDef, CameraControlRange)>();
        foreach (var def in CameraControlCatalog.All)
        {
            if (TryGetRange(def, out var range))
                items.Add((def, range));
        }
        return items;
    }

    /// <summary>
    /// 対応項目の範囲と現在値を<b>1 回の呼び出しでまとめて</b>読む。
    /// 専用スレッド越しに使うので、項目ごとに往復させない
    /// （<see cref="CameraControlWorker"/>）。
    /// </summary>
    public IReadOnlyList<CameraControlSnapshot> ReadAll()
    {
        var items = new List<CameraControlSnapshot>();
        foreach (var (def, range) in SupportedItems())
        {
            bool live = TryGet(def, out int value, out bool auto);
            items.Add(new CameraControlSnapshot(def, range, live ? value : range.Default, live && auto));
        }
        return items;
    }

    /// <summary>
    /// 破棄したフィールドは null 化して冪等にする（<c>EventRecorder.Close</c> と同じ規律）。
    ///
    /// <para>
    /// <b>順序が要る。</b> 制御インターフェイスを先に解放してから、メディアソースを
    /// <c>Shutdown</c> → <c>Release</c> する ── Media Foundation は解放前の
    /// <c>Shutdown</c> を求めており、省くとワーカーキューと Frame Server のハンドルが
    /// プロセス終了まで残る。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_videoProcAmp is not null)
        {
            _videoProcAmp->Release();
            _videoProcAmp = null;
        }
        if (_cameraControl is not null)
        {
            _cameraControl->Release();
            _cameraControl = null;
        }
        if (_source is not null)
        {
            // Shutdown は失敗しても続行する（解放を飛ばす方が害が大きい）。
            try { _source->Shutdown(); } catch (Exception) { }
            _source->Release();
            _source = null;
        }
        GC.SuppressFinalize(this);
    }
}
