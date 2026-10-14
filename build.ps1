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

# 설정 로직 테스트를 먼저 돌리고, 실패하면 빌드를 멈춘다.
& $csc /nologo /target:exe /codepage:65001 /main:FtpDrive.Tests /out:"$dist\build\Tests.exe" @refs "$root\src\FtpDrive.cs" "$root\tests\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw "테스트 컴파일 실패 (종료 코드 $LASTEXITCODE)" }
& "$dist\build\Tests.exe"
if ($LASTEXITCODE -ne 0) { throw '테스트 실패' }

& $csc /nologo /target:winexe /codepage:65001 /optimize+ /out:"$dist\build\FtpDrive.exe" @refs "$root\src\FtpDrive.cs"
if ($LASTEXITCODE -ne 0) { throw "컴파일 실패 (종료 코드 $LASTEXITCODE)" }

& $iscc /Q "$root\installer\FtpDrive.iss"
if ($LASTEXITCODE -ne 0) { throw "설치 파일 생성 실패 (종료 코드 $LASTEXITCODE)" }
Get-Item "$dist\FtpDrive-Setup.exe" | Select-Object FullName, Length
