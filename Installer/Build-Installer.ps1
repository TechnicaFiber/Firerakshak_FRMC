# ==============================================================================
# Fire Rakshak FRMC Installer Build Script
# ==============================================================================
# This script builds the complete FRMC installer including:
# - FRMC Host application
# - WiX MSI and Bundle packages
#
# Prerequisites:
# - .NET 8 SDK
# - WiX Toolset v4+ (dotnet tool install --global wix)
#
# Usage: .\Build-Installer.ps1 [-Configuration Release] [-Clean]
# ==============================================================================

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Clean,

    [switch]$SkipPublish,

    [switch]$SkipMsi,

    [switch]$SkipBundle
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionDir = Split-Path -Parent $ScriptDir
$InstallerDir = $ScriptDir

# Project paths
$FrmcHostProject = Join-Path $SolutionDir "FireRakshak.FRMC.Host\FireRakshak.FRMC.Host.csproj"
$MsiProject = Join-Path $InstallerDir "FRMC.Installer.wixproj"
$BundleProject = Join-Path $InstallerDir "Bundle\FRMC.Bundle.wixproj"

# Output paths
$PublishDir = Join-Path $InstallerDir "publish"
$OutputDir = Join-Path $InstallerDir "bin\$Configuration"

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "Fire Rakshak FRMC Installer Build" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration"
Write-Host "Solution Dir:  $SolutionDir"
Write-Host "Installer Dir: $InstallerDir"
Write-Host ""

# ==============================================================================
# Step 0: Clean (optional)
# ==============================================================================
if ($Clean) {
    Write-Host "[Clean] Cleaning previous builds..." -ForegroundColor Yellow

    if (Test-Path $PublishDir) {
        Remove-Item $PublishDir -Recurse -Force
        Write-Host "  Removed: $PublishDir"
    }

    if (Test-Path $OutputDir) {
        Remove-Item $OutputDir -Recurse -Force
        Write-Host "  Removed: $OutputDir"
    }

    Write-Host "  Clean complete." -ForegroundColor Green
}

# ==============================================================================
# Step 1: Restore NuGet packages
# ==============================================================================
Write-Host ""
Write-Host "[1/4] Restoring NuGet packages..." -ForegroundColor Yellow

dotnet restore $FrmcHostProject
if ($LASTEXITCODE -ne 0) { throw "Failed to restore FRMC Host packages" }

Write-Host "  Restore complete." -ForegroundColor Green

# ==============================================================================
# Step 2: Publish FRMC Host
# ==============================================================================
if (-not $SkipPublish) {
    Write-Host ""
    Write-Host "[2/4] Publishing FRMC Host application..." -ForegroundColor Yellow

    # Create publish directory
    if (-not (Test-Path $PublishDir)) {
        New-Item -ItemType Directory -Path $PublishDir | Out-Null
    }

    # Publish FRMC Host
    dotnet publish $FrmcHostProject `
        --configuration $Configuration `
        --output $PublishDir `
        --runtime win-x64 `
        --self-contained false `
        /p:PublishSingleFile=false `
        /p:PublishReadyToRun=false

    if ($LASTEXITCODE -ne 0) { throw "Failed to publish FRMC Host" }

    # Verify critical files exist
    $FrmcExe = Join-Path $PublishDir "FireRakshak.FRMC.Host.exe"
    if (-not (Test-Path $FrmcExe)) {
        throw "FRMC Host executable not found after publish: $FrmcExe"
    }

    # Copy Config folder
    $ConfigSource = Join-Path $SolutionDir "FireRakshak.FRMC.Host\Config"
    $ConfigDest = Join-Path $PublishDir "Config"
    if (Test-Path $ConfigSource) {
        if (-not (Test-Path $ConfigDest)) {
            New-Item -ItemType Directory -Path $ConfigDest | Out-Null
        }
        Copy-Item "$ConfigSource\*" $ConfigDest -Recurse -Force
        Write-Host "  Copied Config folder"
    }

    Write-Host "  FRMC Host published to: $PublishDir" -ForegroundColor Green
}

# ==============================================================================
# Step 3: Build MSI Package
# ==============================================================================
if (-not $SkipMsi) {
    Write-Host ""
    Write-Host "[3/4] Building MSI package..." -ForegroundColor Yellow

    # Check if WiX is available
    $wixInstalled = $null
    try {
        $wixInstalled = dotnet tool list --global | Select-String "wix"
    } catch { }

    if (-not $wixInstalled) {
        Write-Host "  WiX Toolset not found. Installing..." -ForegroundColor Yellow
        dotnet tool install --global wix
        if ($LASTEXITCODE -ne 0) { throw "Failed to install WiX Toolset" }
    }

    # Build MSI using dotnet build (WiX SDK)
    Push-Location $InstallerDir
    try {
        dotnet build $MsiProject --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Failed to build MSI" }
    } finally {
        Pop-Location
    }

    # Find MSI in output (may be in en-us subdirectory)
    $MsiPath = Get-ChildItem -Path $OutputDir -Filter "*.msi" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($MsiPath) {
        Write-Host "  MSI built: $($MsiPath.FullName)" -ForegroundColor Green
    } else {
        Write-Host "  Warning: MSI file not found in output directory" -ForegroundColor Yellow
    }
}

# ==============================================================================
# Step 4: Build Bundle (Bootstrapper with .NET installer)
# ==============================================================================
if (-not $SkipBundle) {
    Write-Host ""
    Write-Host "[4/4] Building Bundle (bootstrapper)..." -ForegroundColor Yellow

    Push-Location (Join-Path $InstallerDir "Bundle")
    try {
        dotnet build $BundleProject --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Failed to build Bundle" }
    } finally {
        Pop-Location
    }

    $BundlePath = Join-Path $InstallerDir "Bundle\bin\$Configuration\FRMC.Bundle.exe"
    if (Test-Path $BundlePath) {
        Write-Host "  Bundle built: $BundlePath" -ForegroundColor Green
    } else {
        Write-Host "  Warning: Bundle file not found at expected location" -ForegroundColor Yellow
    }
}

# ==============================================================================
# Summary
# ==============================================================================
Write-Host ""
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "Build Complete!" -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Output files:"

$MsiFile = Get-ChildItem -Path $OutputDir -Filter "*.msi" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
$BundlePath = Join-Path $InstallerDir "Bundle\bin\$Configuration\FRMC.Bundle.exe"

if ($MsiFile) {
    $MsiSize = $MsiFile.Length / 1MB
    Write-Host "  MSI:    $($MsiFile.FullName) ($([math]::Round($MsiSize, 2)) MB)" -ForegroundColor White
}

if (Test-Path $BundlePath) {
    $BundleSize = (Get-Item $BundlePath).Length / 1MB
    Write-Host "  Bundle: $BundlePath ($([math]::Round($BundleSize, 2)) MB)" -ForegroundColor White
}

Write-Host ""
Write-Host "Distribution:" -ForegroundColor Yellow
Write-Host "  - Use FRMC.Bundle.exe for end-user installation (includes .NET download)"
Write-Host "  - Use FRMC.Installer.msi for enterprise deployment (requires .NET pre-installed)"
Write-Host ""
Write-Host "Service installed:" -ForegroundColor Yellow
Write-Host "  - FireRakshakFRMC: Main FRMC service (auto-start, auto-restart on crash)"
Write-Host "    Windows SCM handles crash recovery (restarts up to 3 times, 30s delay)"
Write-Host ""
