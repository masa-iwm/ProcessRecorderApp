using Gst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProcessRecorderApp.GStreamer;

internal static partial class ExtendMethods
{
    public static void ParseError(this Message message, out GLib.Error gerror, out string? debug)
    {
        gst_message_parse_error(message.Handle, out var gerrorHandle, out var debugNative);

        gerror = new GLib.Error(new GLib.Internal.ErrorOwnedHandle(gerrorHandle));
        debug = Marshal.PtrToStringUTF8(debugNative);
        GLib.Internal.Functions.Free(debugNative);
    }
    [LibraryImport(ImportResolver.Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void gst_message_parse_error(Gst.Internal.MessageHandle message, out IntPtr gerror, out IntPtr debug);

    public static void ParseWarning(this Message message, out GLib.Error gerror, out string? debug)
    {
        gst_message_parse_warning(message.Handle, out var gerrorHandle, out var debugNative);

        gerror = new GLib.Error(new GLib.Internal.ErrorOwnedHandle(gerrorHandle));
        debug = Marshal.PtrToStringUTF8(debugNative);
        GLib.Internal.Functions.Free(debugNative);
    }
    [LibraryImport(ImportResolver.Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void gst_message_parse_warning(Gst.Internal.MessageHandle message, out IntPtr gerror, out IntPtr debug);

    public static void ParseInfo(this Message message, out GLib.Error gerror, out string? debug)
    {
        gst_message_parse_info(message.Handle, out var gerrorHandle, out var debugNative);

        gerror = new GLib.Error(new GLib.Internal.ErrorOwnedHandle(gerrorHandle));
        debug = Marshal.PtrToStringUTF8(debugNative);
        GLib.Internal.Functions.Free(debugNative);
    }
    [LibraryImport(ImportResolver.Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void gst_message_parse_info(Gst.Internal.MessageHandle message, out IntPtr gerror, out IntPtr debug);

    public static void SetWindowHandle(this GstVideo.VideoOverlayHelper overlay, IntPtr handle)
        => gst_video_overlay_set_window_handle(overlay.Handle.DangerousGetHandle(), handle);
    [LibraryImport(ImportResolver.LibraryVideo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void gst_video_overlay_set_window_handle(IntPtr overlay, IntPtr handle);

    /// <summary>
    /// GObject の gpointer 型プロパティ値を取得する（例: d3d12swapchainsink の "swapchain"）。
    /// GirCore の GObject.Value 経由では gpointer を取り出しにくいため g_object_get を直接呼ぶ。
    /// </summary>
    public static IntPtr GetPointerProperty(this GObject.Object obj, string propertyName)
    {
        // 可変長引数 g_object_get(obj, "name", &value, NULL) を固定シグネチャで呼ぶ
        g_object_get(obj.Handle.DangerousGetHandle(), propertyName, out IntPtr value, IntPtr.Zero);
        return value;
    }
    [LibraryImport(ImportResolver.LibraryGObject, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void g_object_get(IntPtr @object, string firstPropertyName, out IntPtr value, IntPtr terminator);

    /// <summary>
    /// d3d12swapchainsink の "resize" アクションシグナル（void resize(guint width, guint height)）を発火する。
    /// swapchain-width/height は読み取り専用のため、リサイズはこのシグナル経由で行う。
    /// </summary>
    public static void EmitResize(this GObject.Object obj, uint width, uint height)
        => g_signal_emit_by_name_resize(obj.Handle.DangerousGetHandle(), "resize", width, height);
    [LibraryImport(ImportResolver.LibraryGObject, EntryPoint = "g_signal_emit_by_name", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void g_signal_emit_by_name_resize(IntPtr instance, string detailedSignal, uint width, uint height);

}
