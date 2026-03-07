# Configuration
$processName = "exefile" # Change to your target process name
$process = Get-Process -Name $processName -ErrorAction SilentlyContinue
if (-not $process) { Write-Error "Could not find process: $processName"; exit }

$PipeName = "FpsLimiter_$($process[0].MainWindowHandle)"
$JumpGates = @(3689163958, 1537508544, 1768044352)

# --- Protocol Constants ---
$Dir = @{ Query = [byte]0xA1; Update = [byte]0xA2; FireAndForget = [byte]0xA3 }
$Cmd = @{
    Ping          = [byte]0xB2; UnmuteAll     = [byte]0xC1
    UnmuteList    = [byte]0xC2; MuteList      = [byte]0xC3
    GetMuted      = [byte]0xC4; GetHistory    = [byte]0xC5
    SetFocus      = [byte]0xB1
}

function Invoke-Pipe {
    param([byte]$d, [byte]$c, [scriptblock]$payload)
    
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(1000)
        $w = New-Object System.IO.BinaryWriter($pipe)
        $r = New-Object System.IO.BinaryReader($pipe)

        # 1. Send Header
        $w.Write($d); $w.Write($c)

        # 2. Write Data
        if ($Payload) { &$Payload $w }
        $w.Flush()

        # 3. Process Results
        if ($d -eq $Dir.Update) {
            # Updates always return 1 byte (SuccessResponseCode)
            $status = $r.ReadByte()
            Write-Host "Update Success: 0x$($status.ToString('X2'))" -F Green
        }
        elseif ($d -eq $Dir.Query) {
            # PROTOCOL FIX: Only Ping returns 1 byte; others return Int32 Count + Data
            if ($c -eq $Cmd.Ping) {
                $status = $r.ReadByte()
                Write-Host "Ping Response: 0x$($status.ToString('X2'))" -F Green
            }
            else {
                # History and Mute List start with a 4-byte Int32 count
                $count = $r.ReadInt32()

                if ($c -eq $Cmd.GetHistory) {
                    Write-Host "--- Audio History ($count) ---" -F Cyan
                    for ($i=0; $i -lt $count; $i++) {
                        # ID (uint32), Obj (uint64), Time (uint32 ms ago)
                        $eid = $r.ReadUInt32()
                        $oid = $r.ReadUInt64()
                        $ms  = $r.ReadUInt64() 
                        Write-Host "Event: $eid | Obj: $oid | ${ms}"
                    }
                }
                elseif ($c -eq $Cmd.GetMuted) {
                    Write-Host "--- Muted List ($count) ---" -F Yellow
                    for ($i=0; $i -lt $count; $i++) { Write-Host " - $($r.ReadUInt32())" }
                }
            }
        }
    }
    catch { Write-Host "Pipe Error: $_" -F Red }
    finally { $pipe.Dispose() }
}

while($true) {
    Write-Host "`n1: Mute Gates | 2: Unmute All | 3: History | 4: Mutes | 5: Ping | Q: Quit" -F Gray
    $choice = Read-Host "Select"
    switch ($choice) {
        "1" { Invoke-Pipe $Dir.Update $Cmd.MuteList { param($w) $w.Write([int]$JumpGates.Count); foreach($i in $JumpGates){$w.Write([uint32]$i)} } }
        "2" { Invoke-Pipe $Dir.Update $Cmd.UnmuteAll }
        "3" { Invoke-Pipe $Dir.Query $Cmd.GetHistory }
        "4" { Invoke-Pipe $Dir.Query $Cmd.GetMuted }
        "5" { Invoke-Pipe $Dir.Query $Cmd.Ping }
        "Q" { break }
    }
}
