; Inno Setup Script for IppPrinter
; See http://www.jrsoftware.org/ishelp/ for details on Inno Setup script format.

#define MyAppName "IppPrinter"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Daniel Klecha"
#define MyAppExeName "IppPrinter.exe"

[Setup]
AppId={{E8C7A3F0-DE9E-4B07-AB7E-3DFB54C8D003}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/danielklecha/IppPrinter
AppSupportURL=https://github.com/danielklecha/IppPrinter/issues
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\icon.ico
MinVersion=10.0.17763
DefaultDirName={commonpf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=IppPrinterSetup
OutputDir=Output
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
SetupLogging=yes
RestartIfNeededByRun=no
SetupArchitecture=x64
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\IppPrinter\bin\Release\net10.0\win-x64\publish\LICENSE.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
Name: "{autodesktop}\IppPrinter Jobs"; Filename: "{code:GetJobsDir}"; Check: IsDesktopIconSelected
Name: "{autostartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Check: IsStartupAppSelected

[Files]
; Source files should be published before running this script
Source: "..\IppPrinter\bin\Release\net10.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Add-Printer.ps1"; DestDir: "{app}\Setup"; Flags: ignoreversion
Source: "Remove-Printer.ps1"; DestDir: "{app}\Setup"; Flags: ignoreversion
Source: "Add-Certificate.ps1"; DestDir: "{app}\Setup"; Flags: ignoreversion
Source: "Remove-Certificate.ps1"; DestDir: "{app}\Setup"; Flags: ignoreversion
Source: "Update-AppSettings.ps1"; DestDir: "{app}\Setup"; Flags: ignoreversion

[Run]
; 0.5 Configure Kestrel via PowerShell
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Setup\Update-AppSettings.ps1"" -Scheme {code:GetProtocolScheme} -AppDir ""{app}"" -JobsDir ""{code:GetJobsDir}"" -PostProcessName ""{code:GetPostProcessName}"" -PostProcessArguments ""{code:GetPostProcessArguments}"""; Flags: runhidden waituntilterminated; StatusMsg: "Configuring AppSettings..."
; 1. Run PowerShell script to generate and trust certificate (HTTPS only)
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Setup\Add-Certificate.ps1"""; Flags: runhidden waituntilterminated; StatusMsg: "Generating self-signed certificate..."; Check: IsHttpsSelected
; 1.5 Create Windows Service (Service mode only)
Filename: "{sys}\sc.exe"; Parameters: "create {#MyAppName} binPath= ""{app}\{#MyAppExeName}"" start= auto DisplayName= ""{#MyAppName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Registering Windows Service..."; Check: IsServiceSelected
; 2. Add Windows Firewall Rule
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#MyAppName}"" dir=in action=allow protocol=TCP localport=631 program=""{app}\{#MyAppExeName}"" enable=yes"; Flags: runhidden waituntilterminated; StatusMsg: "Configuring Windows Firewall..."
; 3. Start Windows Service (Service mode only)
Filename: "{sys}\sc.exe"; Parameters: "start {#MyAppName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting Windows Service..."; Check: IsServiceSelected
; 3.5. Start Startup Application immediately so port 631 is open for Add-Printer.ps1 (Startup App mode only)
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runhidden runasoriginaluser; StatusMsg: "Starting Startup Application..."; Check: IsStartupAppSelected
; 4. Run PowerShell script to add Windows Printer Queue
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""& '{app}\Setup\Add-Printer.ps1' -Scheme {code:GetProtocolScheme} *>&1 | Out-File -FilePath '{app}\printer_setup.log' -Encoding utf8"""; Flags: runhidden waituntilterminated; StatusMsg: "Installing IPP Printer queue..."
; 5. Post-install options
Filename: "{code:GetJobsDir}"; Description: "Open jobs folder"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "{app}"; Description: "Open installation folder"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "ms-settings:printers"; Description: "Open Windows Printers & Scanners settings"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "{code:GetInstallLogPath}"; Description: "View installation log"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "{app}\THIRD-PARTY-NOTICES.txt"; Description: "View third-party licenses and notices"; Flags: postinstall shellexec skipifsilent unchecked

[UninstallRun]
; 0.5 Stop Windows Service and process synchronously if running
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Stop-Service -Name '{#MyAppName}' -Force -ErrorAction SilentlyContinue; Stop-Process -Name '{#MyAppName}' -Force -ErrorAction SilentlyContinue"""; Flags: runhidden waituntilterminated; RunOnceId: "StopIppPrinterApp"
; 1. Run PowerShell script to remove Windows Printer Queue
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Setup\Remove-Printer.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RemovePrinterQueue"
; 1.5. Run PowerShell script to remove certificate
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Setup\Remove-Certificate.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveCertificate"
; 2. Delete Windows Firewall Rule
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewallRule"
; 3. Delete Windows Service
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyAppName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[Code]
#include "installer.pas"
