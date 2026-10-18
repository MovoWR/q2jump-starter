; SPDX-License-Identifier: GPL-2.0-or-later
; Starter prepares game data when absent, then installs the latest GitHub client.
#ifndef BuildDir
  #error Build with scripts/build.py
#endif
#include AddBackslash(BuildDir) + "release.iss"
#include AddBackslash(BuildDir) + "client-helper.iss"
#ifndef BaselineDownloadEnabled
  #error The generated build must declare baseline download enablement.
#endif
#if BaselineDownloadEnabled != 1
  #error This data-only recipe requires enabled baseline downloads.
#endif

[Setup]
AppId={{913FDAA7-7365-462F-B13E-84BA972A4D9A}
AppName=Q2JUMP Starter
AppVersion={#ReleaseVersion}
AppVerName=Q2JUMP Starter {#ReleaseVersion}
SetupIconFile={#BuildDir}\source-snapshot\packaging\Q2JUMP-compact-dark.ico
DefaultDirName=C:\Q2JUMP
AppendDefaultDirName=no
DirExistsWarning=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=no
SetupMutex=Q2JUMPStarterDataSetup-913FDAA7
CloseApplications=no
RestartApplications=no
Uninstallable=no
OutputDir={#BuildDir}\dist
OutputBaseFilename=Q2JUMP-Starter-{#ReleaseVersion}-setup
Compression=lzma2
SolidCompression=yes
ArchiveExtraction=full
WizardStyle=modern
WizardSizePercent=110,120
LicenseFile={#BuildDir}\payload\.starter\licenses\ORIGINAL-DATA-NOTICES.txt
SetupLogging=yes

[Files]
Source: "{#BuildDir}\compiled-client\Q2JUMP-Starter-Client.exe"; Flags: dontcopy
#include AddBackslash(BuildDir) + "packaged-files.iss"
#include AddBackslash(BuildDir) + "baseline-files.iss"

[Code]
const
  InstallationMarker = 'Q2JUMP_STARTER_DATA_V1';
  MetadataDirectory = '.starter';
  LegacyMetadataDirectory = '.q2jump-starter-data';
  BaselineTemporaryBytes = 536870912;
var
  InstallModePage, ClientPage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  ExtractionPage: TExtractionWizardPage;
  InstallBaseline: Boolean;
  VerificationError: String;
  ClientRunning: Boolean;
  ClientCancelling, ClientWindowEnabled: Boolean;
  LastClientMessage, LastClientError: String;
  ReviewRequest: String;
  ClientReady: array[0..1] of Boolean;
  ClientVersion, ClientOutcome: array[0..1] of String;
  FolderFeedback, SpaceInformation: TNewStaticText;
  ReviewMemo, ResultMemo: TNewMemo;
  ApproveButton, OpenFolderButton, ViewLogButton: TNewButton;
  PreviousCancelClick: TNotifyEvent;
  PreviousCloseQuery: TCloseQueryEvent;
  DesktopShortcutCheck, R1Q2ShortcutCheck, LaunchGameCheck: TNewCheckBox;
  LaunchClientCombo: TNewComboBox;

function InstallingNewGame: Boolean;
begin
  Result := InstallModePage.Values[0];
end;

function GetFileAttributesW(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function EnableWindow(hWnd: HWND; Enable: Boolean): Boolean;
  external 'EnableWindow@user32.dll stdcall';

function GetCurrentProcessId: LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';

procedure CheckTargetPath(Path: String);
var
  Parent: String;
  Attributes: LongWord;
begin
  while Path <> '' do begin
    Attributes := GetFileAttributesW(Path);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then
      RaiseException('Setup cannot write through a linked file or folder: ' + Path);
    Parent := ExtractFileDir(Path);
    if Parent = Path then exit;
    Path := Parent;
  end;
end;

procedure CheckTargetFile(Path: String);
begin
  CheckTargetPath(Path);
  if DirExists(Path) then
    RaiseException('A folder occupies a required file path: ' + Path);
end;

function MatchesFile(Path, Hash: String): Boolean;
begin
  Result := FileExists(Path);
  if Result then
    Result := CompareText(GetSHA256OfFile(Path), Hash) = 0;
end;

procedure RequireFile(Path, Hash: String);
begin
  if not MatchesFile(Path, Hash) then
    RaiseException('Required data failed verification: ' + Path);
end;

procedure CheckMissingOrMatchingFile(Path, Hash: String);
begin
  CheckTargetFile(Path);
  if FileExists(Path) and not MatchesFile(Path, Hash) then
    RaiseException('Setup will not replace an existing file with different contents: ' + Path + #13#10 +
      'Choose a new, empty folder to prepare another copy of the original data.');
end;

function OwnedMetadataDirectory(Root, Directory, OwnerText: String): Boolean;
var
  Path: String;
  Marker: AnsiString;
  Size: Integer;
begin
  Path := Root + '\' + Directory;
  CheckTargetPath(Path);
  if FileExists(Path) then
    RaiseException('A file occupies the Starter metadata folder: ' + Path);
  Result := DirExists(Path);
  if not Result then exit;
  CheckTargetFile(Path + '\owner');
  if not FileSize(Path + '\owner', Size) or (Size <> Length(OwnerText)) then
    RaiseException('This folder has unrelated Starter metadata: ' + Path);
  if not LoadStringFromFile(Path + '\owner', Marker) or (String(Marker) <> OwnerText) then
    RaiseException('This folder has unrelated Starter metadata: ' + Path);
end;

function StarterMetadataDirectory(Root: String): String;
var
  Compact, Legacy, Q2Pro, R1Q2, Launcher: Boolean;
begin
  Compact := OwnedMetadataDirectory(Root, MetadataDirectory, InstallationMarker + #13#10);
  Legacy := OwnedMetadataDirectory(Root, LegacyMetadataDirectory, InstallationMarker + #13#10);
  Q2Pro := OwnedMetadataDirectory(Root, '.q2jump', 'Q2JUMP_MANAGED_FILES_V1' + #10);
  R1Q2 := OwnedMetadataDirectory(Root, '.q2jump-r1q2', 'Q2JUMP_R1Q2_MANAGED_FILES_V1' + #10);
  Launcher := OwnedMetadataDirectory(Root, '.q2jump-launcher', 'Q2JUMP_LAUNCHER_MANAGED_FILES_V1' + #10);
  if Compact and (Legacy or Q2Pro or R1Q2 or Launcher) then
    RaiseException('This folder contains conflicting Starter layouts. Choose another folder.');
  if Legacy or Q2Pro or R1Q2 or Launcher then Result := LegacyMetadataDirectory
  else Result := MetadataDirectory;
end;

function StarterPath(Root, RelativePath: String): String;
var
  Directory: String;
begin
  Result := RelativePath;
  if (RelativePath <> MetadataDirectory) and
     (Copy(RelativePath, 1, Length(MetadataDirectory) + 1) <> MetadataDirectory + '\') then exit;
  Directory := StarterMetadataDirectory(Root);
  if Directory = MetadataDirectory then exit;
  { Original notices previously lived next to baseq2, outside data metadata. }
  if (RelativePath = '.starter\licenses\original') or
     (Copy(RelativePath, 1, Length('.starter\licenses\original\')) = '.starter\licenses\original\') then
    Result := Copy(RelativePath, Length(MetadataDirectory) + 2, MaxInt)
  else Result := Directory + Copy(RelativePath, Length(MetadataDirectory) + 1, MaxInt);
end;

function StarterDestination(RelativePath: String): String;
var
  Root: String;
begin
  Root := ExpandConstant('{app}');
  Result := Root + '\' + StarterPath(Root, RelativePath);
end;

function NeedBaselineFile(RelativePath, Hash: String): Boolean;
var
  Path: String;
begin
  if not InstallBaseline then begin Result := False; exit; end;
  Path := StarterDestination(RelativePath);
  CheckMissingOrMatchingFile(Path, Hash);
  Result := InstallBaseline and not FileExists(Path);
end;

function NeedPackagedFile(RelativePath, Hash: String): Boolean;
var
  Path: String;
begin
  Path := StarterDestination(RelativePath);
  CheckTargetFile(Path);
  Result := not FileExists(Path);
end;

#include AddBackslash(BuildDir) + "baseline-code.iss"
#include AddBackslash(BuildDir) + "packaged-code.iss"

function DirectoryHasEntries(Path: String): Boolean;
var
  Entry: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Path) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          Result := True;
          exit;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function HasGamePak(Root: String): Boolean;
var
  Entry: TFindRec;
  Folder: String;
begin
  Result := False;
  Folder := Root + '\baseq2';
  CheckTargetPath(Folder);
  if FindFirst(Folder + '\*.pak', Entry) then begin
    try
      repeat
        if ((Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) and
           (CompareText(ExtractFileExt(Entry.Name), '.pak') = 0) then begin
          CheckTargetPath(Folder + '\' + Entry.Name);
          Result := True;
          exit;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

procedure RejectLegacyInstallation(Root: String);
begin
  if FileExists(Root + '\Q2JUMP.installation') or DirExists(Root + '\Q2JUMP.installation') then
    RaiseException('This folder belongs to the earlier combined Starter installer. Choose a new, empty folder.');
end;

procedure CheckDestinationRoot(Root: String; VerifyFiles: Boolean);
var
  Directory: String;
  Owned: Boolean;
begin
  if (Trim(Root) = '') or
     (((Length(Root) < 3) or (Copy(Root, 2, 2) <> ':\')) and
      (Copy(Root, 1, 2) <> '\\')) then
    RaiseException('Choose a full folder path, or use Browse.');
  if (Length(Root) > 140) or (String(AnsiString(Root)) <> Root) then
    RaiseException('Choose a shorter installation path using characters supported by your Windows system language.');
  CheckTargetPath(Root);
  if FileExists(Root) then RaiseException('Choose a folder for the data installation.');
  RejectLegacyInstallation(Root);
  Directory := StarterMetadataDirectory(Root);
  Owned := OwnedMetadataDirectory(Root, Directory, InstallationMarker + #13#10);
  if DirectoryHasEntries(Root) then begin
    if InstallingNewGame and not Owned then
      RaiseException('For a new game, choose an empty folder. To keep existing files, select the existing-game option.');
  end;
  if not InstallingNewGame and not HasGamePak(Root) then
    RaiseException('Choose the Quake II root folder containing baseq2\*.pak, not a Q2JUMP subfolder.');
  if VerifyFiles then begin
    CheckPackagedTargets(Root);
    if InstallingNewGame then CheckBaselineTargets(Root);
  end;
end;

procedure CheckDestination;
begin
  CheckDestinationRoot(ExpandConstant('{app}'), True);
end;

procedure UpdateFolderFeedback(Sender: TObject);
var
  Root, Found: String;
begin
  { The built-in estimate covers bundled metadata, not downloads performed
    later. Keep known data costs separate from variable client/backup space. }
  WizardForm.DiskSpaceLabel.Visible := False;
  if InstallingNewGame then
    SpaceInformation.Caption := 'Game data: about ' + IntToStr(BaselineSizeMiB) +
      ' MiB, plus clients and backups.' + #13#10 +
      'Temporary drive: at least ' + IntToStr(BaselineTemporaryBytes div 1048576) + ' MiB free.' + #13#10 +
      'Client space is checked before installation.'
  else
    SpaceInformation.Caption := 'Existing game data will be reused.' + #13#10 +
      'Additional space depends on clients and backups.' + #13#10 +
      'Space is checked before client files change.';
  Root := WizardForm.DirEdit.Text;
  try
    { Read-only structural feedback; full hash checks still run on Next and
      immediately before copying. Never create a probe file while typing. }
    CheckDestinationRoot(Root, False);
    if InstallingNewGame then begin
      if DirectoryHasEntries(Root) then
        FolderFeedback.Caption := 'Starter data found. Setup can continue this installation.'
      else FolderFeedback.Caption := 'Ready for a new game. Setup will prepare this folder.';
    end else begin
      FolderFeedback.Caption := 'Existing Quake II data found. Your PAKs and settings will be kept.';
      Found := '';
      if FileExists(Root + '\q2pro_race.exe') then Found := 'Q2PRO Race';
      if FileExists(Root + '\r1q2_race.exe') then begin
        if Found <> '' then Found := Found + ' and ';
        Found := Found + 'R1Q2 Race';
      end;
      if Found <> '' then FolderFeedback.Caption := FolderFeedback.Caption + #13#10 +
        'Client files found: ' + Found + '. They will be verified during setup.';
    end;
    FolderFeedback.Caption := FolderFeedback.Caption + #13#10 +
      'Write access and space are checked before client files change.';
  except
    FolderFeedback.Caption := GetExceptionMessage;
  end;
end;

procedure ApproveClientChanges(Sender: TObject);
begin
  if not ClientRunning or ClientCancelling or (ReviewRequest = '') then exit;
  if not SaveStringToFile(ExpandConstant('{tmp}\q2jump-client.approve'), ReviewRequest, False) then begin
    WizardForm.StatusLabel.Caption := 'Could not record approval. Please try again or cancel.';
    exit;
  end;
  ReviewRequest := '';
  ApproveButton.Enabled := False;
  ApproveButton.Visible := False;
  ReviewMemo.Visible := False;
  WizardForm.ProgressGauge.Visible := True;
  WizardForm.StatusLabel.Caption := 'Checking approved client changes...';
end;

procedure OpenInstalledFolder(Sender: TObject);
var
  ErrorCode: Integer;
begin
  if not ShellExec('open', ExpandConstant('{app}'), '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    MsgBox('Could not open the game folder: ' + SysErrorMessage(ErrorCode), mbError, MB_OK);
end;

procedure ViewSetupLog(Sender: TObject);
var
  ErrorCode: Integer;
begin
  if not ShellExec('open', ExpandConstant('{log}'), '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    MsgBox('Could not open the setup log: ' + SysErrorMessage(ErrorCode), mbError, MB_OK);
end;

procedure InitializeWizard;
var
  Selection: String;
begin
  InstallModePage := CreateInputOptionPage(wpWelcome, 'Choose what to install',
    'Select how Starter should use the game folder.',
    'A new game downloads Quake II demo data and your selected clients. The existing-game option requires a PAK in baseq2, keeps your files and settings, then reviews client changes before installation.',
    True, False);
  InstallModePage.Add('Install a new game');
  InstallModePage.Add('Install or update clients in an existing Quake II folder');
  InstallModePage.Values[0] := True;
  ClientPage := CreateInputOptionPage(InstallModePage.ID, 'Choose clients',
    'Select the clients to install or update.',
    'Both clients can share the game data and Jump resources. Each keeps its own executable, settings and update records.',
    True, False);
  ClientPage.Add('Q2PRO Race');
  ClientPage.Add('R1Q2 Race');
  ClientPage.Add('Both clients');
  Selection := Lowercase(ExpandConstant('{param:CLIENTS|q2pro}'));
  if Selection = 'q2pro' then ClientPage.Values[0] := True
  else if Selection = 'r1q2' then ClientPage.Values[1] := True
  else if Selection = 'both' then ClientPage.Values[2] := True
  else RaiseException('Invalid /CLIENTS value. Use q2pro, r1q2 or both.');
  DownloadPage := CreateDownloadPage('Download Quake II data',
    'Preparing original demo and official 3.20 update data.', nil);
  ExtractionPage := CreateExtractionPage('Prepare Quake II data',
    'Extracting the verified original archives.', nil);

  SpaceInformation := TNewStaticText.Create(WizardForm);
  SpaceInformation.Parent := WizardForm.SelectDirPage;
  SpaceInformation.Left := WizardForm.DiskSpaceLabel.Left;
  SpaceInformation.Width := WizardForm.DiskSpaceLabel.Width;
  SpaceInformation.Height := ScaleY(64);
  SpaceInformation.Top := WizardForm.DiskSpaceLabel.Top + WizardForm.DiskSpaceLabel.Height - SpaceInformation.Height;
  SpaceInformation.AutoSize := False;
  SpaceInformation.WordWrap := True;
  SpaceInformation.Anchors := [akLeft, akRight, akBottom];
  WizardForm.DiskSpaceLabel.Visible := False;

  FolderFeedback := TNewStaticText.Create(WizardForm);
  FolderFeedback.Parent := WizardForm.SelectDirPage;
  FolderFeedback.Left := WizardForm.DirEdit.Left;
  FolderFeedback.Top := WizardForm.DirEdit.Top + WizardForm.DirEdit.Height + ScaleY(16);
  FolderFeedback.Width := WizardForm.SelectDirPage.ClientWidth - FolderFeedback.Left;
  FolderFeedback.Height := SpaceInformation.Top - FolderFeedback.Top - ScaleY(8);
  FolderFeedback.AutoSize := False;
  FolderFeedback.WordWrap := True;
  FolderFeedback.Anchors := [akLeft, akTop, akRight, akBottom];
  WizardForm.DirEdit.OnChange := @UpdateFolderFeedback;

  ReviewMemo := TNewMemo.Create(WizardForm);
  ReviewMemo.Parent := WizardForm.InstallingPage;
  ReviewMemo.Left := WizardForm.ProgressGauge.Left;
  ReviewMemo.Top := WizardForm.ProgressGauge.Top;
  ReviewMemo.Width := WizardForm.ProgressGauge.Width;
  ReviewMemo.Height := WizardForm.InstallingPage.ClientHeight - ReviewMemo.Top - ScaleY(42);
  ReviewMemo.ReadOnly := True;
  ReviewMemo.ScrollBars := ssVertical;
  ReviewMemo.WordWrap := True;
  ReviewMemo.Anchors := [akLeft, akTop, akRight, akBottom];
  ReviewMemo.Visible := False;
  ApproveButton := TNewButton.Create(WizardForm);
  ApproveButton.Parent := WizardForm.InstallingPage;
  ApproveButton.Width := ScaleX(160);
  ApproveButton.Height := ScaleY(25);
  ApproveButton.Left := ReviewMemo.Left + ReviewMemo.Width - ApproveButton.Width;
  ApproveButton.Top := ReviewMemo.Top + ReviewMemo.Height + ScaleY(8);
  ApproveButton.Anchors := [akRight, akBottom];
  ApproveButton.Caption := 'Install / Update';
  ApproveButton.OnClick := @ApproveClientChanges;
  ApproveButton.Visible := False;

  ResultMemo := TNewMemo.Create(WizardForm);
  ResultMemo.Parent := WizardForm.FinishedPage;
  ResultMemo.Left := WizardForm.FinishedLabel.Left;
  ResultMemo.Top := WizardForm.FinishedLabel.Top + ScaleY(42);
  ResultMemo.Width := WizardForm.FinishedPage.ClientWidth - ResultMemo.Left;
  ResultMemo.Height := WizardForm.FinishedPage.ClientHeight - ResultMemo.Top - ScaleY(124);
  ResultMemo.ReadOnly := True;
  ResultMemo.ScrollBars := ssVertical;
  ResultMemo.WordWrap := True;
  ResultMemo.Anchors := [akLeft, akTop, akRight, akBottom];
  WizardForm.FinishedLabel.Height := ScaleY(36);

  DesktopShortcutCheck := TNewCheckBox.Create(WizardForm);
  DesktopShortcutCheck.Parent := WizardForm.FinishedPage;
  DesktopShortcutCheck.Left := WizardForm.FinishedLabel.Left;
  DesktopShortcutCheck.Top := WizardForm.FinishedPage.ClientHeight - ScaleY(116);
  DesktopShortcutCheck.Width := WizardForm.FinishedPage.ClientWidth - DesktopShortcutCheck.Left - ScaleX(12);
  DesktopShortcutCheck.Height := ScaleY(22);
  DesktopShortcutCheck.Anchors := [akLeft, akRight, akBottom];
  DesktopShortcutCheck.Caption := 'Create a Q2PRO Race desktop shortcut';
  DesktopShortcutCheck.Checked := True;
  DesktopShortcutCheck.Visible := False;

  R1Q2ShortcutCheck := TNewCheckBox.Create(WizardForm);
  R1Q2ShortcutCheck.Parent := WizardForm.FinishedPage;
  R1Q2ShortcutCheck.Left := DesktopShortcutCheck.Left;
  R1Q2ShortcutCheck.Top := DesktopShortcutCheck.Top + ScaleY(24);
  R1Q2ShortcutCheck.Width := DesktopShortcutCheck.Width;
  R1Q2ShortcutCheck.Height := DesktopShortcutCheck.Height;
  R1Q2ShortcutCheck.Anchors := [akLeft, akRight, akBottom];
  R1Q2ShortcutCheck.Caption := 'Create an R1Q2 Race desktop shortcut';
  R1Q2ShortcutCheck.Checked := True;
  R1Q2ShortcutCheck.Visible := False;

  LaunchGameCheck := TNewCheckBox.Create(WizardForm);
  LaunchGameCheck.Parent := WizardForm.FinishedPage;
  LaunchGameCheck.Left := DesktopShortcutCheck.Left;
  LaunchGameCheck.Top := DesktopShortcutCheck.Top + ScaleY(48);
  LaunchGameCheck.Width := (DesktopShortcutCheck.Width div 2) - ScaleX(4);
  LaunchGameCheck.Height := DesktopShortcutCheck.Height;
  LaunchGameCheck.Anchors := [akLeft, akBottom];
  LaunchGameCheck.Caption := 'Launch Q2PRO Race';
  LaunchGameCheck.Checked := True;
  LaunchGameCheck.Visible := False;
  LaunchClientCombo := TNewComboBox.Create(WizardForm);
  LaunchClientCombo.Parent := WizardForm.FinishedPage;
  LaunchClientCombo.Left := LaunchGameCheck.Left + LaunchGameCheck.Width + ScaleX(8);
  LaunchClientCombo.Top := LaunchGameCheck.Top;
  LaunchClientCombo.Width := DesktopShortcutCheck.Width - LaunchGameCheck.Width - ScaleX(8);
  LaunchClientCombo.Style := csDropDownList;
  LaunchClientCombo.Anchors := [akLeft, akRight, akBottom];
  LaunchClientCombo.Items.Add('Q2PRO Race');
  LaunchClientCombo.Items.Add('R1Q2 Race');
  LaunchClientCombo.ItemIndex := 0;
  LaunchClientCombo.Visible := False;

  OpenFolderButton := TNewButton.Create(WizardForm);
  OpenFolderButton.Parent := WizardForm.FinishedPage;
  OpenFolderButton.Left := DesktopShortcutCheck.Left;
  OpenFolderButton.Top := WizardForm.FinishedPage.ClientHeight - ScaleY(28);
  OpenFolderButton.Width := ScaleX(110);
  OpenFolderButton.Height := ScaleY(25);
  OpenFolderButton.Anchors := [akLeft, akBottom];
  OpenFolderButton.Caption := 'Open folder';
  OpenFolderButton.OnClick := @OpenInstalledFolder;
  ViewLogButton := TNewButton.Create(WizardForm);
  ViewLogButton.Parent := WizardForm.FinishedPage;
  ViewLogButton.Left := OpenFolderButton.Left + OpenFolderButton.Width + ScaleX(8);
  ViewLogButton.Top := OpenFolderButton.Top;
  ViewLogButton.Width := ScaleX(110);
  ViewLogButton.Height := OpenFolderButton.Height;
  ViewLogButton.Anchors := [akLeft, akBottom];
  ViewLogButton.Caption := 'View setup log';
  ViewLogButton.OnClick := @ViewSetupLog;
end;

function ClientSelected(Index: Integer): Boolean;
begin
  Result := ClientPage.Values[Index] or ClientPage.Values[2];
end;

function SelectedClients: String;
begin
  if ClientPage.Values[2] then Result := 'both'
  else if ClientPage.Values[1] then Result := 'r1q2'
  else Result := 'q2pro';
end;

function ClientName(Index: Integer): String;
begin
  if Index = 1 then Result := 'R1Q2 Race' else Result := 'Q2PRO Race';
end;

function ClientExecutable(Index: Integer): String;
begin
  if Index = 1 then Result := 'r1q2_race.exe' else Result := 'q2pro_race.exe';
end;

function GameLaunchParameters(Root: String; Index: Integer): String;
begin
  Result := '+set basedir "' + Root + '" +set game jump';
  if Index = 0 then Result := Result + ' +pushmenu main';
end;

function DesktopShortcutPath(Index: Integer): String;
begin
  Result := ExpandConstant('{userdesktop}\') + ClientName(Index) + ' (Jump).lnk';
end;

procedure CreateDesktopShortcut(Root: String; Index: Integer);
var
  ShortcutPath, GamePath, CreatedPath: String;
begin
  ShortcutPath := DesktopShortcutPath(Index);
  if GetFileAttributesW(ShortcutPath) <> $FFFFFFFF then
    RaiseException('A desktop item named ' + ClientName(Index) + ' (Jump) already exists. It was not changed.');
  GamePath := Root + '\' + ClientExecutable(Index);
  CreatedPath := CreateShellLink(ShortcutPath, ClientName(Index) + ' - Jump', GamePath,
    GameLaunchParameters(Root, Index), Root, GamePath, 0, SW_SHOWNORMAL);
  Log('Created desktop shortcut: ' + CreatedPath);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Root, GamePath: String;
  ExitCode, LaunchIndex: Integer;
begin
  Result := True;
  if CurPageID = wpFinished then begin
    if WizardSilent then exit;
    Root := ExpandConstant('{app}');
    try
      if ClientSelected(0) and ClientReady[0] and DesktopShortcutCheck.Checked then begin
        CreateDesktopShortcut(Root, 0);
        DesktopShortcutCheck.Checked := False;
        DesktopShortcutCheck.Enabled := False;
        DesktopShortcutCheck.Caption := 'Q2PRO Race shortcut created';
      end;
      if ClientSelected(1) and ClientReady[1] and R1Q2ShortcutCheck.Checked then begin
        CreateDesktopShortcut(Root, 1);
        R1Q2ShortcutCheck.Checked := False;
        R1Q2ShortcutCheck.Enabled := False;
        R1Q2ShortcutCheck.Caption := 'R1Q2 Race shortcut created';
      end;
      if ClientReady[0] and ClientReady[1] then LaunchIndex := LaunchClientCombo.ItemIndex
      else if ClientReady[1] then LaunchIndex := 1
      else LaunchIndex := 0;
      GamePath := Root + '\' + ClientExecutable(LaunchIndex);
      if LaunchGameCheck.Checked and ClientReady[LaunchIndex] and
         not Exec(GamePath, GameLaunchParameters(Root, LaunchIndex), Root, SW_SHOWNORMAL, ewNoWait, ExitCode) then
        RaiseException('Could not launch ' + ClientName(LaunchIndex) + ': ' + SysErrorMessage(ExitCode));
    except
      MsgBox(GetExceptionMessage + #13#10#13#10 +
        'Clear the option or retry Finish. The installed game is retained.', mbError, MB_OK);
      Result := False;
    end;
    exit;
  end;
  if CurPageID <> wpSelectDir then exit;
  try
    CheckDestination;
  except
    MsgBox(GetExceptionMessage, mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  FreeSpace, TotalSpace: Int64;
begin
  Result := '';
  VerificationError := '';
  InstallBaseline := InstallingNewGame;
  try
    CheckDestination;
    if InstallBaseline then begin
      if not GetSpaceOnDisk64(ExpandConstant('{tmp}'), FreeSpace, TotalSpace) then
        RaiseException('Could not check free space for temporary downloads.');
      if FreeSpace < BaselineTemporaryBytes then
        RaiseException('Setup needs at least ' + IntToStr(BaselineTemporaryBytes div 1048576) +
          ' MiB free on the temporary-file drive.');
      DownloadPage.Clear;
      AddBaselineDownloads;
      DownloadPage.Show;
      try
        DownloadPage.Download;
      finally
        DownloadPage.Hide;
      end;
      ExtractionPage.Clear;
      AddBaselineExtractions;
      ExtractionPage.Show;
      try
        ExtractionPage.Extract;
      finally
        ExtractionPage.Hide;
      end;
      VerifyStagedBaseline;
    end;
    { Recheck all destinations after downloads and before any copy. }
    CheckDestination;
  except
    Result := GetExceptionMessage + #13#10#13#10 +
      'Setup has not completed. Resolve the problem and retry.';
  end;
end;

function TakeClientField(var Line: String): String;
var
  Separator: Integer;
begin
  Separator := Pos(#9, Line);
  if Separator = 0 then begin
    Result := Line;
    Line := '';
  end else begin
    Result := Copy(Line, 1, Separator - 1);
    Delete(Line, 1, Separator);
  end;
end;

function ValidReviewRequest(Request: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  if Length(Request) <> 32 then exit;
  for I := 1 to Length(Request) do
    if Pos(Lowercase(Copy(Request, I, 1)), '0123456789abcdef') = 0 then exit;
  Result := True;
end;

procedure ClientLog(const S: String; const Error, FirstLine: Boolean);
var
  Line, Kind, Id, Value: String;
  Index, Percent: Integer;
begin
  Log(S);
  if not ClientWindowEnabled then begin
    { ExecAndLogOutput disables task windows while waiting. Its message loop
      continues to run; re-enable only the wizard so inline approval and safe
      cancellation work. Back/Next remain disabled throughout the helper. }
    WizardForm.BackButton.Enabled := False;
    WizardForm.NextButton.Enabled := False;
    EnableWindow(WizardForm.Handle, True);
    ClientWindowEnabled := True;
  end;
  if Error then begin
    if Trim(S) <> '' then LastClientError := S;
    exit;
  end;
  Line := S;
  if TakeClientField(Line) <> 'Q2JUMP' then begin
    { Inno merges stdout/stderr. Error means a pipe failure, not stderr;
      retain the helper's plain-text exception instead of stale progress. }
    if Trim(S) <> '' then LastClientError := S;
    exit;
  end;
  Kind := TakeClientField(Line);
  Id := TakeClientField(Line);
  if Id = 'q2pro' then Index := 0
  else if Id = 'r1q2' then Index := 1
  else exit;
  if not ClientSelected(Index) then exit;
  if Kind = 'result' then begin
    ClientReady[Index] := TakeClientField(Line) = 'ready';
    ClientVersion[Index] := TakeClientField(Line);
    ClientOutcome[Index] := Line;
    exit;
  end;
  if ClientCancelling then exit;
  if Kind = 'status' then begin
    LastClientMessage := ClientName(Index) + ': ' + Line;
    WizardForm.StatusLabel.Caption := LastClientMessage;
    WizardForm.FilenameLabel.Caption := '';
    WizardForm.ProgressGauge.Style := npbstMarquee;
  end else if Kind = 'progress' then begin
    Percent := StrToIntDef(TakeClientField(Line), -1);
    if (Percent < 0) or (Percent > 100) then exit;
    WizardForm.StatusLabel.Caption := ClientName(Index) + ': ' + Line;
    WizardForm.FilenameLabel.Caption := Line;
    WizardForm.ProgressGauge.Style := npbstNormal;
    WizardForm.ProgressGauge.Min := 0;
    WizardForm.ProgressGauge.Max := 100;
    WizardForm.ProgressGauge.Position := Percent;
  end else if Kind = 'review-begin' then begin
    ReviewRequest := '';
    ApproveButton.Enabled := False;
    ApproveButton.Visible := False;
    WizardForm.StatusLabel.Caption := 'Review ' + ClientName(Index) + ' changes';
    WizardForm.FilenameLabel.Caption := 'Custom PAKs, settings and unrelated files are kept.';
    WizardForm.ProgressGauge.Visible := False;
    ReviewMemo.Lines.Text := Line + #13#10;
    ReviewMemo.Visible := True;
  end else if Kind = 'review-file' then begin
    ReviewMemo.Lines.Add(Line);
  end else if Kind = 'review-ready' then begin
    Value := TakeClientField(Line);
    if not ValidReviewRequest(Value) or WizardSilent then exit;
    ReviewRequest := Value;
    ApproveButton.Visible := True;
    ApproveButton.Enabled := True;
    if ApproveButton.CanFocus then WizardForm.ActiveControl := ApproveButton;
  end;
end;

procedure RequestClientCancellation;
begin
  if not ClientRunning or ClientCancelling then exit;
  if not SaveStringToFile(ExpandConstant('{tmp}\q2jump-client.cancel'), 'cancel', False) then begin
    WizardForm.StatusLabel.Caption := 'Could not request cancellation. Please try Cancel again.';
    exit;
  end;
  ClientCancelling := True;
  ReviewRequest := '';
  ApproveButton.Enabled := False;
  WizardForm.CancelButton.Enabled := False;
  WizardForm.StatusLabel.Caption := 'Cancelling client setup safely...';
end;

procedure ClientCancelClick(Sender: TObject);
begin
  RequestClientCancellation;
end;

procedure ClientCloseQuery(Sender: TObject; var CanClose: Boolean);
begin
  { Inno's built-in Cancel event is inactive in ssPostInstall. Keep the
    wizard open until the helper acknowledges cancellation and exits. }
  CanClose := False;
  RequestClientCancellation;
end;

procedure InstallLatestClient(Root: String);
var
  ExitCode: Integer;
  HelperPath, ModeArg, InterfaceArg: String;
  Index: Integer;

begin
  HelperPath := ExpandConstant('{tmp}\Q2JUMP-Starter-Client.exe');
  ExtractTemporaryFile('Q2JUMP-Starter-Client.exe');
  RequireFile(HelperPath, '{#ClientHelperSha256}');
  DeleteFile(ExpandConstant('{tmp}\q2jump-client.cancel'));
  DeleteFile(ExpandConstant('{tmp}\q2jump-client.approve'));
  LastClientMessage := 'Client installation did not complete.';
  LastClientError := '';
  if InstallingNewGame then ModeArg := 'new' else ModeArg := 'existing';
  if WizardSilent then InterfaceArg := 'silent' else InterfaceArg := 'wizard';
  for Index := 0 to 1 do begin
    ClientReady[Index] := False;
    ClientVersion[Index] := '';
    ClientOutcome[Index] := 'Not completed.';
  end;
  ClientRunning := True;
  ClientCancelling := False;
  ClientWindowEnabled := False;
  ReviewRequest := '';
  PreviousCancelClick := WizardForm.CancelButton.OnClick;
  PreviousCloseQuery := WizardForm.OnCloseQuery;
  WizardForm.CancelButton.OnClick := @ClientCancelClick;
  WizardForm.OnCloseQuery := @ClientCloseQuery;
  WizardForm.CancelButton.Enabled := True;
  WizardForm.StatusLabel.Caption := 'Preparing selected clients...';
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    if not ExecAndLogOutput(HelperPath, '"' + Root + '" ' + ModeArg + ' ' + SelectedClients +
        ' ' + InterfaceArg + ' ' + IntToStr(GetCurrentProcessId), ExpandConstant('{tmp}'),
        SW_HIDE, ewWaitUntilTerminated, ExitCode, @ClientLog) then
      RaiseException('Could not start client setup: ' + SysErrorMessage(ExitCode));
    if ExitCode <> 0 then begin
      if LastClientError <> '' then RaiseException(LastClientError);
      RaiseException(LastClientMessage);
    end;
    for Index := 0 to 1 do
      if ClientSelected(Index) and not ClientReady[Index] then
        RaiseException('Could not confirm final verification for ' + ClientName(Index) +
          '. View the setup log and rerun Starter.');
  finally
    ClientRunning := False;
    WizardForm.CancelButton.OnClick := PreviousCancelClick;
    WizardForm.OnCloseQuery := PreviousCloseQuery;
    ReviewRequest := '';
    ApproveButton.Visible := False;
    ReviewMemo.Visible := False;
    WizardForm.ProgressGauge.Visible := True;
    WizardForm.ProgressGauge.Style := npbstNormal;
    WizardForm.CancelButton.Enabled := True;
  end;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if ClientRunning then begin
    Cancel := False; Confirm := False;
    RequestClientCancellation;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Root, Directory, MarkerPath, TemporaryMarker: String;
begin
  Root := ExpandConstant('{app}');
  if CurStep = ssInstall then begin
    CheckDestination;
    { Persist separate data ownership before file copying so interrupted work can retry. }
    Directory := StarterMetadataDirectory(Root);
    if not ForceDirectories(Root + '\' + Directory) then
      RaiseException('Could not create the data installation folder.');
    MarkerPath := Root + '\' + Directory + '\owner';
    if not FileExists(MarkerPath) then begin
      TemporaryMarker := ExpandConstant('{tmp}\q2jump-starter-data-owner');
      if not SaveStringToFile(TemporaryMarker, InstallationMarker + #13#10, False) then
        RaiseException('Could not prepare data ownership.');
      if not CopyFile(TemporaryMarker, MarkerPath, True) then
        RaiseException('Could not record data ownership without replacing an existing file.');
    end;
  end;
  if CurStep = ssPostInstall then begin
    try
      VerifyInstalledPackaged(Root);
      if not HasGamePak(Root) then
        RaiseException('A .pak file is required in baseq2. Rerun Starter to download demo data.');
      InstallLatestClient(Root);
    except
      VerificationError := GetExceptionMessage;
      Log('Starter installation failed: ' + VerificationError);
    end;
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if VerificationError <> '' then Result := 1;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Index: Integer;
  Summary: String;
begin
  if CurPageID = wpSelectDir then UpdateFolderFeedback(nil);
  if CurPageID = wpFinished then begin
    DesktopShortcutCheck.Visible := ClientReady[0] and ClientSelected(0);
    R1Q2ShortcutCheck.Visible := ClientReady[1] and ClientSelected(1);
    LaunchClientCombo.Visible := ClientReady[0] and ClientReady[1];
    LaunchGameCheck.Visible := ClientReady[0] or ClientReady[1];
    OpenFolderButton.Enabled := DirExists(ExpandConstant('{app}'));
    ViewLogButton.Enabled := FileExists(ExpandConstant('{log}'));
    ResultMemo.Lines.Clear;
    for Index := 0 to 1 do if ClientSelected(Index) then begin
      Summary := ClientName(Index);
      if ClientVersion[Index] <> '' then Summary := Summary + ' ' + ClientVersion[Index];
      if ClientReady[Index] then Summary := Summary + ' - verified and ready.'
      else begin
        Summary := Summary + ' - not ready.';
        if ClientOutcome[Index] <> '' then Summary := Summary + ' ' + ClientOutcome[Index];
      end;
      ResultMemo.Lines.Add(Summary);
    end;
    ResultMemo.Lines.Add('');
    ResultMemo.Lines.Add('Game folder: ' + ExpandConstant('{app}'));
    if VerificationError <> '' then begin
      WizardForm.FinishedHeadingLabel.Caption := 'Q2JUMP setup is incomplete';
      WizardForm.FinishedLabel.Caption := 'Review the results below. Ready clients can still be launched.';
      ResultMemo.Lines.Add('');
      ResultMemo.Lines.Add(VerificationError);
      ResultMemo.Lines.Add('Your files are retained. Rerun Starter to continue unfinished setup.');
    end else begin
      WizardForm.FinishedHeadingLabel.Caption := 'Q2JUMP is ready';
      WizardForm.FinishedLabel.Caption := 'Your selected clients are ready. Choose what to do when you click Finish.';
    end;
    { Inno sizes this label when entering the finish page. Keep its bounds
      above the results panel after replacing the built-in message. }
    WizardForm.FinishedLabel.Height := ScaleY(36);
    if ClientReady[0] and ClientReady[1] then LaunchGameCheck.Caption := 'Launch a client'
    else if ClientReady[1] then LaunchGameCheck.Caption := 'Launch R1Q2 Race'
    else LaunchGameCheck.Caption := 'Launch Q2PRO Race';
    if GetFileAttributesW(DesktopShortcutPath(1)) <> $FFFFFFFF then begin
      R1Q2ShortcutCheck.Checked := False;
      R1Q2ShortcutCheck.Enabled := False;
      R1Q2ShortcutCheck.Caption := 'R1Q2 Race shortcut already exists (kept)';
    end;
    if GetFileAttributesW(DesktopShortcutPath(0)) <> $FFFFFFFF then begin
      DesktopShortcutCheck.Checked := False;
      DesktopShortcutCheck.Enabled := False;
      DesktopShortcutCheck.Caption := 'Q2PRO Race shortcut already exists (kept)';
    end;
  end;
end;
