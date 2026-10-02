# NeutrinoOS history-redraw verification (VGA + serial input).
#
# Reproduces/verifies the command-history redraw bug:
#   - Leg 1: input via PS/2 keyboard (QEMU monitor sendkey) -> VGA tail path
#     type help <ret>, cd / <ret>, then partial "xy"; UP/UP/DOWN/DOWN must
#     show exactly "root-/> cd /", "root-/> help", "root-/> cd /",
#     "root-/> xy" on the VGA text buffer - no leftovers, no doubled text.
#   - Leg 2: input via serial (FIFO) -> serial tail path; the redraw output is
#     mirrored to VGA, so the same VGA-buffer assertions apply for a no-output
#     command ("cd /") recall.
#
# Usage: powershell -ExecutionPolicy Bypass -File build\history-vga-check.ps1
#        (expects build/x64/neutrinoos.img to exist; rebuild first if needed)

param(
    [string]$Distro = "Ubuntu-24.04",
    [string]$WslRepo = "/mnt/d/Projects/Code/NeutrinoOS",
    [int]$MonitorPort = 5599,
    [int]$BootTimeoutSec = 60,
    [switch]$KeepRunning
)

$ErrorActionPreference = "Stop"

function Invoke-WslBash {
    param([string]$Command)
    wsl.exe -d $Distro -u root -- bash -c $Command
    if ($LASTEXITCODE -ne 0) { throw "WSL command failed: $Command" }
}

function Start-NeutrinoQemu {
    Invoke-WslBash "bash $WslRepo/build/history-vga-boot.sh $MonitorPort"
}

# ---------------- monitor client ----------------
class QemuMonitor {
    [System.Net.Sockets.TcpClient]$client
    [System.Net.Sockets.NetworkStream]$stream

    QemuMonitor([int]$port) {
        $this.client = New-Object System.Net.Sockets.TcpClient
        $this.client.Connect("127.0.0.1", $port)
        $this.stream = $this.client.GetStream()
        $this.stream.ReadTimeout = 300
        Start-Sleep -Milliseconds 300
        $this.Drain()
    }

    [string] Drain() {
        $sb = New-Object System.Text.StringBuilder
        $buf = New-Object byte[] 65536
        try {
            while ($true) {
                $read = $this.stream.Read($buf, 0, $buf.Length)
                if ($read -le 0) { break }
                [void]$sb.Append([System.Text.Encoding]::ASCII.GetString($buf, 0, $read))
            }
        } catch { }
        return $sb.ToString()
    }

    [string] Send([string]$command) {
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($command + "`n")
        $this.stream.Write($bytes, 0, $bytes.Length)
        Start-Sleep -Milliseconds 250
        return $this.Drain()
    }

    [void] Close() {
        try { $this.stream.Close(); $this.client.Close() } catch { }
    }
}

function Read-VgaText {
    param([QemuMonitor]$Monitor)
    $sb = New-Object System.Text.StringBuilder
    for ($offset = 0; $offset -lt 4000; $offset += 512) {
        $addr = 0xB8000 + $offset
        $out = $Monitor.Send(("xp/512bx 0x{0:X}" -f $addr))
        $bytes = New-Object System.Collections.Generic.List[int]
        foreach ($line in ($out -split "`n")) {
            if ($line -match '^\s*[0-9a-f]+:\s+(.*)$') {
                foreach ($tok in ($matches[1] -split '\s+')) {
                    if ($tok -match '^0x([0-9a-fA-F]{1,2})$') {
                        $bytes.Add([Convert]::ToInt32($matches[1], 16))
                    }
                }
            }
        }
        for ($i = 0; $i -lt $bytes.Count; $i += 2) {
            $ch = $bytes[$i]
            if ($ch -ge 32 -and $ch -lt 127) { [void]$sb.Append([char]$ch) }
            else { [void]$sb.Append(" ") }
        }
    }
    return $sb.ToString()
}

