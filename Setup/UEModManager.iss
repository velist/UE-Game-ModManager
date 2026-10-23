; 爱酱 MOD 管理器：Inno Setup 6.7+。使用根目录 Build-Installer.ps1 构建。
#define MyAppName "爱酱MOD管理器"
#ifndef MyAppVersion
  #define MyAppVersion "2.1.2"
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
AppName={cm:AppName}
AppVersion={#MyAppVersion}
AppVerName={cm:AppName} {#MyAppDisplayVer}
AppPublisher={cm:Publisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyHelpDocUrl}
AppUpdatesURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription=Ai-chan Mod Manager Installer
VersionInfoCompany=Ai-chan Studio
VersionInfoProductName=Ai-chan Mod Manager
DefaultDirName={userpf}\UEModManager
DefaultGroupName={cm:AppName}
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
WizardBackColor=#0f0f11
WizardBackImageFile=wizard-images\installer-background.png
WizardImageFile=wizard-images\installer-sidebar.png
WizardSmallImageFile=wizard-images\installer-mark.png
WizardImageBackColor=#0f0f11
WizardSmallImageBackColor=#0f0f11
LicenseFile=LICENSE.txt
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
MinVersion=10.0
ShowLanguageDialog=yes
CloseApplications=yes
CloseApplicationsFilter=UEModManager.exe
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"; LicenseFile: "LICENSE.en.txt"
Name: "chs"; MessagesFile: "Languages\ChineseSimplified.isl"; LicenseFile: "LICENSE.txt"

[LangOptions]
chs.DialogFontName=Microsoft YaHei UI
chs.WelcomeFontName=Microsoft YaHei UI
en.DialogFontName=Segoe UI
en.WelcomeFontName=Segoe UI
DialogFontSize=9
WelcomeFontSize=20

[Messages]
chs.WelcomeLabel1=欢迎使用%n爱酱 MOD 管理器
chs.WelcomeLabel2=导入、分类、启用 MOD。%n支持多款游戏，无需登录即可管理。
chs.ClickNext=
chs.WizardLicense=使用许可与隐私说明
chs.LicenseLabel=阅读许可与隐私说明后继续。
chs.WizardSelectDir=选择安装位置
chs.SelectDirDesc=设置程序位置与启动选项。
chs.SelectDirLabel3=程序安装位置
chs.SelectDirBrowseLabel=MOD 仓库将在首次启动时单独设置。
chs.WizardInstalling=正在安装
chs.InstallingLabel=正在写入程序文件，请稍候。
chs.FinishedHeadingLabel=安装完成%n准备开始游戏
chs.FinishedLabel=首次启动：选择 MOD 仓库，添加游戏。%n%n旧版数据迁移工具位于开始菜单。
chs.ButtonNext=继续(&N)
chs.ButtonInstall=开始安装(&I)
chs.ButtonFinish=完成(&F)
en.WelcomeLabel1=Welcome to%nAi-chan Mod Manager
en.WelcomeLabel2=Import, organize, and enable mods.%nManage multiple games without signing in.
en.ClickNext=
en.WizardLicense=License and privacy
en.LicenseLabel=Read the license and privacy information to continue.
en.WizardSelectDir=Choose an installation folder
en.SelectDirDesc=Set the app folder and startup options.
en.SelectDirLabel3=Application folder
en.SelectDirBrowseLabel=Choose a separate mod repository on first launch.
en.WizardInstalling=Installing
en.InstallingLabel=Copying app files. Please wait.
en.FinishedHeadingLabel=Installation complete%nReady to play
en.FinishedLabel=On first launch, choose a mod repository and add a game.%n%nFind the legacy data migration tool in the Start menu.
en.ButtonNext=&Continue
en.ButtonInstall=&Install
en.ButtonFinish=&Finish

[CustomMessages]
chs.AppName=爱酱MOD管理器
en.AppName=Ai-chan Mod Manager
chs.Publisher=爱酱工作室
en.Publisher=Ai-chan Studio
chs.DesktopShortcut=创建桌面快捷方式(&D)
en.DesktopShortcut=Create a &desktop shortcut
chs.ShortcutGroup=快捷方式：
en.ShortcutGroup=Shortcuts:
chs.AutoStart=登录 Windows 时启动(&A)
en.AutoStart=Start with Windows (&A)
chs.StartupGroup=启动方式：
en.StartupGroup=Startup:
chs.Migration=一键迁移老版本数据
en.Migration=Migrate legacy data (Chinese)
chs.Help=使用帮助
en.Help=Help
chs.Uninstall=卸载 %1
en.Uninstall=Uninstall %1
chs.Launch=启动 %1
en.Launch=Launch %1
chs.OpenHelp=打开使用帮助
en.OpenHelp=Open online help
chs.VersionPlatform=%1  /  WINDOWS 64 位
en.VersionPlatform=%1  /  WINDOWS 64-BIT
chs.RuntimeReady=.NET 8 桌面运行时  ·  已就绪
en.RuntimeReady=.NET 8 Desktop Runtime  ·  Ready
chs.RuntimeMissing=需要 .NET 8 桌面运行时（x64）
en.RuntimeMissing=Requires .NET 8 Desktop Runtime (x64)
chs.GetRuntime=下载 .NET 8 桌面运行时
en.GetRuntime=Download .NET 8 Desktop Runtime
chs.RuntimeUrl=https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0
en.RuntimeUrl=https://dotnet.microsoft.com/en-us/download/dotnet/8.0
chs.WelcomeNote=升级前请关闭管理器。%n已有 MOD、配置和备份会保留。
en.WelcomeNote=Close the manager before upgrading.%nYour mods, settings, and backups are kept.
chs.DataNote=程序与 MOD 分开存放。%n卸载会保留用户数据和 MOD 仓库。
en.DataNote=The app and mods are stored separately.%nUninstalling keeps user data and the mod repository.
chs.StartSetup=开始设置(&N)
en.StartSetup=&Get started
chs.StartInstall=开始安装(&I)
en.StartInstall=&Install
chs.FinishedNeedsRuntime=安装 .NET 8 桌面运行时（x64）后即可启动。%n%n首次启动时选择 MOD 仓库；已有数据和备份会保留。
en.FinishedNeedsRuntime=Install .NET 8 Desktop Runtime (x64) to launch.%n%nChoose a mod repository on first launch. Existing data and backups are kept.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:ShortcutGroup}"
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:StartupGroup}"; Flags: unchecked

