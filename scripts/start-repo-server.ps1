<#
.SYNOPSIS
    Start a local NeutrinoOS package repository over HTTP (Windows 11).

.DESCRIPTION
    Serves a directory of .npkg packages to NeutrinoOS devices.  The
    repository index (repository.json) is regenerated on every request and
    signed with the configured Ed25519 private key, so publishing a new
    package is just copying the .npkg file into the repository directory.

    Generate a signing key first if you have not already:

        npkg-host keygen            # -> %USERPROFILE%\.neutrinoos\private.key

    On the device:

        neutrinoos> npkg repo add local http://<host-ip>:8080
        neutrinoos> npkg install <package>

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\start-repo-server.ps1
    powershell -ExecutionPolicy Bypass -File scripts\start-repo-server.ps1 -RepoDir C:\repo -Port 9000
#>
param(
    [string]$RepoDir = (Join-Path $env:USERPROFILE ".neutrinoos\repo"),
    [int]$Port = 8080,
    [string]$Key = (Join-Path $env:USERPROFILE ".neutrinoos\private.key"),
    [string]$Bind = "0.0.0.0",
    [string]$Name = "local"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "sdk\repo-server\Npkg.RepoServer.csproj"

if (-not (Test-Path $project)) {
    throw "repo server project not found: $project (run this script from the NeutrinoOS repository)"
}

New-Item -ItemType Directory -Force -Path $RepoDir | Out-Null

$serverArgs = @(
    "run", "--project", $project, "-c", "Release", "--",
    "--dir", $RepoDir, "--port", "$Port", "--bind", $Bind, "--name", $Name
)

if (Test-Path $Key) {
    $serverArgs += @("--key", $Key)
    Write-Host "Signing index with $Key"
} else {
    Write-Warning "private key not found at $Key - serving an UNSIGNED index (run 'npkg-host keygen' first)"
}

$hostIp = (Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.PrefixOrigin -ne "WellKnown" } |
    Select-Object -First 1).IPAddress

Write-Host "Repository directory : $RepoDir"
Write-Host "Listening            : http://$Bind`:$Port/"
if ($hostIp) {
    Write-Host "Device command       : npkg repo add local http://${hostIp}:$Port"
}
Write-Host ""

dotnet @serverArgs
