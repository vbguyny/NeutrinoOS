# Phase 9 acceptance tests.
#
# Runs the machine-checkable Phase 9 evidence end to end:
#   1. USB: xHCI controller + USB keyboard + USB mass storage + CDC-ACM
#      over QEMU (build/p9-usb-test.sh)
#   2. IPv6: SLAAC, DHCPv6, NDP, ICMPv6 ping6, DNS AAAA over virtio-net
#      (build/p9-ipv6-acc3.sh)
#   3. HTTP/2: in-guest h2test suite (12 checks) plus the host-side wire
#      matrix over a TAP link: HTTP/1.1, h2 prior knowledge, h2c
#      upgrade, connection reuse/plexing, TLS h1.1, TLS h2 ALPN
#      (build/p9-h2test.sh + build/p9-h2-tap.sh)
#   4. HTTP/3: aioquic HTTP/3 client against the guest QUIC server
#      (build/p9-quic-test.sh)
#   5. ACPI power: QEMU `poweroff` (S5, QEMU exits by itself), `reboot`
#      (boot banner appears again in the same QEMU process), S3 sleep +
#      monitor wake, `cpupower` (build/p9-acpi-test.sh)
#   6. NVMe: controller bring-up, IDENTIFY, LBA0 signature read,
#      write/flush/read/verify on the last LBA (build/p9-nvme-test.sh)
#   7. VirtualBox: `poweroff` and `reboot` in the NeutrinoOSPowerTest VM
#      (scripts/test-vbox-power.ps1; skipped when VirtualBox is absent)
#   8. Regression: the Phase 4-8 acceptance runners
#      (tests/run-phase4-tests.ps1 ... run-phase8-tests.ps1)
#
# Prerequisites: the WSL build tree (Ubuntu-24.04, /root/neutrino) and
# the usual QEMU/OVMF/mtools packages (see docs/PHASE9-ACCEPTANCE.md).
# The runner refreshes the run image itself (leg "deploy").
#
# Usage: pwsh -File tests\run-phase9-tests.ps1 [-Only <leg>[,<leg>...]]
#   legs: deploy, usb, ipv6, http2, http3, acpi, nvme, vbox, regress
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

Write-Host "=== Phase 9 acceptance tests ===" -ForegroundColor Cyan

$wslLegs = @("usb", "ipv6", "http2", "http3", "acpi", "nvme") | Where-Object { Want $_ }
if ((Want "deploy") -and $wslLegs.Count -gt 0) {
    Write-Host "`n--- 0/8 WSL build + deploy (fresh image with utilities) ---" -ForegroundColor Yellow
    $out = Wsl "bash /root/p9-build.sh > /root/p9r-deploy.log 2>&1; bash -c 'cd /root/neutrino && bash build/p5-apps-build.sh >> /root/p9r-deploy.log 2>&1 && bash build/p5-deploy.sh >> /root/p9r-deploy.log 2>&1'; mdir -i /root/run.img ::/drivers 2>/dev/null | grep -ci nvme; grep -a 'p5-deploy' /root/p9r-deploy.log | head -1" 3600
    $ok = ([regex]::Matches($out, "(?m)^1\s*$")).Count -ge 1
    Record "WSL image (make image + utilities + deploy)" $ok ""
}

if (Want "usb") {
    Write-Host "`n--- 1/8 USB (xHCI, HID keyboard, BOT storage, CDC-ACM) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p9-usb-test.sh 2>&1" 1200
    $ok = $out -match "usb summary: ALL-PASS"
    Record "USB stack: keyboard + mass storage + serial (QEMU)" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
}

if (Want "ipv6") {
    Write-Host "`n--- 2/8 IPv6 (SLAAC, DHCPv6, NDP, ping6, DNS AAAA) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p9-ipv6-acc3.sh 2>&1" 1500
    $ok = $out -match "PASS=(\d+) FAIL=0" -and [int]$Matches[1] -ge 6
    Record "IPv6 dual stack end to end (virtio-net + slirp)" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
}

if (Want "http2") {
    Write-Host "`n--- 3/8 HTTP/2 (in-guest suite + host wire matrix) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p9-h2test.sh > /root/p9r-h2test.out 2>&1; grep -c 'h2test: PASS' /root/p9h2t.txt" 900
    $n = 0
    [int]::TryParse(($out.Trim() -split "`n" | Select-Object -Last 1), [ref]$n) | Out-Null
    # 12 numbered check lines + a trailing "h2test: PASS" summary line.
    Record "in-guest h2test: 12/12 frame/HPACK/flow checks" ($n -ge 13) "count=$n"

    $out = Wsl "bash $root/build/p9-h2-tap.sh 2>&1" 900
    $h1    = $out -match "h1 rc=0"
    $prior = $out -match "prior rc=0"
    $up    = $out -match "upgrade rc=0"
    $tls   = $out -match "tls rc=0"
    $tls1  = $out -match "tls1 rc=0"
    $mux   = ($out -match "two rc=0") -and ([regex]::Matches($out, "(?m)^2\s*$").Count -ge 1)
    $clean = ($out -notmatch "RAWV") -and ($out -notmatch "RAWW")
    Record "wire: HTTP/1.1 + h2 prior knowledge + h2c upgrade + TLS ALPN" ($h1 -and $prior -and $up -and $tls -and $tls1) ""
    Record "wire: h2 connection reuse (two requests, one connection)" $mux ""
    Record "wire: no raw faults during the h2/TLS matrix" $clean ""
}

