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

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, // Use a delegate for stability
        uint idProcess,
        uint idThread,
        uint dwFlags);

    // Define the delegate for the hook callback
    internal delegate void WinEventProc(
        IntPtr hWinEventHook,
        uint @event,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMessage(
        out MSG lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        // x64 Padding: message (4 bytes) + padding (4 bytes) = 8 bytes to align wParam
        private readonly uint _padding1;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX; // Part of POINT
        public int ptY; // Part of POINT
        // Final padding to ensure the struct is a multiple of 8 (48 bytes total)
        private readonly uint _padding2;
    }
    
    // --- Context & Thread Management ---

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetThreadContext(IntPtr hThread, CONTEXT64* lpContext);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadContext(IntPtr hThread, CONTEXT64* lpContext);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint SuspendThread(IntPtr hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(IntPtr hThread);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    internal static partial IntPtr CreateEvent(IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        [MarshalAs(UnmanagedType.LPWStr)] string lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetEvent(IntPtr hEvent);
    
    // --- Exception Handling ---

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr AddVectoredExceptionHandler(uint first, delegate* unmanaged[Stdcall]<EXCEPTION_POINTERS*, int> handler);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint RemoveVectoredExceptionHandler(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "RtlZeroMemory")]
    public static partial void ZeroMemory(IntPtr destination, nuint length);

    #region BINARY-SAFE NATIVE STRUCTURES (x64)

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct EXCEPTION_POINTERS
    {
        public EXCEPTION_RECORD* ExceptionRecord;
        public CONTEXT64* ContextRecord;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public EXCEPTION_RECORD* ExceptionRecordNext;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
        private uint __padding;
        public fixed ulong ExceptionInformation[15];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    internal unsafe struct CONTEXT64
    {
        // --- Offset 0x00 ---
        public ulong P1Home, P2Home, P3Home, P4Home, P5Home, P6Home;

        // --- Offset 0x30 ---
        public uint ContextFlags;
        public uint MxCsr;

        // --- Offset 0x38 ---
        public ushort SegCs, SegDs, SegEs, SegFs, SegGs, SegSs;
        public uint EFlags;

        // --- Offset 0x48 ---
        public ulong Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;

        // --- Offset 0x78 ---
        public ulong Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi;

        // --- Offset 0xB8 ---
        public ulong R8, R9, R10, R11, R12, R13, R14, R15;

        // --- Offset 0xF8 (248) ---
        public ulong Rip;
        // Rip is 8 bytes. 248 + 8 = 256 (0x100).

        // --- Offset 0x100 (256) ---
        // Total Size Requirement: 1232 bytes.
        // 1232 - 256 (Header) = 976 bytes remaining.
        public fixed byte VectorRegisterArea[976];
    }

    #endregion
}