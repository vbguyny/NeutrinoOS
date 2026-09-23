# Phase 6 acceptance test runner (Windows 11 + WSL2).
#
# Runs the full Phase 6 verification:
#   1. managed crypto KATs in QEMU            (cryptotest, 30/30)
#   2. SSH server end-to-end                  (p6-ssh-test.sh)
#   3. web host: HTTP + TLS 1.3 + boot params (p6-web-test.sh)
#   4. firewall deny/allow                    (p6-firewall-test.sh)
#   5. Windows-side checks against a persistent VM:
#        - OpenSSH client exec (uname/ls/gc)
#        - curl.exe http://localhost:8080/
#        - curl.exe -k https://localhost:8444/health
#
# Usage:  powershell -ExecutionPolicy Bypass -File tests\run-phase6-tests.ps1
#         powershell -ExecutionPolicy Bypass -File tests\run-phase6-tests.ps1 -Step5Only
# Requires: WSL2 Ubuntu-24.04 with the Phase 6 toolchain, QEMU/OVMF.
#
# Note: Windows curl.exe uses Schannel, which does not offer Ed25519
# signature algorithms; the HTTPS check therefore runs "curl -k" from WSL
# (OpenSSL). Browsers (BoringSSL/NSS) also work.

param([switch]$Step5Only)

$ErrorActionPreference = "Continue"
$wsld = "Ubuntu-24.04"
$root = "/mnt/d/Projects/Code/NeutrinoOS"

function Wsl([string]$cmd, [int]$timeoutSec = 600) {
    $job = Start-Job -ScriptBlock {
        param($d, $c)
        wsl.exe -d $d -u root -- bash -c $c 2>&1
    } -ArgumentList $wsld, $cmd
    if (-not (Wait-Job $job -Timeout $timeoutSec)) {
        Stop-Job $job; Remove-Job $job -Force
        return "[TIMEOUT after ${timeoutSec}s]"
    }
    $out = Receive-Job $job
    Remove-Job $job -Force
    return ($out | Out-String)
}

$results = @()
function Record([string]$name, [bool]$ok, [string]$detail = "") {
    $script:results += [pscustomobject]@{ Test = $name; Result = $(if ($ok) { "PASS" } else { "FAIL" }); Detail = $detail }
    $color = if ($ok) { "Green" } else { "Red" }
    Write-Host ("[{0}] {1} {2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $name, $detail) -ForegroundColor $color
}

Write-Host "=== Phase 6 acceptance tests ===" -ForegroundColor Cyan
if (-not $Step5Only) {

# ---- 1. crypto KATs ----
Write-Host "`n--- 1/5 crypto KATs (cryptotest in QEMU) ---" -ForegroundColor Yellow
$out = Wsl "bash $root/build/p6-crypto-test.sh 2>&1 | grep -a -E 'summary: PASS=|\[FAIL\]' | tail -3"
Record "crypto KATs (PASS=30 FAIL=0)" ($out -match "summary: PASS=30 FAIL=0") (($out -split "`n" | Select-Object -Last 1).Trim())

# ---- 2. SSH E2E ----
Write-Host "`n--- 2/5 SSH server end-to-end ---" -ForegroundColor Yellow
$out = Wsl "bash $root/build/p6-ssh-test.sh 2>&1" 420
$sshOk = ($out -match "hello-from-ssh") -and ($out -match "rc=0") -and ($out -match "Permission denied") -and ($out -match "PW-OK")
Record "SSH exec + interactive + password + negative auth" $sshOk ""
if (-not $sshOk) { Write-Host $out }

# ---- 3. web host (HTTP + TLS + boot params) ----
Write-Host "`n--- 3/5 web host (HTTP + HTTPS + boot params) ---" -ForegroundColor Yellow
$out = Wsl "bash $root/build/p6-web-test.sh 2>&1" 420
$httpOk = ($out -match '"status":"ok"') -and ($out -match "static file from /var/www") -and ($out -match "code=200")
Record "HTTP routes + static + TLS 1.3 (code=200)" $httpOk ""
$bootOk = ($out -match "\[boot\]") -or ($out -match "NetMgr\] eth0 configured") -or ($out -match "\[web\] listening")
Record "boot params autostart (dhcp + webhost)" $bootOk ""
if (-not ($httpOk -and $bootOk)) { Write-Host $out }

# ---- 4. firewall ----
Write-Host "`n--- 4/5 firewall (deny 443, allow 80) ---" -ForegroundColor Yellow
$out = Wsl "bash $root/build/p6-firewall-test.sh 2>&1" 300
$fwOk = ($out -match "denied by /etc/firewall.conf") -and ($out -match "code=200") -and ($out -match "code=000")
Record "firewall deny/allow" $fwOk ""
if (-not $fwOk) { Write-Host $out }

}  # end of suites 1-4 ($Step5Only skips these)

# ---- 5. Windows-side checks against a persistent VM ----
Write-Host "`n--- 5/5 Windows OpenSSH + curl against a live VM ---" -ForegroundColor Yellow
function Test-TcpPort([string]$h, [int]$p) {
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $ar = $c.BeginConnect($h, $p, $null, $null)
        $ok = $ar.AsyncWaitHandle.WaitOne(1500)
        if ($ok) { $c.EndConnect($ar) }
        $c.Close()
        return $ok
    } catch { return $false }
}

