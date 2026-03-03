# Configuration
$processName = "exefile" # e.g. exefile, 3DMarkSteelNomad
$targetFpsFocused = 120
$targetFpsBackground = 10

# Prefixes from your C# code
[byte]$prefixSetting = 0xA2
[byte]$prefixFocused = 0xF1
[byte]$prefixBackground = 0xF2

# Find the Process ID
$procs = Get-Process -Name $processName -ErrorAction SilentlyContinue
if (!$procs) { 
    Write-Host "[-] ERROR: No processes named '$processName' found!" -ForegroundColor Red
    exit 
}

foreach ($process in $procs) 
{
    $pipeName = "FpsLimiter_$($process.MainWindowHandle)"
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

        # Send Focused FPS 
        $writer.Write($prefixSetting)
        $writer.Write($prefixFocused)
        $writer.Write([int]$targetFpsFocused)

        # Send Background FPS 
        $writer.Write($prefixBackground)
        $writer.Write([int]$targetFpsBackground)
        $writer.Flush();

        Write-Host "Success! Sent Focused: $targetFpsFocused, Background: $targetFpsBackground" -ForegroundColor Green

        # Create a reader to get the reply
        $reader = New-Object System.IO.BinaryReader($pipe)
        $response = $reader.ReadByte()

        Write-Host "Server replied with: $response" -ForegroundColor Green
    }
    catch {
        Write-Error "Failed to update FPS: $($_.Exception.Message)"
    }
    finally {
        $pipe.Close();
        $writer.Close()
        $reader.Close()
        $pipe.Dispose()
    }
}

