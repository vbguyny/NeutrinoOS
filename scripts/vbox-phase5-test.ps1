param(
    [string]$VmName = "NeutrinoOSCli",
    [int]$ShellTimeoutSec = 150
)
# Phase 5 VirtualBox test: boots the "NeutrinoOSCli" VM (GUI image built by
# build/p5-vbox-image.sh, which carries the 32 Phase 5 utilities in /bin),
# types a scripted shell session with VBoxManage keyboardputstring, then
# asserts the results from the VGA-mirrored serial transcript
# (build\vbox-gui-serial.log) and drops a screenshot. Every typed line is
# verified against its echo in the transcript and retyped on mismatch, so
# a dropped keystroke cannot silently corrupt the session.
#
# Covers: prompt, uname -a, /bin contents + pipes (wc), redirection
# (> >> <), background jobs, env, date/uptime/free/df/ifconfig/mount/ps,
# kill error path, loopback ping, PS1 customization and clean exit.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\vbox-phase5-test.ps1
# The VM is always recreated fresh (gui-vm.ps1 -NoStart) so the boot, the
# image and the transcript are clean for every run.
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

# --- keyboard typing with echo verification -----------------------------------
# Lines are typed with VBoxManage keyboardputstring (VBox generates the
# scancodes itself) and then verified against the shell's echo in the
# transcript; a dropped character triggers a clear + retype, so the session
# cannot be silently corrupted by lost input (keyboardputscancode bursts
# were observed dropping occasional codes).
function Read-LastLine() {
    $t = Read-SerialLog $serial
    if ($t.Length -gt 2000) { $t = $t.Substring($t.Length - 2000) }
    $idx = $t.LastIndexOf("`n")
    if ($idx -lt 0) { return $t }
    return $t.Substring($idx + 1)
}

function Send-Enter() {
    & $vb controlvm $VmName keyboardputscancode 1c 9c | Out-Null
    Start-Sleep -Milliseconds 250
}

function Clear-Line() {
    $bs = @()
    1..48 | ForEach-Object { $bs += '0e'; $bs += '8e' }
    & $vb controlvm $VmName keyboardputscancode $bs | Out-Null
    Send-Enter   # discard any residue (Enter on an empty line is harmless)
}

function Send-Line([string]$text) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        & $vb controlvm $VmName keyboardputstring $text | Out-Null
        $matched = $false
        for ($w = 0; $w -lt 5; $w++) {
            Start-Sleep -Milliseconds 250
            $line = (Read-LastLine).TrimEnd()
            if ($line.EndsWith($text.TrimEnd())) { $matched = $true; break }
        }
        if ($matched) {
            Send-Enter
            return
        }
        Write-Host "   echo mismatch - clearing and retyping..."
        Clear-Line
        Start-Sleep -Milliseconds 250
    }
    Write-Warning "could not type '$text' cleanly after 3 attempts"
    Send-Enter
}

# Wait until `pattern` occurs at least `minCount` times in the serial log.
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

# --- recreate the VM fresh (clean boot, clean transcript) ---------------------
# (gui-vm.ps1 powers off and deletes any previous instance itself)
Write-Host "Creating VM '$VmName' (gui-vm.ps1 -NoStart)..."
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "gui-vm.ps1") -NoStart
if ($LASTEXITCODE -ne 0) { throw "gui-vm.ps1 failed ($LASTEXITCODE)" }

Remove-Item -Force $serial -ErrorAction SilentlyContinue

Write-Host "Starting '$VmName'..."
Start-Process -FilePath $vb -ArgumentList @("startvm", $VmName) -WindowStyle Hidden | Out-Null

