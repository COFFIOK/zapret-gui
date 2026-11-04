import base64,sys
tpl=open('setup-template.ps1',encoding='utf-8-sig').read().replace('\r\n','\n')
exe=open(sys.argv[1] if len(sys.argv)>1 else '/mnt/user-data/uploads/Zapret/csharp/out/Zapret.exe','rb').read()
ico=open('zapret.ico','rb').read()
import os
_f=os.path.join(os.path.dirname(os.path.abspath(__file__)),'..','app','ui','ZPixel.ttf')
font=open(_f if os.path.exists(_f) else '/mnt/user-data/outputs/csharp/ui/ZPixel.ttf','rb').read()
first,rest=tpl.split('\n',1)
assert first.startswith('param(')
payload="$Payload = @{\n  'Zapret.exe' = '%s'\n  'zapret.ico' = '%s'\n  'ZPixel.ttf' = '%s'\n}\n"%(base64.b64encode(exe).decode(),base64.b64encode(ico).decode(),base64.b64encode(font).decode())
out=(first+'\n'+payload+rest).replace('\n','\r\n')
open('/tmp/claude-0/s/gobuild/setup.ps1','wb').write(b'\xef\xbb\xbf'+out.encode('utf-8'))
print('setup.ps1',len(out))
