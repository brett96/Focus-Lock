#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Releases Focus Lock service protection so files can be replaced or the MSI can run.
    Blocked while a Strict mode session is active (same rule as uninstall).

.EXAMPLE
    .\scripts\Unlock-StuckServices.ps1
#>
$ErrorActionPreference = 'Continue'

$sessionFile = Join-Path $env:ProgramData 'FocusLock\session.json'
if (Test-Path $sessionFile) {
    try {
        $session = Get-Content $sessionFile -Raw | ConvertFrom-Json
        if ($session.Status -eq 'Active' -and $session.Mode -eq 'Strict') {
            Write-Host @'

Cannot unlock or uninstall: a Strict mode session is active.
Wait for the session deadline to pass, then run this script or uninstall again.

'@ -ForegroundColor Red
            exit 1
        }
    }
    catch { }
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sddl = 'D:(A;;GA;;;SY)(A;;GA;;;BA)'
$installDir = Join-Path ${env:ProgramFiles} 'FocusLock'

Write-Host @'

WARNING: Do NOT run "sc stop FocusLockService" or "sc stop FocusLockWatchdog".
Use this script only (sdset + unlock + registry disable if needed).

'@ -ForegroundColor Yellow

Remove-Item $sessionFile -Force -ErrorAction SilentlyContinue

Write-Host 'Resetting service DACLs...' -ForegroundColor Cyan
cmd.exe /c "sc sdset FocusLockService `"$sddl`""
cmd.exe /c "sc sdset FocusLockWatchdog `"$sddl`""

$unlockExe = Join-Path $installDir 'FocusLock.SetupHelper.exe'
if (-not (Test-Path $unlockExe)) {
    $unlockExe = Join-Path $root 'publish\setup-ca\FocusLock.SetupHelper.exe'
}
if (-not (Test-Path $unlockExe)) {
    $unlockExe = Join-Path $root 'src\FocusLock.SetupHelper\bin\Release\net9.0-windows10.0.17763.0\FocusLock.SetupHelper.exe'
    if (-not (Test-Path $unlockExe)) {
        Write-Host 'Building FocusLock.SetupHelper...' -ForegroundColor Cyan
        dotnet build (Join-Path $root 'src\FocusLock.SetupHelper\FocusLock.SetupHelper.csproj') -c Release | Out-Null
    }
}

if (Test-Path $unlockExe) {
    Write-Host "Running unlock: $unlockExe" -ForegroundColor Cyan
    & $unlockExe --full
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else {
    Write-Host 'WARNING: FocusLock.SetupHelper.exe not found; DACL reset only.' -ForegroundColor Yellow
}

$main = Get-Service FocusLockService -ErrorAction SilentlyContinue
$watch = Get-Service FocusLockWatchdog -ErrorAction SilentlyContinue

if ($main.Status -eq 'Running' -or $watch.Status -eq 'Running') {
    Write-Host 'Disabling services until reboot (no sc stop)...' -ForegroundColor Cyan
    Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\FocusLockService' -Name Start -Value 4 -Type DWord -ErrorAction SilentlyContinue
    Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\FocusLockWatchdog' -Name Start -Value 4 -Type DWord -ErrorAction SilentlyContinue
    Write-Host 'REBOOT, then run the MSI or uninstall from Settings.' -ForegroundColor Green
}
else {
    Write-Host 'Both services stopped. You can run the MSI or uninstall from Settings.' -ForegroundColor Green
}

Get-Service FocusLockWatchdog, FocusLockService -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType
