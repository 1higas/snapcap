@echo off
rem Builds bin\ChargeLimit.exe with the C# compiler that ships with the .NET Framework in Windows.
rem No SDK or Visual Studio needed.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\FrameworkArm64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)

if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /debug- ^
  /win32manifest:src\app.manifest ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /out:bin\ChargeLimit.exe src\*.cs
