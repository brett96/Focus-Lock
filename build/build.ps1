<#
.SYNOPSIS
    Full build: publish all projects then build the WiX installer MSI.

.PARAMETER Configuration
    Build configuration. Default: Release.

.EXAMPLE
    .\build\build.ps1
    .\build\build.ps1 -Configuration Debug
#>
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"

$root       = Resolve-Path "$PSScriptRoot\.."
$publishAll = Join-Path $root "publish\FocusLock"
$publishSvc = Join-Path $root "publish\service-exe"

function Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok([string]$msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "    ERROR: $msg" -ForegroundColor Red; throw $msg }

# ── Clean ─────────────────────────────────────────────────────────────────────
Step "Cleaning publish directories"
foreach ($dir in @($publishAll, $publishSvc)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -Confirm:$false }
}
Ok "Clean done."

# ── Publish ───────────────────────────────────────────────────────────────────
Step "Publishing FocusLock.UI"
dotnet publish "$root\src\FocusLock.UI" -c $Configuration -o $publishAll --nologo
if ($LASTEXITCODE -ne 0) { Fail "FocusLock.UI publish failed." }
Ok "FocusLock.UI published."

Step "Publishing FocusLock.BlockerStub"
dotnet publish "$root\src\FocusLock.BlockerStub" -c $Configuration -o $publishAll --nologo
if ($LASTEXITCODE -ne 0) { Fail "FocusLock.BlockerStub publish failed." }
Ok "FocusLock.BlockerStub published."

Step "Publishing FocusLock.Service"
dotnet publish "$root\src\FocusLock.Service" -c $Configuration -o $publishAll --nologo
if ($LASTEXITCODE -ne 0) { Fail "FocusLock.Service publish failed." }
Ok "FocusLock.Service published."

# ── Isolate service exe ───────────────────────────────────────────────────────
#
# WiX Package.wxs defines FocusLock.Service.exe separately with ServiceInstall /
# ServiceControl. To avoid a duplicate-file conflict with the AppFiles component
# (which harvests everything in publish\FocusLock\), the service exe is moved to
# its own directory. BindPath in the wixproj resolves "FocusLock.Service.exe"
# from either directory, so the installer still installs it to INSTALLFOLDER.
#
Step "Isolating FocusLock.Service.exe for installer"
New-Item -ItemType Directory -Path $publishSvc -Force | Out-Null
Move-Item "$publishAll\FocusLock.Service.exe" "$publishSvc\FocusLock.Service.exe" -Force
Ok "FocusLock.Service.exe moved to publish\service-exe\."

# ── Build installer ───────────────────────────────────────────────────────────
Step "Building WiX installer"
dotnet build "$root\installer\FocusLock.Installer" -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { Fail "Installer build failed." }

$msi = Get-ChildItem "$root\installer\FocusLock.Installer\bin\$Configuration" `
    -Filter "*.msi" -Recurse | Select-Object -First 1
if ($null -eq $msi) { Fail "MSI not found after build." }

Ok "Installer ready:"
Write-Host "    $($msi.FullName)" -ForegroundColor Yellow

# ── Summary ───────────────────────────────────────────────────────────────────
$size = [math]::Round($msi.Length / 1MB, 2)
Write-Host "`nBuild complete. MSI size: ${size} MB" -ForegroundColor Green