[Files]
; 只打入本次全新 publish 目录；双重排除凭据、私人运行数据和无关平台文件。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.env,.env*,*.enc,*.pfx,*.p12,*.key,*.log,*.pdb,\config.json,\Data,\Data\*,\Backups,\Backups\*,\UserData,\UserData\*,*.so,*.dylib,*.a"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE.en.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\Quickstart.en.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "Languages\initial-en.txt"; DestDir: "{app}"; DestName: "initial-language.txt"; Languages: en; Flags: ignoreversion
Source: "Languages\initial-zh.txt"; DestDir: "{app}"; DestName: "initial-language.txt"; Languages: chs; Flags: ignoreversion
Source: "bundled\一键迁移老版本数据.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\彻底清理UEModManager用户数据.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\使用说明.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\使用说明-精简版.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\故障排查.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundled\捐赠引导.jpg"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{cm:AppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:Migration}"; Filename: "{app}\一键迁移老版本数据.bat"
Name: "{group}\{cm:Help}"; Filename: "{#MyHelpDocUrl}"
Name: "{group}\{cm:Uninstall,{cm:AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{cm:AppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "UEModManager"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "UEModManager"; Tasks: not autostart; Flags: deletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:Launch,{cm:AppName}}"; Flags: nowait postinstall skipifsilent; Check: CanLaunchApp
Filename: "{#MyHelpDocUrl}"; Description: "{cm:OpenHelp}"; Flags: nowait postinstall skipifsilent shellexec unchecked

; 卸载保留用户数据、MOD 仓库与部署备份。完全清理由用户显式运行随包附带的脚本。
[Code]
#include "InstallerUi.iss"
