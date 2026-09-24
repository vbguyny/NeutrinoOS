# Phase 7: launch NeutrinoOS in QEMU on Windows 11.
# Requires: QEMU for Windows (winget install qemu) with its bundled OVMF.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\run-qemu.ps1 `
#       [-Image dist\neutrinoos-1.0.0.qcow2] [-Memory 2G] [-Serial] [-Gui]
param(
    [string]$Image = "$PSScriptRoot\..\dist\neutrinoos-1.0.0.qcow2",
    [string]$Memory = "2G",
    [switch]$Serial,     # attach stdio serial (headless console)
    [switch]$Gui         # show a graphical window (VGA console)
)

$ErrorActionPreference = "Stop"

$qemu = Get-Command qemu-system-x86_64.exe -ErrorAction SilentlyContinue
if (-not $qemu) { $qemu = Get-Command qemu-system-x86_64 -ErrorAction SilentlyContinue }
if (-not $qemu) { throw "qemu-system-x86_64 not found (winget install qemu)." }

if (-not (Test-Path $Image)) { throw "Image not found: $Image" }
$imgFormat = if ($Image -match "\.qcow2$") { "qcow2" } else { "raw" }

# OVMF firmware (bundled with QEMU for Windows).
$ovmfCandidates = @(
    "$env:ProgramFiles\qemu\share\edk2-x86_64-code.fd",
    "$env:ProgramFiles\qemu\share\OVMF_CODE.fd",
    "$env:ProgramFiles\qemu\share\edk2-i386-code.fd"
)
$ovmf = $ovmfCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $ovmf) { throw "OVMF firmware not found under $env:ProgramFiles\qemu\share (needed for UEFI boot)." }

$args = @(
    "-machine", "q35",
    "-m", $Memory,
    "-smp", "1",
    "-drive", "if=pflash,format=raw,readonly=on,file=`"$ovmf`"",
    "-drive", "format=$imgFormat,file=`"$Image`"",
    "-netdev", "user,id=n0,hostfwd=tcp::2222-:22,hostfwd=tcp::8080-:80,hostfwd=tcp::8444-:443",
    "-device", "virtio-net-pci,netdev=n0",
    "-no-reboot"
)

if ($Serial) { $args += @("-serial", "stdio") } else { $args += @("-serial", "file:$PSScriptRoot\..\dist\serial.log") }
if (-not $Gui) { $args += @("-display", "none") }

Write-Host "[qemu] $($qemu.Source) $($args -join ' ')"
Write-Host "[qemu] SSH: -p 2222 (after 'dhcp' + 'sshd'), HTTP: 8080, HTTPS: 8444"
if (-not $Serial) { Write-Host "[qemu] serial log: dist\serial.log" }

& $qemu.Source @args
