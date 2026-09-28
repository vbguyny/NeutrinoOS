param(
    [switch]$Rebuild,
    [switch]$NoStart,
    [switch]$Net,
    [switch]$NoNet,
    [string]$SerialPipe = ""
)
# Create (or recreate) a separate VirtualBox VM "NeutrinoOSCli" that boots the
# GUI image variant produced by build/gui-image.sh:
#   - VGA text console mirrored from early boot (boot log + shell visible in
#     the VM window)
#   - PS/2 keyboard active (console-active-vga): type directly in the window
#   - boot tests skipped for fast manual boots
#   - virtio NIC + NAT by DEFAULT (DHCP works out of the box; `ifconfig`,
#     `dns`, `ping www.google.com` all work in the VM). Pass -NoNet for a
#     NIC-less VM. -Net is accepted for compatibility (= default).
#
# This VM is independent of scripts/test-vbox.ps1, which owns and recreates
# the "NeutrinoOSTest" VM on every run - the two do not interfere.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\gui-vm.ps1
#       -> regenerate the image if missing, recreate the VM, start it (window)
#   ... -Rebuild   -> first rebuild the kernel in WSL, then regenerate the image
#   ... -NoStart   -> create the VM but do not start it
$ErrorActionPreference = "Stop"
$vb = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
$name = "NeutrinoOSCli"
$base = "d:\Projects\Code\NeutrinoOS\build"
$img = Join-Path $base "neutrinoos-gui.img"
$vdi = Join-Path $base "neutrinoos-gui.vdi"
$serial = Join-Path $base "vbox-gui-serial.log"
# Stable VDI UUID: the media registry remembers media by file path; recreating
# the file with a fresh random UUID breaks storageattach ("does not match the
# value stored in the media registry"). A fixed UUID keeps it consistent.
$vdiUuid = "5a2b8c41-2f7e-4d9a-b6c3-1d4e5f6a7b8c"

# --- 1. Ensure the GUI image exists ------------------------------------------
if ($Rebuild -or -not (Test-Path $img)) {
    if ($Rebuild) {
        # Full chain: kernel -> utilities -> deploy image (/root/run.img, the
        # source gui-image.sh prefers) -> GUI image. A kernel-only rebuild
        # would produce an image without the /bin shell utilities.
        Write-Host "Rebuilding kernel + utilities + images in WSL (this takes a few minutes)..."
        wsl.exe -d Ubuntu-24.04 -u root -- timeout 480 bash -c "cd /root/neutrino; bash /mnt/d/Projects/Code/NeutrinoOS/build/wsl-rebuild.sh 2>&1 | tail -2; bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-apps-build.sh 2>&1 | tail -2; bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-deploy.sh 2>&1 | tail -2; bash /mnt/d/Projects/Code/NeutrinoOS/build/gui-image.sh 2>&1 | tail -6"
    } else {
        Write-Host "GUI image missing - generating it with build/gui-image.sh..."
        wsl.exe -d Ubuntu-24.04 -u root -- timeout 60 bash /mnt/d/Projects/Code/NeutrinoOS/build/gui-image.sh
    }
    if (-not (Test-Path $img)) { throw "GUI image was not produced: $img" }
} else {
    Write-Host "Using existing $img (pass -Rebuild to refresh it)"
}

# --- 2. Recreate the VM ------------------------------------------------------
# Settings mirror scripts/test-vbox.ps1 (known-good):
#   - 2 vCPUs are REQUIRED (VirtualBox EFI firmware #GPs with 1 vCPU)
#   - --hpet on (kernel uses the ACPI HPET table for boot timing/TSC)
$ErrorActionPreference = "Continue"
& $vb controlvm $name poweroff 2>&1 | Out-Null
Start-Sleep -Seconds 2
& $vb unregistervm $name --delete 2>&1 | Out-Null
$ErrorActionPreference = "Stop"
Remove-Item -Force $serial -ErrorAction SilentlyContinue
Remove-Item -Force $vdi -ErrorAction SilentlyContinue

Write-Host "Converting GUI image to VDI..."
& $vb convertfromraw $img $vdi --format VDI | Out-Null
& $vb internalcommands sethduuid $vdi $vdiUuid | Out-Null

Write-Host "Creating VM '$name'..."
& $vb createvm --name $name --ostype Other_64 --register | Out-Null
if (-not $NoNet) {
    # NAT with port forwards for the Phase 6 network tests (sshd/webhost).
    # nictype virtio: the kernel's only NIC driver is virtio-net.
    & $vb modifyvm $name --memory 2048 --cpus 2 --firmware efi --ioapic on --nic1 nat --nictype1 virtio --hpet on | Out-Null
    # Port forwards and NIC tracing are conveniences for the Phase 6 tests:
    # if a host port is excluded/busy the VM still boots, so do not abort.
    $ErrorActionPreference = "Continue"
    & $vb modifyvm $name --natpf1 "ssh,tcp,,2222,,22" 2>&1 | Out-Null
    & $vb modifyvm $name --natpf1 "http,tcp,,8080,,80" 2>&1 | Out-Null
    & $vb modifyvm $name --natpf1 "https,tcp,,8444,,443" 2>&1 | Out-Null
    & $vb modifyvm $name --nictrace1 on --nictracefile1 (Join-Path $base "vbox-nic.pcap") 2>&1 | Out-Null
    $ErrorActionPreference = "Stop"
} else {
    & $vb modifyvm $name --memory 2048 --cpus 2 --firmware efi --ioapic on --nic1 none --hpet on | Out-Null
}
& $vb storagectl $name --name SATA --add sata --controller IntelAhci | Out-Null
& $vb storageattach $name --storagectl SATA --port 0 --device 0 --type hdd --medium $vdi | Out-Null
if ($SerialPipe -ne "") {
    # Server-pipe UART: the host connects with a named-pipe client and can
    # both capture the transcript and type into the guest's line discipline.
    # NOTE: the legacy --uartmode1 server form is used on purpose - the
    # newer --uart-mode1 "server pipe ..." spelling fails to open the host
    # device on VirtualBox 7.1 (VERR_INVALID_HANDLE).
    & $vb modifyvm $name --uart1 0x3F8 4 --uartmode1 server ("\\.\pipe\" + $SerialPipe) | Out-Null
} else {
    & $vb modifyvm $name --uart1 0x3F8 4 --uartmode1 file $serial | Out-Null
}

if ($NoStart) {
    Write-Host "VM '$name' created (not started). Start it from the VirtualBox GUI or with:"
    Write-Host "  VBoxManage startvm $name"
    exit 0
}

# --- 3. Start it and wait for the shell --------------------------------------
Write-Host "Starting '$name' (window)..."
& $vb startvm $name | Out-Null

$deadline = (Get-Date).AddSeconds(20)
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $serial) {
        $t = Get-Content $serial -Raw -ErrorAction SilentlyContinue
        if ($t -match "neutrinoos>") { $ready = $true; break }
    }
}
if ($ready) {
    Write-Host "[OK] Shell ready. The boot log is mirrored on the VM screen -"
    Write-Host "     type directly in the VM window (PS/2 keyboard is active)."
} else {
    Write-Host "[WARN] Shell prompt not seen within 20s. Check the VM window or:"
    Write-Host "       $serial"
}
Write-Host "Serial transcript: $serial"
