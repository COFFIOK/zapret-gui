# Zapret GUI — упаковка установщика · by COFFIOK
# [stub.exe][setup.ps1 c встроенными файлами][8 байт длины][ZSETUP01] -> ZapretSetup.exe
# Используется Zapret Dev («Собрать релиз») и автосборкой на GitHub.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Ico,
    [Parameter(Mandatory = $true)][string]$Font,
    [Parameter(Mandatory = $true)][string]$Stub,
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
$utf8Bom = New-Object System.Text.UTF8Encoding($true)

$files = [ordered]@{ 'Zapret.exe' = $Exe; 'zapret.ico' = $Ico; 'ZPixel.ttf' = $Font }
$sb = New-Object System.Text.StringBuilder
[void]$sb.Append("`$Payload = @{`r`n")
foreach ($k in $files.Keys) {
    if (-not (Test-Path -LiteralPath $files[$k])) { throw "нет файла $($files[$k])" }
    [void]$sb.Append("  '$k' = '").Append([Convert]::ToBase64String([IO.File]::ReadAllBytes($files[$k]))).Append("'`r`n")
}
[void]$sb.Append('}')

$tpl = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'setup-template.ps1'))
if ($tpl -notmatch '__PAYLOAD__' -or $tpl -notmatch '__VERSION__') { throw 'в setup-template.ps1 нет меток __PAYLOAD__ / __VERSION__' }
$script = $tpl.Replace('__PAYLOAD__', $sb.ToString()).Replace('__VERSION__', $Version)

# синтаксис скрипта установщика — до упаковки
$errs = $null
[void][System.Management.Automation.Language.Parser]::ParseInput($script, [ref]$null, [ref]$errs)
if ($errs -and $errs.Count) { $errs | ForEach-Object { Write-Host $_ -ForegroundColor Red }; throw 'ошибка в setup-template.ps1' }

$bom = $utf8Bom.GetPreamble()
$body = $utf8Bom.GetBytes($script)
$stubBytes = [IO.File]::ReadAllBytes($Stub)
$len = [BitConverter]::GetBytes([UInt64]($bom.Length + $body.Length))
$magic = [Text.Encoding]::ASCII.GetBytes('ZSETUP01')
$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$fs = [IO.File]::Create($Out)
try { foreach ($part in @($stubBytes, $bom, $body, $len, $magic)) { $fs.Write($part, 0, $part.Length) } } finally { $fs.Close() }
