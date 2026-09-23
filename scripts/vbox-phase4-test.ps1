param(
    [string]$VmName = "NeutrinoOSCli",
    [int]$ShellTimeoutSec = 90
)
# Type a scripted Phase 4 session into the running "NeutrinoOSCli" VM via
# VBoxManage keyboardputscancode (PS/2 keyboard -> VGA console is the active
# input in the GUI image), then assert the results from the serial transcript
# (build\vbox-gui-serial.log) and drop a few screenshots.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\gui-vm.ps1      # create + start VM first
#   powershell -ExecutionPolicy Bypass -File scripts\vbox-phase4-test.ps1
$ErrorActionPreference = "Stop"
$vb = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
$base = "d:\Projects\Code\NeutrinoOS\build"
$serial = Join-Path $base "vbox-gui-serial.log"

function Read-SerialLog([string]$path) {
    if (-not (Test-Path $path)) { return "" }
    try {
        $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
              [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $sr = New-Object System.IO.StreamReader($fs)
        $t = $sr.ReadToEnd()
        $sr.Close(); $fs.Close()
        return $t
    } catch { return "" }
}

# --- scancode (set 1) typing --------------------------------------------------
$sc = @{}
$digits = "1234567890"
$digitCodes = 0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0A,0x0B
for ($i = 0; $i -lt 10; $i++) { $sc[$digits[$i]] = $digitCodes[$i] }
$letters = "qwertyuiopasdfghjklzxcvbnm"
$letterCodes = 0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,
               0x1E,0x1F,0x20,0x21,0x22,0x23,0x24,0x25,0x26,
               0x2C,0x2D,0x2E,0x2F,0x30,0x31,0x32
for ($i = 0; $i -lt $letters.Length; $i++) { $sc[$letters[$i]] = $letterCodes[$i] }
$sc[[char]' '] = 0x39
$sc[[char]'/'] = 0x35
$sc[[char]'.'] = 0x34

function Send-Chunk([string]$text) {
    $codes = @()
    foreach ($ch in $text.ToCharArray()) {
        $c = [char]::ToLowerInvariant($ch)
        if (-not $sc.ContainsKey($c)) { throw "no scancode for '$ch'" }
        $m = $sc[$c]
        $codes += ('{0:x2}' -f $m)
        $codes += ('{0:x2}' -f ($m -bor 0x80))
    }
    if ($codes.Count -gt 0) {
        & $vb controlvm $VmName keyboardputscancode $codes | Out-Null
    }
}

function Send-Line([string]$text) {
    # type in chunks so the guest console keeps up
    for ($i = 0; $i -lt $text.Length; $i += 12) {
        $len = [Math]::Min(12, $text.Length - $i)
        Send-Chunk $text.Substring($i, $len)
        Start-Sleep -Milliseconds 90
    }
    Start-Sleep -Milliseconds 200
    & $vb controlvm $VmName keyboardputscancode @('1c', '9c') | Out-Null   # Enter
    Start-Sleep -Milliseconds 200
}

function Save-Screenshot([string]$name) {
    $out = Join-Path $base $name
    & $vb controlvm $VmName screenshotpng $out 2>&1 | Out-Null
    return $out
}

# Wait until `pattern` occurs at least `minCount` times in the serial log.
# App runtimes vary a lot (VirtualBox has no KVM: p4linq can take minutes),
# so each step is bounded by a generous timeout instead of a fixed sleep.
function Wait-ForMarker([string]$pattern, [int]$minCount, [int]$timeoutSec, [string]$what) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $text = Read-SerialLog $serial
        if (([regex]::Matches($text, $pattern)).Count -ge $minCount) { return $true }
        Start-Sleep -Milliseconds 1000
    }
    Write-Warning "timeout (${timeoutSec}s) waiting for: $what"
    return $false
}

