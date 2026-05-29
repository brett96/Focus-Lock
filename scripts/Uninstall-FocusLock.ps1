#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes Focus Lock when normal uninstall fails.

.DESCRIPTION
    msiexec /x always uses the MSI cached at install time (e.g. C:\Windows\Installer\4a567.msi).
    Focus Lock 1.1.0's cached package runs a broken unlock custom action (error 1721), so
    this script defaults to manual removal. Use -UseMsiOnly only if you know uninstall works.

.EXAMPLE
    .\scripts\Uninstall-FocusLock.ps1
#>
param(
    [switch]$UseMsiOnly,
    [switch]$KeepUserData
)

$manualScript = Join-Path $PSScriptRoot 'Remove-FocusLock-Manual.ps1'
if (-not $UseMsiOnly) {
    $args = @()
    if ($KeepUserData) { $args += '-KeepUserData' }
    & $manualScript @args
    exit $LASTEXITCODE
}

# Legacy path: msiexec only (fails on 1.1.0 with error 1721).
$productCode = '8AF7A534-9A53-474F-9056-0A164D528037'
$log = Join-Path $env:TEMP "focuslock-uninstall-$productCode.log"
Write-Host "msiexec /x {$productCode} (log: $log)" -ForegroundColor Cyan
$proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList "/x", "{$productCode}", "/qn", "/l*v", "`"$log`"" -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    Write-Host "Failed (exit $($proc.ExitCode)). Use .\scripts\Remove-FocusLock-Manual.ps1 instead." -ForegroundColor Red
    exit $proc.ExitCode
}
Write-Host 'Uninstalled via MSI.' -ForegroundColor Green
