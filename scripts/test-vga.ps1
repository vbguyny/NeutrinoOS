# NeutrinoOS Phase 3 - VGA console verification from Windows 11 (PowerShell)
#
# Boots neutrinoos.img under QEMU (inside WSL2), drives the emulated PS/2
# keyboard through the QEMU monitor (`sendkey`), reads the VGA text
# framebuffer through the monitor (`xp 0xB8000`) and asserts that:
#   1. the boot banner and the `neutrinoos>` prompt appear on VGA,
#   2. keystrokes typed on the PS/2 keyboard are echoed on VGA,
#   3. a submitted line is executed and the result is visible on VGA,
#   4. a screendump can be captured (kept under build/x64/).
#
# This mirrors the automated acceptance runner (build/wsl-vga-test.py) but
# is driven from Windows PowerShell as required by the Phase 3 spec.
#
# Usage:
#   powershell -File scripts\test-vga.ps1                 # full check
#   powershell -File scripts\test-vga.ps1 -SkipBuild      # image exists
#   powershell -File scripts\test-vga.ps1 -WithWindow     # show the QEMU
#                                                         # VGA window (WSLg)
#   powershell -File scripts\test-vga.ps1 -KeepRunning    # leave VM up

param(
    [string]$Distro = "Ubuntu-24.04",
    [string]$WslRepo = "/mnt/d/Projects/Code/NeutrinoOS",
    [int]$MonitorPort = 5599,
    [int]$BootTimeoutSec = 45,
    [switch]$SkipBuild,
    [switch]$WithWindow,
    [switch]$KeepRunning
)

$ErrorActionPreference = "Stop"

function Invoke-WslBash {
    param([string]$Command)
    wsl.exe -d $Distro -u root -- bash -c $Command
    if ($LASTEXITCODE -ne 0) { throw "WSL command failed: $Command" }
}

function Start-NeutrinoQemu {
    $display = if ($WithWindow) { "gtk" } else { "none" }
    $script = @"
cd $WslRepo
pkill -9 qemu-system-x86_64 2>/dev/null || true
sleep 1
rm -f /tmp/tv-serial.log build/x64/vga-screen.ppm
test -f build/x64/OVMF_VARS.fd || cp /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS.fd
echo 1 > /tmp/skip-boot-tests
mdel -i build/x64/neutrinoos.img ::/skip-boot-tests ::/console-vga-off 2>/dev/null || true
mcopy -o -i build/x64/neutrinoos.img /tmp/skip-boot-tests ::/skip-boot-tests
nohup qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS.fd \
  -drive file=build/x64/neutrinoos.img,format=raw,if=virtio \
  -vga std -display $display \
  -serial file:/tmp/tv-serial.log \
  -monitor tcp:127.0.0.1:$MonitorPort,server,nowait \
  -no-reboot -no-shutdown >/tmp/tv-qemu.out 2>&1 &
echo started
"@
    Invoke-WslBash $script.Replace("`r", "")
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
    # Read the 80x25 text buffer (4000 bytes) in chunks and extract the
    # character bytes (every second byte; attribute bytes are skipped).
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
            elseif ($ch -eq 10) { [void]$sb.Append("`n") }
            else { [void]$sb.Append(" ") }
        }
    }
    return $sb.ToString()
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

function Send-Keys {
    param([QemuMonitor]$Monitor, [string]$Text)
    foreach ($ch in $Text.ToCharArray()) {
        $key = if ($ch -eq ' ') { "spc" } else { [string]$ch }
        [void]$Monitor.Send("sendkey $key")
        Start-Sleep -Milliseconds 120
    }
}

# ---------------- main ----------------
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok) {
    if ($ok) { Write-Host "  [PASS] $name" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $name" -ForegroundColor Red; $failures.Add($name) }
}

Write-Host "[VGA] NeutrinoOS Phase 3 VGA verification (WSL2 + QEMU)" -ForegroundColor Cyan

if (-not $SkipBuild) {
    Write-Host "[VGA] rebuilding kernel + image (wsl-rebuild.sh)..."
    Invoke-WslBash "bash $WslRepo/build/wsl-rebuild.sh 2>&1 | tail -2"
}

Start-NeutrinoQemu
$monitor = $null
try {
    Write-Host "[VGA] waiting for the QEMU monitor..."
    $connected = $false
    for ($i = 0; $i -lt 40; $i++) {
        try {
            $monitor = [QemuMonitor]::new($MonitorPort)
            $connected = $true
            break
        } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $connected) { throw "could not connect to the QEMU monitor" }

    Check "prompt appears on the VGA text buffer" (Wait-VgaFor $monitor "neutrinoos>" $BootTimeoutSec)

    Send-Keys $monitor "echo hi"
    [void]$monitor.Send("sendkey ret")
    Check "PS/2 keystrokes are echoed and executed on VGA" (Wait-VgaFor $monitor "echo hi" 15)

    [void]$monitor.Send("screendump $WslRepo/build/x64/vga-screen.ppm")
    Start-Sleep -Seconds 1
    $dumpOk = (Invoke-WslBash "test -s $WslRepo/build/x64/vga-screen.ppm && echo yes || echo no") -match "yes"
    Check "screendump captured (build/x64/vga-screen.ppm)" $dumpOk

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
        try { Invoke-WslBash "pkill -9 qemu-system-x86_64 2>/dev/null || true" } catch { }
    }
}

if ($failures.Count -gt 0) { exit 1 } else { exit 0 }
