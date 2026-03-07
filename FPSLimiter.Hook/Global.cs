using System.Diagnostics;

namespace FPSLimiter.Hook;

internal static class Global
{
    // We're going to assume that the MainWindowHandle is the one we care about.
    // This may not be true for every game, but it should hold true most the time, and it should do what I need for now...
    internal static IntPtr ThisClientsHandle = Process.GetCurrentProcess().MainWindowHandle;
    
    internal static int OwnerProcessId = -1; // -1 means anyone. just run with no owner.

}