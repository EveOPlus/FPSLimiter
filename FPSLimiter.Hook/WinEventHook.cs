using System.Runtime.InteropServices;

namespace FPSLimiter.Hook;

internal static unsafe class WinEventHook
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;

    private static Action<IntPtr>? _onForegroundAction;
    private static IntPtr _lastHandleChecked = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "OnForegroundChanged")]
    public static void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Ignore anything that isn't a window, such as a dialog box that makes it hard to debug / troubleshoot.
        if (idObject != OBJID_WINDOW)
        {
            return;
        }

        // If the focus hasn't actually changed since the last time, do nothing.
        if (_lastHandleChecked == hwnd)
        {
            return;
        }

        _lastHandleChecked = hwnd;

        _onForegroundAction?.Invoke(hwnd);
    }

    public static void StartListening(Action<IntPtr> callback)
    {
        _onForegroundAction = callback;

        Thread listenerThread = new Thread(RunHookListener);
        listenerThread.IsBackground = true;
        listenerThread.Start();
    }

    private static void RunHookListener()
    {
        IntPtr hook = NativeMethods.SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            &OnForegroundChanged,
            0, 0, WINEVENT_OUTOFCONTEXT);

        if (hook == IntPtr.Zero) return;

        // This loop blocks the BACKGROUND thread, waiting for OS notifications
        NativeMethods.MSG msg;
        while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0)) { }
    }
}