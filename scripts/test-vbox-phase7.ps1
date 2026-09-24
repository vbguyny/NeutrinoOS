# Phase 7 VirtualBox acceptance (file-serial design).
#
# Boots a TEST VARIANT of the release image in VirtualBox with
# /etc/boot.params baked in (net.ip=dhcp, sshd.webhost autostart) so the
# verification needs no interactive console session: everything is
# asserted from the serial transcript plus host-side network probes.
# (VirtualBox UART server-pipe input proved unreliable - writes to the
# pipe can block indefinitely - hence file-serial only.)
#
# Verifies:
#   - release banner v1.0.0, boot-tests-skipped, shell prompt, no halt
#   - [SEC] security self-test (ring-3 isolation + W^X) 4/4
#   - /etc/neutrinoos-release content (artifact check via mtools)
#   - dhcp + sshd + webhost autostart on the default NIC (virtio-net)
#   - host probes: SSH banner :2222, HTTP 200 + HTTPS 200 (8080/8444)
#   - OVA export of the clean release VM via scripts/build-ova.ps1
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\test-vbox-phase7.ps1
param(
    [int]$BootTimeoutSec = 240,
    [switch]$SkipOva
)

# Windows PowerShell 5.1: VBoxManage writes harmless notes to stderr.
$ErrorActionPreference = "Continue"
$vb = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
$root = "d:\Projects\Code\NeutrinoOS"
$base = Join-Path $root "build"
$img = Join-Path $base "vbox-p7-serve.img"
$cleanImg = Join-Path $base "neutrinoos-gui.img"
$vdi = Join-Path $base "vbox-p7-serve.vdi"
$serial = Join-Path $base "vbox-p7-serial.log"
$name = "NeutrinoOSCli"
$outLog = Join-Path $base "vbox-p7-acceptance.log"

if (-not (Test-Path $img)) { throw "serve image missing: $img (run build/p7-vbox-serve-image.sh in WSL first)" }

