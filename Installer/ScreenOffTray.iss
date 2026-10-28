; Compile only with pinned Inno Setup 7.1.0. Main build supplies /DSourceRoot=<allowlisted payload>.
#ifndef SourceRoot
  #define SourceRoot AddBackslash(SourcePath) + "..\打包暂存\payload"
#endif

[Setup]
AppId={{F8732557-3873-4D44-ABF7-D267AF16E126}
AppName=ScreenOffTray
AppVersion=0.1.9
AppVerName=ScreenOffTray 0.1.9
VersionInfoVersion=0.1.9.0
DefaultDirName={autopf64}\ScreenOffTray
DefaultGroupName=ScreenOffTray
DisableProgramGroupPage=yes
PrivilegesRequired=admin
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
AppMutex=Local\ScreenOffTray
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
AlwaysRestart=no
AllowCancelDuringInstall=no
OutputDir=..\安装包
OutputBaseFilename=ScreenOffTray-0.1.9-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\ScreenOffTray.exe
UninstallFilesDir={app}\卸载
SetupLogging=yes
; Publisher is intentionally omitted until the owner supplies actual publisher/signing identity.
; No startup task, HKCU startup registry or user-settings deletion: migration belongs to app.

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; Exact package allowlist. No recursive file collection or system/runtime/tool payloads.
Source: "{#SourceRoot}\ScreenOffTray.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\ScreenOffTray.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\ScreenOffTray.DriverSetup.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\Driver\MttVDD.inf"; DestDir: "{app}\Driver"; Flags: ignoreversion
Source: "{#SourceRoot}\Driver\MttVDD.dll"; DestDir: "{app}\Driver"; Flags: ignoreversion
Source: "{#SourceRoot}\Driver\mttvdd.cat"; DestDir: "{app}\Driver"; Flags: ignoreversion
Source: "{#SourceRoot}\帮助.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\配置模板\默认设置说明.json"; DestDir: "{app}\配置模板"; Flags: ignoreversion
Source: "{#SourceRoot}\Licenses\Virtual-Display-Driver-MIT.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "{#SourceRoot}\Licenses\第三方材料说明.md"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "{#SourceRoot}\Licenses\Inno-Setup-License.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\ScreenOffTray"; Filename: "{app}\ScreenOffTray.exe"
Name: "{group}\使用帮助"; Filename: "{app}\帮助.html"

[Code]
var
  HelperCode: Integer;
  DriverReady: Boolean;

procedure FinishLaunch; forward;

function HasNet48: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64,
    'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and
    (Release >= 528040);
end;

function InitializeSetup: Boolean;
begin
  Result := HasNet48;
  if not Result then
    MsgBox('需要预先离线安装 .NET Framework 4.8。安装程序不会联网下载依赖，请安装完成后重试。', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Started: Boolean;
begin
  if CurStep = ssPostInstall then begin
    DriverReady := False;
    Started := Exec(ExpandConstant('{app}\ScreenOffTray.DriverSetup.exe'),
      'install --bundle-dir "' + ExpandConstant('{app}\Driver') + '"',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, HelperCode);
    if not Started then
      RaiseException('无法启动驱动安装助手。请保留安装日志和 ProgramData\ScreenOffTray\DriverSetup 中的恢复材料。');
    if (HelperCode <> 0) and (HelperCode <> 3010) then
      RaiseException('驱动安装失败（退出码 ' + IntToStr(HelperCode) + '）。未启动关屏功能。请保留 ProgramData\ScreenOffTray\DriverSetup 中的收据、配置和恢复材料。');
    DriverReady := HelperCode = 0;
    if HelperCode = 3010 then
      MsgBox('驱动报告需要重新启动。安装程序不会自动重启，也不会现在启动关屏功能。请自行重启后重新运行安装程序完成可用性检查。', mbInformation, MB_OK);
  end;
  if CurStep = ssDone then FinishLaunch;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and (HelperCode = 3010) then
    WizardForm.FinishedLabel.Caption := '驱动需要您手动重启。安装程序不会自动重启。重启后重新运行安装程序完成可用性检查。';
  if (CurPageID = wpFinished) and DriverReady then begin
    WizardForm.FinishedLabel.Caption := '程序文件已安装。接下来将检查显示条件，并引导您一键完成关屏配置。' + #13#10 + #13#10 +
      '点击“完成并配置”继续；关屏是否可用，以随后配置窗口的检查结果为准。';
    WizardForm.NextButton.Caption := '完成并配置';
  end;
end;

procedure FinishLaunch;
var
  Code: Integer;
begin
  if DriverReady and (not WizardSilent) then begin
    if not ExecAsOriginalUser(ExpandConstant('{app}\ScreenOffTray.exe'), '--setup',
      ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, Code) then
      MsgBox('安装文件已复制，但 ScreenOffTray 未能启动，关屏尚未启用。请从开始菜单手动打开 ScreenOffTray；若仍无法启动，请保留安装日志后检查错误，不要重复安装其他显示驱动。', mbError, MB_OK);
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  if HelperCode = 3010 then Result := 3010 else Result := 0;
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  if CheckForMutexes('Local\ScreenOffTray') then begin
    MsgBox('请先从托盘菜单退出 ScreenOffTray，再卸载。卸载程序不会强制结束进程。', mbError, MB_OK);
    Exit;
  end;
  if not HasNet48 then begin
    MsgBox('驱动卸载助手需要 .NET Framework 4.8。已停止卸载并保留文件。', mbError, MB_OK);
    Exit;
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Started: Boolean;
  Code: Integer;
  StartupValue: String;
  InstalledExe: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  if CheckForMutexes('Local\ScreenOffTray') then
    RaiseException('ScreenOffTray 仍在运行。已停止卸载；请从托盘退出后重试。');
  Started := Exec(ExpandConstant('{app}\ScreenOffTray.DriverSetup.exe'),
    'uninstall --bundle-dir "' + ExpandConstant('{app}\Driver') + '"',
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code);
  if not Started then begin
    RaiseException('无法启动驱动卸载助手。已停止卸载并保留程序、驱动和恢复材料。');
  end;
  if Code = 3010 then begin
    RaiseException('驱动移除报告需要重启。请手动重启后再次卸载；本次已停止删除文件并保留恢复材料。');
  end;
  if Code <> 0 then begin
    RaiseException('驱动卸载检查失败（退出码 ' + IntToStr(Code) + '）。可能存在所有权冲突或移除失败。已停止卸载并保留恢复材料。');
  end;
  { Only remove the calling account's exact command for this installation.
    Never enumerate or edit another user's hive, nor delete a changed command. }
  InstalledExe := ExpandConstant('{app}\ScreenOffTray.exe');
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
    'ScreenOffTray', StartupValue) then begin
    if (CompareText(StartupValue, '"' + InstalledExe + '"') = 0) or
      (CompareText(StartupValue, InstalledExe) = 0) then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ScreenOffTray');
  end;
end;
