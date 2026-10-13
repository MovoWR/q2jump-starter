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
LicenseFile={#BuildDir}\payload\.q2jump-starter-data\licenses\ORIGINAL-DATA-NOTICES.txt
SetupLogging=yes

[Files]
Source: "{#BuildDir}\compiled-client\Q2JUMP-Starter-Client.exe"; Flags: dontcopy
#include AddBackslash(BuildDir) + "packaged-files.iss"
#include AddBackslash(BuildDir) + "baseline-files.iss"

[Code]
const
  InstallationMarker = 'Q2JUMP_STARTER_DATA_V1';
  MetadataDirectory = '.q2jump-starter-data';
var
  InstallModePage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  ExtractionPage: TExtractionWizardPage;
  InstallBaseline: Boolean;
  VerificationError: String;
  ClientRunning: Boolean;
  LastClientMessage, LastClientError: String;
  DesktopShortcutCheck, LaunchGameCheck: TNewCheckBox;

function InstallingNewGame: Boolean;
begin
  Result := InstallModePage.Values[0];
end;

function GetFileAttributesW(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

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

function NeedBaselineFile(RelativePath, Hash: String): Boolean;
var
  Path: String;
begin
  if not InstallBaseline then begin Result := False; exit; end;
  Path := ExpandConstant('{app}') + '\' + RelativePath;
  CheckMissingOrMatchingFile(Path, Hash);
  Result := InstallBaseline and not FileExists(Path);
end;

function NeedPackagedFile(RelativePath, Hash: String): Boolean;
var
  Path: String;
begin
  Path := ExpandConstant('{app}') + '\' + RelativePath;
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

procedure CheckDestination;
var
  Root, MarkerPath: String;
  Marker: AnsiString;
  Owned: Boolean;
begin
  Root := ExpandConstant('{app}');
  if (Length(Root) > 140) or (String(AnsiString(Root)) <> Root) then
    RaiseException('Choose a shorter installation path using characters supported by your Windows system language.');
  CheckTargetPath(Root);
  if FileExists(Root) then RaiseException('Choose a folder for the data installation.');
  RejectLegacyInstallation(Root);
  CheckTargetPath(Root + '\' + MetadataDirectory);
  if FileExists(Root + '\' + MetadataDirectory) then
    RaiseException('A file occupies the Starter metadata folder. Choose a new, empty folder.');
  MarkerPath := Root + '\' + MetadataDirectory + '\owner';
  CheckTargetFile(MarkerPath);
  Owned := False;
  if DirectoryHasEntries(Root) then begin
    if LoadStringFromFile(MarkerPath, Marker) then begin
      if Trim(String(Marker)) <> InstallationMarker then
        RaiseException('This folder has an unrelated Starter ownership record. Choose another folder.');
      Owned := True;
    end else if DirExists(Root + '\' + MetadataDirectory) then
      RaiseException('This folder has unrelated Starter metadata. Choose another folder.');
    if InstallingNewGame and not Owned then
      RaiseException('For a new game, choose an empty folder. To keep existing files, select the existing-game option.');
  end;
  if not InstallingNewGame and not HasGamePak(Root) then
    RaiseException('Choose the Quake II root folder containing baseq2\*.pak, not a Q2JUMP subfolder.');
  CheckPackagedTargets(Root);
  if InstallingNewGame then CheckBaselineTargets(Root);
end;

procedure InitializeWizard;
begin
  InstallModePage := CreateInputOptionPage(wpWelcome, 'Choose what to install',
    'Select how Starter should use the game folder.',
    'A new game downloads Quake II demo data and the latest Q2PRO Race client. The existing-game option requires a PAK in baseq2, keeps your files and settings, then reviews client changes before installation.',
    True, False);
  InstallModePage.Add('Install a new game');
  InstallModePage.Add('Install or update Q2PRO Race in an existing Quake II folder');
  InstallModePage.Values[0] := True;
  DownloadPage := CreateDownloadPage('Download Quake II data',
    'Preparing original demo and official 3.20 update data.', nil);
  ExtractionPage := CreateExtractionPage('Prepare Quake II data',
    'Extracting the verified original archives.', nil);

  DesktopShortcutCheck := TNewCheckBox.Create(WizardForm);
  DesktopShortcutCheck.Parent := WizardForm.FinishedPage;
  DesktopShortcutCheck.Left := WizardForm.FinishedLabel.Left;
  DesktopShortcutCheck.Top := WizardForm.FinishedPage.ClientHeight - ScaleY(72);
  DesktopShortcutCheck.Width := WizardForm.FinishedPage.ClientWidth - DesktopShortcutCheck.Left - ScaleX(12);
  DesktopShortcutCheck.Height := ScaleY(22);
  DesktopShortcutCheck.Anchors := [akLeft, akRight, akBottom];
  DesktopShortcutCheck.Caption := 'Create a desktop shortcut';
  DesktopShortcutCheck.Checked := True;
  DesktopShortcutCheck.Visible := False;

  LaunchGameCheck := TNewCheckBox.Create(WizardForm);
  LaunchGameCheck.Parent := WizardForm.FinishedPage;
  LaunchGameCheck.Left := DesktopShortcutCheck.Left;
  LaunchGameCheck.Top := DesktopShortcutCheck.Top + ScaleY(28);
  LaunchGameCheck.Width := DesktopShortcutCheck.Width;
  LaunchGameCheck.Height := DesktopShortcutCheck.Height;
  LaunchGameCheck.Anchors := [akLeft, akRight, akBottom];
  LaunchGameCheck.Caption := 'Launch Q2PRO Race';
  LaunchGameCheck.Checked := True;
  LaunchGameCheck.Visible := False;
end;

function GameLaunchParameters(Root: String): String;
begin
  Result := '+set basedir "' + Root + '" +set game jump +pushmenu main';
end;

function DesktopShortcutPath: String;
begin
  Result := ExpandConstant('{userdesktop}\Q2PRO Race (Jump).lnk');
end;

procedure CreateDesktopShortcut(Root: String);
var
  ShortcutPath, GamePath, CreatedPath: String;
begin
  ShortcutPath := DesktopShortcutPath;
  if GetFileAttributesW(ShortcutPath) <> $FFFFFFFF then
    RaiseException('A desktop item named Q2PRO Race (Jump) already exists. It was not changed.');
  GamePath := Root + '\q2pro_race.exe';
  CreatedPath := CreateShellLink(ShortcutPath, 'Q2PRO Race - Jump', GamePath,
    GameLaunchParameters(Root), Root, GamePath, 0, SW_SHOWNORMAL);
  Log('Created desktop shortcut: ' + CreatedPath);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Root, GamePath: String;
  ExitCode: Integer;
begin
  Result := True;
  if CurPageID = wpFinished then begin
    if WizardSilent or (VerificationError <> '') then exit;
    Root := ExpandConstant('{app}');
    GamePath := Root + '\q2pro_race.exe';
    try
      if DesktopShortcutCheck.Checked then begin
        CreateDesktopShortcut(Root);
        DesktopShortcutCheck.Checked := False;
        DesktopShortcutCheck.Enabled := False;
        DesktopShortcutCheck.Caption := 'Desktop shortcut created';
      end;
      if LaunchGameCheck.Checked and
         not Exec(GamePath, GameLaunchParameters(Root), Root, SW_SHOWNORMAL, ewNoWait, ExitCode) then
        RaiseException('Could not launch Q2PRO Race: ' + SysErrorMessage(ExitCode));
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
      if FreeSpace < 536870912 then
        RaiseException('Setup needs at least 512 MiB free on the temporary-file drive.');
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

procedure ClientLog(const S: String; const Error, FirstLine: Boolean);
begin
  Log(S);
  if Error and (Trim(S) <> '') then
    LastClientError := S
  else if not Error and (Trim(S) <> '') then begin
    LastClientMessage := S;

    WizardForm.StatusLabel.Caption := S;
  end;
end;

procedure InstallLatestClient(Root: String);
var
  ExitCode: Integer;
  HelperPath, ModeArg: String;

begin
  HelperPath := ExpandConstant('{tmp}\Q2JUMP-Starter-Client.exe');
  ExtractTemporaryFile('Q2JUMP-Starter-Client.exe');
  RequireFile(HelperPath, '{#ClientHelperSha256}');
  DeleteFile(ExpandConstant('{tmp}\q2jump-client.cancel'));
  LastClientMessage := 'Client installation did not complete.';
  LastClientError := '';
  if InstallingNewGame then ModeArg := 'new' else ModeArg := 'existing';
  ClientRunning := True;
  WizardForm.StatusLabel.Caption := 'Client setup is open. Use its Cancel button to stop safely.';
  try
    if not ExecAndLogOutput(HelperPath, '"' + Root + '" ' + ModeArg, ExpandConstant('{tmp}'),
        SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode, @ClientLog) then
      RaiseException('Could not start client setup: ' + SysErrorMessage(ExitCode));
    if ExitCode <> 0 then begin
      if LastClientError <> '' then RaiseException(LastClientError);
      RaiseException(LastClientMessage);
    end;
  finally
    ClientRunning := False;

  end;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if ClientRunning then begin
    Cancel := False; Confirm := False;
    SaveStringToFile(ExpandConstant('{tmp}\q2jump-client.cancel'), 'cancel', False);
    WizardForm.CancelButton.Enabled := False;
    WizardForm.StatusLabel.Caption := 'Cancelling client setup safely...';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Root, MarkerPath, TemporaryMarker: String;
begin
  Root := ExpandConstant('{app}');
  if CurStep = ssInstall then begin
    CheckDestination;
    { Persist separate data ownership before file copying so interrupted work can retry. }
    if not ForceDirectories(Root + '\' + MetadataDirectory) then
      RaiseException('Could not create the data installation folder.');
    MarkerPath := Root + '\' + MetadataDirectory + '\owner';
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
begin
  if CurPageID = wpFinished then begin
    DesktopShortcutCheck.Visible := VerificationError = '';
    LaunchGameCheck.Visible := VerificationError = '';
    if VerificationError <> '' then begin
      WizardForm.FinishedHeadingLabel.Caption := 'Q2JUMP setup is incomplete';
      WizardForm.FinishedLabel.Caption := VerificationError + #13#10#13#10 +
        'Your files are retained. Rerun Starter to retry client setup.';
    end else begin
      WizardForm.FinishedHeadingLabel.Caption := 'Q2JUMP is ready';
      WizardForm.FinishedLabel.Caption := 'Game data and the latest Q2PRO Race client are installed.' + #13#10#13#10 +
        'Choose what to do when you click Finish.';
      if GetFileAttributesW(DesktopShortcutPath) <> $FFFFFFFF then begin
        DesktopShortcutCheck.Checked := False;
        DesktopShortcutCheck.Enabled := False;
        DesktopShortcutCheck.Caption := 'Desktop shortcut already exists (left unchanged)';
      end;
    end;
  end;
end;
