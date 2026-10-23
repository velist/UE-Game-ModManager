{ 与客户端一致的文字层级；TColor 使用 BGR 顺序。 }
const
  Text100 = $00F5F4F4;
  Text300 = $00D8D4D4;
  Text500 = $00948B8B;
  AccentCyan = $00EED322;

var
  RuntimeReady: Boolean;
  RuntimeStatus, WelcomeNote, VersionLabel, FooterVersion, DataNote: TNewStaticText;
  ProgressTrack, ProgressFill: TPanel;
  HelpButton, RuntimeButton: TNewButton;
  DesktopShortcut, AutoStart: TNewCheckBox;

function HasDesktopRuntimeAt(const DotNetExe: String): Boolean;
var
  Code, I: Integer;
  Output: TExecOutput;
begin
  Result := False;
  if not FileExists(DotNetExe) then Exit;
  try
    if ExecAndCaptureOutput(DotNetExe, '--list-runtimes', '', SW_HIDE,
      ewWaitUntilTerminated, Code, Output) and (Code = 0) and not Output.Error then
      for I := 0 to GetArrayLength(Output.StdOut) - 1 do
        if Pos('Microsoft.WindowsDesktop.App 8.', Trim(Output.StdOut[I])) = 1 then
        begin
          Result := True;
          Exit;
        end;
  except
    Log('Desktop runtime detection failed: ' + GetExceptionMessage);
  end;
end;

function IsDotNet8Available(): Boolean;
var
  InstallRoot: String;
begin
  { 显式使用 x64 主机，避免 PATH 指向 x86 运行时。 }
  Result := HasDesktopRuntimeAt(ExpandConstant('{commonpf64}\dotnet\dotnet.exe'));
  if Result then Exit;
  if RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64',
    'InstallLocation', InstallRoot) then
    Result := HasDesktopRuntimeAt(AddBackslash(InstallRoot) + 'dotnet.exe');
  if Result then Exit;
  InstallRoot := GetEnv('DOTNET_ROOT_X64');
  if InstallRoot = '' then InstallRoot := GetEnv('DOTNET_ROOT');
  if InstallRoot <> '' then
    Result := HasDesktopRuntimeAt(AddBackslash(InstallRoot) + 'dotnet.exe');
end;

function CanLaunchApp(): Boolean;
begin
  Result := RuntimeReady;
end;

procedure OpenExternalUrl(const Url: String);
var
  Code: Integer;
