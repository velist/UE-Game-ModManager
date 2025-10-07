[Setup]

AppName=虚幻引擎MOD管理器

AppVersion=1.7.37

AppPublisher=爱酱工作室

AppPublisherURL=https://github.com/

DefaultDirName={autopf}\UEModManager

DefaultGroupName=虚幻引擎MOD管理器

OutputDir=installer_output

OutputBaseFilename=UEModManager_v1.7.37_Setup

SetupIconFile=UEModManager\图标.ico

Compression=lzma

SolidCompression=yes

WizardStyle=modern

PrivilegesRequired=lowest

ArchitecturesAllowed=x64

ArchitecturesInstallIn64BitMode=x64

; 强制显示目录选择页面

DisableDirPage=no

UsePreviousAppDir=no

; 修复说明：此版本修复了备份目录默认在C盘临时文件夹的问题

AppComments=修复版本：配置和备份文件现在存储在程序安装目录，不再使用C盘临时文件夹



[Languages]

Name: "english"; MessagesFile: "compiler:Default.isl"



[Tasks]

Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1



[Files]

; 递归复制发布目录全部文件（直接从编译输出目录）

Source: "UEModManager\\bin\\Release\\net8.0-windows\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Backups\\*;Logs\\*;Data\\*;temp\\*;Reports\\*;UserData\\*;*.log;console*.log;*.pdb;*.mdb;*.db;*.cache;*.etl;*.env"
Source: "brevo.env"; DestDir: "{app}"; Flags: ignoreversion


; 复制资源文件

Source: "UEModManager\捐赠引导.jpg"; DestDir: "{app}\resources"; Flags: ignoreversion

Source: "UEModManager\图标.ico"; DestDir: "{app}\resources"; Flags: ignoreversion

Source: "UEModManager\黑神话悟空MOD-百度网盘.png"; DestDir: "{app}\resources"; Flags: ignoreversion

Source: "UEModManager\黑神话悟空MOD-迅雷云盘.png"; DestDir: "{app}\resources"; Flags: ignoreversion

Source: "UEModManager\剑星MOD-百度网盘.png"; DestDir: "{app}\resources"; Flags: ignoreversion

Source: "UEModManager\剑星MOD-迅雷云盘.png"; DestDir: "{app}\resources"; Flags: ignoreversion

Source: "UEModManager\光与影百度网盘mod.png"; DestDir: "{app}\resources"; Flags: ignoreversion

Source: "UEModManager\光与影迅雷云盘mod.png"; DestDir: "{app}\resources"; Flags: ignoreversion

Source: "UEModManager\明末MOD-百度网盘.png"; DestDir: "{app}\resources"; Flags: ignoreversion

Source: "UEModManager\明末MOD-迅雷云盘.png"; DestDir: "{app}\resources"; Flags: ignoreversion



[Icons]

Name: "{group}\虚幻引擎MOD管理器"; Filename: "{app}\UEModManager.exe"; IconFilename: "{app}\resources\图标.ico"

Name: "{group}\{cm:UninstallProgram,虚幻引擎MOD管理器}"; Filename: "{uninstallexe}"

Name: "{autodesktop}\虚幻引擎MOD管理器"; Filename: "{app}\UEModManager.exe"; IconFilename: "{app}\resources\图标.ico"; Tasks: desktopicon

Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\虚幻引擎MOD管理器"; Filename: "{app}\UEModManager.exe"; IconFilename: "{app}\resources\图标.ico"; Tasks: quicklaunchicon



[Run]

Filename: "{app}\UEModManager.exe"; Description: "{cm:LaunchProgram,虚幻引擎MOD管理器}"; Flags: nowait postinstall skipifsilent



[Code]

procedure CreateReadmeFile();
var
  ReadmeContent: string;
  ReadmeFile: string;
begin
  ReadmeFile := ExpandConstant('{app}\README.txt');
  ReadmeContent := '虚幻引擎MOD管理器 v1.7.37' + #13#10#13#10 +
    '使用说明：' + #13#10 +
    '1. 双击 UEModManager.exe 启动程序' + #13#10 +
    '2. 首次使用请按向导配置游戏路径' + #13#10 +
    '3. 备份文件存储在 Backups 文件夹中' + #13#10 +
    '4. 程序数据存储在 Data 文件夹中' + #13#10 +
    '5. 运行日志存储在 Logs 文件夹中' + #13#10#13#10 +
    '文件夹说明：' + #13#10 +
    '├── UEModManager.exe    # 主程序' + #13#10 +
    '├── resources/          # 资源文件' + #13#10 +
    '├── Backups/            # MOD备份文件夹' + #13#10 +
    '├── Data/               # 程序数据文件夹' + #13#10 +
    '└── Logs/               # 日志文件夹';
  SaveStringToFile(ReadmeFile, ReadmeContent, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    // 创建必要的目录结构
    CreateDir(ExpandConstant('{app}\Data'));
    CreateDir(ExpandConstant('{app}\Logs'));
    CreateDir(ExpandConstant('{app}\Backups'));

    // 隐藏bin文件夹（设置隐藏属性）
    Exec('attrib', '+H "' + ExpandConstant('{app}\bin') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // 创建README文件
    CreateReadmeFile();

    // 隐藏敏感配置文件（brevo.env），首启后程序会自动加密迁移至AppData
    if FileExists(ExpandConstant('{app}\brevo.env')) then
    begin
      Exec('attrib', '+H +S "' + ExpandConstant('{app}\brevo.env') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;

    // 安装完成后自动打开引导图片
    if FileExists(ExpandConstant('{app}\resources\捐赠引导.jpg')) then
    begin
      ShellExec('open', ExpandConstant('{app}\resources\捐赠引导.jpg'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
  end;
end;

