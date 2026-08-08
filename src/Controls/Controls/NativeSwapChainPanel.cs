using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;

namespace ProcessRecorderApp.Controls;

/// <summary>
/// ネイティブ(DXGI コンポジション)スワップチェーンをバインドする SwapChainPanel。
///
/// SwapChainPanel へのネイティブ描画は、ISwapChainPanelNative::SetSwapChain によるバインドに加え、
/// 高DPI環境で正しく表示するために IDXGISwapChain2::SetMatrixTransform による逆スケール補正が
/// 必須になる。これらの定型的な相互運用コードを画面側のコードビハインドへ書く代わりに、
/// 本コントロールへ集約する。
///
/// 画面側は <see cref="SwapChainHandle"/> にネイティブスワップチェーンのハンドルを設定し、
/// <see cref="SwapChainSizeRequested"/> でリサイズ要求（パネルの物理ピクセルサイズ）を受け取って
/// 実際のスワップチェーン（GStreamer 等が所有する実体）のバッファをリサイズする、という分担になる。
/// </summary>
public sealed partial class NativeSwapChainPanel : SwapChainPanel
{
    public NativeSwapChainPanel()
    {
        SizeChanged += OnSizeChanged;
        CompositionScaleChanged += OnCompositionScaleChanged;
    }

    /// <summary>バインドするネイティブスワップチェーンのハンドル（IDXGISwapChain1* 相当）。0 で解除する。</summary>
    public static readonly DependencyProperty SwapChainHandleProperty
        = DependencyProperty.Register(nameof(SwapChainHandle), typeof(long), typeof(NativeSwapChainPanel),
            new PropertyMetadata(0L, OnSwapChainHandleChanged));

    public long SwapChainHandle
    {
        get => (long)GetValue(SwapChainHandleProperty);
        set => SetValue(SwapChainHandleProperty, value);
    }