Wsl "pkill -9 qemu-system 2>/dev/null; sleep 1; true" 30 | Out-Null
# Host the VM in a PowerShell job so it stays up for the whole check block.
$vmJob = Start-Job -ScriptBlock {
    param($d, $c)
    wsl.exe -d $d -u root -- bash -c $c 2>&1
} -ArgumentList $wsld, "bash $root/build/p6-qemu-serve.sh > /root/p6serve-out.log 2>&1"
Write-Host "[serve] starting VM (job $($vmJob.Id))..."
$up = $false
for ($i = 0; $i -lt 90; $i++) {
    if (Test-TcpPort 127.0.0.1 2222) { $up = $true; break }
    if ($vmJob.State -ne "Running") { break }
    Start-Sleep -Seconds 2
}
if (-not $up) {
    $serveLog = Wsl "strings /root/p6serve-out.log 2>/dev/null | tail -8; strings /root/p6serve.log 2>/dev/null | tail -8" 30
    Record "windows ssh/curl checks" $false "VM did not open port 2222; $serveLog"
} else {
    # 25s for guest boot params (dhcp, sshd/webhost autostart) before first ssh
    Start-Sleep -Seconds 25

    $key = Join-Path $env:TEMP "neutrinoos-p6key"
    Wsl "cp -f /root/p6key /mnt/c/Users/$env:USERNAME/AppData/Local/Temp/neutrinoos-p6key 2>/dev/null; true" 30 | Out-Null
    icacls $key /inheritance:r /grant:r "$($env:USERNAME):R" 2>&1 | Out-Null

    $ssh = Join-Path $env:SystemRoot "System32\OpenSSH\ssh.exe"
    $sshOpts = @("-i", $key, "-p", "2222", "-o", "StrictHostKeyChecking=no",
                 "-o", "UserKnownHostsFile=NUL", "-o", "BatchMode=yes",
                 "-o", "ConnectTimeout=10", "-o", "LogLevel=ERROR")

    # retry until the guest shell/sshd are live (hostfwd accepts early)
    # NOTE: use the numeric address; 'localhost' resolves to ::1 first on Windows.
    $uname = ""; $sshRc = 1
    for ($t = 0; $t -lt 10; $t++) {
        $uname = & $ssh @sshOpts user@127.0.0.1 "uname" 2>&1
        $sshRc = $LASTEXITCODE
        if ($sshRc -eq 0 -and "$uname" -match "NeutrinoOS") { break }
        Start-Sleep -Seconds 8
    }
    Record "Windows ssh.exe -> uname" ($sshRc -eq 0 -and "$uname" -match "NeutrinoOS") "out: $uname"

    $lsOut = & $ssh @sshOpts user@127.0.0.1 "ls /" 2>&1
    Record "Windows ssh.exe -> ls /" ($LASTEXITCODE -eq 0) ""

    $gcOut = & $ssh @sshOpts user@127.0.0.1 "gc" 2>&1
    Record "Windows ssh.exe -> gc" ($LASTEXITCODE -eq 0) ""

    $curl = (Get-Command curl.exe -ErrorAction SilentlyContinue)
    if ($curl) {
        $httpOut = ""
        for ($t = 0; $t -lt 10; $t++) {
            $httpOut = & curl.exe -s --max-time 10 http://127.0.0.1:8080/health 2>&1
            if ("$httpOut" -match '"status":"ok"') { break }
            Start-Sleep -Seconds 5
        }
        Record "Windows curl.exe -> http /health" ("$httpOut" -match '"status":"ok"') "$httpOut"
    } else {
        Record "Windows curl.exe http" $false "curl.exe not found"
    }

    # HTTPS from the Windows side uses curl.exe/Schannel, which does not
    # offer Ed25519: use WSL's curl (OpenSSL) for the on-wire check.
    $httpsOut = Wsl "curl -sk --max-time 15 https://127.0.0.1:8444/health" 40
    Record "HTTPS /health (WSL curl, TLS 1.3)" ("$httpsOut" -match '"status":"ok"') "$($httpsOut.Trim())"
    Write-Host "[info] curl.exe/Schannel cannot verify Ed25519 handshakes; browsers (BoringSSL/NSS) work." -ForegroundColor DarkGray
}
Stop-Job $vmJob -ErrorAction SilentlyContinue
Remove-Job $vmJob -Force -ErrorAction SilentlyContinue
Wsl "pkill -9 qemu-system 2>/dev/null; true" 30 | Out-Null

# ---- summary ----
Write-Host "`n=== Phase 6 summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = ($results | Where-Object { $_.Result -eq "FAIL" }).Count
if ($failed -eq 0) {
    Write-Host "ALL PHASE 6 CHECKS PASSED" -ForegroundColor Green
    exit 0
} else {
    Write-Host "$failed CHECK(S) FAILED" -ForegroundColor Red
    exit 1
}
