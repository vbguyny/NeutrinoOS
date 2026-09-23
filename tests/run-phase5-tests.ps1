# NeutrinoOS Phase 5 test runner (Windows 11 host, WSL2 required).
#
# Rebuilds the kernel + 32 utilities, deploys a scratch image, then runs
# the scripted Phase 5 sessions (shell walkthrough, system utilities,
# tab completion, optionally a NIC session) and reports PASS/FAIL.
#
# Usage:
#   pwsh tests/run-phase5-tests.ps1
#   pwsh tests/run-phase5-tests.ps1 -SkipBuild     # reuse the last image
#   pwsh tests/run-phase5-tests.ps1 -WithNet       # add the NIC session (slower)
#
# Exit code: 0 when every expectation passes.
#
# Requires the local build helpers (build/p5-*.sh) which the phase build
# scripts install alongside the kernel image.

param(
    [switch]$SkipBuild,
    [switch]$WithNet
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repo "build"
$failures = 0

function Invoke-WslBash([string]$script) {
    $out = & wsl.exe -d Ubuntu-24.04 -u root -- bash $script 2>&1
    return ($out | Out-String)
}

function Check-Count([string]$name, [string]$text, [string]$pattern, [int]$min) {
    $count = ([regex]::Matches($text, $pattern)).Count
    $ok = $count -ge $min
    if (-not $ok) { $script:failures++ }
    $status = if ($ok) { "PASS" } else { "FAIL" }
    Write-Host ("  [{0}] {1} (count={2}, expected>={3})" -f $status, $name, $count, $min)
    return $ok
}

function Check-Script([string]$name, [string]$text) {
    $pass = ([regex]::Matches($text, "(?m)^PASS ")).Count
    $fail = ([regex]::Matches($text, "(?m)^FAIL ")).Count
    if ($fail -gt 0 -or $pass -eq 0) { $script:failures++ ; $ok = $false } else { $ok = $true }
    $status = if ($ok) { "PASS" } else { "FAIL" }
    Write-Host ("  [{0}] {1} ({2} PASS, {3} FAIL)" -f $status, $name, $pass, $fail)
    return $ok
}

if (-not $SkipBuild) {
    Write-Host "[phase5] rebuilding kernel + utilities (about 4 minutes)..."
    $r = Invoke-WslBash "/mnt/d/Projects/Code/NeutrinoOS/build/p5-all.sh"
    if ($r -notmatch "rebuild=0" -or $r -notmatch "utils=0" -or $r -notmatch "deploy=0") {
        Write-Host $r
        throw "build failed"
    }
    Write-Host "[phase5] build OK"
}

Write-Host "[phase5] running shell walkthrough session (~3 min)..."
$session = Invoke-WslBash "/mnt/d/Projects/Code/NeutrinoOS/build/p5-session.sh"
Check-Count "session: hello world"      $session "hello world" 1
Check-Count "session: append"           $session "second line" 1
Check-Count "session: missing file rc"  $session "no such file" 1
Check-Count "session: missing dir rc"   $session "no such directory" 1
Check-Count "session: env PATH"         $session "PATH=/bin:/apps" 1
Check-Count "session: env USER"         $session "USER=root" 1
Check-Count "session: clean exit"       $session "logout" 1
if ($session -match "SYSTEM HALTED") {
    $failures++
    Write-Host "  [FAIL] session: kernel crash marker present"
} else {
    Write-Host "  [PASS] session: no kernel crash markers"
}

Write-Host "[phase5] running system utilities session (~2 min)..."
$sys = Invoke-WslBash "/mnt/d/Projects/Code/NeutrinoOS/build/p5-sysinfo-test.sh"
Check-Script "system utilities" $sys

Write-Host "[phase5] running tab completion session (~2 min)..."
$tab = Invoke-WslBash "/mnt/d/Projects/Code/NeutrinoOS/build/p5-tab-test.sh"
Check-Count "tab: command completion"  $tab "echo hello : [1-9]" 1
Check-Count "tab: path completion"     $tab "HELLO1\.TXT : [1-9]" 1
Check-Count "tab: listing"             $tab "logout : [1-9]" 1
Check-Count "tab: dir completion"      $tab "ls /apps/ : [1-9]" 1

if ($WithNet) {
    Write-Host "[phase5] running NIC session (~8 min, starts local HTTP/TCP servers)..."
    $net = Invoke-WslBash "/mnt/d/Projects/Code/NeutrinoOS/build/p5-net-test.sh"
    Check-Script "network utilities" $net
}

Write-Host ""
Write-Host "[phase5] see docs/PHASE5-ACCEPTANCE.md for the full checklist and"
Write-Host "[phase5] docs/PHASE5-REPORT.md for the current status."
if ($failures -gt 0) {
    Write-Host "[phase5] FAILED ($failures expectation(s))"
    exit 1
}
Write-Host "[phase5] ALL EXPECTATIONS PASSED"
exit 0
