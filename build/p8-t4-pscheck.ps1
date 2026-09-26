# Syntax-checks PowerShell scripts (parse only, no execution).
param([string[]]$Files = @("scripts\start-repo-server.ps1"))

$bad = 0
foreach ($f in $Files) {
    $errs = $null
    [System.Management.Automation.PSParser]::Tokenize((Get-Content -Raw $f), [ref]$errs) | Out-Null
    if ($errs.Count -eq 0) {
        Write-Host "PARSE-OK: $f"
    } else {
        $bad++
        Write-Host "PARSE-FAIL: $f"
        $errs | ForEach-Object { Write-Host ("  " + $_.Message) }
    }
}
exit $bad
