@echo off
rem Сборка ZapretDev.exe (пульт разработчика: сборка, релиз с подписью ключом с флешки, Zapret Stats).
rem Результат кладётся в корень папки: ..\ZapretDev.exe. Двойной клик — и готово.
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
set FONT=..\ui\ZPixel.ttf
if not exist "%FONT%" set FONT=..\app\ui\ZPixel.ttf
rem свой значок Zapret Dev (янтарный «Z_»), чтобы не путать с Zapret
set ICO=dev.ico
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /utf8output /codepage:65001 /define:DEVTOOL /main:ZapretDevTool.Program ^
  /out:..\ZapretDev.exe /win32icon:%ICO% ^
  /lib:"%FW%\WPF" ^
  /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Core.dll ^
  /resource:%FONT%,ZPixel.ttf ^
  *.cs ..\stats\StatsApp.cs ..\stats\ChatView.cs > build-devtool.log 2>&1
if errorlevel 1 (
  type build-devtool.log
  echo BUILD FAILED
  if not "%1"=="ci" pause
  exit /b 1
)
type build-devtool.log
echo BUILD OK: ZapretDev.exe
if "%1"=="ci" exit /b 0
rem ярлыки «Zapret Dev» на рабочем столе и в меню «Пуск» — открывать как обычное приложение
for %%I in ("..\ZapretDev.exe") do (set "EXE=%%~fI" & set "ROOT=%%~dpI")
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; foreach ($d in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) { $l = $ws.CreateShortcut((Join-Path $d 'Zapret Dev.lnk')); $l.TargetPath = '%EXE%'; $l.WorkingDirectory = '%ROOT%'; $l.IconLocation = '%EXE%,0'; $l.Description = 'Zapret Dev'; $l.Save() }"
echo Shortcuts: Desktop + Start menu "Zapret Dev"
start "" "%EXE%"
