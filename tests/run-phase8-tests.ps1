# Phase 8 acceptance tests.
#
# Runs the machine-checkable Phase 8 evidence end to end:
#   1. npkg package manager: install/verify/upgrade/remove + signature and
#      journal checks over the guest shell (build/p8-npkg-test.sh)
#   2. driver framework: packaged + builtin drivers bind on the device tree
#      (build/p8-t2probe.sh)
#   3. ARM64: rebuild, headless boot to the shell (VBAR_EL1 vectors, GICv2,
#      generic-timer ticks > 0), then interactive 'help'/'version' typed
#      over the PL011 RX interrupt (build/p8-arm64-test.sh)
#
# Prerequisites for the npkg leg: run build/p8-npkg-deploy.sh once
# (it builds the npkg test image and installs the fixture repository).
#
# Usage: pwsh -File tests\run-phase8-tests.ps1 [-Only <leg>[,<leg>...]]
#   legs: npkg, drivers, arm64
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

Write-Host "=== Phase 8 acceptance tests ===" -ForegroundColor Cyan

if ((Want "drivers") -or (Want "npkg")) {
    Write-Host "`n--- 0/3 npkg test image (shared prereq for driver + npkg legs) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-npkg-deploy.sh > /root/p8-deploy.log 2>&1; test -f /root/npkgtest.img && echo IMAGE-OK" 3600
    Record "npkg test image deployed (x64 image + fixture repo)" ($out -match "IMAGE-OK") ""
}

if (Want "arm64") {
    Write-Host "`n--- 3/3 ARM64 boot + interrupts + shell input ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-arm64-test.sh 2>&1" 1800
    $ok = $out -match "arm64 summary: ALL-PASS"
    Record "arm64 boot to shell, timer ticks, PL011 RX input" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
    if (-not $ok) { $out -split "`n" | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" } }
}

if (Want "drivers") {
    Write-Host "`n--- 2/3 driver framework (device tree binding) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-t2probe.sh 2>&1" 900
    $bound   = $out -match "bound 'vga-text'|bound 'uart16550'|bound 'ps2-keyboard'"
    $count   = $out -match "(\d+) driver\(s\) registered, (\d+) device\(s\) started"
    Record "drivers bind on the device tree (builtin + packaged)" ($bound -and $count) ""
    $noFatal = ($out -notmatch "RAWV") -and ($out -notmatch "EH\] FATAL")
    Record "no raw faults / unhandled exceptions during driver boot" $noFatal ""
}

if (Want "npkg") {
    Write-Host "`n--- 1/3 npkg package manager ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-npkg-test.sh 2>&1" 3600
    $ok = $out -match "npkg summary: ALL-PASS"
    Record "npkg install/verify/upgrade/remove over the guest shell" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
    if (-not $ok -and $out -match "run build/p8-npkg-deploy.sh") {
        Write-Host "  hint: run 'bash build/p8-npkg-deploy.sh' first" -ForegroundColor DarkYellow
    }
}

Write-Host "`n=== Phase 8 summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = ($results | Where-Object { $_.Result -eq "FAIL" }).Count
if ($failed -eq 0) {
    Write-Host "ALL-PASS ($($results.Count) checks)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "$failed check(s) FAILED" -ForegroundColor Red
    exit 1
}
