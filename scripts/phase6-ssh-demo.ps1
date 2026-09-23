# Phase 6 SSH demo (Windows 11).
#
# Connects to a running NeutrinoOS VM (QEMU hostfwd or VirtualBox NAT
# forward on port 2222) with the Windows OpenSSH client and runs a small
# interactive demo: system info, listing the root, showing a file, and a
# manual GC.
#
# Usage:
#   scripts\phase6-ssh-demo.ps1                 # expects the VM on localhost:2222
#   scripts\phase6-ssh-demo.ps1 -Port 2222 -User user
#
# The matching private key is taken from WSL (/root/p6key, created by the
# Phase 6 test harness) and copied to the Windows temp directory.

param(
    [string]$TargetHost = "localhost",
    [int]$Port = 2222,
    [string]$User = "user",
    [string]$KeyFile = "",
    [string]$WslKeyPath = "/root/p6key"
)

$ErrorActionPreference = "Stop"
$ssh = Join-Path $env:SystemRoot "System32\OpenSSH\ssh.exe"
if (-not (Test-Path $ssh)) { $ssh = "ssh.exe" }

if (-not $KeyFile) {
    $KeyFile = Join-Path $env:TEMP "neutrinoos-p6key"
    if (-not (Test-Path $KeyFile)) {
        Write-Host "[demo] copying key from WSL ($WslKeyPath)..."
        wsl.exe -d Ubuntu-24.04 -u root -- cp $WslKeyPath "/mnt/c/Users/$env:USERNAME/AppData/Local/Temp/neutrinoos-p6key"
        icacls $KeyFile /inheritance:r /grant:r "$($env:USERNAME):R" | Out-Null
    }
}

$opts = @(
    "-i", $KeyFile,
    "-p", "$Port",
    "-o", "StrictHostKeyChecking=no",
    "-o", "UserKnownHostsFile=NUL",
    "-o", "BatchMode=yes",
    "-o", "ConnectTimeout=10",
    "-o", "LogLevel=ERROR"
)

Write-Host "=== NeutrinoOS SSH demo ($User@${TargetHost}:$Port) ===" -ForegroundColor Cyan

$commands = @(
    @("uname -a",           "system information"),
    @("ls /",               "root directory"),
    @("cat /etc/boot.params", "boot parameters"),
    @("uptime",             "system uptime"),
    @("date",               "wall clock"),
    @("free",               "memory usage"),
    @("gc",                 "garbage collection"),
    @("echo hello-from-windows", "round-trip echo")
)

$fail = 0
foreach ($c in $commands) {
    $cmd = $c[0]; $label = $c[1]
    Write-Host "`n--- $label : $cmd" -ForegroundColor Yellow
    $out = & $ssh @opts "$User@$TargetHost" $cmd 2>&1
    $rc = $LASTEXITCODE
    $out | ForEach-Object { Write-Host "    $_" }
    if ($rc -ne 0) {
        Write-Host "    [FAIL rc=$rc]" -ForegroundColor Red
        $fail++
    }
}

if ($fail -eq 0) {
    Write-Host "`n[demo] all commands succeeded" -ForegroundColor Green
} else {
    Write-Host "`n[demo] $fail command(s) failed" -ForegroundColor Red
    exit 1
}
