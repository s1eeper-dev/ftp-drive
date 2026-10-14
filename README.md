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

- 처음 실행할 때 방식, 서버 주소, 포트, 아이디, 드라이브 문자를 입력합니다.
- 다음부터는 실행하고 비밀번호만 입력한 뒤 Enter 를 누르면 연결됩니다.
- [연결 해제]를 누르거나 창을 닫으면 드라이브가 사라집니다.
- 서버 정보는 `%APPDATA%\FtpDrive\settings.ini`에 저장되고, 비밀번호는 저장하지 않습니다.
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
