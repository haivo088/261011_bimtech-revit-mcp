; Setup.exe for users of the BIMTECH Revit MCP add-in. Built by scripts\package.ps1, which stages the files and passes
; AppVersion, Stage (staged files) and OutDir. Installs for the current user only (no admin rights):
;   - the add-in into %APPDATA%\Autodesk\Revit\Addins\2025 (or /ADDINSDIR=),
;   - Node.js and the bundled MCP server into %LOCALAPPDATA%\BIMTECH MCP,
;   - the bimtech-revit-mcp entry into Claude Desktop's config (or /CLAUDECONFIG=), via claude-config.ps1.
; The developer tools are never part of it. Test install: package.ps1 -TestInstall.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Stage
  #define Stage "..\dist\stage"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
AppId={{85A06FC6-325F-4E4E-9F2F-B8CA85F40C9B}
AppName=BIMTECH Revit MCP
AppVersion={#AppVersion}
AppVerName=BIMTECH Revit MCP {#AppVersion}
AppPublisher=BIMTECH
DefaultDirName={localappdata}\BIMTECH MCP
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutDir}
OutputBaseFilename=BIMTECH-MCP-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=BIMTECH Revit MCP {#AppVersion}
CloseApplications=no
SetupLogging=yes

[Messages]
WelcomeLabel2=Trình cài đặt sẽ cài [name/ver] cho Revit 2025 và Claude Desktop trên máy này.%n%nHãy đóng Revit và Claude Desktop trước khi tiếp tục.
FinishedLabel=Đã cài xong [name].%n%n1. Khởi động lại Claude Desktop.%n2. Mở Revit 2025, chọn Always Load nếu Revit hỏi về add-in.%n3. Add-Ins → BIMTECH MCP → MCP Bridge để bật cầu nối.%n4. Hỏi Claude: "ping revit".

[Files]
Source: "{#Stage}\addin\Bimtech.RevitMcp.addin"; DestDir: "{code:AddinsDir}"; Flags: ignoreversion
Source: "{#Stage}\addin\Bimtech.RevitMcp\*"; DestDir: "{code:AddinsDir}\Bimtech.RevitMcp"; Flags: ignoreversion recursesubdirs
Source: "{#Stage}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[UninstallDelete]
Type: files; Name: "{app}\install.ini"
Type: dirifempty; Name: "{code:AddinsDir}\Bimtech.RevitMcp"
Type: dirifempty; Name: "{app}"

[Code]
const
  RevitExe = '{commonpf64}\Autodesk\Revit 2025\Revit.exe';

function AddinsDir(Param: String): String;
begin
  if IsUninstaller then
    Result := GetIniString('install', 'AddinsDir', ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2025'),
      ExpandConstant('{app}\install.ini'))
  else
    Result := ExpandConstant('{param:ADDINSDIR|{userappdata}\Autodesk\Revit\Addins\2025}');
end;

// Revit locks the add-in DLL while it runs.
function IsRevitRunning: Boolean;
var
  Locator, Service, Found: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''Revit.exe''');
    Result := Found.Count > 0;
  except
    Result := False;
  end;
end;

function WaitForRevitClosed: Boolean;
begin
  Result := True;
  while IsRevitRunning do
  begin
    if SuppressibleMsgBox('Revit đang mở. Hãy lưu và đóng Revit rồi bấm Retry.', mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if ExpandConstant('{param:SKIPREVITCHECK|0}') = '1' then Exit;
  if not FileExists(ExpandConstant(RevitExe)) then
    if SuppressibleMsgBox('Không thấy Revit 2025 trên máy này (' + ExpandConstant(RevitExe) + ').' + #13#10 +
        'Add-in chỉ chạy với Revit 2025. Vẫn cài tiếp?', mbConfirmation, MB_YESNO, IDNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  Result := WaitForRevitClosed;
end;

function InitializeUninstall: Boolean;
begin
  Result := WaitForRevitClosed;
end;

// Runs claude-config.ps1; returns its exit code and output.
function RunClaudeConfig(Arguments: String; var Output: String): Integer;
var
  LogFile: String;
  Lines: AnsiString;
begin
  LogFile := AddBackslash(GetTempDir) + 'bimtech-claude-config.log';
  Exec(ExpandConstant('{cmd}'), '/C powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\claude-config.ps1') + '" ' + Arguments + ' > "' + LogFile + '" 2>&1',
    '', SW_HIDE, ewWaitUntilTerminated, Result);
  if LoadStringFromFile(LogFile, Lines) then Output := String(Lines) else Output := '';
  Log('claude-config.ps1 ' + Arguments + ' -> ' + IntToStr(Result) + ': ' + Output);
end;

function ConfigArgument: String;
var
  Config: String;
begin
  if IsUninstaller then
    Config := GetIniString('install', 'ClaudeConfig', '', ExpandConstant('{app}\install.ini'))
  else
    Config := ExpandConstant('{param:CLAUDECONFIG|}');
  if Config = '' then Result := '' else Result := ' -Config "' + Config + '"';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Output: String;
begin
  if CurStep <> ssPostInstall then Exit;
  // The uninstaller removes the add-in and the Claude entry from where this install put them.
  SetIniString('install', 'AddinsDir', AddinsDir(''), ExpandConstant('{app}\install.ini'));
  SetIniString('install', 'ClaudeConfig', ExpandConstant('{param:CLAUDECONFIG|}'), ExpandConstant('{app}\install.ini'));
  Code := RunClaudeConfig('-Action add -Node "' + ExpandConstant('{app}\node\node.exe') + '" -Server "' +
    ExpandConstant('{app}\server\index.mjs') + '"' + ConfigArgument, Output);
  if Code <> 0 then
    SuppressibleMsgBox('Đã cài add-in nhưng chưa thêm được BIMTECH MCP vào cấu hình Claude Desktop:' + #13#10 + Output + #13#10 +
      'Thêm tay theo hướng dẫn trong ' + ExpandConstant('{app}\README.txt') + '.', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Output: String;
begin
  if CurUninstallStep = usUninstall then
    RunClaudeConfig('-Action remove' + ConfigArgument, Output);
end;
