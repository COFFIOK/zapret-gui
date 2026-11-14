# Установщик Zapret GUI

- `setup-template.ps1` — скрипт установщика с метками `__PAYLOAD__` (Zapret.exe, zapret.ico, ZPixel.ttf в base64) и `__VERSION__`.
- `stub/` — заглушка `ZapretSetup.exe` на Go. Скрипт не встраивается при компиляции, а дописывается в конец exe:
  `[stub.exe][setup.ps1][8 байт длины, little-endian]["ZSETUP01"]`.
  Заглушка кладёт скрипт в `%SystemRoot%\Temp` и запускает его через PowerShell (`-Auto` — тихое обновление).
- Сборка заглушки: `stub/build-stub.sh` (Go + `rsrc`). Готовый `stub.exe` в репозиторий не кладётся (`*.exe` в .gitignore).
- Сборка релиза делается скриптом `build-release.cmd` в рабочей папке: он ставит версию в `AssemblyInfo.cs`,
  собирает приложение и склеивает установщик.
