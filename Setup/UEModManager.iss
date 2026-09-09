; 爱酱 MOD 管理器：Inno Setup 6.7+。使用根目录 Build-Installer.ps1 构建。
#define MyAppName "爱酱MOD管理器"
#ifndef MyAppVersion
  #define MyAppVersion "2.1.0"
#endif
#ifndef MyAppDisplayVer
  #define MyAppDisplayVer "v" + MyAppVersion
#endif
#ifndef MyOutputBaseFilename
  #define MyOutputBaseFilename "UEModManager_v" + MyAppVersion + "_Setup"
#endif
#define MyAppPublisher "爱酱工作室"
#define MyAppURL "https://www.modmanger.com"
#define MyAppExeName "UEModManager.exe"
#define MyHelpDocUrl "https://www.modmanger.com/help"
#define DotNetRuntimeUrl "https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0"
#ifndef SourceDir
  #define SourceDir "..\UEModManager\bin\Release\net8.0-windows"
#endif

[Setup]
AppId={{8E4A2D5C-1F4B-4E7B-9C8E-2A3D4F5B6C7D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppDisplayVer}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyHelpDocUrl}
AppUpdatesURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={userpf}\UEModManager
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableDirPage=no
DisableReadyPage=yes
OutputDir=..\installer_output
OutputBaseFilename={#MyOutputBaseFilename}
SetupIconFile=..\UEModManager\mnlogo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern dark windows11 hidebevels
WizardSizePercent=130,130
WizardBackColor=#15171b
WizardBackImageFile=wizard-images\installer-background.png
WizardImageFile=wizard-images\installer-sidebar.png
WizardSmallImageFile=wizard-images\installer-mark.png
WizardImageBackColor=#15171b
WizardSmallImageBackColor=#15171b
LicenseFile=LICENSE.txt
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
MinVersion=10.0
ShowLanguageDialog=no
CloseApplications=yes
CloseApplicationsFilter=UEModManager.exe
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chs"; MessagesFile: "Languages\ChineseSimplified.isl"

[LangOptions]
DialogFontName=Microsoft YaHei UI
DialogFontSize=9
WelcomeFontName=Microsoft YaHei UI
WelcomeFontSize=22

[Messages]
WelcomeLabel1=让 MOD 管理%n简单一点。
WelcomeLabel2=导入、分类、启用，把时间留给游戏。%n支持多款游戏，核心管理无需登录。
ClickNext=
WizardLicense=使用许可与隐私说明
LicenseLabel=请阅读以下说明，再继续安装。
WizardSelectDir=安装到哪里？
SelectDirDesc=选择程序位置，按你的习惯设置启动方式。
SelectDirLabel3=程序安装位置
SelectDirBrowseLabel=这里只存放程序。MOD 仓库将在首次启动时单独设置。
WizardInstalling=正在安装
InstallingLabel=正在准备程序和所需文件，请稍候。
FinishedHeadingLabel=安装完成。%n现在，开始整理 MOD。
FinishedLabel=首次启动时，选择 MOD 的存储位置，再添加游戏。%n%n升级用户请保留原数据；需要迁移旧版时，可使用开始菜单中的迁移工具。
ButtonNext=继续(&N)
ButtonInstall=开始安装(&I)
ButtonFinish=完成(&F)

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："
Name: "autostart"; Description: "登录 Windows 时启动管理器"; GroupDescription: "启动方式："; Flags: unchecked

[Files]
; 只打入本次全新 publish 目录；双重排除凭据、私人运行数据和无关平台文件。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.env,.env*,*.enc,*.pfx,*.p12,*.key,*.log,*.pdb,\config.json,\Data,\Data\*,\Backups,\Backups\*,\UserData,\UserData\*,*.so,*.dylib,*.a"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\一键迁移老版本数据.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\彻底清理UEModManager用户数据.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\使用说明.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\使用说明-精简版.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\故障排查.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\捐赠引导.jpg"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\一键迁移老版本数据"; Filename: "{app}\一键迁移老版本数据.bat"
Name: "{group}\使用帮助"; Filename: "{#MyHelpDocUrl}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "UEModManager"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "UEModManager"; Tasks: not autostart; Flags: deletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent; Check: CanLaunchApp
Filename: "{#MyHelpDocUrl}"; Description: "打开使用帮助"; Flags: nowait postinstall skipifsilent shellexec unchecked

; 卸载保留用户数据、MOD 仓库与部署备份。完全清理由用户显式运行随包附带的脚本。
[Code]
#include "InstallerUi.iss"
