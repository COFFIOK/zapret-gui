#!/bin/sh
# Пересборка заглушки установщика (нужен Go и rsrc: go install github.com/akavel/rsrc@latest)
# Результат: ..\stub.exe. Обычной сборке релиза это не нужно — stub.exe уже лежит в installer\.
set -e
cd "$(dirname "$0")"
rsrc -arch amd64 -manifest setup.manifest -ico ../../app/zapret.ico -o rsrc_windows_amd64.syso
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags "-s -w -H windowsgui" -o ../stub.exe .
rm -f rsrc_windows_amd64.syso
