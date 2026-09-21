# NeutrinoOS - build a .NET 10 console app for the boot image (Windows 11).
#
# Usage:
#   pwsh scripts/build-app.ps1 -Project path\to\app.csproj -AppName myapp [-Output stage]
#
# - Builds the project with the .NET 10 SDK (Release, net10.0).
# - Stages the output DLL (and any local class libraries) under -Output.
# - Prints the mcopy deploy commands (run them from WSL) and the shell line.
#
# NOTES
#  * AppName must be FAT 8.3-safe (<= 8 chars): the NeutrinoOS FAT driver
#    resolves files by short name; longer names need LFN support that the
#    driver does not have yet.
#  * Class libraries that the app references with a ProjectReference are
#    deployed to /lib/<Name>.dll; the kernel AssemblyLoader loads them on
#    demand (see docs/PHASE4-DESIGN.md).

param(
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][string]$AppName,
    [string]$Output = "stage",
    [string]$Configuration = "Release",
    [string]$Framework = "net10.0"
)

$ErrorActionPreference = "Stop"

if ($AppName.Length -gt 8) {
    Write-Warning "AppName '$AppName' is longer than 8 characters; FAT short-name lookup will not find '$AppName.dll' on NeutrinoOS."
}

$ProjectPath = (Resolve-Path $Project).Path
$OutputPath = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

Write-Host "[build-app] dotnet build $ProjectPath -c $Configuration -f $Framework"
& dotnet build $ProjectPath -c $Configuration -f $Framework -o $OutputPath --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet build failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "[build-app] staged output: $OutputPath"
$dlls = Get-ChildItem -Path $OutputPath -Filter *.dll | Where-Object { $_.Name -notmatch '^(System|Microsoft|netstandard)' }
foreach ($dll in $dlls) { Write-Host ("  - " + $dll.Name) }

Write-Host ""
Write-Host "[build-app] Deploy (from WSL, building first with: ./build.sh):"
Write-Host "    cp build/x64/neutrinoos.img /tmp/deploy.img"
Write-Host "    mcopy -o -i /tmp/deploy.img $AppName.dll ::/apps/$AppName.dll"
foreach ($dll in $dlls) {
    if ($dll.BaseName -ne $AppName) {
        Write-Host "    mcopy -o -i /tmp/deploy.img $($dll.Name) ::/lib/$($dll.Name)"
    }
}
Write-Host ""
Write-Host "[build-app] Run in the NeutrinoOS shell:"
Write-Host "    neutrinoos> run /apps/$AppName.dll"
