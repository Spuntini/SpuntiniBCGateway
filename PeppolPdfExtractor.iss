[Setup]
AppName=Peppol PDF Extractor
AppVersion=1.0.0
AppPublisher=Spuntini
AppPublisherURL=https://www.spuntini.be
AppSupportURL=https://www.spuntini.be
AppUpdatesURL=https://www.spuntini.be
DefaultDirName={autopf}\Spuntini\PeppolPdfExtractor
DefaultGroupName=Spuntini\Peppol PDF Extractor
AllowNoIcons=yes
OutputBaseFilename=PeppolPdfExtractor-1.0.0-Installer
OutputDir=installers
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "PeppolPdfExtractor\bin\Release\net9.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Peppol PDF Extractor"; Filename: "{app}\PeppolPdfExtractor.exe"; IconFilename: "{app}\PeppolPdfExtractor.exe"; Comment: "Extract PDFs from Peppol UBL documents"
Name: "{group}\{cm:UninstallProgram,Peppol PDF Extractor}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Peppol PDF Extractor"; Filename: "{app}\PeppolPdfExtractor.exe"; IconFilename: "{app}\PeppolPdfExtractor.exe"; Tasks: desktopicon; Comment: "Extract PDFs from Peppol UBL documents"

[Run]
Filename: "{app}\PeppolPdfExtractor.exe"; Description: "{cm:LaunchProgram,Peppol PDF Extractor}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
