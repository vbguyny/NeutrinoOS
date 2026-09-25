# NeutrinoOS - build a driver package (.npkg) on Windows 11.
#
# Usage:
#   pwsh scripts/build-driver.ps1 -Project path\to\NeutrinoDriver.csproj [-Key .\private.key] [-Out artifacts]
#
# - Builds the driver project (Release, dll only, no apphost/pdb/deps).
# - Reads name/version from the project's manifest.json (next to the csproj).
# - Builds the npkg-host CLI if needed and packs (+ signs with -Key) the
#   driver into <Out>\<name>-<version>.npkg.
#
# See docs/PHASE8-DRIVER.md and templates/NeutrinoDriver/README.md.

param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$Key,
    [string]$Out = "artifacts",
    [string]$Configuration = "Release",
    [string]$NpkgHost,          # optional: path to an existing npkg-host.dll
    [string]$RepoDir            # optional: also add to this repository dir
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$ProjectPath = (Resolve-Path $Project).Path
$ProjectDir = Split-Path -Parent $ProjectPath
$ManifestPath = Join-Path $ProjectDir "manifest.json"

if (-not (Test-Path $ManifestPath)) {
    Write-Error "manifest.json not found next to the project: $ManifestPath"
}

$manifest = Get-Content -Raw -Path $ManifestPath | ConvertFrom-Json
$Name = $manifest.name
$Version = $manifest.version
if (-not $Name -or -not $Version) {
    Write-Error "manifest.json must contain 'name' and 'version'"
}

$OutPath = [System.IO.Path]::GetFullPath($Out)
$PayloadPath = Join-Path $OutPath ".payload"
New-Item -ItemType Directory -Force -Path $OutPath | Out-Null
if (Test-Path $PayloadPath) { Remove-Item -Recurse -Force $PayloadPath }
New-Item -ItemType Directory -Force -Path $PayloadPath | Out-Null

Write-Host "[build-driver] dotnet build $ProjectPath -c $Configuration"
# Note: no -o - referenced projects would be dumped into the same output dir.
& dotnet build $ProjectPath -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build failed with exit code $LASTEXITCODE" }

# Stage only the driver's own entry assembly (the ABI assembly is provided
# by the OS at load time).
$EntryPoint = $manifest.driver.entryPoint
if (-not $EntryPoint) { Write-Error "manifest.json must contain driver.entryPoint (the driver assembly file name)" }
$BuiltDll = Get-ChildItem -Path (Join-Path $ProjectDir "bin\$Configuration") -Filter $EntryPoint -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $BuiltDll) {
    Write-Error "built assembly '$EntryPoint' not found under $ProjectDir\bin\$Configuration - check AssemblyName and driver.entryPoint"
}
Copy-Item -Force $BuiltDll.FullName (Join-Path $PayloadPath $EntryPoint)
Write-Host "[build-driver] payload: $EntryPoint ($($BuiltDll.Length) bytes)"

# Locate or build the host CLI.
if (-not $NpkgHost) {
    $CliDll = Join-Path $OutPath ".tools\npkg-host.dll"
    if (-not (Test-Path $CliDll)) {
        Write-Host "[build-driver] building npkg-host CLI"
        & dotnet build (Join-Path $Root "sdk\npkg\Npkg.Cli.csproj") -c Release -o (Join-Path $OutPath ".tools") --nologo -v q
        if ($LASTEXITCODE -ne 0) { Write-Error "npkg-host build failed with exit code $LASTEXITCODE" }
    }
    $NpkgHost = $CliDll
}

$PkgPath = Join-Path $OutPath "$Name-$Version.npkg"
$packArgs = @($NpkgHost, "pack", "--manifest", $ManifestPath, "--payload-dir", $PayloadPath, "--out", $PkgPath)
if ($Key) { $packArgs += @("--key", (Resolve-Path $Key).Path) }

Write-Host "[build-driver] dotnet $($packArgs -join ' ')"
& dotnet @packArgs
if ($LASTEXITCODE -ne 0) { Write-Error "npkg-host pack failed with exit code $LASTEXITCODE" }

if ($RepoDir) {
    $RepoPath = [System.IO.Path]::GetFullPath($RepoDir)
    New-Item -ItemType Directory -Force -Path $RepoPath | Out-Null
    Copy-Item -Force $PkgPath $RepoPath
    Write-Host "[build-driver] copied to repository dir: $RepoPath"
    $idxArgs = @($NpkgHost, "repo-index", "--dir", $RepoPath, "--name", "local")
    if ($Key) { $idxArgs += @("--key", (Resolve-Path $Key).Path) }
    & dotnet @idxArgs
    if ($LASTEXITCODE -ne 0) { Write-Error "repo-index failed with exit code $LASTEXITCODE" }
}

Write-Host ""
Write-Host "[build-driver] package: $PkgPath"
Write-Host "[build-driver] install on device: copy the .npkg into /repo on the image,"
Write-Host "[build-driver]   then: neutrinoos> npkg install $Name"
