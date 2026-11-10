#include "build\Prerequisites.iss"
#define TestAppId "EasyHiking-Installed-0EE343F3-212F-4B44-ABBF-7FF0B80AB4E7"
#define ProductName "易行徒步规划制作器"
#ifndef ProductVersion
  #define ProductVersion "1.8.0"
#endif
#ifndef PayloadBase
  #define PayloadBase "build\payload"
#endif
#ifndef ManualBase
  #define ManualBase "..\docs"
#endif
[Setup]
AppId={#TestAppId}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppVerName={#ProductName} V{#ProductVersion}
VersionInfoVersion={#ProductVersion}.0
VersionInfoDescription=易行徒步规划制作器Program Files安装包
DefaultDirName={autopf}\EasyHiking
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
#if TargetLegacy
MinVersion=6.1sp1
#else
MinVersion=6.1sp1
#endif
WizardStyle=modern dynamic
WizardSizePercent=115
SetupIconFile=..\src\HikingGuide.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\易行徒步规划制作器.exe
OutputDir=output
#if TargetLegacy
OutputBaseFilename=EasyHiking-{#ProductVersion}-Windows7-x64-Setup
#else
OutputBaseFilename=EasyHiking-{#ProductVersion}-Windows10-11-x64-Setup
#endif
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=2
DiskSpanning=no
UsePreviousAppDir=yes
UsePreviousGroup=no
CloseApplications=no
RestartApplications=no
SetupLogging=yes
DisableWelcomePage=no
Uninstallable=yes
ChangesAssociations=no
[Languages]
Name: "chinesesimp"; MessagesFile: "build\ChineseSimplified.isl"
[Messages]
#if !TargetLegacy
WindowsVersionNotSupported=此安装包需要64位x64 Windows 10（1607/14393或更高版本）或Windows 11。请核对系统版本和位数，并取消兼容模式后重试。
#endif
[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
[Dirs]
; 按用户档案设置同盘个人目录权限，应用使用普通权限运行。
[Files]
#if TargetLegacy
Source: "{#PayloadBase}\PayloadWin7\*"; DestDir: "{app}"; Excludes: "EasyHiking.Installation.ini,docs\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PrereqRoot}\VC_redist-2019-x64.exe"; Flags: dontcopy noencryption
#else
Source: "{#PayloadBase}\PayloadModern\*"; DestDir: "{app}"; Excludes: "EasyHiking.Installation.ini,docs\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PrereqRoot}\windowsdesktop-runtime-10-win-x64.exe"; Flags: dontcopy noencryption
Source: "{#PrereqRoot}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Flags: dontcopy noencryption
#endif
Source: "build\EasyHiking.Installation.ini"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ManualBase}\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\易行徒步规划制作器.exe"; WorkingDir: "{app}"
Name: "{group}\操作说明"; Filename: "{app}\docs\易行徒步规划制作器-操作说明.html"
Name: "{group}\卸载易行徒步规划制作器"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#ProductName}"; Filename: "{app}\易行徒步规划制作器.exe"; WorkingDir: "{app}"; Tasks: desktopicon
[Run]
Filename: "{app}\易行徒步规划制作器.exe"; Description: "启动软件并进行首次数据设置"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent runasoriginaluser; Check: PrerequisitesReady
Filename: "{app}\docs\易行徒步规划制作器-操作说明.html"; Description: "阅读操作说明"; Flags: postinstall shellexec skipifsilent unchecked
[UninstallDelete]
; 用户配置只由下方显式选择后的归属检查与清理处理，默认保留。
[InstallDelete]
Type: files; Name: "{commondesktop}\易行徒步规划制作器（安装版）.lnk"
Type: files; Name: "{commonprograms}\易行徒步规划制作器（安装版）\易行徒步规划制作器（安装版）.lnk"
Type: files; Name: "{commonprograms}\易行徒步规划制作器（安装版）\操作说明.lnk"
Type: files; Name: "{commonprograms}\易行徒步规划制作器（安装版）\卸载安装版.lnk"
Type: dirifempty; Name: "{commonprograms}\易行徒步规划制作器（安装版）"
[Code]
var
  RuntimePage: TWizardPage;
  DotnetStatus, WebViewStatus, RuntimeNote: TNewStaticText;
  InstallMissing: TNewCheckBox;
  NeedReboot: Boolean;
  PreviousUserDataDrive: String;

#include "TargetSystem.iss"

function UseLegacyBuild: Boolean;
begin Result := {#TargetLegacy} = 1; end;
function UseModernBuild: Boolean;
begin Result := not UseLegacyBuild; end;

function VcRuntimeAt(Root: Integer): Boolean;
var Installed, Major, Minor: Cardinal;
begin
  Result := RegQueryDWordValue(Root,'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64','Installed',Installed) and (Installed=1) and
    RegQueryDWordValue(Root,'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64','Major',Major) and
    RegQueryDWordValue(Root,'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64','Minor',Minor) and (Major=14) and (Minor>=29);
end;
function VcRuntimeReady: Boolean;
begin Result := VcRuntimeAt(HKLM32) or VcRuntimeAt(HKLM64);end;

function Version10Folder(const Root: String): Boolean;
var F: TFindRec; Base: String;
begin
  Result := False;
  Base := AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\';
  if not FileExists(AddBackslash(Root) + 'dotnet.exe') then Exit;
  if FindFirst(Base + '10.0.*', F) then begin
    try
      repeat
        if ((F.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           FileExists(Base + F.Name + '\PresentationFramework.dll') and
           FileExists(AddBackslash(Root) + 'shared\Microsoft.NETCore.App\' + F.Name + '\System.Private.CoreLib.dll') then begin Result := True; Break; end;
      until not FindNext(F);
    finally FindClose(F); end;
  end;
end;

function DotnetReady: Boolean;
var Root: String;
begin
  if UseLegacyBuild then begin Result := True; Exit; end;
  Result := Version10Folder(ExpandConstant('{commonpf64}\dotnet'));
  if not Result then begin
    if RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) then Result := Version10Folder(Root);
  end;
  if not Result then Result := Version10Folder(GetEnv('DOTNET_ROOT_X64'));
  if not Result then Result := Version10Folder(GetEnv('DOTNET_ROOT'));
end;

function ValidWebViewVersion(const Value: String): Boolean;
var Major: Integer; P: Integer;
begin
  P := Pos('.', Value); Major := 0;
  if P > 1 then Major := StrToIntDef(Copy(Value, 1, P-1), 0);
  Result := Major >= 120;
end;

function WebViewReady: Boolean;
var V: String;
begin
  if UseLegacyBuild then begin Result := True; Exit; end;
  Result := False;
  if RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) then Result := ValidWebViewVersion(V);
  if not Result then if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) then Result := ValidWebViewVersion(V);
  if not Result then if RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) then Result := ValidWebViewVersion(V);
end;

function PrerequisitesReady: Boolean;
begin Result := DotnetReady and WebViewReady and not NeedReboot and (UseModernBuild or VcRuntimeReady); end;

procedure RefreshRuntimeStatus;
begin
  if UseLegacyBuild then begin
    DotnetStatus.Caption := 'Windows 7版：私有.NET 6与WebView2 109随应用提供';
    if VcRuntimeReady then WebViewStatus.Caption := 'Visual C++ x64：已满足'
    else WebViewStatus.Caption := 'Visual C++ 2019 x64：需要安装';
    Exit;
  end;
  if DotnetReady then DotnetStatus.Caption := '.NET 10 桌面运行时 x64：已满足，将跳过安装'
  else DotnetStatus.Caption := '.NET 10 桌面运行时 x64：未检测到，需要安装';
  if WebViewReady then WebViewStatus.Caption := 'WebView2 运行时：已满足，将跳过安装'
  else WebViewStatus.Caption := 'WebView2 运行时：未满足，需要安装';
end;

procedure RefreshClick(Sender: TObject);
begin RefreshRuntimeStatus; end;

procedure DotnetLinkClick(Sender: TObject);
var E: Integer;
begin
  if UseLegacyBuild then ShellExec('open','https://learn.microsoft.com/zh-cn/dotnet/core/install/windows#dependencies','', '',SW_SHOWNORMAL,ewNoWait,E)
  else ShellExec('open', 'https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, E);
end;

procedure WebViewLinkClick(Sender: TObject);
var E: Integer;
begin ShellExec('open', 'https://developer.microsoft.com/zh-cn/microsoft-edge/webview2/', '', '', SW_SHOWNORMAL, ewNoWait, E); end;

function StatusText(Value: Boolean): String;
begin if Value then Result := 'present' else Result := 'missing'; end;

function InitializeSetup: Boolean;
var Report, PreviousDirectory, SystemError: String; Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Log('系统检测：'+IntToStr(Version.Major)+'.'+IntToStr(Version.Minor)+'.'+IntToStr(Version.Build)+'；64位='+IntToStr(Ord(IsWin64)));
  SystemError := TargetSystemError(UseLegacyBuild,Version.Major,Version.Minor,Version.Build,Version.ServicePackMajor,IsWin64);
  if SystemError<>'' then begin SuppressibleMsgBox(SystemError,mbError,MB_OK,IDOK);Result:=False;Exit;end;
  NeedReboot := False;
  PreviousUserDataDrive := '';
  if RegQueryStringValue(HKLM64,'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#TestAppId}_is1','InstallLocation',PreviousDirectory) then
    if GetIniString('Installation','DataMode','',AddBackslash(PreviousDirectory)+'EasyHiking.Installation.ini')='InstallDriveCurrentUser' then
      PreviousUserDataDrive:=ExtractFileDrive(PreviousDirectory);
  Report := ExpandConstant('{param:PREREQREPORT|}');
  if Report <> '' then SaveStringToFile(Report, 'dotnet=' + StatusText(DotnetReady) + #13#10 + 'webview=' + StatusText(WebViewReady) + #13#10 + 'offline_prerequisites=embedded' + #13#10, False);
  Result := ExpandConstant('{param:CHECKONLY|0}') <> '1';
end;

procedure InitializeWizard;
var Button: TNewButton;
begin
  WizardForm.WelcomeLabel2.Caption := '默认安装到C:\Program Files\EasyHiking，可修改到其它硬盘或路径。安装时需要管理员权限，日常运行不需要。' + #13#10#13#10 +
    '配置、收藏、缓存和BRouter数据沿用Users\用户名\AppData\Local\EasyHiking目录结构，只把盘符改为所选程序安装盘。卸载时可选择保留或删除。' + #13#10#13#10 +
    '本包仅包含所选系统版本的程序和必要环境，Windows 7与Windows 10/11安装包独立提供。包含当前在线图源，不包含DEM、已下载路由数据和个人数据。';
  RuntimePage := CreateCustomPage(wpSelectDir, '检查运行环境', '只检测本系统版本所需环境；已满足的项目直接跳过。');
  DotnetStatus := TNewStaticText.Create(RuntimePage); DotnetStatus.Parent := RuntimePage.Surface;
  DotnetStatus.SetBounds(0, ScaleY(12), RuntimePage.SurfaceWidth, ScaleY(24));
  WebViewStatus := TNewStaticText.Create(RuntimePage); WebViewStatus.Parent := RuntimePage.Surface;
  WebViewStatus.SetBounds(0, ScaleY(45), RuntimePage.SurfaceWidth, ScaleY(24));
  InstallMissing := TNewCheckBox.Create(RuntimePage); InstallMissing.Parent := RuntimePage.Surface;
  InstallMissing.SetBounds(0, ScaleY(84), RuntimePage.SurfaceWidth, ScaleY(30));
  InstallMissing.Caption := '自动安装缺少的运行环境（已内置离线安装程序，建议勾选）'; InstallMissing.Checked := True;
  RuntimeNote := TNewStaticText.Create(RuntimePage); RuntimeNote.Parent := RuntimePage.Surface;
  RuntimeNote.SetBounds(0, ScaleY(128), RuntimePage.SurfaceWidth, ScaleY(100)); RuntimeNote.WordWrap := True;
  RuntimeNote.Caption := '缺少.NET时，微软安装程序可能请求管理员权限；可取消后自行安装。未满足环境时不会自动启动软件。' + #13#10#13#10 +
    '程序与配置位于同一盘符，例如程序D:\Program Files\EasyHiking，配置D:\Users\用户名\AppData\Local\EasyHiking。首次切换会复制旧配置，原目录保留。Java和GDAL随软件提供。';
  Button := TNewButton.Create(RuntimePage); Button.Parent := RuntimePage.Surface;
  Button.SetBounds(0, ScaleY(245), ScaleX(155), ScaleY(28)); Button.Caption := '.NET官方下载'; Button.OnClick := @DotnetLinkClick;
  if UseLegacyBuild then Button.Caption := '.NET与系统补丁说明';
  Button := TNewButton.Create(RuntimePage); Button.Parent := RuntimePage.Surface;
  Button.SetBounds(ScaleX(167), ScaleY(245), ScaleX(155), ScaleY(28)); Button.Caption := 'WebView2官方下载'; Button.OnClick := @WebViewLinkClick;
  Button := TNewButton.Create(RuntimePage); Button.Parent := RuntimePage.Surface;
  Button.SetBounds(ScaleX(334), ScaleY(245), ScaleX(100), ScaleY(28)); Button.Caption := '重新检测'; Button.OnClick := @RefreshClick;
  RefreshRuntimeStatus;
end;

function DirectoryIsEmpty(const Path: String): Boolean;
var F: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Path) + '*', F) then begin
    try repeat
      if (F.Name <> '.') and (F.Name <> '..') then begin Result := False; Break; end;
    until not FindNext(F);
    finally FindClose(F); end;
  end;
end;

function DestinationError: String;
var P: String;
begin
    Result := '';
    P := RemoveBackslashUnlessRoot(ExpandFileName(WizardDirValue));
    if (Length(ExtractFileDrive(P)) <> 2) or (P[2] <> ':') then begin Result := '请选择本地盘符下的独立目录，以便将用户配置放到相同盘符。'; Exit; end;
    if (Length(P) <= 3) or (CompareText(P, ExpandConstant('{win}')) = 0) or
       (Pos(Lowercase(AddBackslash(ExpandConstant('{win}'))), Lowercase(AddBackslash(P))) = 1) or
       (CompareText(P, ExpandConstant('{commonpf64}')) = 0) or
       (CompareText(P, ExpandConstant('{commonpf32}')) = 0) then begin
      Result := '请选择独立的软件子目录，例如C:\Program Files\EasyHiking或D:\Program Files\EasyHiking。不能直接使用磁盘根目录、Windows目录或Program Files根目录。'; Exit;
    end;
    if DirExists(P) and not DirectoryIsEmpty(P) and
       (GetIniString('Installation', 'AppId', '', P + '\EasyHiking.Installation.ini') <> '{#TestAppId}') then begin
      Result := '此目录已有其他文件，且不是本软件创建的目录。为保护现用版及项目，请选择新的空目录。'; Exit;
    end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var ErrorText: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then begin
    ErrorText := DestinationError;
    if ErrorText <> '' then begin SuppressibleMsgBox(ErrorText, mbError, MB_OK, IDOK); Result := False; Exit; end;
    RefreshRuntimeStatus;
  end;
end;

function InstallRuntime(const FileName, ExpectedHash, Arguments: String; Elevate: Boolean; var ErrorText: String): Boolean;
var E: Integer; OK: Boolean; Verb: String;
begin
  Result := False; ExtractTemporaryFile(FileName);
  if CompareText(GetSHA256OfFile(ExpandConstant('{tmp}\') + FileName), ExpectedHash) <> 0 then begin ErrorText := '运行环境安装程序校验失败，请重新获取安装包。'; Exit; end;
  Verb := 'open'; if Elevate then Verb := 'runas';
  OK := ShellExec(Verb, ExpandConstant('{tmp}\') + FileName, Arguments, '', SW_SHOWNORMAL, ewWaitUntilTerminated, E);
  if not OK then begin ErrorText := '未能运行必要环境安装程序，可能是权限请求被取消。可以返回取消自动安装，再通过官方下载页面手动安装。'; Exit; end;
  if E = 3010 then begin NeedReboot := True; Result := True; end
  else if E = 0 then Result := True
  else ErrorText := '运行环境安装失败，退出代码：' + IntToStr(E) + '。请通过官方下载页面安装后重试。';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := DestinationError;
  if Result <> '' then Exit;
  if InstallMissing.Checked then begin
    if UseLegacyBuild and not VcRuntimeReady then begin
      if not InstallRuntime('VC_redist-2019-x64.exe','5D9999036F2B3A930F83B7FE3E2186B12E79AE7C007D538F52E3582E986A37C3','/install /quiet /norestart',True,Result) then Exit;
    end;
#if !TargetLegacy
    if not DotnetReady then begin
      if not InstallRuntime('windowsdesktop-runtime-10-win-x64.exe', '{#DotNetSha256}', '/install /quiet /norestart', True, Result) then Exit;
      if not DotnetReady and not NeedReboot then begin Result := '.NET桌面运行时安装后仍未满足要求，请手动安装后重新检测。'; Exit; end;
    end;
    if not WebViewReady then begin
      if not InstallRuntime('MicrosoftEdgeWebView2RuntimeInstallerX64.exe', '{#WebViewSha256}', '/silent /install', False, Result) then Exit;
      if not WebViewReady and not NeedReboot then begin Result := 'WebView2运行时安装后仍未满足要求，请手动安装后重新检测。'; Exit; end;
    end;
#endif
  end;
  NeedsRestart := NeedReboot;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = RuntimePage.ID then RefreshRuntimeStatus;
  if CurPageID = wpFinished then begin
    if not PrerequisitesReady then WizardForm.FinishedLabel.Caption := '程序文件已安装。运行环境尚未满足或需要重启，请通过微软官方下载页面补齐后，再从软件快捷方式启动。'
    else WizardForm.FinishedLabel.Caption := '安装完成。首次启动会依次打开DEM挂载和BRouter导入/下载设置。等高线默认开启，阴影叠加为30%。用户配置沿用原目录结构，盘符跟随程序安装盘。卸载时默认保留，也可勾选删除。';
  end;
end;

#include "UserDataPolicy.iss"
procedure CurStepChanged(Step: TSetupStep);
begin
  if Step=ssPostInstall then begin
    PrepareUserDataPermissions;
    if PreviousUserDataDrive<>'' then SetIniString('Installation','PreviousUserDataDrive',PreviousUserDataDrive,ExpandConstant('{app}\EasyHiking.Installation.ini'));
  end;
end;
function InitializeUninstall: Boolean;
begin Result := ChooseUserDataRemoval;end;
procedure CurUninstallStepChanged(Step: TUninstallStep);
begin if Step=usPostUninstall then RemoveSelectedUserData;end;



