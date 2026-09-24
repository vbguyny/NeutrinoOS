# Phase 7: write a NeutrinoOS image to a USB stick (DESTRUCTIVE).
# The image is a raw FAT volume that UEFI firmware can boot directly.
#
# Usage (run as Administrator):
#   powershell -ExecutionPolicy Bypass -File scripts\flash-usb.ps1 `
#       -Image dist\neutrinoos-1.0.0.img -Drive E:
param(
    [Parameter(Mandatory = $true)][string]$Image,
    [Parameter(Mandatory = $true)][string]$Drive      # e.g. E: or \\.\PHYSICALDRIVE3
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this script from an elevated (Administrator) PowerShell." }
if (-not (Test-Path $Image)) { throw "Image not found: $Image" }

# --- Resolve drive -------------------------------------------------------------
if ($Drive -match '^([A-Za-z]):$') {
    $letter = $Matches[1].ToUpper()
    $part = Get-Partition -DriveLetter $letter -ErrorAction Stop
    $disk = Get-Disk -Number $part.DiskNumber
} elseif ($Drive -match 'PHYSICALDRIVE(\d+)') {
    $disk = Get-Disk -Number ([int]$Matches[1])
} else {
    throw "Unrecognized -Drive '$Drive'. Use a drive letter (E:) or \\.\PHYSICALDRIVE<n>."
}

# --- Safety checks ---------------------------------------------------------------
if ($disk.BusType -ne "USB") {
    throw "Refusing to write to non-USB disk #$($disk.Number) ($($disk.FriendlyName), bus $($disk.BusType))."
}
Write-Host "[usb] target: disk $($disk.Number)  $($disk.FriendlyName)  $([math]::Round($disk.Size/1GB,1)) GB"
Write-Host "[usb] image:  $Image ($([math]::Round((Get-Item $Image).Length/1MB,1)) MB)"
Write-Host ""
Write-Host "WARNING: EVERYTHING on this USB disk will be permanently erased." -ForegroundColor Red
$answer = Read-Host "Type the disk number ($($disk.Number)) to confirm"
if ($answer -ne "$($disk.Number)") { Write-Host "[usb] aborted."; exit 1 }

# --- Write ---------------------------------------------------------------------
Write-Host "[usb] taking disk offline for raw write..."
Set-Disk -Number $disk.Number -IsOffline $true

try {
    $physPath = "\\.\PHYSICALDRIVE$($disk.Number)"
    $src = [System.IO.File]::OpenRead($Image)
    try {
        $dst = New-Object System.IO.FileStream($physPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Write)
        try {
            $buf = New-Object byte[] (4MB)
            $total = 0
            while (($read = $src.Read($buf, 0, $buf.Length)) -gt 0) {
                $dst.Write($buf, 0, $read)
                $total += $read
            }
            $dst.Flush()
            Write-Host "[usb] wrote $([math]::Round($total/1MB,1)) MB"
        } finally { $dst.Dispose() }
    } finally { $src.Dispose() }
} finally {
    Write-Host "[usb] onlining disk..."
    Set-Disk -Number $disk.Number -IsOffline $false
}

Write-Host "[usb] done. Remove the stick and boot a UEFI machine from it."
Write-Host "[usb] note: firmware must find the FAT volume; some firmware wants a partition table -"
Write-Host "[usb]       if the stick does not appear in the boot menu, use the .qcow2/.img with a hypervisor instead."
