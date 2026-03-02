# --- CONFIGURATION ---
$processName = "exefile" 
$dllPath = "C:\dev\FPSLimiter\FPSLimiter.Hook\bin\Release\net10.0\win-x64\publish\FPSLimiter.Hook.dll"

$Win32Definitions = @'
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
'@

$Win32 = Add-Type -Name "Win32Native" -MemberDefinition $Win32Definitions -PassThru

# 1. Get ALL processes with the name
$procs = Get-Process -Name $processName -ErrorAction SilentlyContinue
if (!$procs) { 
    Write-Host "[-] ERROR: No processes named '$processName' found!" -ForegroundColor Red
    exit 
}

Write-Host "[+] Found $($procs.Count) instance(s) of $processName" -ForegroundColor Cyan

# 2. Prepare local DLL info once to avoid redundant loads
$fullPath = [System.IO.Path]::GetFullPath($dllPath)
$pathBytes = [System.Text.Encoding]::ASCII.GetBytes($fullPath + "`0")
$localModule = $Win32::LoadLibrary($fullPath)
$localInitAddr = $Win32::GetProcAddress($localModule, "Initialize")
$offset = if ($localInitAddr -ne [IntPtr]::Zero) { $localInitAddr.ToInt64() - $localModule.ToInt64() } else { 0 }

# 3. Iterate through each process
foreach ($proc in $procs) {
    Write-Host "`n[#] Target PID: $($proc.Id)" -ForegroundColor Yellow
    
    $hProc = $Win32::OpenProcess(0x1F0FFF, $false, $proc.Id)
    if ($hProc -eq [IntPtr]::Zero) { Write-Host "  [-] Failed to open process." -ForegroundColor Red; continue }

    # Allocate & Write DLL Path
    $remoteAddr = $Win32::VirtualAllocEx($hProc, [IntPtr]::Zero, [uint32]$pathBytes.Length, 0x3000, 0x40)
    $bytesWritten = [IntPtr]::Zero
    $Win32::WriteProcessMemory($hProc, $remoteAddr, $pathBytes, [uint32]$pathBytes.Length, [ref]$bytesWritten)

    # Inject DLL
    $loadLibAddr = $Win32::GetProcAddress($Win32::GetModuleHandle("kernel32.dll"), "LoadLibraryA")
    $hThread = $Win32::CreateRemoteThread($hProc, [IntPtr]::Zero, 0, $loadLibAddr, $remoteAddr, 0, [IntPtr]::Zero)

    if ($hThread -eq [IntPtr]::Zero) {
        Write-Host "  [-] Injection failed." -ForegroundColor Red
    } else {
        Write-Host "  [+] Injection sent. Verifying..." -ForegroundColor Gray
        Start-Sleep -Milliseconds 500
        
        # Verify and Call Initialize
        $loadedModule = Get-Process -Id $proc.Id -Module | Where-Object { $_.FileName -match "FPSLimiter" }
        if ($loadedModule -and $offset -ne 0) {
            $remoteInitAddr = $loadedModule.BaseAddress.ToInt64() + $offset
            $Win32::CreateRemoteThread($hProc, [IntPtr]::Zero, 0, [IntPtr]$remoteInitAddr, [IntPtr]::Zero, 0, [IntPtr]::Zero)
            Write-Host "  [SUCCESS] Hooked and Initialized!" -ForegroundColor Green
        } else {
            Write-Host "  [-] Verification failed or 'Initialize' not found." -ForegroundColor Red
        }
    }
}