$script:failures = 0
$script:transcript = New-Object System.Text.StringBuilder
function Log([string]$line) {
    Write-Host $line
    [void]$script:transcript.AppendLine($line)
}
function Assert([string]$what, [bool]$ok, [string]$detail = "") {
    if ($ok) { Log ("[PASS] {0} {1}" -f $what, $detail) }
    else     { Log ("[FAIL] {0} {1}" -f $what, $detail); $script:failures++ }
}
function Read-Serial([string]$path) {
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
function Stop-Vm {
    & $vb controlvm $name poweroff 2>&1 | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        $running = (& $vb list runningvms) | Select-String -SimpleMatch "`"$name`""
        if (-not $running) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 1
}

Log "=== Phase 7 VirtualBox acceptance (file serial) ($(Get-Date -Format s)) ==="

Stop-Vm
$ErrorActionPreference = "Continue"
& $vb unregistervm $name --delete 2>&1 | Out-Null
Remove-Item -Force $serial, $vdi -ErrorAction SilentlyContinue

Log "--- creating VM (serve image + NAT + serial file) ---"
& $vb convertfromraw $img $vdi --format VDI 2>&1 | Out-Null
& $vb internalcommands sethduuid $vdi "7c1e2d3f-4a5b-6c7d-8e9f-0a1b2c3d4e5f" 2>&1 | Out-Null
& $vb createvm --name $name --ostype Other_64 --register 2>&1 | Out-Null
& $vb modifyvm $name --memory 2048 --cpus 2 --firmware efi --ioapic on --hpet on --nic1 nat --nictype1 virtio 2>&1 | Out-Null
& $vb modifyvm $name --natpf1 "ssh,tcp,,2222,,22" 2>&1 | Out-Null
& $vb modifyvm $name --natpf1 "http,tcp,,8080,,80" 2>&1 | Out-Null
& $vb modifyvm $name --natpf1 "https,tcp,,8444,,443" 2>&1 | Out-Null
& $vb storagectl $name --name SATA --add sata --controller IntelAhci 2>&1 | Out-Null
& $vb storageattach $name --storagectl SATA --port 0 --device 0 --type hdd --medium $vdi 2>&1 | Out-Null
& $vb modifyvm $name --uart1 0x3F8 4 --uartmode1 file $serial 2>&1 | Out-Null

Log "--- booting (headless) ---"
& $vb startvm $name --type headless 2>&1 | Out-Null

$start = Get-Date
$deadline = $start.AddSeconds($BootTimeoutSec)
$lastLog = Get-Date
$prompt = $false
$text = ""
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700
    $text = Read-Serial $serial
    if ($text -match "neutrinoos> ") { $prompt = $true; break }
    if ($text -match "SYSTEM HALTED|X64 Exception Type") { break }
    if (((Get-Date) - $lastLog).TotalSeconds -ge 20) {
        $lastLog = Get-Date
        Log ("  [boot] {0:N0}s elapsed, {1:N0} chars" -f ((Get-Date) - $start).TotalSeconds, $text.Length)
    }
}
$bootSecs = ((Get-Date) - $start).TotalSeconds
Log ("--- boot finished: prompt={0} after {1:N0}s ({2:N0} chars) ---" -f $prompt, $bootSecs, $text.Length)

Assert "banner NeutrinoOS v1.0.0" ($text -match "NeutrinoOS v1\.0\.0 \(x86-64 UEFI\)")
Assert "release image skips boot tests" ($text -match "Boot tests skipped")
Assert "security self-test 4 pass" ($text -match "\[SEC\] result: 4 pass, 0 fail")
Assert "ring-3 cannot read kernel identity" ($text -match "user cannot read kernel identity memory")
Assert "ring-3 cannot read physmap" ($text -match "user cannot read the kernel physmap")
Assert "kernel image executable (W^X)" ($text -match "kernel image is executable")
Assert "ordinary RAM is NX (W^X)" ($text -match "ordinary RAM is NX")
Assert "shell prompt reached" $prompt
Assert "no halt / exception" (-not ($text -match "SYSTEM HALTED|X64 Exception Type"))

Log "--- artifact identity ---"
$rel = (& wsl.exe -d Ubuntu-24.04 -u root -- bash -c "mtype -i /mnt/d/Projects/Code/NeutrinoOS/build/vbox-p7-serve.img ::/etc/neutrinoos-release 2>/dev/null") 2>$null
$relText = ($rel | Out-String)
Assert "/etc/neutrinoos-release = 1.0.0" ($relText -match 'VERSION="1\.0\.0"') ($relText -split "`n" | Select-Object -First 1)

Log "--- services (autostart via /etc/boot.params) ---"
$svcDeadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $svcDeadline) {
    $text = Read-Serial $serial
    if (($text -match "\[sshd\] listening") -and ($text -match "\[web\] listening")) { break }
    Start-Sleep -Seconds 2
}
Assert "dhcp configured eth0" ($text -match "10\.0\.2\.15|eth0 configured|\[dhcp\]") ""
Assert "sshd listening" ($text -match "\[sshd\] listening")
Assert "webhost listening" ($text -match "\[web\] listening")

Log "--- host network probes ---"
Start-Sleep -Seconds 6
$sshBanner = ""
try {
    $tc = New-Object System.Net.Sockets.TcpClient("127.0.0.1", 2222)
    $tc.ReceiveTimeout = 8000
    $st = $tc.GetStream()
    $rb = New-Object byte[] 128
    $n = $st.Read($rb, 0, 128)
    $sshBanner = [System.Text.Encoding]::ASCII.GetString($rb, 0, $n)
    $tc.Close()
} catch { $sshBanner = "ERROR: $_" }
Assert "SSH banner over NAT :2222" ($sshBanner -match "SSH-2\.0-NeutrinoOS_1\.0") $sshBanner.Trim()

$http = (& curl.exe -s -o NUL -w "%{http_code}" --max-time 15 http://127.0.0.1:8080/health) 2>$null
Assert "HTTP /health -> 200" ($http -eq "200") "code=$http"
$httpBody = (& curl.exe -s --max-time 15 http://127.0.0.1:8080/health) 2>$null
Assert "HTTP /health body" ($httpBody -match '"status":"ok"') $httpBody

# The first TLS connection can be slow on NEM hosts (certificate exists but
# the handshake cold-starts the crypto paths); retry a few times.
# NOTE: Windows-native curl/PowerShell use schannel, whose TLS 1.3
# ClientHello the NeutrinoOS TLS server rejects (no matching
# signature_algorithms; serial shows 'hello rejected ... sig=0').
# OpenSSL curl from WSL is the validated client (as in the Phase 6 suite).
$https = ""
for ($k = 1; $k -le 3; $k++) {
    # Probe via a script FILE: inline bash through wsl.exe argument
    # passing mangles '$(...)' and friends (observed as instant 000).
    $https = (& wsl.exe -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/p7-vbox-https-code.sh) 2>$null
    Write-Host ("  [debug] https attempt {0}: raw='{1}'" -f $k, $https)
    if ("$https".Trim() -eq "200") { $https = "200"; break }
    Start-Sleep -Seconds 8
}
Assert "HTTPS (TLS 1.3) /health -> 200 (OpenSSL client)" ("$https".Trim() -eq "200") "code=$https"

# ---- OVA (clean release VM, no autostart fixture) -----------------------------
if (-not $SkipOva) {
    Log "--- OVA packaging (clean release image) ---"
    Stop-Vm
    & (Join-Path $root "scripts\gui-vm.ps1") -NoStart | Out-Host
    & (Join-Path $root "scripts\build-ova.ps1") -VmName $name | Out-Host
    $ova = Join-Path $root "dist\neutrinoos-1.0.0.ova"
    $ovaOk = (Test-Path $ova) -and ((Get-Item $ova).Length -gt 1MB)
    Assert "OVA exported (dist\neutrinoos-1.0.0.ova)" $ovaOk ($(if (Test-Path $ova) { "{0:N1} MB" -f ((Get-Item $ova).Length / 1MB) } else { "missing" }))
}

# ---- summary -------------------------------------------------------------------
Log ""
if ($script:failures -eq 0) { Log "=== phase7 VBox summary: ALL-PASS ===" }
else { Log ("=== phase7 VBox summary: {0} FAILURE(S) ===" -f $script:failures) }
Log ("boot wall time: {0:N0}s" -f $bootSecs)

Set-Content -Path $outLog -Value $script:transcript.ToString()
Set-Content -Path (Join-Path $base "vbox-p7-full-serial.log") -Value $text
Log "Acceptance log: $outLog"

# Leave the clean release VM running in a window for hands-on use.
try {
    $running = (& $vb list runningvms) | Select-String -SimpleMatch "`"$name`""
    if (-not $running) { & $vb startvm $name | Out-Null }
    Log "[vbox] VM '$name' left running (window). Type commands in the VM console."
} catch { Log "[vbox] could not start window: $_" }

exit $script:failures
