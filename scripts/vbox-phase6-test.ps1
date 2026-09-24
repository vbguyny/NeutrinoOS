param(
    [string]$VmName = "NeutrinoOSCli",
    [int]$BootTimeoutSec = 120,
    [switch]$KeepRunning
)
# Phase 6 VirtualBox acceptance test.
# Boots the GUI image variant (built by build/p6-vbox-image.sh, which carries
# /etc/boot.params: dhcp + sshd + webhost autostart) in a NAT-enabled VM
# (gui-vm.ps1 -Net): port forwards 2222->22, 8080->80, 8444->443.
#
# Checks:
#   1. boot: shell prompt, DHCP via VBox NAT (10.0.2.15), sshd + webhost
#      listening (serial transcript build\vbox-gui-serial.log)
#   2. Windows OpenSSH client (ssh.exe -> 127.0.0.1:2222): uname / ls / gc
#   3. Windows curl.exe HTTP: /health, /time, /var/www static, 404
#   4. HTTPS: Microsoft Edge headless if present (BoringSSL); otherwise
#      WSL curl (OpenSSL). Windows curl.exe/Schannel cannot do Ed25519.
#   5. no SYSTEM HALTED; screenshot for evidence
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\vbox-phase6-test.ps1
#   ... -KeepRunning   leave the VM up for manual poking
$ErrorActionPreference = "Stop"
$vb = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
$base = "d:\Projects\Code\NeutrinoOS\build"
$img = Join-Path $base "neutrinoos-gui.img"
$serial = Join-Path $base "vbox-gui-serial.log"
$shot = Join-Path $base "vbox-phase6.png"
$key = Join-Path $env:TEMP "neutrinoos-p6key"

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

function Wait-Marker([string]$pattern, [int]$timeoutSec, [string]$what) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((Read-SerialLog $serial) -match $pattern) { Write-Host "[ok] $what"; return $true }
        Start-Sleep -Milliseconds 750
    }
    Write-Warning "timeout (${timeoutSec}s) waiting for: $what"
    return $false
}

$results = @()
function Record([string]$name, [bool]$ok, [string]$detail = "") {
    $script:results += [pscustomobject]@{ Test = $name; Result = $(if ($ok) { "PASS" } else { "FAIL" }); Detail = $detail }
    $color = if ($ok) { "Green" } else { "Red" }
    Write-Host ("[{0}] {1} {2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $name, $detail) -ForegroundColor $color
}

Write-Host "=== Phase 6 VirtualBox test ===" -ForegroundColor Cyan
if (-not (Test-Path $img)) {
    throw "GUI image missing: $img`nBuild it first:  wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-vbox-image.sh"
}

# --- ssh key (same key pair the QEMU suites use) ------------------------------
if (-not (Test-Path $key)) {
    wsl.exe -d Ubuntu-24.04 -u root -- bash -c "cp -f /root/p6key /mnt/c/Users/$env:USERNAME/AppData/Local/Temp/neutrinoos-p6key"
}
if (-not (Test-Path $key)) { throw "no ssh key at $key (run build/p6-ssh-test.sh once to create /root/p6key)" }
icacls $key /inheritance:r /grant:r "$($env:USERNAME):R" | Out-Null

# --- 1. recreate + boot -------------------------------------------------------
Write-Host "Recreating VM '$VmName' with NAT forwards (gui-vm.ps1 -Net -NoStart)..."
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "gui-vm.ps1") -Net -NoStart

Write-Host "Starting headless..."
& $vb startvm $VmName --type headless | Out-Null

$promptOk = Wait-Marker 'neutrinoos> ' $BootTimeoutSec "boot: shell prompt"
$dhcpOk   = Wait-Marker 'eth0 configured: 10\.0\.2\.15' 90 "net: DHCP via VBox NAT (10.0.2.15)"
$sshdOk   = Wait-Marker '\[sshd\] listening on port 22' 60 "sshd listening"
$webOk    = Wait-Marker '\[web\] listening on port 80' 60 "webhost listening (80 + 443)"
Record "boot + boot.params autostart" ($promptOk -and $dhcpOk -and $sshdOk -and $webOk) ""

