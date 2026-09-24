# VBox HTTPS diagnostic: boot the serve image, wait for [web] listening,
# then run verbose curls against 8080 and 8444 and a raw 8444 connect.
$ErrorActionPreference = "Continue"
$vb = "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
$root = "d:\Projects\Code\NeutrinoOS"
$base = Join-Path $root "build"
$img = Join-Path $base "vbox-p7-serve.img"
$vdi = Join-Path $base "vbox-p7-serve.vdi"
$serial = Join-Path $base "vbox-p7-diag-serial.log"
$name = "NeutrinoOSCli"

& $vb controlvm $name poweroff 2>&1 | Out-Null
Start-Sleep -Seconds 2
& $vb unregistervm $name --delete 2>&1 | Out-Null
Remove-Item -Force $serial, $vdi -ErrorAction SilentlyContinue

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
& $vb startvm $name --type headless 2>&1 | Out-Null

Write-Host "--- waiting for web service ---"
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep -Seconds 1
    $t = Get-Content $serial -Raw -ErrorAction SilentlyContinue
    if ($t -match "\[web\] listening") { Write-Host "web up at ${i}s"; break }
}
Start-Sleep -Seconds 4

Write-Host "`n--- raw TCP connect 8444 ---"
try {
    $tc = New-Object System.Net.Sockets.TcpClient
    $ar = $tc.BeginConnect("127.0.0.1", 8444, $null, $null)
    $ok = $ar.AsyncWaitHandle.WaitOne(5000)
    Write-Host "connect=$ok"
    if ($ok) {
        $st = $tc.GetStream()
        $hello = [byte[]](0x16, 0x03, 0x01, 0x00, 0x05, 0x01, 0x00, 0x00, 0x01, 0x00)  # TLS ClientHello (truncated)
        # send only a TCP SYN-connect; then wait 3s for any server bytes
        $st.ReadTimeout = 3000
        try {
            $rb = New-Object byte[] 512
            $n = $st.Read($rb, 0, 512)
            Write-Host "server sent $n bytes (no ClientHello from us - expect 0/timeout)"
        } catch { Write-Host "read: $($_.Exception.Message)" }
        $tc.Close()
    }
} catch { Write-Host "raw connect error: $_" }

Write-Host "`n--- curl HTTP 8080 ---"
& curl.exe -s -o NUL -w "http_code=%{http_code} time=%{time_total}`n" --max-time 15 http://127.0.0.1:8080/health

Write-Host "`n--- curl HTTPS 8444 verbose (max 90s) ---"
& curl.exe -k -v --max-time 90 https://127.0.0.1:8444/health 2>&1 | Select-Object -First 40

Write-Host "`n--- serial tail ---"
Get-Content $serial -Tail 6 -ErrorAction SilentlyContinue
