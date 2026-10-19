; AllAgent.NET Windows installer (Inno Setup 6).
;
; The payload is the self-contained win-x64 publish of src/AllAgent.NET.Terminal:
; it already carries web/, gui/, browser/, computer/, sandbox/, powershell/ and
; builtin-plugins/, so the installer never needs a .NET runtime on the machine.
; Build with:  tools\build-installer.ps1   (see docs/windows-installer.zh-CN.md)

#define AppName "AllAgent.NET"
#define AppPublisher "AllAgent.NET"
#define AppUrl "https://github.com/luolangaga/AllAgent.NET"
#define AppExeName "allagent.exe"
#define AppGuid "{8F1B4B2E-6C71-4E52-9D0A-2B7C5F3A91D4}"

; Version, payload and output can be overridden from the command line:
;   ISCC.exe /DAppVersion=1.2.3 /DPayloadDir=<abs> /DOutputDir=<abs> AllAgent.NET.iss
#ifndef AppVersion
  #define AppVersion "1.0.7"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\artifacts\installer\payload"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{#AppGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\AllAgent.NET.Terminal\Assets\allagent.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-user by default: no UAC prompt and an install directory the user can write.
; The dialog still offers an all-users install into Program Files.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UsePreviousLanguage=no
UsePreviousSetupType=no
UsePreviousTasks=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.GroupDesktop=桌面快捷方式：
chinesesimplified.GroupOptions=其他选项：
chinesesimplified.TaskTerminal=创建桌面快捷方式：AllAgent.NET 终端（命令行聊天）
chinesesimplified.TaskDesktop=创建桌面快捷方式：AllAgent.NET 桌面端（窗口工作台）
chinesesimplified.TaskPath=将 allagent 命令加入 PATH（在任意终端直接运行）
chinesesimplified.TaskRuntime=补齐缺失的运行环境（WebView2 运行时、Python 3.13、ripgrep）
chinesesimplified.TaskVerify=安装完成后在控制台运行环境自检
chinesesimplified.IconTerminal=命令行聊天，在任意项目目录里直接使用
chinesesimplified.IconDesktop=原生窗口工作台，与终端共享工作区与会话
chinesesimplified.IconDoctor=检查 Python、WebView2、ripgrep 与沙箱执行环境
chinesesimplified.RunTerminal=运行 AllAgent.NET 终端
chinesesimplified.RunDesktop=运行 AllAgent.NET 桌面端
chinesesimplified.RunVerify=运行环境自检
chinesesimplified.RuntimeTitle=运行环境
chinesesimplified.RuntimeProgress=正在补齐运行环境：%1
chinesesimplified.RuntimeDownloading=正在下载 WebView2 运行时...
chinesesimplified.RuntimeInstalling=正在安装 %1...
chinesesimplified.RuntimeFailed=以下组件未能自动安装：%n%n%1%n%n可以稍后在控制台中运行 `allagent doctor --install` 手动补齐。
chinesesimplified.RuntimeNone=运行环境已齐全，无需补齐。
chinesesimplified.RuntimeDone=运行环境补齐完成：%1
chinesesimplified.UninstallKeepData=是否同时删除配置与凭据？%n%n它们保存在 %1（模型、API Key、插件、共享 Python 环境）。%n%n选择“否”会在卸载后保留这些设置，重新安装时可以直接继续使用。
english.GroupDesktop=Desktop shortcuts:
english.GroupOptions=Other options:
english.TaskTerminal=Create a desktop shortcut: AllAgent.NET Terminal (command line chat)
english.TaskDesktop=Create a desktop shortcut: AllAgent.NET Desktop (window workbench)
english.TaskPath=Add the allagent command to PATH (run it from any terminal)
english.TaskRuntime=Install missing runtime components (WebView2 Runtime, Python 3.13, ripgrep)
english.TaskVerify=Run the environment check in a console after installation
english.IconTerminal=Command line chat, usable directly in any project directory
english.IconDesktop=Native window workbench sharing workspace and sessions with the terminal
english.IconDoctor=Checks Python, WebView2, ripgrep and the sandbox execution environment
english.RunTerminal=Run AllAgent.NET Terminal
english.RunDesktop=Run AllAgent.NET Desktop
english.RunVerify=Run the environment check
english.RuntimeTitle=Runtime components
english.RuntimeProgress=Installing missing runtime components: %1
english.RuntimeDownloading=Downloading the WebView2 Runtime...
english.RuntimeInstalling=Installing %1...
english.RuntimeFailed=These components could not be installed automatically:%n%n%1%n%nRun `allagent doctor --install` in a terminal to finish the setup.
english.RuntimeNone=Every runtime component is already present.
english.RuntimeDone=Runtime components installed: %1
english.UninstallKeepData=Also delete configuration and credentials?%n%nThey live in %1 (models, API keys, plugins, shared Python environment).%n%nAnswer No to keep them for a later reinstall.

