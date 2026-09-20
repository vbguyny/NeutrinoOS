# NeutrinoOS Phase 2 - console acceptance test (Windows 11 + WSL2)
#
# Boots the built image in QEMU (via WSL2), drives the interactive
# console_io_test.dll and the neutrinoos> shell with scripted keystrokes,
# and verifies every Phase 2 console criterion against the captured
# serial log (build/x64/serial-conio.log inside WSL).
#
# Usage (from the repo root, PowerShell 7):
#   scripts/test-console.ps1                # rebuild image, then test
#   scripts/test-console.ps1 -SkipBuild     # test the existing image
#
# Exit code 0 = all console checks passed.

#Requires -Version 7
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [int]$TimeoutSec = 900
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$distro = "Ubuntu-24.04"

# Convert the Windows repo path to its WSL /mnt path
$driveLetter = $repo.Substring(0, 1).ToLower()
$wslRepo = "/mnt/$driveLetter" + $repo.Substring(2).Replace('\', '/')
$wslRebuild = "$wslRepo/build/wsl-rebuild.sh"
$wslRunner = "$wslRepo/build/wsl-conio-runner.py"

Write-Host "NeutrinoOS Phase 2 console acceptance"
Write-Host "  repo (WSL): $wslRepo"

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "== Rebuilding boot image (WSL: make image) =="
    & wsl.exe -d $distro -u root -- bash $wslRebuild
    if ($LASTEXITCODE -ne 0) {
        Write-Host "BUILD FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "== Running console acceptance (QEMU + scripted keystrokes) =="
& wsl.exe -d $distro -u root -- python3 $wslRunner "--timeout" "$TimeoutSec"
$rc = $LASTEXITCODE

Write-Host ""
Write-Host "Serial log: \\wsl.localhost\$distro\root\neutrino\build\x64\serial-conio.log"
if ($rc -eq 0) {
    Write-Host "=== PHASE 2 CONSOLE ACCEPTANCE: PASS ===" -ForegroundColor Green
} else {
    Write-Host "=== PHASE 2 CONSOLE ACCEPTANCE: FAIL ===" -ForegroundColor Red
}
exit $rc