    private static void OnSwapChainHandleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NativeSwapChainPanel)d).BindSwapChain((nint)(long)e.NewValue);

    private void BindSwapChain(nint handle)
    {
        SetPanelSwapChain(this, handle);
        if (handle == 0)
            return;
        ApplyInverseCompositionScale(handle, CompositionScaleX, CompositionScaleY);
        RaiseSizeRequested();
    }

    /// <summary>
    /// バインド先スワップチェーンに必要な物理ピクセルサイズが変化したときに発火する
    /// （パネルのサイズ変更・DPI(CompositionScale)変更・バインド直後のいずれでも発火する）。
    /// ハンドラ側で、通知されたサイズへスワップチェーンの実体（バッファ）をリサイズすること。
    /// 本コントロールはスワップチェーンの実体を所有しないため、バッファのリサイズ自体は行わない。
    /// </summary>
    public event EventHandler<SwapChainSizeRequestedEventArgs>? SwapChainSizeRequested;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => RaiseSizeRequested();

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        // スケールが変わったら、現在バインド中のスワップチェーンへ逆変換行列を再適用する
        var handle = (nint)SwapChainHandle;
        if (handle != 0)
            ApplyInverseCompositionScale(handle, CompositionScaleX, CompositionScaleY);
        RaiseSizeRequested();
    }

    private void RaiseSizeRequested()
    {
        if (SwapChainHandle == 0 || CompositionScaleX <= 0 || CompositionScaleY <= 0)
            return;
        int width = (int)Math.Round(ActualWidth * CompositionScaleX);
        int height = (int)Math.Round(ActualHeight * CompositionScaleY);
        if (width <= 0 || height <= 0)
            return;
        SwapChainSizeRequested?.Invoke(this, new(width, height));
    }

    // ---- ISwapChainPanelNative::SetSwapChain ----

    // ISwapChainPanelNative のIID（microsoft.ui.xaml.media.dxinterop.h ── WinUI 3 用）。
    // UWP 用ヘッダー windows.ui.xaml.media.dxinterop.h の同名インターフェイスは
    // **別の IID**（f92f19d2-3ade-45a6-a20c-f6f1ea90554b）で、そちらへ「直す」と
    // QueryInterface が必ず失敗する。
    private static readonly Guid IID_ISwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    /// <summary>
    /// SwapChainPanel にネイティブスワップチェーンを関連付ける（ISwapChainPanelNative::SetSwapChain）。
    /// .As&lt;T&gt;()/GeneratedComInterface は WinRT 相互運用で不安定なため、パネルの IUnknown を
    /// 直接 QueryInterface して vtable 経由で呼ぶ（Native AOT 安全）。必ず UI スレッドから呼ぶこと。
    /// </summary>
    private static unsafe void SetPanelSwapChain(SwapChainPanel panel, nint swapChain)
    {
        nint unknown = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(panel);
        if (unknown == 0)
            return;
        try
        {
            int qi = Marshal.QueryInterface(unknown, in IID_ISwapChainPanelNative, out nint native);
            if (qi < 0 || native == 0)
            {
                // 失敗を無記録にしない ── 症状は「プレビューが黒いまま」だけで、
                // ここが唯一の観測点になる（ハンドル生成の打ち切りはホスト側が記録する）。
                Components.ActivityLog.Error("preview.error",
                    $"ISwapChainPanelNative QueryInterface failed (hr=0x{qi:X8})");
                return;
            }
            try
            {
                // vtable: 0=QueryInterface, 1=AddRef, 2=Release, 3=SetSwapChain
                nint vtbl = Marshal.ReadIntPtr(native);
                var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)Marshal.ReadIntPtr(vtbl, 3 * nint.Size);
                int hr = setSwapChain(native, swapChain);
                if (hr < 0)
                {
                    Components.ActivityLog.Error("preview.error",
                        $"ISwapChainPanelNative::SetSwapChain failed (hr=0x{hr:X8})");
                }
            }
            finally
            {
                Marshal.Release(native);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    // ---- IDXGISwapChain2::SetMatrixTransform（高DPI補正） ----

    // IDXGISwapChain2 のIID（dxgi1_3.h）
    private static readonly Guid IID_IDXGISwapChain2 = new("a8be2ac4-199f-4946-b331-79599fb98de7");

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_MATRIX_3X2_F
    {
        public float _11, _12, _21, _22, _31, _32;
    }

    /// <summary>
    /// コンポジションスワップチェーンへ逆DPIスケール行列を設定する（IDXGISwapChain2::SetMatrixTransform）。
    /// スワップチェーンは物理ピクセル解像度で確保する前提のため、パネルの論理(DIP)サイズへ縮小合成させる。
    /// これを行わないと、DirectComposition はバッファをパネルの論理サイズへ等倍合成するため、
    /// 高DPI環境ではバッファの右・下端がパネル外にはみ出して見えなくなる（縮小されず切れて見える）。
    ///
    /// vtable スロット 34（IUnknown 3 + IDXGIObject 4 + IDXGIDeviceSubObject 1 + IDXGISwapChain 10 +
    /// IDXGISwapChain1 11 + SetSourceSize,GetSourceSize,SetMaximumFrameLatency,GetMaximumFrameLatency,
    /// GetFrameLatencyWaitableObject の5つ = 29+5）を直接呼ぶ。
    /// </summary>
    private static unsafe void ApplyInverseCompositionScale(nint swapChain, float scaleX, float scaleY)
    {
        if (scaleX <= 0 || scaleY <= 0)
            return;
        if (Marshal.QueryInterface(swapChain, in IID_IDXGISwapChain2, out nint swapChain2) < 0 || swapChain2 == 0)
            return;
        try
        {
            var matrix = new DXGI_MATRIX_3X2_F { _11 = 1f / scaleX, _22 = 1f / scaleY };
            nint vtbl = Marshal.ReadIntPtr(swapChain2);
            var setMatrixTransform = (delegate* unmanaged[Stdcall]<nint, DXGI_MATRIX_3X2_F*, int>)Marshal.ReadIntPtr(vtbl, 34 * nint.Size);
            setMatrixTransform(swapChain2, &matrix);
        }
        finally
        {
            Marshal.Release(swapChain2);
        }
    }
}

/// <summary><see cref="NativeSwapChainPanel.SwapChainSizeRequested"/> のイベント引数。</summary>
public sealed class SwapChainSizeRequestedEventArgs(int width, int height) : EventArgs
{
    /// <summary>要求される物理ピクセル幅。</summary>
    public int Width { get; } = width;
    /// <summary>要求される物理ピクセル高さ。</summary>
    public int Height { get; } = height;
}
