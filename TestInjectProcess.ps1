# --- CONFIGURATION ---
$processName = "3DMarkSteelNomad" # e.g. exefile, 3DMarkSteelNomad
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

$proc = Get-Process -Name $processName -ErrorAction SilentlyContinue
if (!$proc) { 
    Write-Host "[-] ERROR: Game '$processName' not found! Is it running?" -ForegroundColor Red
    exit 
}
Write-Host "[+] Found $processName (PID: $($proc.Id))" -ForegroundColor Cyan

# Open Process with All Access
$hProc = $Win32::OpenProcess(0x1F0FFF, $false, $proc.Id)
if ($hProc -eq [IntPtr]::Zero) { Write-Host "[-] Failed to open process." -ForegroundColor Red; exit }

# Convert path to Absolute ASCII Bytes (Crucial for LoadLibraryA)
$fullPath = [System.IO.Path]::GetFullPath($dllPath)
$pathBytes = [System.Text.Encoding]::ASCII.GetBytes($fullPath + "`0")

# Allocate memory in game
$remoteAddr = $Win32::VirtualAllocEx($hProc, [IntPtr]::Zero, [uint32]$pathBytes.Length, 0x3000, 0x40)
if ($remoteAddr -eq [IntPtr]::Zero) { Write-Host "[-] Memory allocation failed." -ForegroundColor Red; exit }

# Write DLL Path to game memory
$bytesWritten = [IntPtr]::Zero
$success = $Win32::WriteProcessMemory($hProc, $remoteAddr, $pathBytes, [uint32]$pathBytes.Length, [ref]$bytesWritten)
if (!$success) { Write-Host "[-] Failed to write to memory." -ForegroundColor Red; exit }

# Find LoadLibraryA address and start thread
$loadLibAddr = $Win32::GetProcAddress($Win32::GetModuleHandle("kernel32.dll"), "LoadLibraryA")
$hThread = $Win32::CreateRemoteThread($hProc, [IntPtr]::Zero, 0, $loadLibAddr, $remoteAddr, 0, [IntPtr]::Zero)

if ($hThread -eq [IntPtr]::Zero) {
    Write-Host "[-] CreateRemoteThread failed." -ForegroundColor Red
} else {
    Write-Host "[+] Injection command sent! Checking status..." -ForegroundColor Green
    Start-Sleep -Seconds 2
    
    # --- VERIFY ---
    $loadedModule = Get-Process -Id $proc.Id -Module | Where-Object { $_.FileName -match "FPSLimiter" }
    if ($loadedModule) {
        Write-Host "[SUCCESS] DLL is verified inside the game process!" -ForegroundColor Green
        Write-Host "Location: $($loadedModule.FileName)" -ForegroundColor Gray
    } else {
        Write-Host "[FAILURE] DLL was not loaded. Possible causes: Bitness mismatch (x86 vs x64) or Anti-Cheat." -ForegroundColor Red
    }
}


# Get the base address of the DLL already in the game
$baseAddr = $loadedModule.BaseAddress

# Load the DLL into OUR PowerShell process to find the 'Initialize' offset
$localModule = $Win32::LoadLibrary($fullPath)
if ($localModule -eq [IntPtr]::Zero) { Write-Host "[-] Failed to load DLL locally." -ForegroundColor Red; exit }

$localInitAddr = $Win32::GetProcAddress($localModule, "Initialize")
if ($localInitAddr -eq [IntPtr]::Zero) { Write-Host "[-] Could not find 'Initialize' export. Did you rebuild?" -ForegroundColor Red; exit }

# Calculate the offset (Init Address - Module Base)
$offset = $localInitAddr.ToInt64() - $localModule.ToInt64()

# Apply that offset to the game's base address
$remoteInitAddr = $baseAddr.ToInt64() + $offset

# Start a new thread in the game at that calculated address
$hThread2 = $Win32::CreateRemoteThread($hProc, [IntPtr]::Zero, 0, [IntPtr]$remoteInitAddr, [IntPtr]::Zero, 0, [IntPtr]::Zero)

Write-Host "[+] Initialize command sent to remote thread at address: $remoteInitAddr" -ForegroundColor Yellow

# Open a new terminal to reduce locking issues next time we build.
powershell