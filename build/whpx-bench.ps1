# QEMU WHPX vs TCG benchmark for NeutrinoOS (headless).
# Boots a prepared SSH-ready image (autostart sshd), measures boot milestones,
# then times scp upload/download of a 2 MiB file + ssh exec.
param(
  [string]$Accel = "tcg",
  [int]$Port = 3322,
  [string]$Label = "",
  [string]$Vga = "std",
  [string]$Disk = "virtio"
)
$ErrorActionPreference = "Continue"
if (-not $Label) { $Label = $Accel }
$b = "D:\Projects\Code\NeutrinoOS\build"
$q = "C:\Program Files\qemu\qemu-system-x86_64.exe"

$vars = Join-Path $b "whpx-vars-$Label.fd"
$img  = Join-Path $b "whpx-run-$Label.img"
$ser  = Join-Path $b "whpx-serial-$Label.log"
$err  = Join-Path $b "whpx-stderr-$Label.log"
Copy-Item "C:\Program Files\qemu\share\edk2-i386-vars.fd" $vars -Force
Copy-Item (Join-Path $b "whpx-test.img") $img -Force
Remove-Item $ser, $err -ErrorAction SilentlyContinue

$key = Join-Path $b "p6key"
icacls $key /inheritance:r /grant:r "$($env:USERNAME):(R)" | Out-Null

$file = Join-Path $b "whpx-file.bin"
if (-not (Test-Path $file)) {
  [IO.File]::WriteAllBytes($file, [byte[]]::new(2097152))
}

$blkArgs = if ($Disk -eq "ahci") {
  @("-drive","id=bootdisk,if=none,format=raw,file=$img",
    "-device","ide-hd,drive=bootdisk,bus=ide.0")
} else {
  @("-drive","id=bootdisk,if=none,format=raw,file=$img",
    "-device","virtio-blk-pci,drive=bootdisk,disable-legacy=on")
}

$qargs = @(
  "-machine","q35","-m","2G","-smp","1","-accel",$Accel,
  "-drive",'if=pflash,format=raw,readonly=on,file="C:\Program Files\qemu\share\edk2-x86_64-code.fd"',
  "-drive","if=pflash,format=raw,file=$vars"
) + $blkArgs + @(
  "-netdev","user,id=n0,hostfwd=tcp::$Port-:22",
  "-device","virtio-net-pci,netdev=n0",
  "-display","none","-vga",$Vga,"-serial","file:$ser","-no-reboot","-no-shutdown"
)

$t0 = Get-Date
$p = Start-Process -FilePath $q -ArgumentList $qargs -PassThru -WindowStyle Hidden -RedirectStandardError $err
try {
  $bootMs = $null; $sshdMs = $null
  for ($i = 0; $i -lt 720; $i++) {
    Start-Sleep -Milliseconds 250
    if ($p.HasExited) { break }
    if (Test-Path $ser) {
      $txt = Get-Content $ser -Raw -ErrorAction SilentlyContinue
      if (-not $bootMs -and $txt -match "Type 'help'") { $bootMs = [int]((Get-Date) - $t0).TotalMilliseconds }
      if (-not $sshdMs -and $txt -match '\[sshd\] listening') { $sshdMs = [int]((Get-Date) - $t0).TotalMilliseconds }
      if ($bootMs -and $sshdMs) { break }
    }
  }
  Start-Sleep -Seconds 2

  function Invoke-Timed([string]$exe, [string[]]$a, [int]$timeoutMs) {
    $sp = Start-Process -FilePath $exe -ArgumentList $a -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $b "tool-$Label.out") -RedirectStandardError (Join-Path $b "tool-$Label.err")
    if (-not $sp.WaitForExit($timeoutMs)) { Stop-Process -Id $sp.Id -Force; return @{ rc = -999; ms = $timeoutMs } }
    return @{ rc = $sp.ExitCode; ms = [int]((Get-Date) - $t0).TotalMilliseconds }
  }

  $sopts = @("-i",$key,"-P","$Port","-o","StrictHostKeyChecking=no","-o","UserKnownHostsFile=NUL","-o","LogLevel=ERROR","-o","ConnectTimeout=15")

  $ex = @("-i",$key,"-p","$Port","-o","StrictHostKeyChecking=no","-o","UserKnownHostsFile=NUL","-o","LogLevel=ERROR","-o","ConnectTimeout=15","user@127.0.0.1","uname")
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $r = Invoke-Timed "scp.exe" ($sopts + @($file,"user@127.0.0.1:/home/user/up.bin")) 300000
  $upMs = $sw.ElapsedMilliseconds; $upRc = $r.rc
  $sw.Restart()
  $r = Invoke-Timed "scp.exe" ($sopts + @("user@127.0.0.1:/home/user/up.bin",(Join-Path $b "whpx-down-$Label.bin"))) 300000
  $downMs = $sw.ElapsedMilliseconds; $downRc = $r.rc
  $sw.Restart()
  $r = Invoke-Timed "ssh.exe" $ex 120000
  $execMs = $sw.ElapsedMilliseconds; $execRc = $r.rc
  $downSize = if (Test-Path (Join-Path $b "whpx-down-$Label.bin")) { (Get-Item (Join-Path $b "whpx-down-$Label.bin")).Length } else { -1 }

  $bootLine = ""
  if (Test-Path $ser) { $bootLine = (Select-String -Path $ser -Pattern '\[Boot\] t=\d+ms Boot complete' -AllMatches | ForEach-Object { $_.Matches[0].Value } | Select-Object -Last 1) }

  "=== $Label ($Accel) ==="
  "boot-to-prompt:   $bootMs ms"
  "sshd-listening:   $sshdMs ms (wall)"
  "guest-$bootLine"
  "scp up   2MiB:    rc=$upRc  $upMs ms"
  "scp down 2MiB:    rc=$downRc  $downMs ms  size=$downSize"
  "ssh exec uname:   rc=$execRc  $execMs ms"
  if (Test-Path $err) { $e = Get-Content $err -Raw; if ($e) { "stderr: $($e.Substring(0, [Math]::Min(400, $e.Length)))" } }
}
finally {
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  Start-Sleep -Milliseconds 500
}