# --- wait for the shell -------------------------------------------------------
Write-Host "Waiting for 'neutrinoos>' in $serial ..."
$deadline = (Get-Date).AddSeconds($ShellTimeoutSec)
$ready = $false
while ((Get-Date) -lt $deadline) {
    if ((Read-SerialLog $serial) -match "neutrinoos>") { $ready = $true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $ready) { throw "shell prompt not seen within ${ShellTimeoutSec}s - check the VM window and $serial" }
Write-Host "[OK] shell ready - typing the Phase 5 session"
Start-Sleep -Seconds 1

# --- scripted session ---------------------------------------------------------
# Each step waits (polled) for its marker; generous timeouts cost nothing on
# fast runs. First-run JIT of a utility happens once per boot.
$steps = @(
    @{ Cmd = "uname -a";                        Wait = 30; Marker = "phase5 x86_64" },
    @{ Cmd = "ls /bin";                Wait = 30; Marker = "WGET.DLL" },
    @{ Cmd = "ls /bin | wc -l";                 Wait = 30; Marker = "(?m)^\s+33\r?$" },
    @{ Cmd = "echo hello phase5 > /t.txt";      Wait = 30; Marker = "neutrinoos>" },
    @{ Cmd = "cat /t.txt";                      Wait = 30; Marker = "hello phase5" },
    @{ Cmd = "echo second line >> /t.txt";      Wait = 30; Marker = "neutrinoos>" },
    @{ Cmd = "cat /t.txt";                      Wait = 30; Marker = "second line" },
    @{ Cmd = "wc -l < /t.txt";                  Wait = 30; Marker = "(?m)^\s+2\r?$" },
    @{ Cmd = "sleep 4 &";                       Wait = 30; Marker = "started: sleep 4" },
    @{ Cmd = "jobs";                            Wait = 30; Marker = "sleep 4" },
    @{ Cmd = "env";                             Wait = 30; Marker = "PATH=/bin:/apps" },
    @{ Cmd = "date";                            Wait = 30; Marker = "(?m)^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}" },
    @{ Cmd = "uptime";                          Wait = 30; Marker = "up 0" },
    @{ Cmd = "free";                            Wait = 30; Marker = "GC heap:" },
    @{ Cmd = "df";                              Wait = 30; Marker = "Filesystem" },
    # ping first: it initializes the lazily-created network stack, which
    # registers the loopback interface that ifconfig then lists
    @{ Cmd = "ping -c 1 127.0.0.1";             Wait = 180; Marker = "bytes from 127.0.0.1" },
    @{ Cmd = "ifconfig";                        Wait = 30; Marker = "flags=LOOPBACK" },
    @{ Cmd = "mount";                           Wait = 30; Marker = "boot volume: NEUTRINOOS" },
    @{ Cmd = "ps";                              Wait = 30; Marker = "KERNEL THREADS" },
    @{ Cmd = "kill 9999";                       Wait = 30; Marker = "no such job" },
    @{ Cmd = "history";                         Wait = 30; Marker = "neutrinoos>" },
    @{ Cmd = "export PS1='\u@\h:\w\`$ '";       Wait = 30; Marker = "(?i)root@neutrinoos" },
    @{ Cmd = "exit";                            Wait = 30; Marker = "logout" }
)

foreach ($s in $steps) {
    Write-Host ("-> {0}" -f $s.Cmd)
    # count existing occurrences so a re-run cannot pass against stale output
    $before = ([regex]::Matches((Read-SerialLog $serial), $s.Marker)).Count
    Send-Line $s.Cmd
    $ok = Wait-ForMarker $s.Marker ($before + 1) $s.Wait $s.Cmd
    if (-not $ok) {
        # Slow first-run JIT can outlast the wait; one retry on a warm JIT.
        Write-Host "   retrying once..."
        Send-Line $s.Cmd
        [void](Wait-ForMarker $s.Marker ($before + 1) $s.Wait $s.Cmd)
    }
}

$shot = Join-Path $base "vbox-phase5-final.png"
& $vb controlvm $VmName screenshotpng $shot 2>&1 | Out-Null

# --- assertions ---------------------------------------------------------------
Write-Host ""
Write-Host "[phase5] checking the serial transcript..."
$text = Read-SerialLog $serial
$checks = @(
    @{ Name = "uname -a";            Pattern = "NeutrinoOS 0.5 phase5 x86_64";       Min = 1 },
    @{ Name = "utility in /bin";     Pattern = "WGET.DLL";                           Min = 1 },
    @{ Name = "pipe ls|wc";          Pattern = "(?m)^\s+33\r?$";                     Min = 1 },
    @{ Name = "redirection >";       Pattern = "hello phase5";                       Min = 1 },
    @{ Name = "append >>";           Pattern = "second line";                        Min = 1 },
    @{ Name = "input <";             Pattern = "(?m)^\s+2\r?$";                      Min = 1 },
    @{ Name = "background job";      Pattern = "pid 1001 started: sleep 4";          Min = 1 },
    @{ Name = "env";                 Pattern = "PATH=/bin:/apps";                    Min = 1 },
    @{ Name = "date";                Pattern = "(?m)^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}" ; Min = 1 },
    @{ Name = "uptime";              Pattern = "up 0";                               Min = 1 },
    @{ Name = "free";                Pattern = "GC heap:";                           Min = 1 },
    @{ Name = "df";                  Pattern = "Filesystem";                         Min = 1 },
    @{ Name = "ifconfig (lo)";       Pattern = "flags=LOOPBACK";                     Min = 1 },
    @{ Name = "mount";               Pattern = "boot volume: NEUTRINOOS";            Min = 1 },
    @{ Name = "ps";                  Pattern = "KERNEL THREADS";                     Min = 1 },
    @{ Name = "kill error path";     Pattern = "no such job";                        Min = 1 },
    @{ Name = "ping loopback";       Pattern = "bytes from 127.0.0.1";               Min = 1 },
    @{ Name = "ping stats";          Pattern = "packet loss";                        Min = 1 },
    @{ Name = "PS1 customization";   Pattern = "(?i)root@neutrinoos";                Min = 1 },
    @{ Name = "clean exit";          Pattern = "logout";                             Min = 1 },
    @{ Name = "no system halt";      Pattern = "SYSTEM HALTED";                      Min = 0 }
)
$failures = 0
foreach ($c in $checks) {
    $count = ([regex]::Matches($text, $c.Pattern)).Count
    $ok = if ($c.Min -eq 0) { $count -eq 0 } else { $count -ge $c.Min }
    if (-not $ok) { $failures++ }
    Write-Host ("  [{0}] {1} (count={2}, expected>={3})" -f $(if ($ok) { "PASS" } else { "FAIL" }), $c.Name, $count, $c.Min)
}

Write-Host ""
Write-Host ("[phase5] {0} checks failed" -f $failures)
Write-Host "VM '$VmName' left running (window) for manual inspection."
Write-Host "Screenshot: $shot"
Write-Host "Serial transcript: $serial"
exit $failures
