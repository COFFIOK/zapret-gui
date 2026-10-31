"""Сборка установщика: вшивает Zapret.exe и значок в setup.ps1 для launcher/ (Go).
   1) app\build.cmd (на Windows)            ->  app/out/Zapret.exe
   2) python installer/build.py              ->  installer/launcher/setup.ps1
   3) cd installer/launcher && rsrc -manifest app.manifest -ico ../../app/zapret.ico -arch amd64 -o rsrc_windows_amd64.syso
   4) GOOS=windows GOARCH=amd64 go build -ldflags "-H=windowsgui -s -w" -o ZapretSetup.exe ."""
import base64, pathlib
root = pathlib.Path(__file__).resolve().parent.parent
tpl = (root / 'installer' / 'setup-template.ps1').read_text(encoding='utf-8-sig')
exe = (root / 'app' / 'out' / 'Zapret.exe').read_bytes()
ico = (root / 'app' / 'zapret.ico').read_bytes()
first, rest = tpl.replace('\r\n', '\n').split('\n', 1)
payload = "$Payload = @{\n  'Zapret.exe' = '%s'\n  'zapret.ico' = '%s'\n}\n" % (base64.b64encode(exe).decode(), base64.b64encode(ico).decode())
s = first + '\n' + payload + rest
(root / 'installer' / 'launcher' / 'setup.ps1').write_bytes(b'\xef\xbb\xbf' + s.replace('\n', '\r\n').encode('utf-8'))
print('ok')
