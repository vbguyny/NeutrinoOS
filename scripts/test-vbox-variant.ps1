param(
    [string]$Variant = "cpus2",
    [int]$TimeoutSec = 45
)
# Try a VirtualBox config variant against the existing NeutrinoOSTest VM.
$vb = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
$name = "NeutrinoOSTest"
$base = "d:\Projects\Code\NeutrinoOS\build"
$serial = Join-Path $base "vbox-serial.log"

$ErrorActionPreference = "Continue"
& $vb controlvm $name poweroff 2>&1 | Out-Null
Start-Sleep -Seconds 2

switch ($Variant) {
    "cpus2"    { & $vb modifyvm $name --cpus 2 --firmware efi | Out-Null }
    "efi64"    { & $vb modifyvm $name --firmware efi64 --cpus 1 | Out-Null }
    "ich9"     { & $vb modifyvm $name --firmware efi --cpus 1 --chipset ich9 | Out-Null }
    "piix3"    { & $vb modifyvm $name --firmware efi --cpus 1 --chipset piix3 | Out-Null }
    default    { Write-Host "unknown variant"; exit 2 }
}
$ErrorActionPreference = "Stop"

Remove-Item -Force $serial -ErrorAction SilentlyContinue
& $vb startvm $name --type headless | Out-Null

$deadline = (Get-Date).AddSeconds($TimeoutSec)
$result = "TIMEOUT"
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if (Test-Path $serial) {
        try {
            $fs = [System.IO.File]::Open($serial, [System.IO.FileMode]::Open,
                  [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            $sr = New-Object System.IO.StreamReader($fs)
            $text = $sr.ReadToEnd(); $sr.Close(); $fs.Close()
        } catch { $text = "" }
        if ($text -match "neutrinoos> ") { $result = "SHELL"; break }
        if ($text -match "X64 Exception Type") { $result = "FIRMWARE-CRASH"; break }
        if ($text -match "SYSTEM HALTED") { $result = "OS-HALTED"; break }
    }
}
Write-Host "VARIANT=$Variant RESULT=$result"
if (Test-Path $serial) {
    Get-Content $serial | Select-String -Pattern "K!|^S|^8|^r|Exception Type|neutrinoos" | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
}
& $vb controlvm $name poweroff 2>&1 | Out-Null
Start-Sleep -Seconds 2
exit 0
