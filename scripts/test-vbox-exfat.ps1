param(
    [int]$Port = 49910,
    [int]$TimeoutSec = 240
)
# NeutrinoOS Phase 10 VirtualBox acceptance: boot the deployed image in
# VirtualBox (EFI, IntelAhci SATA) with an exFAT data disk (hdb) and a
# blank disk (hdc), drive the guest over the serial TCP console, run the
# exFAT flow (mount / I-O / mkdir / mv / df -T / fsck / mkexfat /
# exfatlabel), then verify the disks from Linux (FUSE + exfatprogs).
#
# Prereqs: WSL build flow complete (build/p9-build.sh + p5-apps-build.sh).
# This script refreshes + prepares the images itself via WSL.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\test-vbox-exfat.ps1
$ErrorActionPreference = "Stop"
$vb = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
$name = "NeutrinoOSP10"
$root = "d:\Projects\Code\NeutrinoOS"
$work = Join-Path $root "build\vbox-p10"
$bootImg = Join-Path $work "boot.img"
$dataImg = Join-Path $work "data.img"
$blankImg = Join-Path $work "blank.img"
$transcriptPath = Join-Path $work "transcript.log"

$pass = 0; $fail = 0
function Check([bool]$ok, [string]$label) {
    if ($ok) { Write-Host "  [PASS] $label" -ForegroundColor Green; $script:pass++ }
    else { Write-Host "  [FAIL] $label" -ForegroundColor Red; $script:fail++ }
}

Write-Host "=== NeutrinoOS Phase 10 VirtualBox acceptance ===" -ForegroundColor Cyan

# ---------------------------------------------------------------- prep ---
Write-Host "`n--- preparing images (WSL) ---"
$wslOut = & wsl.exe -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/p10-vbox-prep.sh 2>&1
$wslOut | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
if (-not ($wslOut -match "VBOX-PREP: OK")) {
    Write-Host "image prep failed" -ForegroundColor Red
    exit 1
}

# ------------------------------------------------------------ vm setup ---
Write-Host "`n--- creating VM (EFI, IntelAhci, serial tcp :$Port) ---"
# VBoxManage writes progress/notice text to stderr; keep Continue through
# the whole setup so it never aborts the script (exit codes are checked
# where they matter).
$ErrorActionPreference = "Continue"
& $vb controlvm $name poweroff 2>&1 | Out-Null
Start-Sleep -Seconds 2
& $vb unregistervm $name --delete 2>&1 | Out-Null

$vdiBoot = Join-Path $work "boot.vdi"
$vdiData = Join-Path $work "data.vdi"
$vdiBlank = Join-Path $work "blank.vdi"
Remove-Item -Force $vdiBoot, $vdiData, $vdiBlank -ErrorAction SilentlyContinue

& $vb convertfromraw $bootImg $vdiBoot --format VDI | Out-Null
& $vb convertfromraw $dataImg $vdiData --format VDI | Out-Null
& $vb convertfromraw $blankImg $vdiBlank --format VDI | Out-Null
& $vb internalcommands sethduuid $vdiBoot "9a205361-0001-4a10-9f01-000000000001" | Out-Null
& $vb internalcommands sethduuid $vdiData "9a205361-0002-4a10-9f01-000000000002" | Out-Null
& $vb internalcommands sethduuid $vdiBlank "9a205361-0003-4a10-9f01-000000000003" | Out-Null

& $vb createvm --name $name --ostype Other_64 --register | Out-Null
& $vb modifyvm $name --memory 2048 --cpus 2 --firmware efi --ioapic on --nic1 none --hpet on | Out-Null
& $vb storagectl $name --name SATA --add sata --controller IntelAhci | Out-Null
& $vb storageattach $name --storagectl SATA --port 0 --device 0 --type hdd --medium $vdiBoot | Out-Null
& $vb storageattach $name --storagectl SATA --port 1 --device 0 --type hdd --medium $vdiData | Out-Null
& $vb storageattach $name --storagectl SATA --port 2 --device 0 --type hdd --medium $vdiBlank | Out-Null

# Serial console: VirtualBox connects OUT to a listener we own (tcpclient
# mode) - the tcpserver mode fails with VERR_ACCESS_DENIED on this host.
# Windows/Hyper-V reserve excluded port ranges (e.g. 49732-49931), so walk
# upward until we find a port that we can actually bind.
$listener = $null
for ($cand = $Port; $cand -lt ($Port + 400); $cand++) {
    try {
        $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $cand)
        $l.Start()
        $listener = $l
        $Port = $cand
        break
    } catch { }
}
if ($null -eq $listener) {
    Write-Host "could not bind a serial-console port near $Port" -ForegroundColor Red
    exit 1
}
if ($Port -ne 49910) {
    Write-Host "  (serial port $Port - 49910 falls inside an excluded range)"
}

& $vb modifyvm $name --uart1 0x3F8 4 | Out-Null
& $vb modifyvm $name --uart-mode1 tcpclient "127.0.0.1:$Port" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  (falling back to legacy uartmode syntax)"
    & $vb modifyvm $name --uartmode1 tcpclient "127.0.0.1:$Port" | Out-Null
}

# ----------------------------------------------------------- run + drive ---
$ErrorActionPreference = "Stop"
Write-Host "`n--- starting headless ---"
& $vb startvm $name --type headless | Out-Null

Write-Host "waiting for the serial console to connect on port $Port ..."
$client = $null
for ($i = 0; $i -lt 120; $i++) {
    if ($listener.Pending()) { $client = $listener.AcceptTcpClient(); break }
    Start-Sleep -Milliseconds 500
}
if ($null -eq $client) {
    Write-Host "serial console never connected" -ForegroundColor Red
    $listener.Stop()
    & $vb controlvm $name poweroff 2>&1 | Out-Null
    exit 1
}
Write-Host "  serial console connected"
$stream = $client.GetStream()
$sb = New-Object System.Text.StringBuilder
$script:scanPos = 0

