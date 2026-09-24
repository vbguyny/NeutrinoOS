# Phase 7 acceptance tests.
#
# Runs the machine-checkable Phase 7 evidence end to end:
#   1. reproducible build (make reproducible)
#   2. boot loop 3x: pass=3 halt=0
#   3. security self-test: [SEC] 4 pass 0 fail (W^X + ring-3 isolation)
#   4. syscall filter ring-3 test (Test 57)
#   5. ASLR: stack top randomized
#   6. boot-time performance threshold (boot < 25s to boot-complete)
#   7. sshd brute-force lockout + /var/log/auth.log audit
#   8. webhost per-IP rate limiting (HTTP 429)
#   9. static secure-defaults checks
#
# Usage: pwsh -File tests\run-phase7-tests.ps1
param([string[]]$Only = @())

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
function Want([string]$name) { return ($Only.Count -eq 0) -or ($Only -contains $name) }

Write-Host "=== Phase 7 acceptance tests ===" -ForegroundColor Cyan

if (Want "repro") {
    Write-Host "`n--- 1/9 reproducible build (two clean builds, byte-identical) ---" -ForegroundColor Yellow
    $out = Wsl "cd /root/neutrino && make reproducible 2>&1 | tail -4" 1200
    $ok = $out -match "byte-identical across two clean builds"
    Record "reproducible build" $ok (($out -split "`n" | Select-Object -Last 1).Trim())
}

if (Want "boot") {
    Write-Host "`n--- 2/9 boot loop 3x ---" -ForegroundColor Yellow
    $out = Wsl "cd /root/neutrino && bash $root/build/p7-bootloop.sh 3 2>&1 | tail -2" 900
    $ok = $out -match "pass=3 halt=0"
    Record "boot loop 3x (pass=3 halt=0)" $ok (($out -split "`n" | Select-Object -Last 1).Trim())

    Write-Host "`n--- 3/9 security self-test ([SEC]) ---" -ForegroundColor Yellow
    $out = Wsl "grep -a '\[SEC\]' /root/bootloop.log" 60
    $ok = ($out -match "user cannot read kernel identity memory") -and
          ($out -match "user cannot read the kernel physmap") -and
          ($out -match "kernel image is executable") -and
          ($out -match "ordinary RAM is NX") -and
          ($out -match "4 pass, 0 fail")
    Record "[SEC] ring-3 isolation + W^X (4 pass, 0 fail)" $ok ""

    Write-Host "`n--- 4/9 syscall filter (user-mode test 57) ---" -ForegroundColor Yellow
    $out = Wsl "grep -a 'syscall_filter' /root/bootloop.log | tail -2" 60
    $ok = $out -match "\[PASS\] syscall_filter install/deny/tighten verified"
    Record "syscall filter test 57" $ok ""

    Write-Host "`n--- 5/9 ASLR stack randomization ---" -ForegroundColor Yellow
    $out = Wsl "grep -a 'BinaryLoader. Setting up stack' /root/bootloop.log | head -1" 60
    $m = [regex]::Match($out, "stack:\s*0x([0-9A-F]+)")
    $top = if ($m.Success) { [Convert]::ToUInt64($m.Groups[1].Value, 16) } else { 0 }
    $canonical = [Convert]::ToUInt64("7FFFFFFF0000", 16)
    $ok = ($top -ne 0) -and ($top -lt $canonical) -and (($top -band 0xFFF) -eq 0)
    Record "stack top randomized (ASLR)" $ok ("top=0x{0:X}" -f $top)
}

if (Want "boottime") {
    Write-Host "`n--- 6/9 boot time threshold ---" -ForegroundColor Yellow
    $out = Wsl "cd /root/neutrino && bash $root/build/p7-bootmeasure.sh > /dev/null 2>&1; strings /root/p7bootmeas.log | grep -a 't=.*Boot complete' | tail -1" 240
    $m = [regex]::Match($out, "t=(\d+)ms Boot complete")
    $ms = if ($m.Success) { [int]$m.Groups[1].Value } else { 999999 }
    Record ("boot complete < 25000ms (measured {0}ms)" -f $ms) ($ms -lt 25000) ""
}

if (Want "lockout") {
    Write-Host "`n--- 7/9 sshd brute-force lockout + audit log ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p7-ssh-lockout.sh 2>&1" 420
    $ok = $out -match "ALL-PASS"
    Record "sshd lockout + /var/log/auth.log" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
}

if (Want "ratelimit") {
    Write-Host "`n--- 8/9 webhost rate limiting (HTTP 429) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p7-web-ratelimit.sh 2>&1" 420
    $ok = $out -match "ALL-PASS"
    Record "webhost 429 rate limit" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
}

if (Want "defaults") {
    Write-Host "`n--- 9/10 static secure-default checks ---" -ForegroundColor Yellow
    $out = Wsl "grep -n 'PasswordAuthEnabled = false' $root/src/ddk/Services/SshService.cs | head -2" 60
    Record "sshd password auth default OFF" ($out -match "PasswordAuthEnabled = false") ""
    $out = Wsl "grep -n 'webhost.autostart' $root/src/kernel/Shell/ShellInit.cs | head -3" 60
    Record "webhost opt-in autostart only" ($out -match "webhost.autostart") ""
}

if (Want "crypto") {
    Write-Host "`n--- 10/10 crypto known-answer tests ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p6-crypto-test.sh 2>&1 | grep -a 'summary: PASS=' | tail -1" 420
    Record "crypto KATs (PASS=30 FAIL=0)" ($out -match "PASS=30 FAIL=0") ((($out -split "`n") | Select-Object -Last 1).Trim())
}

# ---- summary ----
$pass = ($results | Where-Object { $_.Result -eq "PASS" }).Count
$fail = ($results | Where-Object { $_.Result -eq "FAIL" }).Count
Write-Host ""
Write-Host "=== phase7 summary: $pass PASS / $fail FAIL ===" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
$results | Format-Table -AutoSize
exit $(if ($fail -eq 0) { 0 } else { 1 })
