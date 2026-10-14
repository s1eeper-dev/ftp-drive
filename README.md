# FTP 드라이브

FTP, FTPS, SFTP 서버를 Windows 드라이브 문자(예: `Z:`)로 연결하고 해제하는 작은 프로그램입니다.
광고 없이 필요할 때만 로그인해서 쓰고, 다 쓰면 해제합니다. 연결에는 [rclone](https://rclone.org)과 [WinFsp](https://winfsp.dev)를 사용합니다.

## 설치

1. [FtpDrive-Setup.exe 내려받기](https://github.com/s1eeper-dev/ftp-drive/releases/latest/download/FtpDrive-Setup.exe)
2. 내려받은 파일을 실행합니다. 서명되지 않은 프로그램이라 'Windows의 PC 보호' 창이 뜨면 [추가 정보] → [실행]을 누릅니다.
3. 설치 중에 WinFsp와 rclone을 공식 배포처에서 내려받아 함께 설치합니다. 인터넷 연결이 필요합니다.

인터넷이 안 되는 PC라면 다른 PC에서 아래 두 파일을 받아 `FtpDrive-Setup.exe`와 같은 폴더에 두고 실행합니다.

- WinFsp 설치 파일 `winfsp-버전.msi` — https://winfsp.dev/rel/
- rclone `rclone-버전-windows-amd64.zip` — https://rclone.org/downloads/

## 사용

- 처음 실행할 때 별칭, 방식, 서버 주소, 포트, 아이디, 드라이브 문자를 입력합니다. 별칭은 탐색기에 보이는 드라이브 이름이 됩니다.
- 연결한 서버는 [연결 목록]에 별칭으로 저장되고, 최근에 연결한 것이 맨 위에 옵니다. [새 연결]로 다른 서버를 추가하고, [삭제]로 지웁니다.
- 다음부터는 실행하고 목록에서 고른 뒤 비밀번호만 입력하고 Enter 를 누르면 연결됩니다.
- [연결 해제]를 누르거나 창을 닫으면 드라이브가 사라집니다.
- 서버 정보는 `%APPDATA%\FtpDrive\settings.ini`에 저장되고, 비밀번호는 저장하지 않습니다.

### 업로드가 바로 끝나 보이는 이유

드라이브에 파일을 복사하면 먼저 이 PC의 임시 저장소(캐시)에 쓰이고, 실제 서버 업로드는 그 뒤에 진행됩니다. 그래서 탐색기 복사 창은 금방 닫힙니다.
이렇게 해야 사진 편집기나 Office 같은 프로그램이 드라이브의 파일을 바로 열고 저장할 수 있습니다.
실제 업로드 상황은 FTP 드라이브 창 아래에 '서버로 업로드 중 · 남은 파일 N개'로 표시됩니다.
업로드가 남은 채로 해제하면 [예]를 눌러 업로드가 끝난 뒤 자동으로 해제할 수 있습니다.
- 연결에 실패하면 원인 안내와 함께 `%APPDATA%\FtpDrive\rclone.log`에 기록이 남습니다.
- 관리자 권한으로 실행하지 마세요. 탐색기에서 드라이브가 보이지 않을 수 있습니다.

## 제거

[설정 > 앱]에서 'FTP 드라이브'를 제거합니다. WinFsp는 다른 프로그램도 쓸 수 있어 자동으로 지우지 않으니, 필요 없으면 같은 곳에서 따로 제거합니다.

## 빌드

Windows, .NET Framework 4.x(Windows 10/11 기본 탑재), [Inno Setup](https://jrsoftware.org/isinfo.php) 6.5 이상이 필요합니다.

```powershell
.\build.ps1
```

`dist\FtpDrive-Setup.exe`가 만들어집니다.
