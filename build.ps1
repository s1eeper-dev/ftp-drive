# FtpDrive.exe 를 컴파일하고 Inno Setup 으로 설치 마법사(dist\FtpDrive-Setup.exe)를 만드는 빌드 스크립트.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
$csc = "$env:windir\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) 을 찾을 수 없습니다.' }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory (Join-Path $dist 'build') | Out-Null

$refs = '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll'
$sources = "$root\src\FtpDrive.cs", "$root\src\Updater.cs"
$version = [regex]::Match((Get-Content "$root\src\FtpDrive.cs" -Raw -Encoding UTF8), 'AssemblyVersion\("(\d+\.\d+\.\d+)').Groups[1].Value
if (-not $version) { throw 'src\FtpDrive.cs 에서 AssemblyVersion 을 찾지 못했습니다.' }

# 설정·업데이트 로직 테스트를 먼저 돌리고, 실패하면 빌드를 멈춘다.
& $csc /nologo /target:exe /codepage:65001 /main:FtpDrive.Tests /out:"$dist\build\Tests.exe" @refs @sources "$root\tests\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw "테스트 컴파일 실패 (종료 코드 $LASTEXITCODE)" }
& "$dist\build\Tests.exe"
if ($LASTEXITCODE -ne 0) { throw '테스트 실패' }

& $csc /nologo /target:winexe /codepage:65001 /optimize+ /out:"$dist\build\FtpDrive.exe" @refs @sources
if ($LASTEXITCODE -ne 0) { throw "컴파일 실패 (종료 코드 $LASTEXITCODE)" }

& $iscc /Q "/DAppVersion=$version" "$root\installer\FtpDrive.iss"
if ($LASTEXITCODE -ne 0) { throw "설치 파일 생성 실패 (종료 코드 $LASTEXITCODE)" }
Get-Item "$dist\FtpDrive-Setup.exe" | Select-Object FullName, Length, @{ n = 'Version'; e = { $version } }
