# NeutrinoOS Phase 5 demo (Windows 11 host).
#
# Boots the OS in QEMU (WSL) and runs the scripted demo tour: shell
# basics, pipes/redirection, background jobs, system info utilities,
# tab completion, prompt customization, loopback ping, ifconfig and
# history - printing the transcript.
#
# Usage:
#   pwsh scripts/phase5-demo.ps1

$ErrorActionPreference = "Stop"
Write-Host "[demo] starting NeutrinoOS Phase 5 demo (about 3 minutes)..."
& wsl.exe -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/scripts/phase5-demo.sh
exit $LASTEXITCODE
