; Inno Setup script per Qdl Scan (WinForms, .NET Framework 4.8).
; Sostituisce il vecchio progetto Visual Studio Setup (.vdproj / Scan_App_SetUp.msi).
;
; Build dell'app prima di compilare questo script:
;   dotnet publish ../QdlScan/QdlScan.csproj -c Release
; Poi compilare con Inno Setup (ISCC):
;   ISCC setup.iss
;
; Prerequisito sulla macchina di destinazione: .NET Framework 4.8, incluso in Windows 10/11
; (o distribuito via Windows Update) — nessun runtime aggiuntivo da installare a parte,
; a differenza della precedente build WinUI 3 / .NET 10 + Windows App SDK.

#define AppName "Qdl Scan"
#define AppVersion "2.0.0"
#define AppPublisher "Qdl Scan"
#define AppExe "QdlScan.exe"
; NON cambiare: identifica l'app per l'upgrade/uninstall automatico delle installazioni
; precedenti (vedi [Code] sotto), a prescindere dal nome con cui furono pubblicate.
#define AppId "{8F2A6C10-2D4B-4E2A-9C1F-7A1B2C3D4E5F}"
; Cartella di pubblicazione di 'dotnet publish' (aggiustare se necessario)
#define PublishDir "..\QdlScan\bin\Release\net48\publish"

[Setup]
AppId={{#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; Blocca install/uninstall se l'app e' in esecuzione (stesso mutex di QdlScan/Program.cs)
AppMutex=QdlScan-SingleInstance-Mutex
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=QdlScanSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
SetupIconFile=..\QdlScan\Assets\app.ico
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

[UninstallDelete]
; Rimuove le impostazioni utente persistite (porta, allowlist, DPI) alla disinstallazione.
Type: filesandordirs; Name: "{userappdata}\QdlScan"

[Registry]
; Registra la rimozione del valore di autostart alla disinstallazione, senza scriverlo
; in fase di install (il toggle in-app resta l'unico punto che lo crea).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "QdlScan"; Flags: uninsdeletevalue

[Code]
function InitializeSetup(): Boolean;
var
  UninstallString: String;
  ResultCode: Integer;
begin
  Result := True;

  // Se una versione precedente e' gia' installata (stesso AppId), disinstallala
  // silenziosamente prima di copiare i nuovi file: rimuove anche eventuali file
  // "orfani" di build precedenti con un publish output diverso (es. runtime WinUI3),
  // perche' l'uninstaller di quella versione conosce esattamente cosa ha copiato lui.
  if RegQueryStringValue(HKCU,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1',
       'UninstallString', UninstallString) then
  begin
    UninstallString := RemoveQuotes(Trim(UninstallString));
    if FileExists(UninstallString) then
      Exec(UninstallString, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '',
           SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  // Reset esplicito e idempotente (no-op se assenti): garantisce impostazioni pulite
  // ad ogni install/upgrade anche per aggiornamenti provenienti da installer precedenti
  // a questa fix, il cui uninstaller non conosce ancora [UninstallDelete]/[Registry].
  DelTree(ExpandConstant('{userappdata}\QdlScan'), True, True, True);
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'QdlScan');

  // Residui della vecchia app "Scan App" (nome precedente al rebranding): l'uninstaller
  // silenzioso sopra li rimuove già se l'installazione precedente aveva questa stessa
  // fix, ma non per installazioni ancora più vecchie il cui uninstaller non la conosceva.
  DelTree(ExpandConstant('{userappdata}\ScanApp'), True, True, True);
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ScanApp');
end;