function Get-VgaRow {
    param([QemuMonitor]$Monitor, [int]$Row)
    $screen = Read-VgaText $Monitor
    if ($screen.Length -lt 2000) { return "<read-failed>" }
    return $screen.Substring($Row * 80, 80)
}

function Get-LastVgaLine {
    param([QemuMonitor]$Monitor)
    $screen = Read-VgaText $Monitor
    if ($screen.Length -lt 2000) { return "<read-failed>" }
    $last = ""
    for ($i = 0; $i -lt 25; $i++) {
        $row = $screen.Substring($i * 80, 80).TrimEnd()
        if ($row.Length -gt 0) { $last = $row }
    }
    return $last
}

function Get-LastLines {
    param([QemuMonitor]$Monitor, [int]$Count)
    $screen = Read-VgaText $Monitor
    if ($screen.Length -lt 2000) { return @("<read-failed>") }
    $rows = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt 25; $i++) {
        $row = $screen.Substring($i * 80, 80).TrimEnd()
        if ($row.Length -gt 0) { $rows.Add($row) }
    }
    $n = [Math]::Min($Count, $rows.Count)
    if ($n -le 0) { return @("") }
    return $rows.GetRange($rows.Count - $n, $n).ToArray()
}

function Find-VgaRow {
    param([QemuMonitor]$Monitor, [string]$Prefix)
    $screen = Read-VgaText $Monitor
    if ($screen.Length -lt 2000) { return -1 }
    # Bottom-up: the most recent render of a line is the lowest match
    # (e.g. an earlier typed echo may still contain the same text).
    for ($i = 24; $i -ge 0; $i--) {
        if ($screen.Substring($i * 80, 80).StartsWith($Prefix)) { return $i }
    }
    return -1
}

function Wait-VgaFor {
    param([QemuMonitor]$Monitor, [string]$Text, [int]$TimeoutSec)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $screen = Read-VgaText $Monitor
        if ($screen -match [regex]::Escape($Text)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Get-SendkeyName([char]$ch) {
    if ($ch -eq ' ') { return "spc" }
    if ($ch -eq '/') { return "slash" }
    if ($ch -eq '-') { return "minus" }
    if ($ch -eq '.') { return "dot" }
    if ($ch -eq ',') { return "comma" }
    return [string]$ch
}

function Send-Keys {
    param([QemuMonitor]$Monitor, [string]$Text, [int]$DelayMs = 120)
    foreach ($ch in $Text.ToCharArray()) {
        [void]$Monitor.Send("sendkey $(Get-SendkeyName $ch)")
        Start-Sleep -Milliseconds $DelayMs
    }
}

# ---------------- main ----------------
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host "  [PASS] $name" -ForegroundColor Green }
    else {
        Write-Host "  [FAIL] $name" -ForegroundColor Red
        if ($detail) { Write-Host "         observed: $detail" -ForegroundColor DarkYellow }
        $failures.Add($name)
    }
}

Write-Host "[HIST] NeutrinoOS history redraw verification (VGA + serial)" -ForegroundColor Cyan

