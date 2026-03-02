# Configuration
$processName = "exefile" # Change to your target process name
#$processName = "3DMarkSteelNomad" # Change to your target process name
$targetFpsFocused = 120
$targetFpsBackground = 1

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
    $pipeName = "FpsLimiter_$($process.Id)"
    Write-Host "Connecting to pipe: \\.\pipe\$pipeName" -ForegroundColor Cyan

    try {
        # 1. Setup the client connection
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", 
        $pipeName, [System.IO.Pipes.PipeDirection]::InOut, 
        [System.IO.Pipes.PipeOptions]::None, 
        [System.Security.Principal.TokenImpersonationLevel]::None)
        $pipe.Connect(2000) # Wait up to 2 seconds

        # 2. Use BinaryWriter for your protocol
        $writer = New-Object System.IO.BinaryWriter($pipe)

        # 3. Send Focused FPS (Prefix + Int32)
        $writer.Write($prefixSetting)
        $writer.Write($prefixFocused)
        $writer.Write([int]$targetFpsFocused)

        # 4. Send Background FPS (Prefix + Int32)
        $writer.Write($prefixBackground)
        $writer.Write([int]$targetFpsBackground)
        $writer.Flush();

        Write-Host "Success! Sent Focused: $targetFpsFocused, Background: $targetFpsBackground" -ForegroundColor Green

        # 5. Create a reader to get the "reply"
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

