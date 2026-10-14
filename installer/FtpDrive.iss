; FTP 드라이브 설치 마법사 스크립트. WinFsp 와 rclone 은 설치 중에 공식 배포처에서 내려받는다.
#define AppName "FTP 드라이브"
#define AppVersion "1.1.0"
#define WinFspUrl "https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi"
#define RcloneUrl "https://downloads.rclone.org/rclone-current-windows-amd64.zip"

[Setup]
AppId={{82F2D64E-2E44-491E-988A-1F69C2D104F6}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=s1eeper-dev
AppPublisherURL=https://github.com/s1eeper-dev/ftp-drive
DefaultDirName={autopf}\FtpDrive
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=FtpDrive_SingleInstance
UninstallDisplayIcon={sys}\shell32.dll,9
OutputDir=..\dist
OutputBaseFilename=FtpDrive-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\build\FtpDrive.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\FtpDrive.exe"; IconFilename: "{sys}\shell32.dll"; IconIndex: 9
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\FtpDrive.exe"; IconFilename: "{sys}\shell32.dll"; IconIndex: 9; Tasks: desktopicon

[Run]
Filename: "{app}\FtpDrive.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\rclone.exe"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  NeedWinFsp, NeedRclone: Boolean;
  WinFspMsi, RcloneZip: String;

function WinFspInstalled: Boolean;
var
  Dir: String;
begin
  Result := RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', Dir)
    and FileExists(AddBackslash(Dir) + 'bin\winfsp-x64.dll');
end;

{ 인터넷이 안 되는 PC 를 위해 Setup.exe 옆에 받아 둔 파일이 있으면 그것을 쓴다. }
function FindBesideSetup(const Pattern: String): String;
var
  Rec: TFindRec;
begin
  Result := '';
  if FindFirst(ExpandConstant('{src}\') + Pattern, Rec) then
  begin
    Result := ExpandConstant('{src}\') + Rec.Name;
    FindClose(Rec);
  end;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Count: Integer;
begin
  Result := True;
  if CurPageID <> wpReady then Exit;

  NeedWinFsp := not WinFspInstalled;
  NeedRclone := not FileExists(ExpandConstant('{app}\rclone.exe'));
  WinFspMsi := '';
  RcloneZip := '';
  if NeedWinFsp then WinFspMsi := FindBesideSetup('winfsp*.msi');
  if NeedRclone then RcloneZip := FindBesideSetup('rclone*.zip');

  Count := 0;
  DownloadPage.Clear;
  if NeedWinFsp and (WinFspMsi = '') then
  begin
    DownloadPage.Add('{#WinFspUrl}', 'winfsp.msi', '');
    WinFspMsi := ExpandConstant('{tmp}\winfsp.msi');
    Count := Count + 1;
  end;
  if NeedRclone and (RcloneZip = '') then
  begin
    DownloadPage.Add('{#RcloneUrl}', 'rclone.zip', '');
    RcloneZip := ExpandConstant('{tmp}\rclone.zip');
    Count := Count + 1;
  end;
  if Count = 0 then Exit;

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox('필요한 파일을 내려받지 못했습니다.' + #13#10 + AddPeriod(GetExceptionMessage) + #13#10#13#10 +
          '인터넷 연결을 확인하세요. 인터넷이 안 되는 PC라면 다른 PC에서 winfsp-버전.msi 와 ' +
          'rclone-버전-windows-amd64.zip 을 받아 Setup.exe 와 같은 폴더에 두고 다시 실행하세요.',
          mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Zip, Dest: String;
begin
  if CurStep <> ssPostInstall then Exit;

  if NeedWinFsp then
  begin
    WizardForm.StatusLabel.Caption := 'WinFsp 설치 중...';
    Exec('msiexec.exe', '/i "' + WinFspMsi + '" /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if not WinFspInstalled then
      SuppressibleMsgBox('WinFsp 설치에 실패했습니다 (코드 ' + IntToStr(ResultCode) + '). 설치 프로그램을 다시 실행하세요.',
        mbError, MB_OK, IDOK);
  end;

  if NeedRclone then
  begin
    WizardForm.StatusLabel.Caption := 'rclone 준비 중...';
    { PowerShell 작은따옴표 문자열 안에 넣으므로 경로의 ' 는 '' 로 바꾼다. }
    Zip := RcloneZip;
    StringChangeEx(Zip, '''', '''''', True);
    Dest := ExpandConstant('{app}\rclone.exe');
    StringChangeEx(Dest, '''', '''''', True);
    Exec('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; ' +
      '$z = [IO.Compression.ZipFile]::OpenRead(''' + Zip + '''); ' +
      'try { $e = $z.Entries | Where-Object { $_.Name -eq ''rclone.exe'' } | Select-Object -First 1; ' +
      '[IO.Compression.ZipFileExtensions]::ExtractToFile($e, ''' + Dest + ''', $true) } finally { $z.Dispose() }"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if not FileExists(ExpandConstant('{app}\rclone.exe')) then
      SuppressibleMsgBox('rclone 을 준비하지 못했습니다. 설치 프로그램을 다시 실행하세요.', mbError, MB_OK, IDOK);
  end;
end;
