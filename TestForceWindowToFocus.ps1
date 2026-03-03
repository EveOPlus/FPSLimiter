# Configuration
$processName = "3DMarkSteelNomad" # e.g. exefile, 3DMarkSteelNomad


# Pipe instructions
[byte]$prefixFireAndForget = 0xA3
[byte]$setFocusedCommand = 0xB1

 $MB_OK = 0x00000000L 
 $MB_ICONQUESTION = 0x00000020L

$Win32Definitions = @'
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    public static extern void SetFocus(IntPtr window);
'@

$Win32 = Add-Type -Name "Win32Native" -MemberDefinition $Win32Definitions -PassThru

$proc = Get-Process -Name $processName -ErrorAction SilentlyContinue
if (!$proc) { 
    Write-Host "ERROR: Game '$processName' not found! Is it running?" -ForegroundColor Red
    exit 
}
Write-Host "Found $processName (PID: $($proc.Id))" -ForegroundColor Cyan

# Use the named pipe to interupt the fps limiting and make it faster to take focus.
$pipeName = "FpsLimiter_$($proc.Id)"
Write-Host "Connecting to pipe: \\.\pipe\$pipeName" -ForegroundColor Cyan

try {
    # Setup the client connection
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", 
    $pipeName, [System.IO.Pipes.PipeDirection]::InOut, 
    [System.IO.Pipes.PipeOptions]::None, 
    [System.Security.Principal.TokenImpersonationLevel]::None)
    $pipe.Connect(2000) # Wait up to 2 seconds

    # Use BinaryWriter for your protocol
    $writer = New-Object System.IO.BinaryWriter($pipe)

    # Send FireAndForget to give focus.
    $writer.Write($prefixFireAndForget)
    $writer.Write($setFocusedCommand)
    $writer.Flush();

    Write-Host "Success! Sent Focus Command" -ForegroundColor Green
}
catch {
    Write-Error "Failed to send named pipe command: $($_.Exception.Message)"
}
finally {
    $pipe.Close();
    $writer.Close()
    $pipe.Dispose()
}

Write-Host $TargetProcess.MainWindowHandle

$r1 = $Win32::SetForegroundWindow($TargetProcess.MainWindowHandle)   
$Win32::SetFocus($TargetProcess.MainWindowHandle)

Write-Error "SetForegroundWindow result: $r1"
    
Write-Host "Action called for $ProcessName."