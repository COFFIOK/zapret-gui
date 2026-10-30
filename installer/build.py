"""Сборка установщика: вшивает приложение и значок в setup.ps1 для launcher/ (Go).
   python installer/build.py  ->  installer/launcher/setup.ps1
   cd installer/launcher && rsrc -manifest app.manifest -ico ../../app/zapret.ico -arch amd64 -o rsrc_windows_amd64.syso
   GOOS=windows GOARCH=amd64 go build -ldflags "-H=windowsgui -s -w" -o ZapretSetup.exe ."""
import base64, pathlib
root = pathlib.Path(__file__).resolve().parent.parent
tpl = (root / 'installer' / 'setup-template.ps1').read_text(encoding='utf-8')
gui = (root / 'app' / 'zapret-gui.ps1').read_bytes()
ico = (root / 'app' / 'zapret.ico').read_bytes()
first, rest = tpl.split('\n', 1)
payload = "$Payload = @{\n  'zapret-gui.ps1' = '%s'\n  'zapret.ico' = '%s'\n}\n" % (base64.b64encode(gui).decode(), base64.b64encode(ico).decode())
s = first + '\n' + payload + rest
(root / 'installer' / 'launcher' / 'setup.ps1').write_bytes(b'\xef\xbb\xbf' + s.replace('\r\n', '\n').replace('\n', '\r\n').encode('utf-8'))
print('ok')
