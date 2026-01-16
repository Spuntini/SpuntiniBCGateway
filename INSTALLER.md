# Creating an Installer for Peppol PDF Extractor

This directory contains everything needed to build a professional Windows installer for the Peppol PDF Extractor application.

## Prerequisites

### Required
- .NET 9.0 SDK (for building)
- Windows 10 or later

### For Creating the Installer
- **Inno Setup 6** (Free, download from https://jrsoftware.org/isdl.php)

## Installation Steps

### 1. Install Inno Setup (One-time setup)

1. Download Inno Setup from: https://jrsoftware.org/isdl.php
2. Run the installer (accept all defaults)
3. Restart your computer (recommended)

### 2. Build the Installer

Choose one of the following methods:

#### **Method A: Using PowerShell (Recommended)**
```powershell
cd "c:\Users\lode.vanderstichele\Spuntini BVBA\Spuntinigroup - IT - Documenten\Project\SpuntiniBCGateway"
.\build-installer.ps1
```

#### **Method B: Using Command Prompt**
```cmd
cd "c:\Users\lode.vanderstichele\Spuntini BVBA\Spuntinigroup - IT - Documenten\Project\SpuntiniBCGateway"
build-installer.bat
```

#### **Method C: Manual Compilation**
1. Right-click `PeppolPdfExtractor.iss` file
2. Select "Compile with Inno Setup"
3. Inno Setup will compile and create the installer

## Output

The installer will be created at:
```
installers\PeppolPdfExtractor-1.0.0-Installer.exe
```

## Distributing the Installer

You can now share `PeppolPdfExtractor-1.0.0-Installer.exe` with users. The installer will:

✅ **Installation Features:**
- Automatically detect system architecture (64-bit Windows)
- Install to `C:\Program Files\Spuntini\PeppolPdfExtractor`
- Create Start Menu shortcuts
- Create optional Desktop shortcut
- Register in Windows "Programs and Features"
- Provide one-click uninstall

✅ **User Experience:**
- Professional installer wizard
- Modern UI
- Welcome and license screens
- Optional start program after installation
- Uninstall support

## System Requirements for End Users

- Windows 10 or later (64-bit)
- .NET 9.0 Runtime (included in the installer - self-contained)
- ~150 MB disk space

## Customization

To customize the installer, edit `PeppolPdfExtractor.iss`:

### Change Application Version
```ini
AppVersion=1.0.0  <- Change this
```

### Change Installation Directory
```ini
DefaultDirName={autopf}\Spuntini\PeppolPdfExtractor  <- Modify path
```

### Change Start Menu Folder
```ini
DefaultGroupName=Spuntini\Peppol PDF Extractor  <- Modify name
```

### Change Output Filename
```ini
OutputBaseFilename=PeppolPdfExtractor-1.0.0-Installer  <- Change this
```

After editing, recompile by running the build script again.

## Troubleshooting

### "Inno Setup is not installed"
- Download and install Inno Setup from: https://jrsoftware.org/isdl.php
- Make sure to complete the installation wizard
- Restart your computer
- Run the build script again

### "Build failed" during compilation
- Run `dotnet clean` first
- Run `dotnet restore` 
- Try again: `.\build-installer.ps1`

### Installer is too large
- The installer is ~100+ MB because it includes the .NET 9.0 runtime
- This is necessary for the application to work without requiring .NET to be pre-installed
- Users don't need to install .NET separately

## Additional Notes

- The installer is self-contained, meaning users don't need .NET SDK or runtime installed beforehand
- The application will work on any Windows 10+ (64-bit) system
- Users can uninstall through "Programs and Features" in Windows Settings
- All application files are isolated in the installation directory for clean uninstall

## Inno Setup Documentation

For more advanced customization, see:
- https://jrsoftware.org/ishelp/
- Inno Setup Help (F1 in Inno Setup IDE)