[Tasks]
Name: "desktopicon_terminal"; Description: "{cm:TaskTerminal}"; GroupDescription: "{cm:GroupDesktop}"
Name: "desktopicon_desktop"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:GroupDesktop}"
Name: "addtopath"; Description: "{cm:TaskPath}"; GroupDescription: "{cm:GroupOptions}"
Name: "runtime"; Description: "{cm:TaskRuntime}"; GroupDescription: "{cm:GroupOptions}"
Name: "verify"; Description: "{cm:TaskVerify}"; GroupDescription: "{cm:GroupOptions}"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName} 终端"; Filename: "{app}\{#AppExeName}"; Parameters: "chat"; WorkingDir: "{%USERPROFILE}"; Comment: "{cm:IconTerminal}"
Name: "{autoprograms}\{#AppName} 桌面端"; Filename: "{app}\{#AppExeName}"; Parameters: "-guimode"; WorkingDir: "{%USERPROFILE}"; Comment: "{cm:IconDesktop}"
Name: "{autoprograms}\{#AppName} 环境自检"; Filename: "{cmd}"; Parameters: "/k """"{app}\{#AppExeName}"" doctor"""; WorkingDir: "{%USERPROFILE}"; Comment: "{cm:IconDoctor}"
Name: "{autoprograms}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName} 终端"; Filename: "{app}\{#AppExeName}"; Parameters: "chat"; WorkingDir: "{%USERPROFILE}"; Comment: "{cm:IconTerminal}"; Tasks: desktopicon_terminal
Name: "{autodesktop}\{#AppName} 桌面端"; Filename: "{app}\{#AppExeName}"; Parameters: "-guimode"; WorkingDir: "{%USERPROFILE}"; Comment: "{cm:IconDesktop}"; Tasks: desktopicon_desktop

[Registry]
; An all-users install belongs on the machine PATH, a per-user install on the user's own.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NeedsAddPath('{app}') and IsAdminInstallMode; Tasks: addtopath; Flags: preservestringtype
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NeedsAddPath('{app}') and not IsAdminInstallMode; Tasks: addtopath; Flags: preservestringtype

[Run]
Filename: "{cmd}"; Parameters: "/k """"{app}\{#AppExeName}"" doctor"""; WorkingDir: "{%USERPROFILE}"; Description: "{cm:RunVerify}"; Tasks: verify; Flags: postinstall nowait skipifsilent
Filename: "{app}\{#AppExeName}"; Parameters: "chat"; WorkingDir: "{%USERPROFILE}"; Description: "{cm:RunTerminal}"; Flags: postinstall nowait skipifsilent unchecked
Filename: "{app}\{#AppExeName}"; Parameters: "-guimode"; WorkingDir: "{%USERPROFILE}"; Description: "{cm:RunDesktop}"; Flags: postinstall nowait skipifsilent unchecked

[Code]
const
  WebView2Client = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2BootstrapperUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  PythonPackageId = 'Python.Python.3.13';
  RipgrepPackageId = 'BurntSushi.ripgrep.MSVC';

var
  RuntimeFailures: String;
  RuntimeInstalled: String;

{ ------------------------------------------------------------------ helpers }

function RunHidden(const Filename, Parameters: String; var ResultCode: Integer): Boolean;
begin
  Result := Exec(Filename, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function FindOnPath(const ExecutableName: String): String;
begin
  Result := FileSearch(ExecutableName, GetEnv('PATH'));
end;

procedure Note(const Name: String);
begin
  if RuntimeInstalled <> '' then
    RuntimeInstalled := RuntimeInstalled + '、';
  RuntimeInstalled := RuntimeInstalled + Name;
end;

procedure Fail(const Name: String);
begin
  RuntimeFailures := RuntimeFailures + Name + #13#10;
end;

{ ----------------------------------------------------- dependency detection }

function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(HKLM32, WebView2Client, 'pv', Version) or
            RegQueryStringValue(HKLM, WebView2Client, 'pv', Version) or
            RegQueryStringValue(HKCU, WebView2Client, 'pv', Version);
  Result := Result and (Version <> '');
end;

function PythonInstalled(): Boolean;
var
  Python: String;
  ResultCode: Integer;
begin
  Result := False;
  Python := FindOnPath('python.exe');
  if Python = '' then
    Exit;
  { The Microsoft Store placeholder is found by FileSearch too; a probe with -I -S
    only succeeds for a real interpreter inside the supported 3.10-3.14 range. }
  if not RunHidden(Python, '-I -S -c "import sys; sys.exit(0 if (3,10) <= sys.version_info[:2] <= (3,14) else 1)"', ResultCode) then
    Exit;
  Result := ResultCode = 0;
end;

function RipgrepInstalled(): Boolean;
begin
  Result := FindOnPath('rg.exe') <> '';
end;

{ -------------------------------------------------- dependency installation }

function InstallWebView2(): Boolean;
var
  Bootstrapper: String;
  Script: String;
  ResultCode: Integer;
begin
  Result := False;
  Bootstrapper := ExpandConstant('{tmp}\MicrosoftEdgeWebView2Setup.exe');
  Script := '-NoProfile -ExecutionPolicy Bypass -Command "' +
    '$ProgressPreference = ''SilentlyContinue''; ' +
    'Invoke-WebRequest -Uri ''' + WebView2BootstrapperUrl + ''' -OutFile ''' + Bootstrapper + ''' -UseBasicParsing; ' +
    '$signature = Get-AuthenticodeSignature ''' + Bootstrapper + '''; ' +
    'if ($signature.Status -ne ''Valid'' -or $signature.SignerCertificate.Subject -notmatch ''Microsoft Corporation'') { exit 1 }"';
  if not RunHidden(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Script, ResultCode) then
    Exit;
  if ResultCode <> 0 then
    Exit;
  RunHidden(Bootstrapper, '/silent /install', ResultCode);
  Result := WebView2Installed();
end;

function InstallWingetPackage(const PackageId: String): Boolean;
var
  Winget: String;
  ResultCode: Integer;
begin
  Result := False;
  Winget := FindOnPath('winget.exe');
  if Winget = '' then
    Exit;
  RunHidden(Winget, 'install --id ' + PackageId + ' --exact --source winget --silent ' +
    '--accept-source-agreements --accept-package-agreements --disable-interactivity', ResultCode);
  Result := ResultCode = 0;
end;

procedure EnsureRuntime();
var
  Page: TOutputProgressWizardPage;
begin
  RuntimeFailures := '';
  RuntimeInstalled := '';
  Page := CreateOutputProgressPage(CustomMessage('RuntimeTitle'), CustomMessage('RuntimeProgress'));
  Page.SetProgress(0, 3);
  try
    if WebView2Installed() then
    begin
      Page.SetProgress(1, 3);
    end
    else
    begin
      Page.SetText(CustomMessage('RuntimeDownloading'), 'WebView2 Runtime');
      if InstallWebView2() then
        Note('WebView2')
      else
        Fail('WebView2 运行时 / WebView2 Runtime');
      Page.SetProgress(1, 3);
    end;

    Page.SetText('', 'Python 3.13');
    if PythonInstalled() then
    begin end
    else if InstallWingetPackage(PythonPackageId) then
      Note('Python 3.13')
    else
      Fail('Python 3.13');
    Page.SetProgress(2, 3);

    Page.SetText('', 'ripgrep');
    if RipgrepInstalled() then
    begin end
    else if InstallWingetPackage(RipgrepPackageId) then
      Note('ripgrep')
    else
      Fail('ripgrep');
    Page.SetProgress(3, 3);
  finally
    Page.Hide;
  end;
end;

{ ------------------------------------------------------------ PATH handling }

{ The allagent entry belongs in the hive that matches the install mode. }
function PathRootKey(): Integer;
begin
  if IsAdminInstallMode then Result := HKLM else Result := HKCU;
end;

function PathSubKey(): String;
begin
  if IsAdminInstallMode then
    Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  else
    Result := 'Environment';
end;

function NeedsAddPath(Param: String): Boolean;
var
  Current: String;
  Entry: String;
  Rest: String;
  Separator: Integer;
begin
  Result := True;
  if not RegQueryStringValue(PathRootKey(), PathSubKey(), 'Path', Current) then
    Exit;
  Rest := Current;
  while Rest <> '' do
  begin
    Separator := Pos(';', Rest);
    if Separator = 0 then
    begin
      Entry := Rest;
      Rest := '';
    end
    else
    begin
      Entry := Copy(Rest, 1, Separator - 1);
      Delete(Rest, 1, Separator);
    end;
    if CompareText(Trim(Entry), Param) = 0 then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function RemoveFromPathInRoot(RootKey: Integer; const SubKey, Directory: String): Boolean;
var
  Current: String;
  Entry: String;
  Rest: String;
  Remaining: String;
  Separator: Integer;
begin
  Result := False;
  if not RegQueryStringValue(RootKey, SubKey, 'Path', Current) then
    Exit;
  Remaining := '';
  Rest := Current;
  while Rest <> '' do
  begin
    Separator := Pos(';', Rest);
    if Separator = 0 then
    begin
      Entry := Rest;
      Rest := '';
    end
    else
    begin
      Entry := Copy(Rest, 1, Separator - 1);
      Delete(Rest, 1, Separator);
    end;
    if (Trim(Entry) <> '') and (CompareText(Trim(Entry), Directory) <> 0) then
    begin
      if Remaining <> '' then
        Remaining := Remaining + ';';
      Remaining := Remaining + Trim(Entry);
    end;
  end;
  Result := RegWriteExpandStringValue(RootKey, SubKey, 'Path', Remaining);
end;

{ Older builds always wrote the user hive; drop the entry wherever it sits, so an upgrade
  from a per-user install to an all-users one (or the reverse) leaves nothing stale. }
function RemoveFromPath(const Directory: String): Boolean;
begin
  Result := RemoveFromPathInRoot(HKCU, 'Environment', Directory);
  Result := RemoveFromPathInRoot(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', Directory) or Result;
end;

function RemoveFromOtherPath(const Directory: String): Boolean;
begin
  if IsAdminInstallMode then
    Result := RemoveFromPathInRoot(HKCU, 'Environment', Directory)
  else
    Result := RemoveFromPathInRoot(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', Directory);
end;

{ -------------------------------------------------------------- setup steps }

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not WizardIsTaskSelected('runtime') then
    Exit;
  { Runs before the first file is copied, so a failure never leaves a half install. }
  EnsureRuntime();
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;
  if WizardIsTaskSelected('addtopath') then
    RemoveFromOtherPath(ExpandConstant('{app}'));
  if (RuntimeInstalled = '') and (RuntimeFailures = '') then
    Log('Runtime components already present.')
  else
    Log('Runtime components installed: ' + RuntimeInstalled + ' | failures: ' + RuntimeFailures);
  if RuntimeFailures <> '' then
    MsgBox(FmtMessage(CustomMessage('RuntimeFailed'), [RuntimeFailures]), mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SharedDirectory: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  RemoveFromPath(ExpandConstant('{app}'));
  SharedDirectory := ExpandConstant('{%APPDATA}\AllAgent.NET');
  { Defaults to No: keeping credentials is the safe answer for Enter and for /SUPPRESSMSGBOXES. }
  if DirExists(SharedDirectory) and
     (MsgBox(FmtMessage(CustomMessage('UninstallKeepData'), [SharedDirectory]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    DelTree(SharedDirectory, True, True, True);
end;