if (Want "http3") {
    Write-Host "`n--- 4/8 HTTP/3 (QUIC v1 + QPACK, aioquic client) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p9-quic-test.sh 2>&1" 900
    $ok = ($out -match "RESULT: PASS") -and ($out -match "status: 200") -and ($out -notmatch "RAWV")
    Record "HTTP/3 handshake + GET /health = 200 (real QUIC client)" $ok (($out -split "`n" | Where-Object { $_ -match "RESULT|status:" } | Select-Object -First 1))
}

if (Want "acpi") {
    Write-Host "`n--- 5/8 ACPI power (S5 poweroff, reboot, S3 sleep, cpupower) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p9-acpi-test.sh 2>&1" 1500
    $ok = $out -match "acpi summary: ALL-PASS"
    Record "QEMU poweroff (S5) + reboot + S3 + cpupower" $ok (($out -split "`n" | Where-Object { $_ -match "^FAIL" } | Select-Object -First 1))
}

if (Want "nvme") {
    Write-Host "`n--- 6/8 NVMe (IDENTIFY, read LBA0, write/flush/read verify) ---" -ForegroundColor Yellow
    $out = Wsl "bash $root/build/p9-nvme-test.sh 2>&1" 900
    $ok = $out -match "NVME: PASS"
    Record "NVMe controller bring-up + data path (QEMU)" $ok (($out -split "`n" | Where-Object { $_ -match "signature|write test|BAR0" } | Select-Object -First 2) -join "; ")
}

if (Want "vbox") {
    Write-Host "`n--- 7/8 VirtualBox poweroff + reboot ---" -ForegroundColor Yellow
    $vb = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
    if (-not (Test-Path $vb)) {
        Record "VirtualBox poweroff + reboot" $false "VirtualBox not installed (documented optional leg)"
    } else {
        $job = Start-Job -ScriptBlock {
            param($script)
            powershell -NoProfile -ExecutionPolicy Bypass -File $script 2>&1
        } -ArgumentList (Join-Path $PSScriptRoot "..\scripts\test-vbox-power.ps1")
        if (-not (Wait-Job $job -Timeout 1200)) {
            Stop-Job $job; Remove-Job $job -Force
            Record "VirtualBox poweroff + reboot" $false "[TIMEOUT after 1200s]"
        } else {
            $out = (Receive-Job $job | Out-String)
            Remove-Job $job -Force
            Write-Host $out
            $ok = ($out -match "\[PASS\] vbox poweroff") -and ($out -match "\[PASS\] vbox reboot")
            Record "VirtualBox poweroff + reboot" $ok ""
        }
    }
}

if (Want "regress") {
    Write-Host "`n--- 8/8 Regression: Phase 4-8 acceptance runners ---" -ForegroundColor Yellow
    foreach ($p in 4, 5, 6, 7, 8) {
        $script = Join-Path $PSScriptRoot "run-phase$p-tests.ps1"
        if (-not (Test-Path $script)) {
            Record "phase$p acceptance suite (runner missing)" $false ""
            continue
        }
        $tmp = Join-Path $env:TEMP "p9-regress-p$p.out"
        Write-Host "  running tests\run-phase$p-tests.ps1 ..."
        $job = Start-Job -ScriptBlock {
            param($s, $t)
            & powershell -NoProfile -ExecutionPolicy Bypass -File $s *> $t
            "EXITRC=$LASTEXITCODE"
        } -ArgumentList $script, $tmp
        if (-not (Wait-Job $job -Timeout 5400)) {
            Stop-Job $job; Remove-Job $job -Force
            Record "phase$p acceptance suite" $false "[TIMEOUT after 5400s]"
            continue
        }
        $out = (Receive-Job $job | Out-String)
        Remove-Job $job -Force
        if (Test-Path $tmp) {
            Get-Content $tmp -Tail 3 | ForEach-Object { Write-Host "    $_" }
        }
        Record "phase$p acceptance suite" ($out -match "EXITRC=0") ""
    }
}

Write-Host "`n=== Phase 9 summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = ($results | Where-Object { $_.Result -eq "FAIL" }).Count
if ($failed -eq 0) {
    Write-Host "ALL-PASS ($($results.Count) checks)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "$failed check(s) FAILED" -ForegroundColor Red
    exit 1
}
