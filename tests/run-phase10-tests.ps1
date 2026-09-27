# tests/run-phase10-tests.ps1 - Phase 10 (exFAT) acceptance runner.
#
# Runs every Phase 10 leg on this machine (Windows host + WSL Ubuntu):
#   1. host smoke        - format/mount/write/rename/delete + fsck on the
#                          built-in host harness (build/p10-host-smoke.sh)
#   2. host interop      - 5 mkfs.exfat volume variants + Linux fixtures
#                          (build/p10-interop.sh; 90 checks)
#   3. corruption matrix - deliberate corruptions detect/reapir + exfatprogs
#                          agreement (build/p10-corrupt-test.sh; 41 checks)
#   4. QEMU in-VM        - AHCI exFAT disk + mkexfat/fsck/label in-guest +
#                          USB automount + Linux re-verification
#                          (build/p10-qemu-test.sh; 25 checks)
#   5. npkg package      - pack neutrinoos.utils.exfat, install on-device,
#                          run the installed tools (build/p10-package.sh,
#                          p10-npkg-deploy.sh, p10-npkg-test.sh)
#   6. big cases         - >10K-entry directory + >4 GiB file
#                          (build/p10-big-tests.sh, p10-big4g.sh)
#   7. docs              - Phase 10 documents exist with the required layers
#
# Usage:  powershell -ExecutionPolicy Bypass -File tests\run-phase10-tests.ps1
#         [-Only host|interop|corrupt|qemu|npkg|big|docs]
#
# Prereqs: the WSL build flow has run at least once:
#   p9-build.sh (make image), p5-apps-build.sh (46 utilities),
#   p5-deploy.sh (fresh /root/run.img).
param(
    [string]$Only = ""
)

$ErrorActionPreference = "Stop"
# WSL-side root (scripts run there); the docs checks use the Windows path.
$root = "/mnt/d/Projects/Code/NeutrinoOS"
$winRoot = Split-Path -Parent $PSScriptRoot
$script:results = @()

function Want([string]$name) {
    return ($Only -eq "" -or $Only -eq $name)
}

function Wsl([string]$cmd, [int]$timeoutSec = 3600) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "wsl.exe"
    $psi.Arguments = "-d Ubuntu-24.04 -u root -- bash -c `"$cmd`""
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        try { $p.Kill() } catch { }
        return "TIMEOUT after ${timeoutSec}s"
    }
    $out = $p.StandardOutput.ReadToEnd() + $p.StandardError.ReadToEnd()
    return $out
}

function Record([string]$name, [bool]$ok, [string]$detail) {
    $r = if ($ok) { "PASS" } else { "FAIL" }
    $script:results += [pscustomobject]@{ Check = $name; Result = $r; Detail = $detail }
    $color = if ($ok) { "Green" } else { "Red" }
    Write-Host ("  [{0}] {1}" -f $r, $name) -ForegroundColor $color
    if (-not $ok -and $detail) { Write-Host "        $detail" -ForegroundColor DarkYellow }
}

Write-Host "=== NeutrinoOS Phase 10 acceptance (exFAT) ===" -ForegroundColor Cyan

# ---------------------------------------------------------------- host ---
if (Want "host") {
    Write-Host "`n--- 1/7 host harness smoke ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p10-host-smoke.sh 2>&1" 1800
    $ok = $out -match "SMOKE: ALL-PASS"
    Record "host: format/mount/write/rename/fsck smoke (200 files, long names, 2 MiB)" $ok `
        (($out -split "`n" | Where-Object { $_ -match "HAS-FAILURES|FAIL:" } | Select-Object -First 1))
}

if (Want "interop") {
    Write-Host "`n--- 2/7 host interoperability (mkfs.exfat variants) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p10-interop.sh 2>&1" 3600
    $ok = $out -match "INTEROP: ALL-PASS"
    $detail = ($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 3) -join "; "
    Record "host: interop across 5 mkfs.exfat variants (90 checks)" $ok $detail
}

