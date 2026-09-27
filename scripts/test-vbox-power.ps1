param(
    [int]$TimeoutSec = 180,
    [string]$SerialWait = "neutrinoos>"
)
# NeutrinoOS VirtualBox power acceptance (Phase 9 Task 6):
#
#   1. poweroff: boot build\vbox-power.img headless, wait for the shell,
#      type "poweroff" over the emulated PS/2 keyboard
#      (VBoxManage controlvm keyboardputstring), and assert the VM
#      reaches the "powered off"/"saved" state by itself (ACPI S5
#      shuts VirtualBox down).
#   2. reboot: start it again, type "reboot", and assert a second boot
#      banner appears in the same serial log (the guest reset cycles
#      through the boot timeline again).
#
# The image comes from the WSL build tree (fresh run image with the
# skip-boot-tests marker) and is converted to VDI per run:
#   build\vbox-power.img  <- wsl: cp /root/run.img (or neutrinoos.img)
#
# Verdict lines: "[PASS] vbox poweroff" / "[PASS] vbox reboot"
$ErrorActionPreference = "Continue"
$vb = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
$name = "NeutrinoOSPowerTest"
$base = "d:\Projects\Code\NeutrinoOS\build"
$img = Join-Path $base "vbox-power.img"
$vdi = Join-Path $base "vbox-power.vdi"
$serial = Join-Path $base "vbox-power-serial.log"
$wsld = "Ubuntu-24.04"

if (-not (Test-Path $vb)) {
    Write-Host "[SKIP] VirtualBox not installed ($vb)"
    exit 2
}

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

function Send-Text([string]$vm, [string]$text) {
    # Type the text + Enter over the emulated keyboard (make/break 0x1c
    # is the Enter key).
    & $vb controlvm $vm keyboardputstring $text 2>&1 | Out-Null
    Start-Sleep -Milliseconds 400
    & $vb controlvm $vm keyboardputscancode 1c 9c 2>&1 | Out-Null
}

function Get-VmState([string]$vm) {
    $info = & $vb showvminfo $vm --machinereadable 2>$null
    foreach ($line in $info) {
        if ($line -match '^VMState="(.+)"') { return $Matches[1] }
    }
    return "unknown"
}

function Wait-Prompt([string]$vm, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $text = Read-SerialLog $serial
        if ($text -match [regex]::Escape($SerialWait)) { return $true }
        if ($text -match "X64 Exception Type") { return $false }
    }
    return $false
}

# Fresh image from the WSL build tree (fall back to the existing copy).
if (Test-Path "$base\..") {
    $r = & wsl.exe -d $wsld -u root -- bash -c "cp -f /root/run.img /mnt/d/Projects/Code/NeutrinoOS/build/vbox-power.img && echo IMG-OK" 2>&1
    if (-not ($r -match "IMG-OK")) {
        Write-Host "[WARN] could not refresh build\vbox-power.img from WSL; using existing file"
    }
}
if (-not (Test-Path $img)) {
    Write-Host "[FAIL] image not found: $img (run the WSL build + deploy first)"
    exit 1
}

# Clean previous VM / files.
& $vb controlvm $name poweroff 2>&1 | Out-Null
Start-Sleep -Seconds 2
& $vb unregistervm $name --delete 2>&1 | Out-Null
Remove-Item -Force $serial -ErrorAction SilentlyContinue
Remove-Item -Force $vdi -ErrorAction SilentlyContinue

Write-Host "Converting image to VDI..."
& $vb convertfromraw $img $vdi --format VDI | Out-Null
& $vb internalcommands sethduuid $vdi "8b104360-046c-482f-ab1d-7c2a8b8af8d5" | Out-Null

Write-Host "Creating VM..."
& $vb createvm --name $name --ostype Other_64 --register | Out-Null
& $vb modifyvm $name --memory 2048 --cpus 2 --firmware efi --ioapic on --nic1 none --hpet on | Out-Null
& $vb storagectl $name --name SATA --add sata --controller IntelAhci | Out-Null
& $vb storageattach $name --storagectl SATA --port 0 --device 0 --type hdd --medium $vdi | Out-Null
& $vb modifyvm $name --uart1 0x3F8 4 --uartmode1 file $serial | Out-Null

$exitCode = 1

# ---------- leg 1: poweroff ----------
Write-Host "`n--- leg 1: poweroff ---"
& $vb startvm $name --type headless | Out-Null
if (Wait-Prompt $name $TimeoutSec) {
    Write-Host "[PASS] shell prompt reached"
    Start-Sleep -Seconds 2
    Write-Host "Typing 'poweroff'..."
    Send-Text $name "poweroff"
    $deadline = (Get-Date).AddSeconds(60)
    $state = Get-VmState $name
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $state = Get-VmState $name
        if ($state -eq "poweroff" -or $state -eq "saved" -or $state -eq "aborted") { break }
    }
    if ($state -eq "poweroff" -or $state -eq "saved") {
        $text = Read-SerialLog $serial
        if ($text -match "S5|SLP_TYP|poweroff|Power") {
            Write-Host "[PASS] vbox poweroff (guest shut the VM down, state=$state)"
            $exitCode = 0
        } else {
            Write-Host "[PASS] vbox poweroff (VM state=$state; guest log had no S5 marker)"
            $exitCode = 0
        }
    } else {
        Write-Host "[FAIL] vbox poweroff (VM state stuck at '$state' after 60s)"
        Write-Host "       the keyboard input may not have reached the shell;"
        Write-Host "       check docs/PHASE9-ACCEPTANCE.md section on VirtualBox limits"
        & $vb controlvm $name poweroff 2>&1 | Out-Null
    }
} else {
    Write-Host "[FAIL] no shell prompt within ${TimeoutSec}s"
    $text = Read-SerialLog $serial
    if ($text -match "X64 Exception Type") { Write-Host "       VBox EFI firmware exception (known VBox finding)" }
    & $vb controlvm $name poweroff 2>&1 | Out-Null
}

# ---------- leg 2: reboot ----------
Write-Host "`n--- leg 2: reboot ---"
Start-Sleep -Seconds 2
$before = Read-SerialLog $serial
$bannersBefore = ([regex]::Matches($before, "NeutrinoOS v[\d.]+ \(x86-64 UEFI\)")).Count

& $vb startvm $name --type headless | Out-Null
if (Wait-Prompt $name $TimeoutSec) {
    Write-Host "[PASS] shell prompt reached (second boot)"
    Start-Sleep -Seconds 2
    Write-Host "Typing 'reboot'..."
    Send-Text $name "reboot"
    $rebooted = $false
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $text = Read-SerialLog $serial
        $banners = ([regex]::Matches($text, "NeutrinoOS v[\d.]+ \(x86-64 UEFI\)")).Count
        if ($banners -gt $bannersBefore) { $rebooted = $true; break }
    }
    if ($rebooted) {
        Write-Host "[PASS] vbox reboot (boot banner appeared again after 'reboot')"
        if ($exitCode -eq 0) { $exitCode = 0 }
    } else {
        Write-Host "[FAIL] vbox reboot (no second boot banner within 120s)"
        $exitCode = 1
    }
} else {
    Write-Host "[FAIL] no shell prompt on second boot"
    $exitCode = 1
}

$ErrorActionPreference = "Continue"
& $vb controlvm $name poweroff 2>&1 | Out-Null
Start-Sleep -Seconds 2
Write-Host "`n=== vbox power summary ==="
if ($exitCode -eq 0) { Write-Host "ALL-PASS" -ForegroundColor Green } else { Write-Host "FAILED" -ForegroundColor Red }
exit $exitCode
