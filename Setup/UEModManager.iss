; ============================================================
;   爱酱MOD管理器 - Inno Setup 安装脚本
;   编译要求：Inno Setup 6+
;   触发方式：根目录 Build-Installer.ps1
; ============================================================

#define MyAppName        "爱酱MOD管理器"
#ifndef MyAppVersion
#define MyAppVersion     "2.1.0"
#endif
#ifndef MyAppDisplayVer
#define MyAppDisplayVer  "v2.1.0"
#endif
#ifndef MyOutputBaseFilename
#define MyOutputBaseFilename "UEModManager_v2.1.0_Setup"
#endif
#define MyAppPublisher   "爱酱工作室"
#define MyAppURL         "https://www.modmanger.com"
#define MyAppExeName     "UEModManager.exe"
#define MyHelpDocUrl     "https://www.kdocs.cn/l/chqhf7cWy7K8"
#define DotNetRuntimeUrl "https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0"
#ifndef SourceDir
#define SourceDir        "..\UEModManager\bin\Release\net8.0-windows"
#endif

[Setup]
AppId={{8E4A2D5C-1F4B-4E7B-9C8E-2A3D4F5B6C7D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppDisplayVer}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={userpf}\UEModManager
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\installer_output
OutputBaseFilename={#MyOutputBaseFilename}
SetupIconFile=..\UEModManager\mnlogo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardImageFile=wizard-images\banner.bmp
WizardSmallImageFile=wizard-images\small.bmp
LicenseFile=LICENSE.txt
InfoBeforeFile=INFO_BEFORE.txt
InfoAfterFile=INFO_AFTER.txt
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
MinVersion=10.0
DisableWelcomePage=no

; 中文界面
ShowLanguageDialog=no

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："
Name: "quicklaunchicon"; Description: "创建快速启动栏快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked
Name: "autostart"; Description: "开机自动启动"; GroupDescription: "附加选项："; Flags: unchecked

[Files]
; 主程序及全部依赖（含思源黑体 OFL 字体）
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.log,console_*.log"; Flags: ignoreversion recursesubdirs createallsubdirs

; 一键迁移脚本（用户用于老版本升级）
Source: "..\一键迁移老版本数据.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\彻底清理UEModManager用户数据.bat"; DestDir: "{app}"; Flags: ignoreversion

; v2.0.3+：Brevo API Key 移至 Cloudflare Worker (api.modmanger.com)，客户端不再持有任何 secrets。
; 邮件发送统一走 WorkerEmailService → POST {ApiBaseUrl}/email/send 由 Worker 代理 Brevo API。

; 文档
Source: "..\使用说明.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\使用说明-精简版.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\故障排查.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\UEModManager\捐赠引导.jpg"; DestDir: "{app}"; Flags: ignoreversion

; 安装向导图文教程（仅安装时展示，不复制到安装目录）
Source: "wizard-images\step1.bmp"; Flags: dontcopy
Source: "wizard-images\step2.bmp"; Flags: dontcopy
Source: "wizard-images\step3.bmp"; Flags: dontcopy

[Icons]
Name: "{group}\{#MyAppName}";              Filename: "{app}\{#MyAppExeName}"
Name: "{group}\一键迁移老版本数据";         Filename: "{app}\一键迁移老版本数据.bat"
Name: "{group}\使用说明书（在线）";          Filename: "{#MyHelpDocUrl}"
Name: "{group}\卸载 {#MyAppName}";          Filename: "{uninstallexe}"

Name: "{autodesktop}\{#MyAppName}";          Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: quicklaunchicon

[Registry]
; 开机自启（可选）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "UEModManager"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\捐赠引导.jpg"; Description: "查看开发支持二维码"; Flags: nowait postinstall skipifsilent shellexec

; 在浏览器打开使用说明书（用户可选）
Filename: "{#MyHelpDocUrl}"; Description: "查看使用说明书（联网）"; Flags: nowait postinstall skipifsilent shellexec unchecked

[UninstallDelete]
; 不删用户数据 — %APPDATA%\UEModManager 由用户自行决定保留/清理
; 仅清理可能残留的运行时缓存
Type: filesandordirs; Name: "{app}\Data\Backups"

[Code]
var
  IntroPage1: TWizardPage;
  IntroPage2: TWizardPage;
  IntroPage3: TWizardPage;
  ResultCode: Integer;

procedure AddImageToPage(Page: TWizardPage; FileName: String);
var
  Image: TBitmapImage;
begin
  ExtractTemporaryFile(FileName);
  Image := TBitmapImage.Create(Page);
  Image.Parent := Page.Surface;
  Image.Left := 0;
  Image.Top := 0;
  Image.Width := Page.SurfaceWidth;
  Image.Height := Page.SurfaceHeight;
  Image.Stretch := True;
  Image.Bitmap.LoadFromFile(ExpandConstant('{tmp}\') + FileName);
end;

function IsDotNetDesktopRuntimeInstalledByCommand(): Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec(
      ExpandConstant('{cmd}'),
      '/C dotnet --list-runtimes | findstr /I /C:"Microsoft.WindowsDesktop.App 8." >nul',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode
    ) and (ResultCode = 0);
end;

function IsDotNetDesktopRuntimeInstalledByRegistry(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(
      HKLM64,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App\8.0',
      'Version',
      Version
    ) or
    RegQueryStringValue(
      HKCU64,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App\8.0',
      'Version',
      Version
    ) or
    RegQueryStringValue(
      HKLM,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App\8.0',
      'Version',
      Version
    ) or
    RegQueryStringValue(
      HKCU,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App\8.0',
      'Version',
      Version
    );
end;

function IsDotNetDesktopRuntimeInstalled(): Boolean;
begin
  Result := IsDotNetDesktopRuntimeInstalledByCommand() or IsDotNetDesktopRuntimeInstalledByRegistry();
end;

function InitializeSetup(): Boolean;
begin
  if (not WizardSilent()) and (not IsDotNetDesktopRuntimeInstalled()) then
  begin
    if MsgBox('.NET 8 桌面运行时未检测到。' + #13#10 + #13#10 +
      '建议先安装 Microsoft .NET 8 Desktop Runtime x64，否则程序可能无法启动。' + #13#10 + #13#10 +
      '点击“是”打开官方下载页面；点击“否”继续安装。',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', '{#DotNetRuntimeUrl}', '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
  end;

  Result := True;
end;

procedure InitializeWizard();
begin
  IntroPage1 := CreateCustomPage(wpInfoBefore, '第一步 · 导入 MOD 文件', '选择本地 MOD 压缩包，一键添加到管理器');
  AddImageToPage(IntroPage1, 'step1.bmp');

  IntroPage2 := CreateCustomPage(IntroPage1.ID, '第二步 · 启用与部署', '自动备份、事务日志与安全回滚');
  AddImageToPage(IntroPage2, 'step2.bmp');

  IntroPage3 := CreateCustomPage(IntroPage2.ID, '第三步 · 冲突检查与迁移', '检测冲突并迁移老版本数据');
  AddImageToPage(IntroPage3, 'step3.bmp');
end;