if (Want "corrupt") {
    Write-Host "`n--- 3/7 corruption detect/repair matrix ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p10-corrupt-test.sh 2>&1" 3600
    $ok = $out -match "CORRUPT: ALL-PASS"
    $detail = ($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 3) -join "; "
    Record "host: corruptions detected + repaired, exfatprogs agrees (41 checks)" $ok $detail
}

# ---------------------------------------------------------------- qemu ---
if (Want "qemu") {
    Write-Host "`n--- 4/7 QEMU: in-VM mount, tools, USB automount ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p10-qemu-test.sh 2>&1" 5400
    $ok = $out -match "P10QEMU: ALL-PASS"
    $detail = ($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 3) -join "; "
    Record "qemu: AHCI exFAT disk + mkexfat/fsck/label + USB automount, Linux-verified (25 checks)" $ok $detail
}

# ---------------------------------------------------------------- npkg ---
if (Want "npkg") {
    Write-Host "`n--- 5/7 npkg: neutrinoos.utils.exfat package ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p10-package.sh 2>&1" 1800
    $ok = $out -match "P10-PACKAGE: OK"
    Record "npkg: pack + sign + verify neutrinoos.utils.exfat (0.05 MB, 4 tools)" $ok `
        (($out -split "`n" | Select-Object -Last 3) -join " | ")

    $out = Wsl "bash $root/build/p10-npkg-deploy.sh 2>&1" 900
    $ok = $out -match "deploy OK"
    Record "npkg: acceptance image deployed (tools absent from /bin)" $ok `
        (($out -split "`n" | Select-Object -Last 3) -join " | ")

    $out = Wsl "bash $root/build/p10-npkg-test.sh 2>&1" 3600
    $ok = $out -match "P10NPKG: ALL-PASS"
    $detail = ($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 3) -join "; "
    Record "npkg: on-device install + installed tools format/check a disk (8 checks)" $ok $detail
}

# ---------------------------------------------------------------- big ----
if (Want "big") {
    Write-Host "`n--- 6/7 big cases: >10K entries + >4 GiB file ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p10-big-tests.sh 2>&1" 3600
    $ok = ($out -match "listed 10000/10000") -and ($out -match "clean")
    Record "big: 10 000-entry directory listed + fsck clean" $ok `
        (($out -split "`n" | Where-Object { $_ -match "FAIL" } | Select-Object -First 2) -join "; ")
    $out = Wsl "bash $root/build/p10-big4g.sh 2>&1" 7200
    $ok = ($out -match "big read 4831838208 bytes verify") -and ($out -match "clean")
    Record "big: 4.5 GiB file written + verified + fsck clean" $ok `
        (($out -split "`n" | Where-Object { $_ -match "FAIL|error" } | Select-Object -First 2) -join "; ")
}

# ---------------------------------------------------------------- docs ---
if (Want "docs") {
    Write-Host "`n--- 7/7 documentation ---" -ForegroundColor Yellow
    $docs = @(
        @{ File = "PHASE10-EXFAT.md";       Need = "Volume Structure" },
        @{ File = "PHASE10-TOOLS.md";       Need = "mkexfat" },
        @{ File = "PHASE10-INTEROP.md";     Need = "exfatprogs" },
        @{ File = "PHASE10-EXFAT-PERF.md";  Need = "MB/s" },
        @{ File = "PHASE10-ACCEPTANCE.md";  Need = "qemu" },
        @{ File = "PHASE10-REPORT.md";      Need = "Deliverables" }
    )
    foreach ($d in $docs) {
        $path = Join-Path $winRoot "docs\$($d.File)"
        $ok = (Test-Path $path)
        if ($ok) {
            $text = Get-Content $path -Raw
            $ok = $text.Length -gt 500 -and $text -match [regex]::Escape($d.Need)
        }
        Record "docs: $($d.File)" $ok "missing or incomplete ($path)"
    }
}

Write-Host "`n=== Phase 10 summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = ($results | Where-Object { $_.Result -eq "FAIL" }).Count
if ($failed -eq 0) {
    Write-Host "ALL-PASS ($($results.Count) checks)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "$failed FAILED of $($results.Count) checks" -ForegroundColor Red
    exit 1
}
