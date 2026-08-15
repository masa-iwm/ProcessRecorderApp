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

    // ParseError はバインディング標準の Message.ParseError(out GLib.GException, out string) を
    // 使う（ここには置かない）。Warning は生成側が out IntPtr を返す形なので、
    // GException（コンストラクタが g_error_free まで面倒を見る）へ包み直す。
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