begin
  ShellExecAsOriginalUser('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

procedure HelpButtonClick(Sender: TObject);
begin
  OpenExternalUrl('{#MyHelpDocUrl}');
end;

procedure RuntimeButtonClick(Sender: TObject);
begin
  OpenExternalUrl(CustomMessage('RuntimeUrl'));
end;

function AddText(Parent: TWinControl; const Caption: String;
  Left, Top, Width, Height, FontSize: Integer): TNewStaticText;
begin
  Result := TNewStaticText.Create(WizardForm);
  Result.Parent := Parent;
  Result.AutoSize := False;
  Result.WordWrap := True;
  Result.SetBounds(Left, Top, Width, Height);
  Result.Font.Name := WizardForm.Font.Name;
  Result.Font.Size := FontSize;
  Result.StyleElements := [seClient, seBorder];
  Result.Font.Color := Text500;
  Result.Caption := Caption;
end;

procedure InitializeWizard();
var
  ContentLeft, ContentWidth, NoteTop: Integer;
begin
  RuntimeReady := IsDotNet8Available();
  { 正文与辅助信息分层，保留系统暗色主题的按钮和焦点反馈。 }
  WizardForm.StyleElements := [seClient, seBorder];
  WizardForm.Font.Color := Text300;
  WizardForm.WelcomeLabel1.Font.Size := 20;
  WizardForm.WelcomeLabel1.StyleElements := [seClient, seBorder];
  WizardForm.WelcomeLabel1.Font.Color := Text100;
  WizardForm.WelcomeLabel2.Font.Size := 10;
  WizardForm.WelcomeLabel2.StyleElements := [seClient, seBorder];
  WizardForm.WelcomeLabel2.Font.Color := Text300;
  WizardForm.PageNameLabel.Font.Size := 14;
  WizardForm.PageNameLabel.Height := ScaleY(24);
  WizardForm.PageDescriptionLabel.Top := WizardForm.PageNameLabel.Top + ScaleY(28);
  WizardForm.PageNameLabel.StyleElements := [seClient, seBorder];
  WizardForm.PageNameLabel.Font.Color := Text100;
  WizardForm.PageDescriptionLabel.StyleElements := [seClient, seBorder];
  WizardForm.PageDescriptionLabel.Font.Color := Text500;
  WizardForm.SelectDirLabel.StyleElements := [seClient, seBorder];
  WizardForm.SelectDirLabel.Font.Color := Text100;
  WizardForm.SelectDirBrowseLabel.StyleElements := [seClient, seBorder];
  WizardForm.SelectDirBrowseLabel.Font.Color := Text300;
  WizardForm.DiskSpaceLabel.StyleElements := [seClient, seBorder];
  WizardForm.DiskSpaceLabel.Font.Color := Text500;
  WizardForm.StatusLabel.StyleElements := [seClient, seBorder];
  WizardForm.StatusLabel.Font.Color := Text300;
  WizardForm.FilenameLabel.StyleElements := [seClient, seBorder];
  WizardForm.FilenameLabel.Font.Color := Text500;
  WizardForm.FinishedHeadingLabel.StyleElements := [seClient, seBorder];
  WizardForm.FinishedHeadingLabel.Font.Color := Text100;
  WizardForm.FinishedLabel.Font.Size := 10;
  WizardForm.FinishedLabel.StyleElements := [seClient, seBorder];
  WizardForm.FinishedLabel.Font.Color := Text300;
  WizardForm.RunList.StyleElements := [seClient, seBorder];
  WizardForm.RunList.Font.Color := Text300;
  { 原生进度值与安装逻辑不变，以细青色进度条呈现。 }
  ProgressTrack := TPanel.Create(WizardForm);
  ProgressTrack.Parent := WizardForm.ProgressGauge.Parent;
  ProgressTrack.StyleElements := [];
  ProgressTrack.BevelOuter := bvNone;
  ProgressTrack.Color := $002A2727;
  ProgressTrack.SetBounds(WizardForm.ProgressGauge.Left,
    WizardForm.ProgressGauge.Top, WizardForm.ProgressGauge.Width, ScaleY(8));
  ProgressFill := TPanel.Create(WizardForm);
  ProgressFill.Parent := ProgressTrack;
  ProgressFill.StyleElements := [];
  ProgressFill.BevelOuter := bvNone;
  ProgressFill.Color := $00D4B606;
  ProgressFill.SetBounds(0, 0, 0, ProgressTrack.Height);
  WizardForm.ProgressGauge.Visible := False;
  ContentLeft := WizardForm.WelcomeLabel1.Left;
  ContentWidth := WizardForm.WelcomeLabel1.Width;
  VersionLabel := AddText(WizardForm.WelcomeLabel1.Parent,
    FmtMessage(CustomMessage('VersionPlatform'), ['{#MyAppDisplayVer}']), ContentLeft, ScaleY(26),
    ContentWidth, ScaleY(24), 9);
  VersionLabel.StyleElements := [seClient, seBorder];
  VersionLabel.Font.Color := AccentCyan;
  WizardForm.WelcomeLabel1.Top := ScaleY(66);
  WizardForm.WelcomeLabel1.Height := ScaleY(84);
  WizardForm.WelcomeLabel2.Top := ScaleY(166);
  WizardForm.WelcomeLabel2.Height := ScaleY(60);
  NoteTop := ScaleY(242);
  RuntimeStatus := AddText(WizardForm.WelcomeLabel1.Parent, '', ContentLeft,
    NoteTop, ContentWidth, ScaleY(38), 9);
  if RuntimeReady then
    RuntimeStatus.Caption := CustomMessage('RuntimeReady')
  else
    RuntimeStatus.Caption := CustomMessage('RuntimeMissing');
  RuntimeStatus.StyleElements := [seClient, seBorder];
  RuntimeStatus.Font.Color := AccentCyan;
  RuntimeButton := TNewButton.Create(WizardForm);
  RuntimeButton.Parent := WizardForm.WelcomeLabel1.Parent;
  RuntimeButton.SetBounds(ContentLeft, NoteTop + ScaleY(44), ScaleX(268), ScaleY(30));
  RuntimeButton.Caption := CustomMessage('GetRuntime');
  RuntimeButton.OnClick := @RuntimeButtonClick;
  RuntimeButton.Visible := not RuntimeReady;
  WelcomeNote := AddText(WizardForm.WelcomeLabel1.Parent,
    CustomMessage('WelcomeNote'),
    ContentLeft, NoteTop + ScaleY(90), ContentWidth, ScaleY(58), 9);

  { 安装目录与快捷方式选项放在同一页。 }
  WizardForm.SelectDirLabel.Top := ScaleY(8);
  WizardForm.SelectDirLabel.Font.Style := [fsBold];
  WizardForm.SelectDirBrowseLabel.Top := ScaleY(38);
  WizardForm.SelectDirBrowseLabel.Height := ScaleY(40);
  WizardForm.DirEdit.Top := ScaleY(88);
  WizardForm.DirBrowseButton.Top := WizardForm.DirEdit.Top;
  WizardForm.DiskSpaceLabel.Top := ScaleY(129);
  DesktopShortcut := TNewCheckBox.Create(WizardForm);
  DesktopShortcut.Parent := WizardForm.DirEdit.Parent;
  DesktopShortcut.SetBounds(0, ScaleY(182), WizardForm.DirEdit.Parent.ClientWidth, ScaleY(24));
  DesktopShortcut.StyleElements := [seClient, seBorder];
  DesktopShortcut.Font.Color := Text300;
  DesktopShortcut.Font.Size := 10;
  DesktopShortcut.Caption := CustomMessage('DesktopShortcut');
  DesktopShortcut.Checked := GetPreviousData('DesktopShortcut', '1') = '1';
  AutoStart := TNewCheckBox.Create(WizardForm);
  AutoStart.Parent := WizardForm.DirEdit.Parent;
  AutoStart.SetBounds(0, ScaleY(218), WizardForm.DirEdit.Parent.ClientWidth, ScaleY(24));
  AutoStart.StyleElements := [seClient, seBorder];
  AutoStart.Font.Color := Text300;
  AutoStart.Font.Size := 10;
  AutoStart.Caption := CustomMessage('AutoStart');
  AutoStart.Checked := GetPreviousData('AutoStart', '0') = '1';
  DataNote := AddText(WizardForm.DirEdit.Parent,
    CustomMessage('DataNote'),
    0, ScaleY(274), WizardForm.DirEdit.Parent.ClientWidth, ScaleY(58), 9);
  HelpButton := TNewButton.Create(WizardForm);
  HelpButton.Parent := WizardForm;
  HelpButton.SetBounds(ScaleX(24), WizardForm.CancelButton.Top, ScaleX(78), WizardForm.CancelButton.Height);
  HelpButton.Caption := CustomMessage('Help');
  HelpButton.OnClick := @HelpButtonClick;
  FooterVersion := AddText(WizardForm, '{#MyAppDisplayVer}', ScaleX(116),
    WizardForm.CancelButton.Top + ScaleY(5), ScaleX(90), ScaleY(24), 9);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := PageID = wpSelectTasks;
  { 默认任务初始化后再同步选项，避免跳过任务页时覆盖用户选择。 }
  if Result and not WizardSilent then
  begin
    if DesktopShortcut.Checked then WizardSelectTasks('desktopicon')
    else WizardSelectTasks('!desktopicon');
    if AutoStart.Checked then WizardSelectTasks('autostart')
    else WizardSelectTasks('!autostart');
    Log('Installer options: ' + WizardSelectedTasks(False));
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpWelcome then
    WizardForm.NextButton.Caption := CustomMessage('StartSetup');
  if CurPageID = wpSelectDir then
    WizardForm.NextButton.Caption := CustomMessage('StartInstall');
  if CurPageID = wpFinished then
  begin
    WizardForm.FinishedHeadingLabel.Font.Size := 20;
    WizardForm.FinishedHeadingLabel.Top := ScaleY(66);
    WizardForm.FinishedHeadingLabel.Height := ScaleY(84);
    WizardForm.FinishedLabel.Top := ScaleY(166);
    WizardForm.FinishedLabel.Height := ScaleY(104);
    RuntimeButton.Parent := WizardForm.FinishedLabel.Parent;
    RuntimeButton.Left := WizardForm.FinishedLabel.Left;
    RuntimeButton.Top := ScaleY(278);
    RuntimeButton.Visible := not RuntimeReady;
    WizardForm.RunList.Top := ScaleY(286);
    WizardForm.RunList.Height := ScaleY(62);
    if not RuntimeReady then
    begin
      WizardForm.RunList.Top := ScaleY(330);
      WizardForm.FinishedLabel.Caption := CustomMessage('FinishedNeedsRuntime');
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
    RuntimeReady := IsDotNet8Available();
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  if WizardIsTaskSelected('desktopicon') then
    SetPreviousData(PreviousDataKey, 'DesktopShortcut', '1')
  else SetPreviousData(PreviousDataKey, 'DesktopShortcut', '0');
  if WizardIsTaskSelected('autostart') then
    SetPreviousData(PreviousDataKey, 'AutoStart', '1')
  else SetPreviousData(PreviousDataKey, 'AutoStart', '0');
end;

{ 仅更新进度条显示，不参与文件安装或运行时检测。 }
procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if MaxProgress > 0 then
    ProgressFill.Width := Round((CurProgress * 1.0 / MaxProgress) * ProgressTrack.Width);
end;