# --- 2. Windows OpenSSH -------------------------------------------------------
$sshExe = Join-Path $env:SystemRoot "System32\OpenSSH\ssh.exe"
$sshOpts = @("-i", $key, "-p", "2222", "-o", "StrictHostKeyChecking=no",
             "-o", "UserKnownHostsFile=NUL", "-o", "BatchMode=yes",
             "-o", "ConnectTimeout=10", "-o", "LogLevel=ERROR")
$uname = ""
for ($t = 0; $t -lt 10; $t++) {
    $uname = & $sshExe @sshOpts user@127.0.0.1 "uname" 2>&1
    if ("$uname" -match "NeutrinoOS") { break }
    Start-Sleep -Seconds 3
}
Record "ssh.exe (Windows) -> uname" ("$uname" -match "NeutrinoOS") "out: $uname"

$lsOut = & $sshExe @sshOpts user@127.0.0.1 "ls /" 2>&1
Record "ssh.exe -> ls /" ($LASTEXITCODE -eq 0) ""

$gcOut = & $sshExe @sshOpts user@127.0.0.1 "gc" 2>&1
Record "ssh.exe -> gc" ($LASTEXITCODE -eq 0) ""

# --- 3. Windows HTTP (curl.exe) ------------------------------------------------
$health = ""
for ($t = 0; $t -lt 10; $t++) {
    $health = & curl.exe -s --max-time 10 http://127.0.0.1:8080/health 2>&1
    if ("$health" -match '"status":"ok"') { break }
    Start-Sleep -Seconds 3
}
Record "curl.exe -> http /health" ("$health" -match '"status":"ok"') "$health"

$timeOut = & curl.exe -s --max-time 10 http://127.0.0.1:8080/time 2>&1
Record "curl.exe -> http /time (JSON)" ("$timeOut" -match '"utc"') "$timeOut"

$static = & curl.exe -s --max-time 10 http://127.0.0.1:8080/hello.txt 2>&1
Record "curl.exe -> /var/www static file" ("$static" -match "static file from /var/www") "$static"

$code = & curl.exe -s -o NUL -w "%{http_code}" --max-time 10 http://127.0.0.1:8080/missing 2>&1
Record "curl.exe -> /missing = 404" ("$code" -match "404") "code=$code"

# --- 4. HTTPS + password auth (OpenSSL/pty clients via WSL->host IP) ----------
# Windows Schannel clients (curl.exe, Edge in some modes) do not offer
# Ed25519 signature algorithms, so drive the wire checks from WSL.
$probe = wsl.exe -d Ubuntu-24.04 -u root -- bash -c "bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-vbox-host-probe.sh" 2>&1
$httpsOk = "$probe" -match '"status":"ok"'
$httpsLine = ($probe -split "`n" | Where-Object { $_ -match 'status' } | Select-Object -First 1)
Record "https /health (TLS 1.3, WSL curl/OpenSSL)" $httpsOk "$httpsLine"

$pwOk = "$probe" -match "PW-OK"
Record "ssh password auth (WSL pty driver)" $pwOk ""
if (-not $pwOk) { Write-Host $probe }

# --- 5. health + evidence -------------------------------------------------------
$halted = (Read-SerialLog $serial) -match "SYSTEM HALTED"
Record "no SYSTEM HALTED" (-not $halted) ""
cmd /c "`"$vb`" controlvm $VmName screenshotpng `"$shot`" > nul 2>&1"
Write-Host "screenshot: $shot"

if (-not $KeepRunning) {
    Write-Host "Powering off VM..."
    cmd /c "`"$vb`" controlvm $VmName poweroff > nul 2>&1"
} else {
    Write-Host "VM left running (NeutrinoOSCli) - ssh -p 2222, http :8080, https :8444"
}

Write-Host "`n=== Phase 6 VirtualBox summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = ($results | Where-Object { $_.Result -eq "FAIL" }).Count
if ($failed -eq 0) {
    Write-Host "ALL PHASE 6 VBOX CHECKS PASSED" -ForegroundColor Green
    exit 0
} else {
    Write-Host "$failed CHECK(S) FAILED" -ForegroundColor Red
    exit 1
}
