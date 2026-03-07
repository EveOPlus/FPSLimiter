using System.Diagnostics;

namespace FPSLimiter.Hook;

internal static class DebugLogger
{
    private static readonly IntPtr MainHandle = Process.GetCurrentProcess().MainWindowHandle;
    public static void Info(string message)
    {
        NativeMethods.OutputDebugString($"[EVE-O HOOK] [{MainHandle}] [INFO] {message}");
    }

    public static void Error(string message)
    {
        NativeMethods.OutputDebugString($"[EVE-O HOOK] [{MainHandle}] [ERROR] {message}");
    }

    public static void Error(Exception ex)
    {
        Error(ex.ToString());
    }

    public static void Error(Exception ex, string message)
    {
        Error($"{message}: {ex}");
    }
}