# build-and-verify.ps1 — Publish, hash, and verify binaries before MSI build
# Usage: .\build-and-verify.ps1
#   Step 1: Publishes agent and tray binaries
#   Step 2: Computes SHA256 hashes and writes build-manifest.json
#   Step 3: Verifies hashes match manifest before building MSI
#   If -UpdateManifest is specified, updates the manifest without building MSI

param(
    [switch]$UpdateManifest
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$AgentExe = ".\publish\CbitAgent.exe"
$TrayExe  = ".\publish-tray\CbitAgent.Tray.exe"
$ManifestPath = ".\build-manifest.json"

function Get-FileHash256([string]$Path) {
    (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLower()
}

# Step 1: Publish both binaries
Write-Host "Publishing agent..." -ForegroundColor Cyan
dotnet publish CbitAgent/CbitAgent.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed" }

Write-Host "Publishing tray app..." -ForegroundColor Cyan
dotnet publish CbitAgent.Tray/CbitAgent.Tray.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish-tray --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tray publish failed" }

# Step 2: Compute hashes
$agentHash = Get-FileHash256 $AgentExe
$trayHash  = Get-FileHash256 $TrayExe

Write-Host "CbitAgent.exe   SHA256: $agentHash" -ForegroundColor Green
Write-Host "CbitAgent.Tray.exe SHA256: $trayHash" -ForegroundColor Green

if ($UpdateManifest) {
    # Write new manifest
    $manifest = @{
        _comment = "SHA256 hashes of published binaries. Verified before MSI packaging."
        updated  = (Get-Date -Format "o")
        binaries = @{
            "CbitAgent.exe"      = $agentHash
            "CbitAgent.Tray.exe" = $trayHash
        }
    }
    $manifest | ConvertTo-Json -Depth 3 | Set-Content $ManifestPath -Encoding UTF8
    Write-Host "Manifest updated: $ManifestPath" -ForegroundColor Yellow
    Write-Host "Commit this manifest to source control before building the MSI." -ForegroundColor Yellow
    exit 0
}

# Step 3: Verify hashes against manifest
if (-not (Test-Path $ManifestPath)) {
    throw "build-manifest.json not found. Run with -UpdateManifest first to create it."
}

$manifest = Get-Content $ManifestPath | ConvertFrom-Json
$expectedAgent = $manifest.binaries.'CbitAgent.exe'
$expectedTray  = $manifest.binaries.'CbitAgent.Tray.exe'

if ([string]::IsNullOrEmpty($expectedAgent) -or [string]::IsNullOrEmpty($expectedTray)) {
    throw "build-manifest.json has empty hashes. Run with -UpdateManifest to populate."
}

$errors = @()
if ($agentHash -ne $expectedAgent) {
    $errors += "CbitAgent.exe hash MISMATCH: expected=$expectedAgent actual=$agentHash"
}
if ($trayHash -ne $expectedTray) {
    $errors += "CbitAgent.Tray.exe hash MISMATCH: expected=$expectedTray actual=$trayHash"
}

if ($errors.Count -gt 0) {
    Write-Host "`nBUILD INTEGRITY CHECK FAILED:" -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host "`nIf you intentionally changed the binaries, run:" -ForegroundColor Yellow
    Write-Host "  .\build-and-verify.ps1 -UpdateManifest" -ForegroundColor Yellow
    Write-Host "then commit the updated build-manifest.json." -ForegroundColor Yellow
    throw "MSI build aborted: binary hash verification failed"
}

Write-Host "`nHash verification passed." -ForegroundColor Green

# Step 4: Build MSI
Write-Host "Building MSI installer..." -ForegroundColor Cyan
dotnet build CbitAgent.Installer/CbitAgent.Installer.wixproj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

Write-Host "`nBuild complete:" -ForegroundColor Green
Write-Host "  MSI: CbitAgent.Installer\bin\Release\CbitAgent.Installer.msi" -ForegroundColor Green
