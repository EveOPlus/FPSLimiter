using System.Runtime.CompilerServices;

namespace FPSLimiter.Hook;

using System.Runtime.InteropServices;

internal static unsafe partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualProtect(IntPtr lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MessageBeep(uint uType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr VirtualAlloc(IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", EntryPoint = "OutputDebugStringW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial void OutputDebugString(string lpOutputString);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true)]
    public static partial IntPtr GetProcAddress(IntPtr hModule, [MarshalAs(UnmanagedType.LPStr)] string procName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr GetModuleHandle(string lpModuleName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int dwProcessId);

    //[LibraryImport("user32.dll", SetLastError = true)]
    //public static partial IntPtr SetWinEventHook(
    //    uint eventMin,
    //    uint eventMax,
    //    IntPtr hmodWinEventProc,
    //    delegate* unmanaged<IntPtr, uint, IntPtr, int, int, uint, uint, void> pfnWinEventProc,
    //    uint idProcess,
    //    uint idThread,
    //    uint dwFlags);
    //
    //[LibraryImport("user32.dll", SetLastError = true)]
    //[return: MarshalAs(UnmanagedType.Bool)]
    //public static partial bool GetMessage(
    //    out MSG lpMsg,
    //    IntPtr hWnd,
    //    uint wMsgFilterMin,
    //    uint wMsgFilterMax);
    //
    //[StructLayout(LayoutKind.Sequential)]
    //public struct MSG
    //{
    //    public IntPtr hwnd;
    //    public uint message;
    //    public IntPtr wParam;
    //    public IntPtr lParam;
    //    public uint time;
    //    public int ptX;
    //    public int ptY;
    //}

    [LibraryImport("user32.dll", EntryPoint = "SetWinEventHook")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, int, uint, uint, void> pfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] // 'W' is for Unicode, safer for .NET
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMessage(
        out MSG lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG { IntPtr hwnd; uint message; IntPtr wParam; IntPtr lParam; uint time; int ptX; int ptY; }
}