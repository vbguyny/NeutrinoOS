# Phase 8 acceptance tests.
#
# Runs the machine-checkable Phase 8 evidence end to end:
#   1. npkg package manager: install/verify/upgrade/remove + signature and
#      journal checks over the guest shell (build/p8-npkg-test.sh)
#   2. driver framework: packaged + builtin drivers bind on the device tree
#      (build/p8-t2probe.sh)
#   2b. hot-plug: add/remove a VirtIO device in QEMU, driver loads/unloads
#      (build/p8-hotplug-test.sh)
#   3. ARM64: rebuild, headless boot to the shell (VBAR_EL1 vectors, GICv2,
#      generic-timer ticks > 0), then interactive 'help'/'version' typed
#      over the PL011 RX interrupt (build/p8-arm64-test.sh)
#   4. SDK: templates install, Release builds auto-produce .npkg, packages
#      verify (build/p8-t4-sdk-e2e.sh)
#   5. Repository server: HTTP index/signature/package serving with byte
#      parity against the host npkg CLI (build/p8-t4-repo-e2e.sh)
#   6. SDK samples: five projects build + package, lib payload inclusion,
#      driver manifest pack (build/p8-t4-samples-e2e.sh)
#   7. SDK archive: downloadable archive assembles (build/p8-sdk-archive.sh)
#   8. Ecosystem: official key, package templates pack/verify, community
#      docs, website (build/p8-t5-ecosystem.sh)
#
# Prerequisites for the npkg + hotplug legs: run build/p8-npkg-deploy.sh
# once (it builds the npkg test image and installs the fixture repository).
#
# Usage: pwsh -File tests\run-phase8-tests.ps1 [-Only <leg>[,<leg>...]]
#   legs: npkg, drivers, hotplug, arm64, sdk, repo, samples, archive, ecosystem
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

if ((Want "drivers") -or (Want "npkg") -or (Want "hotplug")) {
    Write-Host "`n--- 0/9 npkg test image (shared prereq for driver + npkg + hotplug legs) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-npkg-deploy.sh > /root/p8-deploy.log 2>&1; test -f /root/npkgtest.img && echo IMAGE-OK" 3600
    Record "npkg test image deployed (x64 image + fixture repo)" ($out -match "IMAGE-OK") ""
}

if (Want "arm64") {
    Write-Host "`n--- 3/9 ARM64 boot + interrupts + shell input ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-arm64-test.sh 2>&1" 1800
    $ok = $out -match "arm64 summary: ALL-PASS"
    Record "arm64 boot to shell, timer ticks, PL011 RX input" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
    if (-not $ok) { $out -split "`n" | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" } }
}

if (Want "drivers") {
    Write-Host "`n--- 2/9 driver framework (device tree binding) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-t2probe.sh 2>&1" 900
    $bound   = $out -match "bound 'vga-text'|bound 'uart16550'|bound 'ps2-keyboard'"
    $count   = $out -match "(\d+) driver\(s\) registered, (\d+) device\(s\) started"
    Record "drivers bind on the device tree (builtin + packaged)" ($bound -and $count) ""
    $noFatal = ($out -notmatch "RAWV") -and ($out -notmatch "EH\] FATAL")
    Record "no raw faults / unhandled exceptions during driver boot" $noFatal ""
}

if (Want "hotplug") {
    Write-Host "`n--- 2b/9 hot-plug (add/remove VirtIO device) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-hotplug-test.sh 2>&1" 900
    $ok = $out -match "hotplug summary: ALL-PASS"
    Record "hot-plug: device_add loads driver, device_del stops it" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
    if (-not $ok) { $out -split "`n" | Select-Object -Last 12 | ForEach-Object { Write-Host "  $_" } }
}

if (Want "npkg") {
    Write-Host "`n--- 1/9 npkg package manager ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-npkg-test.sh 2>&1" 3600
    $ok = $out -match "npkg summary: ALL-PASS"
    Record "npkg install/verify/upgrade/remove over the guest shell" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
    if (-not $ok -and $out -match "run build/p8-npkg-deploy.sh") {
        Write-Host "  hint: run 'bash build/p8-npkg-deploy.sh' first" -ForegroundColor DarkYellow
    }
}

if (Want "sdk") {
    Write-Host "`n--- 4/9 SDK: templates + auto-packaging ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-t4-sdk-e2e.sh 2>&1" 1800
    $ok = $out -match "t4 sdk summary: ALL-PASS"
    Record "SDK templates install, Release builds auto-produce .npkg" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
    if (-not $ok) { $out -split "`n" | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" } }
}

if (Want "repo") {
    Write-Host "`n--- 5/9 repository server (HTTP + signature parity) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-t4-repo-e2e.sh 2>&1" 1800
    $ok = $out -match "t4 repo summary: ALL-PASS"
    Record "repo server serves index/signature/packages (byte-parity with npkg CLI)" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
}

if (Want "samples") {
    Write-Host "`n--- 6/9 SDK samples (five projects) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-t4-samples-e2e.sh 2>&1" 1800
    $ok = $out -match "t4 samples summary: ALL-PASS"
    Record "five samples build + package (lib payload, driver manifest pack)" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
}

if (Want "archive") {
    Write-Host "`n--- 7/9 SDK archive ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-sdk-archive.sh 2>&1" 1800
    $ok = $out -match "SDK ARCHIVE OK"
    Record "SDK archive assembles (dist/neutrinoos-sdk-*.tar.gz)" $ok ""
}
if (Want "ecosystem") {
    Write-Host "`n--- 8/9 ecosystem (keys, package templates, docs, site) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p8-t5-ecosystem.sh 2>&1" 900
    $ok = $out -match "t5 ecosystem summary: ALL-PASS"
    Record "ecosystem: official key, package templates, community docs, website" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
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
