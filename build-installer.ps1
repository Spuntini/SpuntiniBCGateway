# Peppol PDF Extractor - Build and Create Installer Script
# PowerShell version

Write-Host "================================" -ForegroundColor Cyan
Write-Host "Peppol PDF Extractor Installer" -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan
Write-Host ""

# Check if we're in the right directory
if (-not (Test-Path "SpuntiniBCGateway.sln")) {
    Write-Host "Error: Please run this script from the solution root directory" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

# Step 1: Build the solution
Write-Host "Step 1: Building the solution..." -ForegroundColor Yellow
dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

# Step 2: Publish the application
Write-Host ""
Write-Host "Step 2: Publishing PeppolPdfExtractor as self-contained executable..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x64 --self-contained --project PeppolPdfExtractor
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed!" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

# Step 3: Check for Inno Setup
Write-Host ""
Write-Host "Step 3: Checking for Inno Setup..." -ForegroundColor Yellow

$innoSetupPaths = @(
    "C:\Program Files (x86)\Inno Setup 6",
    "C:\Program Files\Inno Setup 6"
)

$innoSetupPath = $null
foreach ($path in $innoSetupPaths) {
    if (Test-Path "$path\iscc.exe") {
        $innoSetupPath = $path
        break
    }
}

if (-not $innoSetupPath) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host "Inno Setup is not installed!" -ForegroundColor Red
    Write-Host ""
    Write-Host "To create the installer, you need to install Inno Setup:" -ForegroundColor Yellow
    Write-Host "1. Download from: https://jrsoftware.org/isdl.php" -ForegroundColor White
    Write-Host "2. Install Inno Setup (choose default installation)" -ForegroundColor White
    Write-Host "3. Run this script again" -ForegroundColor White
    Write-Host ""
    Write-Host "Alternatively, you can manually compile the .iss file:" -ForegroundColor Yellow
    Write-Host "  Right-click PeppolPdfExtractor.iss and select 'Compile'" -ForegroundColor White
    Write-Host "============================================================" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

Write-Host "Found Inno Setup at: $innoSetupPath" -ForegroundColor Green
Write-Host ""
Write-Host "Step 4: Creating installer..." -ForegroundColor Yellow

# Create installers directory if it doesn't exist
if (-not (Test-Path "installers")) {
    New-Item -ItemType Directory -Path "installers" | Out-Null
}

# Compile the installer
& "$innoSetupPath\iscc.exe" "PeppolPdfExtractor.iss"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installer creation failed!" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

Write-Host ""
Write-Host "================================" -ForegroundColor Green
Write-Host "SUCCESS!" -ForegroundColor Green
Write-Host "================================" -ForegroundColor Green
Write-Host ""
Write-Host "The installer has been created:" -ForegroundColor Cyan
Write-Host "  installers\PeppolPdfExtractor-1.0.0-Installer.exe" -ForegroundColor White
Write-Host ""
Read-Host "Press Enter to exit"