Start-NeutrinoQemu
$monitor = $null
try {
    Write-Host "[HIST] waiting for the QEMU monitor..."
    $connected = $false
    for ($i = 0; $i -lt 40; $i++) {
        try { $monitor = [QemuMonitor]::new($MonitorPort); $connected = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $connected) { throw "could not connect to the QEMU monitor" }

    Check "prompt appears on VGA" (Wait-VgaFor $monitor "root-/>" $BootTimeoutSec) ""

    # ---- Leg 1: PS/2 input (VGA tail path) ----
    Write-Host "[HIST] Leg 1: PS/2 keyboard input" -ForegroundColor Yellow
    Send-Keys $monitor "help"
    [void]$monitor.Send("sendkey ret")
    [void](Wait-VgaFor $monitor "External utilities" 20)
    Start-Sleep -Milliseconds 500

    Send-Keys $monitor "cd /"
    [void]$monitor.Send("sendkey ret")
    Start-Sleep -Seconds 2

    Send-Keys $monitor "xy"
    Start-Sleep -Milliseconds 500
    $row = (Get-LastVgaLine $monitor)
    Check "L1a: typed partial line renders cleanly" ($row -eq "root-/> xy") $row

    [void]$monitor.Send("sendkey up")
    Start-Sleep -Seconds 1
    $row = (Get-LastVgaLine $monitor)
    Check "L1b: UP recalls 'cd /' exactly (no leftovers)" ($row -eq "root-/> cd /") $row

    [void]$monitor.Send("sendkey up")
    Start-Sleep -Seconds 1
    $row = (Get-LastVgaLine $monitor)
    Check "L1c: UP again recalls 'help' exactly" ($row -eq "root-/> help") $row

    [void]$monitor.Send("sendkey down")
    Start-Sleep -Seconds 1
    $row = (Get-LastVgaLine $monitor)
    Check "L1d: DOWN shrinks to 'cd /' (no 'help' remnants)" ($row -eq "root-/> cd /") $row

    [void]$monitor.Send("sendkey down")
    Start-Sleep -Seconds 1
    $row = (Get-LastVgaLine $monitor)
    Check "L1e: DOWN returns to the saved partial line 'xy'" ($row -eq "root-/> xy") $row

    [void]$monitor.Send("sendkey ret")
    Start-Sleep -Seconds 2

    # ---- wrapped-line redraw: a line longer than 80 columns ----
    $long = "wrap-" + (("1234567890" * 9) -join "")
    Send-Keys $monitor $long 30
    [void]$monitor.Send("sendkey ret")
    Start-Sleep -Seconds 2
    Send-Keys $monitor "qw"
    Start-Sleep -Milliseconds 500
    [void]$monitor.Send("sendkey up")
    Start-Sleep -Seconds 1
    $full = "root-/> " + $long
    $r = Find-VgaRow $monitor "root-/> wrap-"
    $rowA = if ($r -ge 0) { (Get-VgaRow $monitor $r).TrimEnd() } else { "<not found>" }
    $rowB = if ($r -ge 0 -and $r -lt 24) { (Get-VgaRow $monitor ($r + 1)).TrimEnd() } else { "<no next row>" }
    Check "L1f: wrapped recall renders on two rows" `
        (($rowA -eq $full.Substring(0, 80).TrimEnd()) -and ($rowB -eq $full.Substring(80).TrimEnd())) `
        "$rowA | $rowB"

    [void]$monitor.Send("sendkey up")
    Start-Sleep -Seconds 1
    $rowA = if ($r -ge 0) { (Get-VgaRow $monitor $r).TrimEnd() } else { "<not found>" }
    $rowB = if ($r -ge 0 -and $r -lt 24) { (Get-VgaRow $monitor ($r + 1)).TrimEnd() } else { "<no next row>" }
    Check "L1g: shrinking back to one row clears the wrapped row" `
        (($rowA -eq "root-/> xy") -and ($rowB -eq "")) `
        "$rowA | $rowB"

    [void]$monitor.Send("sendkey ret")
    Start-Sleep -Seconds 2

    # ---- Leg 2: serial input (serial tail path; output mirrored to VGA) ----
    Write-Host "[HIST] Leg 2: serial (FIFO) input" -ForegroundColor Yellow
    Invoke-WslBash "printf 'cd /\r' > /root/qin-hist"
    Start-Sleep -Seconds 3
    Invoke-WslBash "printf 'ab' > /root/qin-hist"
    Start-Sleep -Seconds 2
    $row = (Get-LastVgaLine $monitor)
    Check "L2a: serial-typed partial line renders cleanly" ($row -eq "root-/> ab") $row

    Invoke-WslBash "printf '\033[A' > /root/qin-hist"
    Start-Sleep -Seconds 2
    $row = (Get-LastVgaLine $monitor)
    Check "L2b: serial UP recalls 'cd /' exactly (no doubled prompt)" ($row -eq "root-/> cd /") $row

    Invoke-WslBash "printf '\r' > /root/qin-hist"
    Start-Sleep -Seconds 2

    # ---- wrapped-line redraw via serial input ----
    $slong = "wrap-" + (("1234567890" * 9) -join "")
    Invoke-WslBash "printf '$slong\r' > /root/qin-hist"
    Start-Sleep -Seconds 3
    Invoke-WslBash "printf 'qw' > /root/qin-hist"
    Start-Sleep -Seconds 2
    Invoke-WslBash "printf '\033[A' > /root/qin-hist"
    Start-Sleep -Seconds 2
    $sfull = "root-/> " + $slong
    $r = Find-VgaRow $monitor "root-/> wrap-"
    $rowA = if ($r -ge 0) { (Get-VgaRow $monitor $r).TrimEnd() } else { "<not found>" }
    $rowB = if ($r -ge 0 -and $r -lt 24) { (Get-VgaRow $monitor ($r + 1)).TrimEnd() } else { "<no next row>" }
    Check "L2c: serial wrapped recall renders on two rows" `
        (($rowA -eq $sfull.Substring(0, 80).TrimEnd()) -and ($rowB -eq $sfull.Substring(80).TrimEnd())) `
        "$rowA | $rowB"

    Invoke-WslBash "printf '\033[A' > /root/qin-hist"
    Start-Sleep -Seconds 2
    $rowA = if ($r -ge 0) { (Get-VgaRow $monitor $r).TrimEnd() } else { "<not found>" }
    $rowB = if ($r -ge 0 -and $r -lt 24) { (Get-VgaRow $monitor ($r + 1)).TrimEnd() } else { "<no next row>" }
    Check "L2d: serial shrink clears the wrapped row" `
        (($rowA -eq "root-/> cd /") -and ($rowB -eq "")) `
        "$rowA | $rowB"

    # ---- prompt: {user}-{pwd}> and cd tracking ----
    Invoke-WslBash "printf '\r' > /root/qin-hist"
    Start-Sleep -Seconds 3
    $row = (Get-LastVgaLine $monitor)
    Check "P1: prompt is {user}-{pwd}> (root-/>)" ($row -eq "root-/>") $row

    Invoke-WslBash "printf 'cd /apps\r' > /root/qin-hist"
    Start-Sleep -Seconds 3
    $lines = Get-LastLines $monitor 3
    $prompt = $lines[$lines.Count - 1]
    Check "P2: prompt tracks cd (root-/apps>)" ($prompt -eq "root-/apps>") ($lines -join " | ")

    Invoke-WslBash "printf 'pwd\r' > /root/qin-hist"
    Start-Sleep -Seconds 3
    $lines = Get-LastLines $monitor 3
    $prompt = $lines[$lines.Count - 1]
    Check "P2b: pwd agrees with the prompt" ($lines[$lines.Count - 2] -eq "/apps") ($lines -join " | ")

    Invoke-WslBash "printf 'cd /\r' > /root/qin-hist"
    Start-Sleep -Seconds 3
    $row = (Get-LastVgaLine $monitor)
    Check "P3: prompt back to root-/> after cd /" ($row -eq "root-/>") $row

    Write-Host ""
    if ($failures.Count -eq 0) {
        Write-Host "RESULT: PASS" -ForegroundColor Green
    } else {
        Write-Host ("RESULT: FAIL ({0} failures: {1})" -f $failures.Count, ($failures -join ", ")) -ForegroundColor Red
    }
}
finally {
    if ($monitor) {
        try { [void]$monitor.Send("quit") } catch { }
        $monitor.Close()
    }
    if (-not $KeepRunning) {
        try { Invoke-WslBash "pkill -9 -f qin-hist 2>/dev/null || true; pkill -9 -f qemu-system 2>/dev/null || true; rm -f /root/qin-hist" } catch { }
    }
}

if ($failures.Count -gt 0) { exit 1 } else { exit 0 }