function Pump([int]$ms) {
    $deadline = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $deadline) {
        if ($stream.DataAvailable) {
            $buf = New-Object byte[] 4096
            $n = $stream.Read($buf, 0, $buf.Length)
            if ($n -gt 0) { [void]$sb.Append([System.Text.Encoding]::ASCII.GetString($buf, 0, $n)) }
        } else { Start-Sleep -Milliseconds 100 }
    }
}

function WaitFor([string]$needle, [int]$secs = 30) {
    $deadline = (Get-Date).AddSeconds($secs)
    while ((Get-Date) -lt $deadline) {
        Pump 300
        $idx = $sb.ToString().IndexOf($needle, $script:scanPos)
        if ($idx -ge 0) { $script:scanPos = $idx + $needle.Length; return $true }
    }
    return $false
}

function SendLine([string]$line) {
    Write-Host "  > $line"
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($line + "`r")
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}

Write-Host "`nwaiting for shell prompt..."
$bootOk = WaitFor "neutrinoos> " $TimeoutSec
if (-not $bootOk) {
    Write-Host "shell prompt not reached" -ForegroundColor Red
    Set-Content -Path $transcriptPath -Value $sb.ToString()
    & $vb controlvm $name poweroff 2>&1 | Out-Null
    exit 1
}
Write-Host "  shell ready"

# ------------------------------------------------------------- scenario ---
Check $bootOk "boot: shell ready"

SendLine "mount -t exfat /dev/hdb /mnt/test"
Check (WaitFor 'mounted hdb (exFAT' 30) "mount: exFAT data disk mounted"

SendLine "ls /mnt/test"
Check (WaitFor "linux.txt" 30) "ls: Linux-written file listed"

SendLine "cat /mnt/test/linux.txt"
Check (WaitFor "read by NeutrinoOS" 30) "read: Linux-written file content"

SendLine "echo hello-vbox-guest > /mnt/test/new.txt"
Pump 2000

SendLine "mkdir /mnt/test/dir1"
Pump 2000

SendLine "mv /mnt/test/linux.txt /mnt/test/renamed.txt"
Pump 2000

SendLine "df -T"
Check (WaitFor "P10VBOX" 30) "df -T: exFAT volume reported"

SendLine "umount /mnt/test"
Check (WaitFor "unmounted /mnt/test" 30) "umount: clean release"

SendLine "fsck.exfat /dev/hdb"
Check (WaitFor "fsck.exfat: clean" 60) "fsck.exfat: hdb clean"

SendLine "mkexfat -L VBOXMADE /dev/hdc"
Check (WaitFor "formatted hdc (exFAT" 90) "mkexfat: blank disk formatted in-guest"

SendLine "fsck.exfat /dev/hdc"
Check (WaitFor "fsck.exfat: clean" 60) "fsck.exfat: hdc clean"

SendLine "exfatlabel /dev/hdc"
Check (WaitFor "VBOXMADE" 30) "exfatlabel: label readback"

SendLine "exit"
Pump 2000

Set-Content -Path $transcriptPath -Value $sb.ToString()
Write-Host "`n  transcript saved to $transcriptPath"

# ------------------------------------------------------------- teardown ---
# Stop the VM, then extract the actual disk contents: the guest wrote to
# data.vdi / blank.vdi, NOT to the raw images they were converted from.
$ErrorActionPreference = "Continue"
& $vb controlvm $name poweroff 2>&1 | Out-Null
$stream.Close(); $client.Close(); $listener.Stop()

$stopped = $false
for ($i = 0; $i -lt 60; $i++) {
    $state = & $vb showvminfo $name --machinereadable 2>$null | Select-String '^VMState=' | Select-Object -First 1
    if ($state -match 'poweroff|aborted|saved') { $stopped = $true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $stopped) {
    Write-Host "  (VM did not power down - forcing emergencystop)"
    & $vb startvm $name --type emergencystop 2>&1 | Out-Null
    Start-Sleep -Seconds 3
}

$dataAfter = Join-Path $work "data-after.img"
$blankAfter = Join-Path $work "blank-after.img"
Remove-Item -Force $dataAfter, $blankAfter -ErrorAction SilentlyContinue
& $vb clonemedium $vdiData $dataAfter --format RAW | Out-Null
& $vb clonemedium $vdiBlank $blankAfter --format RAW | Out-Null
$ErrorActionPreference = "Stop"

# --------------------------------------------------- Linux-side verify ---
Write-Host "`n--- verifying disks from Linux (FUSE + exfatprogs) ---"
$wslOut = & wsl.exe -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/p10-vbox-verify.sh /mnt/d/Projects/Code/NeutrinoOS/build/vbox-p10/data-after.img /mnt/d/Projects/Code/NeutrinoOS/build/vbox-p10/blank-after.img 2>&1
$wslOut | ForEach-Object { Write-Host "  $_" }
foreach ($line in $wslOut) {
    if ($line -match "^PASS: ") { $pass++ }
    if ($line -match "^FAIL: ") { $fail++ }
}

Write-Host "`n=== VBox summary: $pass PASS / $fail FAIL ===" -ForegroundColor Cyan
if ($fail -eq 0) { Write-Host "VBOXP10: ALL-PASS" -ForegroundColor Green; exit 0 }
Write-Host "VBOXP10: HAS-FAILURES" -ForegroundColor Red
exit 1
