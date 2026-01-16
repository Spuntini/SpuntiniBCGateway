@echo off
REM Peppol PDF Extractor - Build and Create Installer Script

echo ================================
echo Peppol PDF Extractor Installer
echo ================================
echo.

REM Check if we're in the right directory
if not exist "SpuntiniBCGateway.sln" (
    echo Error: Please run this script from the solution root directory
    pause
    exit /b 1
)

echo Step 1: Building the solution...
dotnet build -c Release
if errorlevel 1 (
    echo Build failed!
    pause
    exit /b 1
)

echo.
echo Step 2: Publishing PeppolPdfExtractor as self-contained executable...
dotnet publish -c Release -r win-x64 --self-contained --project PeppolPdfExtractor
if errorlevel 1 (
    echo Publish failed!
    pause
    exit /b 1
)

echo.
echo Step 3: Checking for Inno Setup...
for /f "tokens=2,*" %%A in ('reg query "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6" /v "InstallLocation" 2^>nul') do set "INNOSETUP=%%B"

if not defined INNOSETUP (
    echo.
    echo ============================================================
    echo Inno Setup is not installed!
    echo.
    echo To create the installer, you need to install Inno Setup:
    echo 1. Download from: https://jrsoftware.org/isdl.php
    echo 2. Install Inno Setup (choose default installation)
    echo 3. Run this script again
    echo.
    echo Alternatively, you can manually compile the .iss file:
    echo   Right-click PeppolPdfExtractor.iss and select "Compile"
    echo ============================================================
    pause
    exit /b 1
)

echo Found Inno Setup at: %INNOSETUP%
echo.
echo Step 4: Creating installer...

if not exist "installers" mkdir installers

"%INNOSETUP%iscc.exe" "PeppolPdfExtractor.iss"
if errorlevel 1 (
    echo Installer creation failed!
    pause
    exit /b 1
)

echo.
echo ================================
echo SUCCESS!
echo ================================
echo.
echo The installer has been created:
echo   installers\PeppolPdfExtractor-1.0.0-Installer.exe
echo.
pause
