# Phase 7: export the NeutrinoOSCli VirtualBox VM as a v1.0.0 OVA appliance.
# Prerequisite: the VM exists (scripts\gui-vm.ps1) and boots the release image.
param(
    [string]$Version = "1.0.0",
    [string]$VmName  = "NeutrinoOSCli",
    [string]$DistDir = "$PSScriptRoot\..\dist"
)

$ErrorActionPreference = "Stop"

$vbox = Get-Command VBoxManage.exe -ErrorAction SilentlyContinue
if (-not $vbox) { $vbox = Get-Command VBoxManage -ErrorAction SilentlyContinue }
if (-not $vbox) { throw "VBoxManage not found - install Oracle VirtualBox first." }

& $vbox.Source list vms | Out-Null
if (-not (& $vbox.Source list vms | Select-String -SimpleMatch "`"$VmName`"")) {
    throw "VM '$VmName' not found. Create it first: powershell -File scripts\gui-vm.ps1"
}

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$ova = Join-Path $DistDir "neutrinoos-$Version.ova"

Write-Host "[ova] exporting $VmName -> $ova"
& $vbox.Source export $VmName --ovf20 -o $ova
if ($LASTEXITCODE -ne 0) { throw "VBoxManage export failed ($LASTEXITCODE)" }

$hash = (Get-FileHash -Algorithm SHA256 $ova).Hash.ToLower()
$size = (Get-Item $ova).Length
Write-Host "[ova] $ova"
Write-Host "[ova] sha256 = $hash ($size bytes)"

# Append to SHA256SUMS if the release scripts already produced it.
$sums = Join-Path $DistDir "SHA256SUMS"
if (Test-Path $sums) {
    Add-Content $sums "$hash  neutrinoos-$Version.ova"
    Write-Host "[ova] appended to $sums"
}
