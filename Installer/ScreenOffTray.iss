; Compile only with pinned Inno Setup 7.1.0. Main build supplies /DSourceRoot=<allowlisted payload>.
#ifndef SourceRoot
  #define SourceRoot AddBackslash(SourcePath) + "..\打包暂存\payload"
#endif

#ifndef ReleaseVersion
#define ReleaseVersion "0.2.1"
#endif
#ifndef RollbackOnly
#define RollbackOnly "0"
#endif
#ifndef VerifierFile
#define VerifierFile AddBackslash(SourceRoot) + "ScreenOffTray.DriverSetup.exe"
#endif
#ifndef PackageSuffix
#define PackageSuffix "Setup-x64"
#endif

#if FileExists(AddBackslash(SourceRoot) + "ScreenOffTray.Settings.exe")
#define HasSettingsUi "1"
#else
#define HasSettingsUi "0"
#endif
#if (HasSettingsUi == "0") && (RollbackOnly == "0")
#error Normal installer requires the settings UI payload.
#endif

[Setup]
AppId={{F8732557-3873-4D44-ABF7-D267AF16E126}
AppName=ScreenOffTray
AppVersion={#ReleaseVersion}
AppVerName=ScreenOffTray {#ReleaseVersion}
VersionInfoVersion={#ReleaseVersion}
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
OutputBaseFilename=ScreenOffTray-{#ReleaseVersion}-{#PackageSuffix}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic polar hidebevels
WizardImageFile=Assets\wizard-side-light.png
WizardImageFileDynamicDark=Assets\wizard-side-dark.png
WizardSmallImageFile=Assets\wizard-small.png
WizardSmallImageFileDynamicDark=Assets\wizard-small.png
WizardBackImageFile=Assets\wizard-back-light.png
WizardBackImageFileDynamicDark=Assets\wizard-back-dark.png
WizardBackImageOpacity=210
WizardImageOpacity=235
SetupIconFile=..\图标\ScreenOffTray.ico
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
#if HasSettingsUi == "1"
Source: "{#SourceRoot}\ScreenOffTray.Settings.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\ScreenOffTray.Settings.exe.config"; DestDir: "{app}"; Flags: ignoreversion
#endif
Source: "{#SourceRoot}\ScreenOffTray.DriverSetup.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\Driver\MttVDD.inf"; DestDir: "{app}\Driver"; Flags: ignoreversion; Check: IsFreshInstall
Source: "{#SourceRoot}\Driver\MttVDD.dll"; DestDir: "{app}\Driver"; Flags: ignoreversion; Check: IsFreshInstall
Source: "{#SourceRoot}\Driver\mttvdd.cat"; DestDir: "{app}\Driver"; Flags: ignoreversion; Check: IsFreshInstall
Source: "{#SourceRoot}\帮助.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\配置模板\默认设置说明.json"; DestDir: "{app}\配置模板"; Flags: ignoreversion
Source: "{#SourceRoot}\Licenses\Virtual-Display-Driver-MIT.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "{#SourceRoot}\Licenses\第三方材料说明.md"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "{#SourceRoot}\Licenses\Inno-Setup-License.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion
#if FileExists(AddBackslash(SourceRoot) + "Licenses\DisplayConfig-MIT.txt")
Source: "{#SourceRoot}\Licenses\DisplayConfig-MIT.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion
#endif

Source: "{#VerifierFile}"; DestName: "ScreenOffTray.UpgradeCheck.exe"; Flags: dontcopy

[Icons]
Name: "{group}\ScreenOffTray"; Filename: "{app}\ScreenOffTray.exe"
Name: "{group}\使用帮助"; Filename: "{app}\帮助.html"

[Code]
var
  HelperCode: Integer;
  DriverReady: Boolean;
  IsUpgrade: Boolean;
  ResumeFirstInstall: Boolean;
  PreviousDirectory: String;
const
  ProductUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{F8732557-3873-4D44-ABF7-D267AF16E126}_is1';
function IsFreshInstall: Boolean;
begin
  Result := not IsUpgrade;
end;
function VerifyUpgrade: String;
var Code: Integer; VersionMS, VersionLS: Cardinal;
    VerifyMode: String;
begin
  Result := '';
  if ('{#RollbackOnly}' <> '1') and FileExists(AddBackslash(WizardDirValue)+'ScreenOffTray.exe') then begin
    if not GetVersionNumbers(AddBackslash(WizardDirValue)+'ScreenOffTray.exe',VersionMS,VersionLS) then begin
      Result := '无法核实旧程序版本，已停止升级。'; Exit;
    end;
    { Numeric file version 0.2.1.0; the separate rollback package explicitly permits downgrade. }
    if (VersionMS > 2) or ((VersionMS = 2) and (VersionLS > 65536)) then begin
      Result := '已安装版本更高，请使用对应版本安装包。'; Exit;
    end;
  end;
  if ResumeFirstInstall then VerifyMode := '--verify-first-install-pending' else VerifyMode := '--verify-existing';
  if not Exec(ExpandConstant('{tmp}\ScreenOffTray.UpgradeCheck.exe'),
    VerifyMode + ' --app-dir "' + WizardDirValue + '"', ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := '无法启动只读升级核验。程序文件尚未覆盖。'
  else if Code <> 0 then
    Result := '现有程序或恢复守护仍在运行，或驱动/收据核验未通过（' + IntToStr(Code) + '）。升级不会安装或修复驱动。若首次安装要求重启，请重启后以 /RESUMEFIRSTINSTALL=1 重新运行本安装包，明确继续该次首次安装。';
end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsUpgrade then begin
    if CompareText(RemoveBackslashUnlessRoot(WizardDirValue), RemoveBackslashUnlessRoot(PreviousDirectory)) <> 0 then begin
      Result := '升级必须沿用已登记的安装目录。'; Exit;
    end;
    ExtractTemporaryFile('ScreenOffTray.UpgradeCheck.exe');
    Result := VerifyUpgrade;
  end else if DirExists(WizardDirValue) and
    (FileExists(AddBackslash(WizardDirValue)+'ScreenOffTray.exe') or FileExists(AddBackslash(WizardDirValue)+'ScreenOffTray.DriverSetup.exe')) then
    Result := '发现未登记的旧程序。已停止，避免误入首次驱动安装。';
end;

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
  IsUpgrade := RegKeyExists(HKLM64, ProductUninstallKey);
  ResumeFirstInstall := ExpandConstant('{param:RESUMEFIRSTINSTALL|0}') = '1';
  if ResumeFirstInstall and ((not IsUpgrade) or ('{#RollbackOnly}' = '1') or WizardSilent) then begin
    Result := False; MsgBox('首次安装续跑仅适用于已有待重启安装，且不能静默或通过回退包执行。', mbError, MB_OK); Exit;
  end;
  if ResumeFirstInstall and (MsgBox('将继续重启前未完成的首次驱动安装，仅核实为本产品创建的待完成设备时才继续。这不是普通程序升级。是否继续？', mbConfirmation, MB_YESNO) <> IDYES) then begin
    Result := False; Exit;
  end;
  if IsUpgrade then begin
    if not RegQueryStringValue(HKLM64, ProductUninstallKey, 'Inno Setup: App Path', PreviousDirectory) or (PreviousDirectory = '') then begin
      Result := False; MsgBox('安装记录不完整。已停止升级，未修改文件或驱动。', mbError, MB_OK); Exit;
    end;
  end;
  if ('{#RollbackOnly}' = '1') and (not IsUpgrade) then begin
    Result := False; MsgBox('此回退包仅用于已有安装，不能首次安装。', mbError, MB_OK); Exit;
  end;
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
    if IsUpgrade and (not ResumeFirstInstall) then begin
      if VerifyUpgrade <> '' then RaiseException('升级后核验未通过；未启动程序，未操作驱动。');
      DriverReady := True; Exit;
    end;
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
  if (CurPageID = wpFinished) and DriverReady and IsUpgrade then begin
    WizardForm.FinishedLabel.Caption := '程序已更新，现有驱动和设置已保留。点击完成启动程序。';
  end;
  if (CurPageID = wpFinished) and DriverReady and (not IsUpgrade) then begin
    WizardForm.FinishedLabel.Caption := '程序文件已安装。接下来将检查显示条件，并引导您一键完成关屏配置。' + #13#10 + #13#10 +
      '点击“完成并配置”继续；关屏是否可用，以随后配置窗口的检查结果为准。';
    WizardForm.NextButton.Caption := '完成并配置';
  end;
end;

procedure FinishLaunch;
var
  Code: Integer;
  Arguments: String;
begin
  if DriverReady and (not WizardSilent) then begin
    if IsUpgrade and (not ResumeFirstInstall) then Arguments := '' else Arguments := '--setup';
#if HasSettingsUi == "1"
    Arguments := '--launch-verified ' + Arguments;
#endif
    if not ExecAsOriginalUser(ExpandConstant('{app}\ScreenOffTray.exe'), Arguments,
      ExpandConstant('{app}'), SW_SHOWNORMAL,
#if HasSettingsUi == "1"
      ewWaitUntilTerminated,
#else
      ewNoWait,
#endif
      Code) then
      MsgBox('文件已安装，但无法启动 ScreenOffTray。请从开始菜单打开程序，不要重复安装显示驱动。', mbError, MB_OK)
#if HasSettingsUi == "1"
    else if Code = 10 then
      MsgBox('程序已启动，关屏配置尚未完成。请在已打开的设置窗口中完成准备；无需重新安装。', mbInformation, MB_OK)
    else if Code <> 0 then
      MsgBox('文件已安装，但15秒内未收到完整启动确认。程序可能仍在初始化，请先核对托盘；若未启动，可从开始菜单重试。请保留安装日志，不要重复安装显示驱动。', mbError, MB_OK)
#endif
    ;
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
#if HasSettingsUi == "1"
  if not Exec(ExpandConstant('{app}\ScreenOffTray.DriverSetup.exe'),
    '--verify-processes --app-dir "' + ExpandConstant('{app}') + '"', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    RaiseException('程序或设置窗口仍在运行，或无法核实进程。已停止卸载，不会强制结束程序。');
#endif
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
