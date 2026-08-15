using Gst;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProcessRecorderApp.GStreamer;

internal static partial class ExtendMethods
{
    /// <summary>
    /// C の <c>GST_BUFFER_COPY_ALL</c> 相当（enum には合成値が生成されない）。
    /// Merge / Deep は含めない ── データ本体は共有のまま複製する。
    /// </summary>
    internal const BufferCopyFlags BufferCopyAll =
        BufferCopyFlags.Flags | BufferCopyFlags.Timestamps | BufferCopyFlags.Meta | BufferCopyFlags.Memory;

    // **バインディング標準の Message.ParseError(out GException, out string) は使わない。**
    // フォークの custom 実装（Gst/custom/Message.cs）は out debug（transfer full）を
    // Utf8PtrToString で写すだけで g_free しないため、Error メッセージ 1 件ごとに
    // ネイティブ文字列が漏れる（ラッパーが無く回収不能。GError 側は GException が解放する）。
    // ここで parse から解放までを自前で行う。
    public static void ParseErrorEx(this Message message, out GLib.GException gerror, out string? debug)
    {
        gst_message_parse_error(message.Handle, out IntPtr gerrorHandle, out IntPtr debugNative);
        gerror = new GLib.GException(gerrorHandle);
        debug = Marshal.PtrToStringUTF8(debugNative);
        g_free(debugNative);
    }
    [LibraryImport(ImportResolver.Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void gst_message_parse_error(IntPtr message, out IntPtr gerror, out IntPtr debug);

    [LibraryImport(ImportResolver.LibraryGLib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void g_free(IntPtr mem);

    // Warning は生成側（generated/Gst/Message.cs）が out debug を PtrToStringGFree で
    // 解放するため無リーク。out IntPtr の GError だけを GException
    // （コンストラクタが g_error_free まで面倒を見る）へ包み直す。
    public static void ParseWarningEx(this Message message, out GLib.GException gerror, out string? debug)
    {
        message.ParseWarning(out IntPtr gerrorHandle, out debug);
        gerror = new GLib.GException(gerrorHandle);
    }

    /// <summary>
    /// GObject の gpointer 型プロパティ値を取得する（例: d3d12swapchainsink の "swapchain"）。
    /// GLib.Value 経由では gpointer を取り出しにくいため g_object_get を直接呼ぶ。
    /// </summary>
    public static IntPtr GetPointerProperty(this GLib.Object obj, string propertyName)
    {
        // 可変長引数 g_object_get(obj, "name", &value, NULL) を固定シグネチャで呼ぶ
        g_object_get(obj.Handle, propertyName, out IntPtr value, IntPtr.Zero);
        return value;
    }
    [LibraryImport(ImportResolver.LibraryGObject, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void g_object_get(IntPtr @object, string firstPropertyName, out IntPtr value, IntPtr terminator);

    /// <summary>
    /// d3d12swapchainsink の "resize" アクションシグナル（void resize(guint width, guint height)）を発火する。
    /// swapchain-width/height は読み取り専用のため、リサイズはこのシグナル経由で行う。
    /// </summary>
    public static void EmitResize(this GLib.Object obj, uint width, uint height)
        => g_signal_emit_by_name_resize(obj.Handle, "resize", width, height);
    [LibraryImport(ImportResolver.LibraryGObject, EntryPoint = "g_signal_emit_by_name", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void g_signal_emit_by_name_resize(IntPtr instance, string detailedSignal, uint width, uint height);
}
