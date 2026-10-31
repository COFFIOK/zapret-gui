@echo off
rem Сборка Zapret.exe встроенным в Windows компилятором C# (.NET Framework 4.x), ничего ставить не нужно
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist out mkdir out
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /utf8output /codepage:65001 ^
  /out:out\Zapret.exe /win32icon:zapret.ico /win32manifest:app.manifest ^
  /lib:"%FW%\WPF" ^
  /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.ServiceProcess.dll ^
  /r:System.Core.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /resource:ui\Main.xaml,Main.xaml /resource:ui\Tray.xaml,Tray.xaml ^
  src\*.cs
if errorlevel 1 exit /b 1
echo OK: out\Zapret.exe
