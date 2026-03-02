# Configuration
$processName = "exefile" # Change to your target process name
#$processName = "3DMarkSteelNomad" # Change to your target process name

# Prefixes from your C# code
[byte]$prefixGetting= 0xA1

# Find the Process ID
$process = Get-Process -Name $processName -ErrorAction SilentlyContinue
if (-not $process) {
    Write-Error "Could not find process: $processName"
    exit
}

$pipeName = "FpsLimiter_$($process.Id)"
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

    # 3Send Focused FPS (Prefix + Int32)
    $writer.Write($prefixGetting)    
    $writer.Write($prefixGetting) #send it twice since this is a reserved byte not used. 
    #$writer.Flush()

    Write-Host "Success! Sent get requst." -ForegroundColor Green

    # Create a reader to get the "reply"
    $reader = New-Object System.IO.BinaryReader($pipe)
    $_ = $reader.ReadByte()
    $focusedFps = $reader.ReadInt32()
    $_ = $reader.ReadByte()
    $backgroundFps = $reader.ReadInt32()

    Write-Host "Server replied with: 0xF1 = $focusedFps and 0xF2 = $backgroundFps" -ForegroundColor Green
}
catch {
    Write-Error "Failed to update FPS: $($_.Exception.Message)"
}
finally {
    $pipe.Close();
    $writer.Dispose()
    $reader.Dispose()
    $pipe.Dispose()
}