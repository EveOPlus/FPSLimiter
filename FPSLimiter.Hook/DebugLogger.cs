using SharpDX.Direct3D11;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FPSLimiter.Hook;

public static class DebugLogger
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern void OutputDebugString(string lpOutputString);

    public static void WriteLine(string outputString, IntPtr myHandle)
    {
        OutputDebugString($"[FPS_LIMITER] [{myHandle}] {outputString}");
    }
}