using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static FPSLimiter.Hook.DebugLogger;

namespace FPSLimiter.Hook;

internal static unsafe class WinEventHook
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;

    private static Action<IntPtr>? _onForegroundAction;
    private static IntPtr _lastHandleChecked = IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
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
        try
        {
            _onForegroundAction = callback;

            // We run the loop on a background thread so it doesn't block the UI
            Thread listenerThread = new Thread(RunHookListener)
            {
                IsBackground = true
            };
            listenerThread.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in {nameof(StartListening)}: {ex}");
        }
    }

    private static unsafe void RunHookListener()
    {
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        // Pass the address (&) of the static OnForegroundChanged method
        IntPtr hook = NativeMethods.SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            &OnForegroundChanged,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        if (hook == IntPtr.Zero) return;

        // The Message Pump: Keeps the thread alive and processes the Hook callbacks
        NativeMethods.MSG msg;
        while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        NativeMethods.UnhookWinEvent(hook);
    }

    //[DllImport("user32.dll")]
    //static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
    //    delegate* unmanaged<IntPtr, uint, IntPtr, int, int, uint, uint, void> pfnWinEventProc,
    //    uint idProcess, uint idThread, uint dwFlags);
    //
    //[DllImport("user32.dll")]
    //static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    //
    //[StructLayout(LayoutKind.Sequential)]
    //struct MSG { IntPtr hwnd; uint message; IntPtr wParam; IntPtr lParam; uint time; int ptX; int ptY; }
}