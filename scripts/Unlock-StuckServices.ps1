#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Safely releases Focus Lock service protection without using sc.exe stop (can BSOD if a
    strict-mode critical flag is still set on an older build).

.EXAMPLE
    .\scripts\Unlock-StuckServices.ps1
#>
$ErrorActionPreference = 'Continue'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sddl = 'D:(A;;GA;;;SY)(A;;GA;;;BA)'

Write-Host @'

WARNING: Do NOT run "sc stop FocusLockService" or "sc stop FocusLockWatchdog" manually.
On strict-mode / older builds that can blue-screen Windows if the process is still critical.
This script uses unlock + sdset only, then disables auto-start so you can reboot safely.

'@ -ForegroundColor Yellow

Remove-Item "$env:ProgramData\FocusLock\session.json" -Force -ErrorAction SilentlyContinue

$unlockExe = Join-Path $root 'src\FocusLock.Service\bin\Release\net9.0-windows10.0.17763.0\FocusLock.Service.exe'
if (-not (Test-Path $unlockExe)) {
    Write-Host 'Building FocusLock.Service...' -ForegroundColor Cyan
    dotnet build (Join-Path $root 'src\FocusLock.Service\FocusLock.Service.csproj') -c Release | Out-Null
}

if (Test-Path $unlockExe) {
    Write-Host "Running unlock: $unlockExe" -ForegroundColor Cyan
    & $unlockExe --unlock-for-setup
}

Write-Host 'Resetting service DACLs (safe)...' -ForegroundColor Cyan
cmd.exe /c "sc sdset FocusLockService `"$sddl`""
cmd.exe /c "sc sdset FocusLockWatchdog `"$sddl`""

$main = Get-Service FocusLockService -ErrorAction SilentlyContinue
$watch = Get-Service FocusLockWatchdog -ErrorAction SilentlyContinue

if ($main.Status -eq 'Running' -or $watch.Status -eq 'Running') {
    Write-Host @'

Services are still running. Disabling them until next reboot (no sc stop)...

'@ -ForegroundColor Cyan
    Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\FocusLockService' -Name Start -Value 4 -Type DWord -ErrorAction SilentlyContinue
    Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\FocusLockWatchdog' -Name Start -Value 4 -Type DWord -ErrorAction SilentlyContinue
    Write-Host 'REBOOT NOW. After reboot, services stay stopped — replace files or uninstall, then reinstall.' -ForegroundColor Green
}
else {
    Write-Host 'Both services are already stopped. You can replace files or run the MSI.' -ForegroundColor Green
}

Get-Service FocusLockWatchdog, FocusLockService -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType
