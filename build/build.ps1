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
$publishWatchdog = Join-Path $root "publish\watchdog-exe"
$publishSetupCa = Join-Path $root "publish\setup-ca"

function Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok([string]$msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "    ERROR: $msg" -ForegroundColor Red; throw $msg }

# ── Clean ─────────────────────────────────────────────────────────────────────
Step "Cleaning publish directories"
foreach ($dir in @($publishAll, $publishSvc, $publishWatchdog, $publishSetupCa)) {
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

Step "Publishing FocusLock.Watchdog"
dotnet publish "$root\src\FocusLock.Watchdog" -c $Configuration -o $publishAll --nologo
if ($LASTEXITCODE -ne 0) { Fail "FocusLock.Watchdog publish failed." }
Ok "FocusLock.Watchdog published."

Step "Publishing FocusLock.SetupHelper"
dotnet publish "$root\src\FocusLock.SetupHelper" -c $Configuration -o $publishAll --nologo
if ($LASTEXITCODE -ne 0) { Fail "FocusLock.SetupHelper publish failed." }
Ok "FocusLock.SetupHelper published."

Step "Publishing self-contained FocusLock.SetupHelper for MSI unlock custom action"
New-Item -ItemType Directory -Path $publishSetupCa -Force | Out-Null
dotnet publish "$root\src\FocusLock.SetupHelper" -c $Configuration -o $publishSetupCa --nologo `
    -r win-x64 --self-contained true `
    /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { Fail "FocusLock.SetupHelper (setup-ca) publish failed." }
Ok "FocusLock.SetupHelper (setup-ca) published."

# ── Code signing setup ───────────────────────────────────────────────────────
Step "Setting up code signing"

$signTool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" `
    -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "x64" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName

$signingCert = $null
if ($null -ne $signTool) {
    $signingCert = Get-ChildItem "Cert:\CurrentUser\My" -CodeSigningCert |
        Where-Object { $_.Subject -match "Focus Lock" -and $_.NotAfter -gt (Get-Date) } |
        Select-Object -First 1

    if ($null -eq $signingCert) {
        Write-Host "    Creating self-signed code signing certificate for 'Focus Lock'..." -ForegroundColor Yellow
        $signingCert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject "CN=Focus Lock" `
            -HashAlgorithm SHA256 `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -NotAfter (Get-Date).AddYears(10)
    }

    # Trust the cert so UAC shows "Focus Lock" instead of "Unknown"
    foreach ($storeName in @("Root", "TrustedPublisher")) {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, "LocalMachine")
        try {
            $store.Open("ReadWrite")
            if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $signingCert.Thumbprint })) {
                $store.Add($signingCert)
            }
        } catch {
            Write-Host "    WARNING: Could not add cert to $storeName store: $_" -ForegroundColor Yellow
        } finally {
            $store.Close()
        }
    }

    Ok "Code signing ready: $($signingCert.Subject)"
} else {
    Write-Host "    WARNING: signtool.exe not found. Skipping code signing." -ForegroundColor Yellow
}

function SignFiles([string[]]$paths, [string]$description = "Focus Lock") {
    if ($null -eq $signingCert -or $null -eq $signTool) { return }
    foreach ($path in $paths) {
        if (-not (Test-Path $path)) { continue }
        # /d sets the friendly name shown in the UAC elevation prompt (otherwise Windows
        # shows the random temp MSI name msiexec copies to C:\Windows\Installer\).
        & $signTool sign /sha1 $signingCert.Thumbprint /fd SHA256 /d $description /q $path
        if ($LASTEXITCODE -eq 0) { Ok "Signed: $(Split-Path $path -Leaf) ($description)" }
        else { Write-Host "    WARNING: Could not sign $(Split-Path $path -Leaf)" -ForegroundColor Yellow }
    }
}

# Sign all published executables
SignFiles @(Get-ChildItem $publishAll -Filter "FocusLock*.exe" | Select-Object -ExpandProperty FullName)

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

Step "Isolating FocusLock.Watchdog.exe for installer"
New-Item -ItemType Directory -Path $publishWatchdog -Force | Out-Null
Move-Item "$publishAll\FocusLock.Watchdog.exe" "$publishWatchdog\FocusLock.Watchdog.exe" -Force
Ok "FocusLock.Watchdog.exe moved to publish\watchdog-exe\."

# ── Build installer ───────────────────────────────────────────────────────────
Step "Building WiX installer"
dotnet build "$root\installer\FocusLock.Installer" -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { Fail "Installer build failed." }

$msi = Get-ChildItem "$root\installer\FocusLock.Installer\bin\$Configuration" `
    -Filter "*.msi" -Recurse | Select-Object -First 1
if ($null -eq $msi) { Fail "MSI not found after build." }

Ok "Installer ready:"
Write-Host "    $($msi.FullName)" -ForegroundColor Yellow

# Sign the MSI (description appears on the UAC prompt instead of a random .msi name)
SignFiles @($msi.FullName) "Focus Lock"

# ── Summary ───────────────────────────────────────────────────────────────────
$size = [math]::Round($msi.Length / 1MB, 2)
Write-Host "`nBuild complete. MSI size: ${size} MB" -ForegroundColor Green
