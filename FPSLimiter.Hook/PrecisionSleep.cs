namespace FPSLimiter.Hook;

using System.Runtime.InteropServices;

internal unsafe class PrecisionSleep
{
    private readonly IntPtr _timerHandle;
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const int TIMER_MODIFY_STATE = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr hTimer, in long pDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    public PrecisionSleep()
    {
        _timerHandle = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_MODIFY_STATE | 0x100000);
    }

    public void Sleep(double milliseconds)
    {
        if (milliseconds <= 0) return;

        // SetWaitableTimer expects time in 100-nanosecond intervals.
        long relativeTime = -(long)(milliseconds * 10000.0);

        if (SetWaitableTimer(_timerHandle, in relativeTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            WaitForSingleObject(_timerHandle, 0xFFFFFFFF); 
        }
    }

    ~PrecisionSleep()
    {
        CloseHandle(_timerHandle);
    }
}