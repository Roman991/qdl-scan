; Inno Setup script per Scan App (WinForms, .NET Framework 4.8).
; Sostituisce il vecchio progetto Visual Studio Setup (.vdproj / Scan_App_SetUp.msi).
;
; Build dell'app prima di compilare questo script:
;   dotnet publish ../NewScan/NewScan.csproj -c Release
; Poi compilare con Inno Setup (ISCC):
;   ISCC setup.iss
;
; Prerequisito sulla macchina di destinazione: .NET Framework 4.8, incluso in Windows 10/11
; (o distribuito via Windows Update) — nessun runtime aggiuntivo da installare a parte,
; a differenza della precedente build WinUI 3 / .NET 10 + Windows App SDK.

#define AppName "Scan App"
#define AppVersion "2.0.0"
#define AppPublisher "ScanAppForWeb"
#define AppExe "NewScan.exe"
; Cartella di pubblicazione di 'dotnet publish' (aggiustare se necessario)
#define PublishDir "..\NewScan\bin\Release\net48\publish"

[Setup]
AppId={{8F2A6C10-2D4B-4E2A-9C1F-7A1B2C3D4E5F}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\ScanApp
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=ScanAppSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
SetupIconFile=..\NewScan\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Copia l'intero output self-contained (exe + dipendenze runtime).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; L'autostart NON viene forzato dall'installer: si gestisce dall'opzione in-app.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
