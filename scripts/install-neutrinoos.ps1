# Phase 7: install NeutrinoOS v1.0.0 on Hyper-V.
# Raw FAT image boots directly as a virtual disk (UEFI, no partition table
# required). Converts the raw .img to VHDX and attaches it to a Generation-2
# (UEFI) VM with Secure Boot off.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\install-neutrinoos.ps1 `
#       -Image dist\neutrinoos-1.0.0.img
param(
    [Parameter(Mandatory = $true)][string]$Image,
    [string]$VmName = "NeutrinoOS",
    [int]$MemoryMB = 2048,
    [int]$CpuCount = 1,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Image)) { throw "Image not found: $Image" }
if (-not (Get-Command Convert-VHD -ErrorAction SilentlyContinue)) {
    throw "Hyper-V PowerShell module not found. Enable Hyper-V (Windows Pro/Enterprise)."
}

# --- VM ------------------------------------------------------------------------
$existing = Get-VM -Name $VmName -ErrorAction SilentlyContinue
if ($existing) {
    if (-not $Force) { throw "VM '$VmName' already exists (use -Force to recreate)." }
    Write-Host "[install] removing existing VM '$VmName'..."
    if ($existing.State -ne "Off") { Stop-VM -Name $VmName -Force }
    Remove-VM -Name $VmName -Force
}

Write-Host "[install] creating UEFI VM '$VmName' (${MemoryMB} MB, $CpuCount vCPU)..."
New-VM -Name $VmName -MemoryStartupBytes ($MemoryMB * 1MB) -Generation 2 -NoVHD | Out-Null
Set-VM -Name $VmName -ProcessorCount $CpuCount -AutomaticCheckpointsEnabled $false
Set-VMFirmware -VMName $VmName -EnableSecureBoot Off    # NeutrinoOS loads an unsigned AOT image
Add-VMComPort -VMName $VmName -Number 1 -Path ".\$VmName-serial.log"

# --- Disk ----------------------------------------------------------------------
$vhdx = Join-Path (Split-Path $Image) "$VmName.vhdx"
if (Test-Path $vhdx) { Remove-Item $vhdx -Force }
Write-Host "[install] converting $Image -> $vhdx ..."
Convert-VHD -Path $Image -DestinationPath $vhdx -VHDType Dynamic
Add-VMHardDiskDrive -VMName $VmName -Path $vhdx

Write-Host "[install] starting VM..."
Start-VM -Name $VmName
Write-Host "[install] done. Connect with: vmconnect localhost $VmName"
Write-Host "[install] serial transcript: .\$VmName-serial.log"
