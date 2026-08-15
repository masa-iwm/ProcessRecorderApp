using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ProcessRecorderApp.GStreamer;

internal static class ImportResolver
{
    // Windows の実ファイル名は MSVC 命名（lib プレフィックス無し）。
    // GstSharpBundle の DllImport が同じ名前をハードコードしているため、
    // ここを揃えることで同一モジュールの解決が保証される（MinGW/MSVC の二重ロード防止）。
    public const string Library = "Gst";
    private const string WindowsLibraryName = "gstreamer-1.0-0.dll";
    private const string LinuxLibraryName = "libgstreamer-1.0.so.0";
    private const string OsxLibraryName = "libgstreamer-1.0.0.dylib";

    public const string LibraryVideo = "GstVideo";
    private const string WindowsLibraryVideoName = "gstvideo-1.0-0.dll";
    private const string LinuxLibraryVideoName = "libgstvideo-1.0.so.0";
    private const string OsxLibraryVideoName = "libgstvideo-1.0.0.dylib";

    public const string LibraryGObject = "GObject";
    private const string WindowsLibraryGObjectName = "gobject-2.0-0.dll";
    private const string LinuxLibraryGObjectName = "libgobject-2.0.so.0";
    private const string OsxLibraryGObjectName = "libgobject-2.0.0.dylib";

    public const string LibraryGLib = "GLib";
    private const string WindowsLibraryGLibName = "glib-2.0-0.dll";
    private const string LinuxLibraryGLibName = "libglib-2.0.so.0";
    private const string OsxLibraryGLibName = "libglib-2.0.0.dylib";

    private static IntPtr TargetLibraryPointer = IntPtr.Zero;
    private static IntPtr TargetLibraryVideoPointer = IntPtr.Zero;
    private static IntPtr TargetLibraryGObjectPointer = IntPtr.Zero;
    private static IntPtr TargetLibraryGLibPointer = IntPtr.Zero;

    public static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == Library)
        {
            if (TargetLibraryPointer != IntPtr.Zero)
                return TargetLibraryPointer;

            var osDependentLibraryName = GetOsDependentLibraryName();
            TargetLibraryPointer = NativeLibrary.Load(osDependentLibraryName, assembly, searchPath);

            return TargetLibraryPointer;
        }
        else if (libraryName == LibraryVideo)
        {
            if (TargetLibraryVideoPointer != IntPtr.Zero)
                return TargetLibraryVideoPointer;

            var osDependentLibraryName = GetOsDependentLibraryVideoName();
            TargetLibraryVideoPointer = NativeLibrary.Load(osDependentLibraryName, assembly, searchPath);

            return TargetLibraryVideoPointer;
        }
        else if (libraryName == LibraryGObject)
        {
            if (TargetLibraryGObjectPointer != IntPtr.Zero)
                return TargetLibraryGObjectPointer;

            var osDependentLibraryName = GetOsDependentLibraryGObjectName();
            TargetLibraryGObjectPointer = NativeLibrary.Load(osDependentLibraryName, assembly, searchPath);

            return TargetLibraryGObjectPointer;
        }
        else if (libraryName == LibraryGLib)
        {
            if (TargetLibraryGLibPointer != IntPtr.Zero)
                return TargetLibraryGLibPointer;

            var osDependentLibraryName = GetOsDependentLibraryGLibName();
            TargetLibraryGLibPointer = NativeLibrary.Load(osDependentLibraryName, assembly, searchPath);

            return TargetLibraryGLibPointer;
        }
        return IntPtr.Zero;
    }

    private static string GetOsDependentLibraryName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsLibraryName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return OsxLibraryName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LinuxLibraryName;

        throw new System.Exception("Unknown platform");
    }
    private static string GetOsDependentLibraryVideoName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsLibraryVideoName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return OsxLibraryVideoName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LinuxLibraryVideoName;

        throw new System.Exception("Unknown platform");
    }
    private static string GetOsDependentLibraryGLibName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsLibraryGLibName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return OsxLibraryGLibName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LinuxLibraryGLibName;

        throw new System.Exception("Unknown platform");
    }

    private static string GetOsDependentLibraryGObjectName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsLibraryGObjectName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return OsxLibraryGObjectName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LinuxLibraryGObjectName;

        throw new System.Exception("Unknown platform");
    }
}
