param(
    [int]$TimeoutSec = 120
)
# NeutrinoOS VirtualBox acceptance: boot build\neutrinoos.img (EFI, serial
# to a log file) and assert the banner + shell prompt appear.
$ErrorActionPreference = "Stop"
$vb = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
$name = "NeutrinoOSTest"
$base = "d:\Projects\Code\NeutrinoOS\build"
$img = Join-Path $base "neutrinoos.img"
$vdi = Join-Path $base "neutrinoos.vdi"
$serial = Join-Path $base "vbox-serial.log"

function Read-SerialLog([string]$path) {
    if (-not (Test-Path $path)) { return "" }
    try {
        $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
              [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $sr = New-Object System.IO.StreamReader($fs)
        $t = $sr.ReadToEnd()
        $sr.Close(); $fs.Close()
        return $t
    } catch { return "" }
}

# Clean previous VM / files (ignore errors when it doesn't exist)
$ErrorActionPreference = "Continue"
& $vb controlvm $name poweroff 2>&1 | Out-Null
Start-Sleep -Seconds 2
& $vb unregistervm $name --delete 2>&1 | Out-Null
$ErrorActionPreference = "Stop"
Remove-Item -Force $serial -ErrorAction SilentlyContinue
Remove-Item -Force $vdi -ErrorAction SilentlyContinue

Write-Host "Converting image to VDI..."
& $vb convertfromraw $img $vdi --format VDI | Out-Null

Write-Host "Creating VM..."
& $vb createvm --name $name --ostype Other_64 --register | Out-Null
# NOTE: 2 vCPUs are required - VirtualBox's EFI firmware #GPs in its
# ExitBootServices teardown path when the VM has a single vCPU.
& $vb modifyvm $name --memory 2048 --cpus 2 --firmware efi --ioapic on --nic1 none | Out-Null
& $vb storagectl $name --name SATA --add sata --controller IntelAhci | Out-Null
& $vb storageattach $name --storagectl SATA --port 0 --device 0 --type hdd --medium $vdi | Out-Null
& $vb modifyvm $name --uart1 0x3F8 4 --uartmode1 file $serial | Out-Null

Write-Host "Starting headless (timeout ${TimeoutSec}s)..."
& $vb startvm $name --type headless | Out-Null

$deadline = (Get-Date).AddSeconds($TimeoutSec)
$result = "TIMEOUT"
$lastReport = Get-Date
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $text = Read-SerialLog $serial
    if ($text -match "neutrinoos> ") { $result = "SHELL"; break }
    if ($text -match "SYSTEM HALTED") { $result = "HALTED"; break }
    if ($text -match "X64 Exception Type") { $result = "FIRMWARE-CRASH"; break }
    if (((Get-Date) - $lastReport).TotalSeconds -ge 30) {
        $lastReport = Get-Date
        $sz = 0
        if (Test-Path $serial) { $sz = (Get-Item $serial).Length }
        Write-Host ("  ...waiting ({0} bytes)" -f $sz)
    }
}

$text = Read-SerialLog $serial
Write-Host "RESULT: $result"
if ($text -match "NeutrinoOS v[\d.]+ \(x86-64 UEFI\)") {
    Write-Host "[PASS] banner"
} else {
    Write-Host "[FAIL] banner not found"
}
if ($result -eq "SHELL") {
    Write-Host "[PASS] shell prompt"
} else {
    Write-Host "[FAIL] shell prompt not reached"
}
if ($result -eq "HALTED") { Write-Host "[FAIL] system halted" }
if ($result -eq "FIRMWARE-CRASH") {
    Write-Host "[FAIL] VirtualBox EFI firmware exception after ExitBootServices"
    Write-Host "       (known finding - see PHASE1-REPORT.md section 5; bootloader"
    Write-Host "        output is visible, crash dump comes from VBox CpuDxe)"
}
if ($text -match "\[SHELL\] NeutrinoOS console ready") {
    Write-Host "[PASS] shell ready line"
}

Write-Host "Powering off..."
$ErrorActionPreference = "Continue"
& $vb controlvm $name poweroff 2>&1 | Out-Null
$ErrorActionPreference = "Stop"
Start-Sleep -Seconds 2
Write-Host "=== done ==="
if ($result -eq "SHELL") { exit 0 } else { exit 1 }
