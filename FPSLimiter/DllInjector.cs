namespace FpsLimiter;

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using System.IO;

public class DllInjector
{
    private const int PROCESS_ALL_ACCESS = 0x1F0FFF;
    private const uint MEM_COMMIT_RESERVE = 0x3000;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    public static void InjectAndInitialize(string processName, string dllPath)
    {
        var processes = Process.GetProcessesByName(processName);

        foreach (var proc in processes)
        {
            if (proc == null) throw new Exception($"Process '{processName}' not found.");

            Console.WriteLine($"Working on {proc.MainWindowTitle} {proc.MainWindowHandle}");
            IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, proc.Id);
            if (hProc == IntPtr.Zero) throw new Exception("Failed to open process.");

            string fullPath = Path.GetFullPath(dllPath);
            byte[] pathBytes = Encoding.ASCII.GetBytes(fullPath + "\0");

            Console.WriteLine($"Allocate memory for DLL path string");
            IntPtr remoteAddr = VirtualAllocEx(hProc, IntPtr.Zero, (uint)pathBytes.Length, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
            if (remoteAddr == IntPtr.Zero) throw new Exception("Memory allocation failed.");

            Console.WriteLine($"Write DLL path to target process");
            if (!WriteProcessMemory(hProc, remoteAddr, pathBytes, (uint)pathBytes.Length, out _))
                throw new Exception("Failed to write to memory.");

            Console.WriteLine($"Call LoadLibraryA in target process");
            IntPtr loadLibAddr = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryA");
            IntPtr hThread = CreateRemoteThread(hProc, IntPtr.Zero, 0, loadLibAddr, remoteAddr, 0, IntPtr.Zero);

            if (hThread == IntPtr.Zero) throw new Exception("CreateRemoteThread for LoadLibrary failed.");

            System.Threading.Thread.Sleep(2000); // Wait for module to load

            Console.WriteLine($"Verify and find 'Initialize' export offset");
            proc.Refresh();
            var loadedModule = proc.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.FileName.Contains("FPSLimiter"));
            if (loadedModule == null) throw new Exception("DLL was not loaded into the target process.");

            IntPtr localModule = LoadLibrary(fullPath);
            IntPtr localInitAddr = GetProcAddress(localModule, "Initialize");
            if (localInitAddr == IntPtr.Zero) throw new Exception("Could not find 'Initialize' export in DLL.");

            Console.WriteLine($"Calculate remote address: (Target Base + (Local Init - Local Base))");
            long offset = localInitAddr.ToInt64() - localModule.ToInt64();
            IntPtr remoteInitAddr = new IntPtr(loadedModule.BaseAddress.ToInt64() + offset);

            // Execute 'Initialize' in target process
            Console.WriteLine($"Execute 'Initialize' in target process");
            CreateRemoteThread(hProc, IntPtr.Zero, 0, remoteInitAddr, IntPtr.Zero, 0, IntPtr.Zero);

            Console.WriteLine($"[+] Successfully injected and initialized at: {remoteInitAddr}");
        }

        Console.WriteLine("Done...");
        Console.Read();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);
}