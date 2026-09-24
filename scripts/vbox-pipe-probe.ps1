# Connect to a NeutrinoOS VirtualBox serial pipe, optionally type commands,
# drain output for a while, and print what the guest said. Reusable for
# manual poking and for diagnosing slow (NEM) VirtualBox boots.
#
# Usage:
#   powershell -File scripts\vbox-pipe-probe.ps1                       # just listen 15s
#   powershell -File scripts\vbox-pipe-probe.ps1 -Send ""              # wake prompt (Enter)
#   powershell -File scripts\vbox-pipe-probe.ps1 -Send "dhcp","sshd" -WaitSec 60
param(
    [string]$PipeName = "NeutrinoP7",
    [string[]]$Send = @(),
    [int]$WaitSec = 15,
    [switch]$Raw
)

$ErrorActionPreference = "Continue"

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
$connected = $false
for ($i = 0; $i -lt 40; $i++) {
    try { $pipe.Connect(1000); $connected = $true; break } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $connected) { Write-Host "[probe] no pipe server \\\\.\\pipe\\$PipeName"; exit 2 }
Write-Host "[probe] connected; waiting ${WaitSec}s (sends: $($Send -join ' / '))"

$buf = New-Object byte[] 8192
$sb = New-Object System.Text.StringBuilder
$asyncOp = $null
$end = (Get-Date).AddSeconds($WaitSec)
$nextSend = 0
$sendAt = (Get-Date).AddSeconds(2)

while ((Get-Date) -lt $end) {
    if ($null -eq $asyncOp) {
        try { $asyncOp = $pipe.BeginRead($buf, 0, $buf.Length, $null, $null) } catch { break }
    }
    if ($asyncOp.AsyncWaitHandle.WaitOne(200)) {
        $n = 0
        try { $n = $pipe.EndRead($asyncOp) } catch { $n = 0 }
        $asyncOp = $null
        if ($n -gt 0) { [void]$sb.Append([System.Text.Encoding]::ASCII.GetString($buf, 0, $n)) }
    }
    if ($nextSend -lt $Send.Count -and (Get-Date) -ge $sendAt) {
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($Send[$nextSend] + "`n")
        $pipe.Write($bytes, 0, $bytes.Length); $pipe.Flush()
        Write-Host "[probe] sent: '$($Send[$nextSend])'"
        $nextSend++
        $sendAt = (Get-Date).AddSeconds(8)
    }
}

$text = $sb.ToString()
if ($Raw) {
    Write-Output $text
} else {
    Write-Host "==== transcript ($($text.Length) chars) ===="
    $lines = $text -split "`r?`n"
    $tail = $lines | Select-Object -Last 40
    $tail | ForEach-Object { Write-Host $_ }
}
$pipe.Dispose()
exit 0