# --- wait for the shell -------------------------------------------------------
Write-Host "Waiting for 'neutrinoos>' in $serial ..."
$deadline = (Get-Date).AddSeconds($ShellTimeoutSec)
$ready = $false
while ((Get-Date) -lt $deadline) {
    if ((Read-SerialLog $serial) -match "neutrinoos>") { $ready = $true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $ready) { throw "shell prompt not seen within ${ShellTimeoutSec}s - is $VmName running (scripts\gui-vm.ps1)?" }
Write-Host "[OK] shell ready - typing the Phase 4 session"
Start-Sleep -Seconds 2

# clear any partially typed line from an earlier aborted run (backspaces + Enter)
$bs = @()
1..12 | ForEach-Object { $bs += '0e'; $bs += '8e' }
& $vb controlvm $VmName keyboardputscancode $bs | Out-Null
& $vb controlvm $VmName keyboardputscancode @('1c', '9c') | Out-Null
Start-Sleep -Milliseconds 500

# --- scripted session ---------------------------------------------------------
# Wait = per-app timeout in seconds (polled; the step continues as soon as the
# marker shows up, so generous values cost nothing on fast runs).
$apps = @(
    @{ Cmd = "run /apps/p4hello.dll";  Wait = 90;  Marker = "Hello, NeutrinoOS!" },
    @{ Cmd = "run /apps/p4multi.dll";  Wait = 240; Marker = "\[multi\] PASS" },
    @{ Cmd = "run /apps/p4net.dll";    Wait = 120; Marker = "\[net\] PASS" },
    @{ Cmd = "run /apps/p4cs14.dll";   Wait = 300; Marker = "\[csharp14\] PASS" },
    @{ Cmd = "run /apps/p4async.dll";  Wait = 300; Marker = "\[async\] PASS" },
    @{ Cmd = "run /apps/p4linq.dll";   Wait = 420; Marker = "\[linq\] PASS" },
    @{ Cmd = "run /apps/p4fileio.dll"; Wait = 300; Marker = "\[fileio\] PASS" }
)

foreach ($a in $apps) {
    Write-Host ("-> {0}" -f $a.Cmd)
    # count existing occurrences so a re-run on the same boot cannot
    # "pass" against markers left by an earlier session
    $before = ([regex]::Matches((Read-SerialLog $serial), $a.Marker)).Count
    Send-Line $a.Cmd
    [void](Wait-ForMarker $a.Marker ($before + 1) $a.Wait $a.Cmd)
}

Write-Host "-> interactive test"
$text = Read-SerialLog $serial
$bannerBefore = ([regex]::Matches($text, "\[interactive\] type lines")).Count
$echoBefore = ([regex]::Matches($text, "\[interactive\] echo: vbox keyboard test")).Count
$byeBefore = ([regex]::Matches($text, "\[interactive\] bye")).Count
Send-Line "run /apps/p4inter.dll"
[void](Wait-ForMarker "\[interactive\] type lines" ($bannerBefore + 1) 120 "p4inter banner")
Send-Line "vbox keyboard test"
[void](Wait-ForMarker "\[interactive\] echo: vbox keyboard test" ($echoBefore + 1) 60 "interactive echo")
Send-Line "exit"
[void](Wait-ForMarker "\[interactive\] bye" ($byeBefore + 1) 60 "interactive bye")
[void](Wait-ForMarker "neutrinoos>" 1 30 "shell prompt after p4inter")

Save-Screenshot "vbox-phase4-final.png" | Out-Null

# --- assertions ---------------------------------------------------------------
Write-Host ""
Write-Host "[phase4] checking the serial transcript..."
$text = Read-SerialLog $serial
$checks = @(
    @{ Name = "hello output";      Pattern = "Hello, NeutrinoOS!";                  Min = 1 },
    @{ Name = "multi PASS";        Pattern = "\[multi\] PASS";                      Min = 1 },
    @{ Name = "net degraded PASS"; Pattern = "\[net\] PASS";                        Min = 1 },
    @{ Name = "cs14 PASS";         Pattern = "\[csharp14\] PASS";                   Min = 1 },
    @{ Name = "async PASS";        Pattern = "\[async\] PASS";                      Min = 1 },
    @{ Name = "linq PASS";         Pattern = "\[linq\] PASS";                       Min = 1 },
    @{ Name = "fileio PASS";       Pattern = "\[fileio\] PASS";                     Min = 1 },
    @{ Name = "interactive echo";  Pattern = "\[interactive\] echo: vbox keyboard test"; Min = 1 },
    @{ Name = "interactive bye";   Pattern = "\[interactive\] bye";                 Min = 1 },
    @{ Name = "no system halt";    Pattern = "SYSTEM HALTED";                       Min = 0 }
)
$failures = 0
foreach ($c in $checks) {
    $count = ([regex]::Matches($text, $c.Pattern)).Count
    $ok = if ($c.Min -eq 0) { $count -eq 0 } else { $count -ge $c.Min }
    if (-not $ok) { $failures++ }
    Write-Host ("  [{0}] {1} (count={2}, expected>={3})" -f $(if ($ok) { "PASS" } else { "FAIL" }), $c.Name, $count, $c.Min)
}

Write-Host ""
Write-Host ("[phase4] {0} checks failed" -f $failures)
Write-Host "VM '$VmName' left running (window) for manual inspection."
Write-Host "Screenshot: $base\vbox-phase4-final.png"
exit $failures
