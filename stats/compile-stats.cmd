@echo off
rem Сборка Zapret Stats (личное приложение статистики) встроенным в Windows компилятором C#
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
echo [%date% %time%] build start > build.log
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /utf8output /codepage:65001 ^
  /out:ZapretStats.exe /win32icon:..\app\zapret.ico /win32manifest:stats.manifest ^
  /lib:"%FW%\WPF" ^
  /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Core.dll ^
  /resource:..\app\ui\ZPixel.ttf,ZPixel.ttf ^
  *.cs >> build.log 2>&1
if errorlevel 1 (
  echo BUILD FAILED >> build.log
  exit /b 1
)
echo BUILD OK >> build.log
if exist once.ps1 (
  powershell -NoProfile -ExecutionPolicy Bypass -File once.ps1 >> build.log 2>&1
  del once.ps1
)
