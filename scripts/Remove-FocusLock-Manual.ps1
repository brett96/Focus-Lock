#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes Focus Lock without msiexec (for 1.1.0 and other builds whose cached MSI unlock CA fails).

.DESCRIPTION
    Windows Installer uninstall always runs the MSI copy stored under C:\Windows\Installer\
    from install time. Focus Lock 1.1.0 runs FocusLock.Service.exe --unlock-for-setup in that
    cached package; that often fails (MSI error 1721), so Settings uninstall and msiexec /x
    never succeed. This script resets service DACLs, removes services and files, and clears
    installer registration so you can install a current build cleanly.

.PARAMETER KeepUserData
    Leave C:\ProgramData\FocusLock\ (session and screen-time config).

.PARAMETER TryMsiLast
    After manual cleanup, attempt msiexec /x once (usually unnecessary).

.EXAMPLE
    .\scripts\Remove-FocusLock-Manual.ps1
#>
param(
    [switch]$KeepUserData,
    [switch]$TryMsiLast
)

$ErrorActionPreference = 'Stop'

$productCode = '{8AF7A534-9A53-474F-9056-0A164D528037}'
$upgradeCode = '{A7B3C5D8-F2E1-4A6F-9B2E-C8D34F5E6A1B}'
$compressedProduct = '435A7FA835A9F4740965A061D4250873'
$compressedUpgrade = '8D5C3B7A1E2FF6A4B9E28C3DF4E5A6B1'
$sddl = 'D:(A;;GA;;;SY)(A;;GA;;;BA)'
$installDir = Join-Path ${env:ProgramFiles} 'FocusLock'
$sessionFile = Join-Path $env:ProgramData 'FocusLock\session.json'

function Test-ServiceRunning {
    param([string]$Name)
    $s = Get-Service $Name -ErrorAction SilentlyContinue
    return ($null -ne $s) -and ($s.Status -eq 'Running')
}

Write-Host @'

Focus Lock manual removal (bypasses broken cached MSI uninstall).
Do NOT run "sc stop FocusLockService" or "sc stop FocusLockWatchdog" on older builds.

'@ -ForegroundColor Yellow

Write-Host 'Step 1: Clear session and reset service DACLs...' -ForegroundColor Cyan
Remove-Item $sessionFile -Force -ErrorAction SilentlyContinue
cmd.exe /c "sc sdset FocusLockService `"$sddl`""
cmd.exe /c "sc sdset FocusLockWatchdog `"$sddl`""

$helper = Join-Path $installDir 'FocusLock.SetupHelper.exe'
$serviceExe = Join-Path $installDir 'FocusLock.Service.exe'
if (Test-Path $helper) {
    Write-Host "Running $helper ..." -ForegroundColor Cyan
    & $helper 2>&1 | ForEach-Object { Write-Host $_ }
}
elseif (Test-Path $serviceExe) {
    Write-Host "Running $serviceExe --unlock-for-setup ..." -ForegroundColor Cyan
    & $serviceExe --unlock-for-setup 2>&1 | ForEach-Object { Write-Host $_ }
}

if (Test-ServiceRunning 'FocusLockService' -or Test-ServiceRunning 'FocusLockWatchdog') {
    Write-Host @'

Services are still running. Disabling until reboot (do not use sc stop).

'@ -ForegroundColor Yellow
    Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\FocusLockService' -Name Start -Value 4 -Type DWord -ErrorAction SilentlyContinue
    Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\FocusLockWatchdog' -Name Start -Value 4 -Type DWord -ErrorAction SilentlyContinue
    Write-Host 'REBOOT, then run this script again to finish removal.' -ForegroundColor Green
    exit 0
}

Write-Host 'Step 2: Delete Windows services...' -ForegroundColor Cyan
foreach ($name in @('FocusLockService', 'FocusLockWatchdog')) {
    cmd.exe /c "sc delete $name" 2>&1 | ForEach-Object { Write-Host $_ }
}

Write-Host 'Step 3: Remove program files...' -ForegroundColor Cyan
if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not $KeepUserData) {
    $dataDir = Join-Path $env:ProgramData 'FocusLock'
    if (Test-Path $dataDir) {
        Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Step 4: Remove registry (services, safe boot, event log, installer)...' -ForegroundColor Cyan
$regPaths = @(
    'HKLM:\SYSTEM\CurrentControlSet\Services\FocusLockService',
    'HKLM:\SYSTEM\CurrentControlSet\Services\FocusLockWatchdog',
    'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\FocusLockService',
    'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\FocusLockWatchdog',
    'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\FocusLock',
    'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\FocusLockWatchdog',
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$productCode",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$productCode",
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\$compressedProduct",
    "HKLM:\SOFTWARE\Classes\Installer\Products\$compressedProduct",
    "HKLM:\SOFTWARE\Classes\Installer\Features\$compressedProduct",
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UpgradeCodes\$compressedUpgrade"
)
foreach ($path in $regPaths) {
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
}

$menuDir = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Focus Lock'
if (Test-Path $menuDir) {
    Remove-Item $menuDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($TryMsiLast) {
    Write-Host 'Step 5: Trying msiexec /x (optional)...' -ForegroundColor Cyan
    $code = $productCode.Trim('{}')
    $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList "/x", "{$code}", "/qn" -Wait -PassThru
    Write-Host "msiexec exit code: $($proc.ExitCode)"
}

Write-Host @'

Manual removal finished.

- Focus Lock should no longer appear in Settings -> Apps (if an entry remains, reboot once).
- Install the current MSI from installer\FocusLock.Installer\bin\Release\Focus Lock.msi

'@ -ForegroundColor Green
