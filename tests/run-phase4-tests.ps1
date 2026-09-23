# NeutrinoOS Phase 4 test runner (Windows 11 host, WSL2 required).
#
# Builds the Phase 4 test applications, deploys them into a scratch test
# image, boots QEMU with a scripted serial session, and reports the
# per-app results.
#
# Usage:
#   pwsh tests/run-phase4-tests.ps1
#   pwsh tests/run-phase4-tests.ps1 -SkipBuild      # reuse last app build
#
# Exit code: 0 when every expected marker is present (all apps are
# expected to pass on the current tree; SYSTEM HALTED must not appear).
# The script doubles as a regression gate for the Phase 4 test suite.

param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repo "build"

function Invoke-Wsl([string]$cmd) {
    & wsl.exe -d Ubuntu-24.04 -u root -- bash -c $cmd
    if ($LASTEXITCODE -ne 0) { throw "WSL command failed ($LASTEXITCODE): $cmd" }
}

if (-not $SkipBuild) {
    Write-Host "[phase4] building test apps..."
    Invoke-Wsl "bash /mnt/d/Projects/Code/NeutrinoOS/build/p4-apps-build.sh > /root/p4apps-run.log 2>&1; grep -c 'Build succeeded' /root/p4apps-run.log"
}

Write-Host "[phase4] deploying image..."
Invoke-Wsl "bash /mnt/d/Projects/Code/NeutrinoOS/build/p4-deploy.sh > /root/deploy-run.log 2>&1; tail -1 /root/deploy-run.log"

Write-Host "[phase4] running scripted QEMU session (about 3 minutes)..."
Invoke-Wsl "bash /mnt/d/Projects/Code/NeutrinoOS/build/p4-session.sh > /root/p4session-run.log 2>&1; cat /root/p4session-run.log"

# Expected markers: value >= 1 means "must appear", 0 means "must not".
# p4net is asserted via its degraded-mode PASS (no NIC in the harness).
$expected = @(
    @{ Name = "hello output";        Pattern = "Hello, NeutrinoOS!";     Min = 1 },
    @{ Name = "multi PASS";          Pattern = "\[multi\] PASS";          Min = 1 },
    @{ Name = "cs14 PASS";           Pattern = "\[csharp14\] PASS";       Min = 1 },
    @{ Name = "async PASS";          Pattern = "\[async\] PASS";          Min = 1 },
    @{ Name = "linq PASS";           Pattern = "\[linq\] PASS";           Min = 1 },
    @{ Name = "interactive bye";     Pattern = "\[interactive\] bye";     Min = 1 },
    @{ Name = "net degraded PASS";   Pattern = "\[net\] PASS";            Min = 1 },
    @{ Name = "fileio PASS";         Pattern = "\[fileio\] PASS";         Min = 1 },
    @{ Name = "no system halt";      Pattern = "SYSTEM HALTED";           Min = 0 }
)

Write-Host ""
Write-Host "[phase4] checking expectations against /root/q4.log..."
$failures = 0
foreach ($e in $expected) {
    $out = & wsl.exe -d Ubuntu-24.04 -u root -- bash -c "strings /root/q4.log | grep -c '$($e.Pattern)' || true"
    $count = 0
    if ([int]::TryParse(($out | Select-Object -First 1), [ref]$count)) {}
    $ok = if ($e.Min -eq 0) { $count -eq 0 } else { $count -ge $e.Min }
    $status = if ($ok) { "PASS" } else { "FAIL" }
    if (-not $ok) { $failures++ }
    Write-Host ("  [{0}] {1} (count={2}, expected>={3})" -f $status, $e.Name, $count, $e.Min)
}

Write-Host ""
Write-Host "[phase4] see docs/PHASE4-ACCEPTANCE.md for the full checklist and"
Write-Host "[phase4] docs/PHASE4-REPORT.md for the current status and outstanding items."
if ($failures -gt 0) { exit 1 }
exit 0
