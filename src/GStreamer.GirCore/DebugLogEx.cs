using Gst;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ProcessRecorderApp.GStreamer;

public static partial class DebugLogEx
{
    [LibraryImport(ImportResolver.Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr _gst_debug_category_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint color, [MarshalAs(UnmanagedType.LPUTF8Str)] string description);
    public static DebugCategory DebugCategoryNew(string name, uint color, string description)
        => new(new Gst.Internal.DebugCategoryOwnedHandle(_gst_debug_category_new(name, color, description)));

    private static DebugCategory? _debugCategory;
    public static void Log(DebugLevel level, string? message,
        GObject.Object? @object = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0,
        [CallerMemberName] string function = "")
    {
        _debugCategory ??= DebugCategoryNew("myapp", 0, "My application");
        Functions.DebugLogLiteral(_debugCategory!, level, file, function, line, @object, message ?? "");
    }
}
