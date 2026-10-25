@echo off
rem Builds bin\Snapcap-Portable.exe and bin\Snapcap-Setup.exe (the same file; the name makes
rem the second one run as the installer) with the C# compiler that ships with the .NET
rem Framework in Windows. No SDK or Visual Studio needed.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\FrameworkArm64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)

rem "build.cmd demo" builds bin\Snapcap-Demo.exe, which accepts --demo (made-up chart for screenshots).
set "OUT=bin\Snapcap-Portable.exe"
set "DEFINE="
if /i "%~1"=="demo" (
  set "OUT=bin\Snapcap-Demo.exe"
  set "DEFINE=/define:DEMO"
)

if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /debug- %DEFINE% ^
  /win32manifest:src\app.manifest /win32icon:src\app.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /out:%OUT% src\*.cs || exit /b 1
if not defined DEFINE copy /y bin\Snapcap-Portable.exe bin\Snapcap-Setup.exe >nul
