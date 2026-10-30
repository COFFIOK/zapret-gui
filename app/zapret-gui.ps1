# Zapret GUI - графическая оболочка для zapret-discord-youtube
# Запуск: Zapret.bat (сам попросит права администратора)

param([switch]$AutoStart)
$ErrorActionPreference = 'Stop'
# ---- версия приложения и где искать обновления ----
$AppVersion = '1.2.1'
$UpdateRepo = 'COFFIOK/zapret-gui'   # GitHub: владелец/репозиторий, в Releases лежит ZapretSetup.exe
$GuiDir = $PSScriptRoot
$LogFile = Join-Path $GuiDir 'gui-error.log'
$SettingsFile = Join-Path $GuiDir 'gui-settings.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Write-Log($msg) {
    try { Add-Content -LiteralPath $LogFile -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg) -Encoding UTF8 } catch {}
}

# ---- одна копия приложения: повторный запуск просто показывает уже открытое окно (например, из трея) ----
$script:instCreated = $false
$script:instMutex = New-Object System.Threading.Mutex($true, 'Local\ZapretGUI.Instance', [ref]$script:instCreated)
$script:showEvent = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, 'Local\ZapretGUI.Show')
if (-not $script:instCreated) {
    # подождём чуть-чуть: вдруг это перезапуск и старое окно как раз закрывается
    try { $script:instCreated = $script:instMutex.WaitOne(1500) } catch { $script:instCreated = $true }   # прошлая копия закрылась аварийно
    if (-not $script:instCreated) { [void]$script:showEvent.Set(); exit 0 }
}
function Release-Instance { try { if ($script:instCreated) { $script:instMutex.ReleaseMutex(); $script:instCreated = $false } } catch {} }

try {
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing

# Имя, значок и команда перезапуска окна на панели задач (вместо «Windows PowerShell»)
try {
    Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
namespace ZapretGuiShell {
  [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct PropertyKey { public Guid fmtid; public uint pid; public PropertyKey(Guid g, uint p) { fmtid = g; pid = p; } }
  [StructLayout(LayoutKind.Explicit)] public struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }
  [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  public interface IPropertyStore { int GetCount(out uint c); int GetAt(uint i, out PropertyKey k); int GetValue(ref PropertyKey k, out PropVariant v); int SetValue(ref PropertyKey k, ref PropVariant v); int Commit(); }
  public static class TaskbarId {
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ps);
    static void Set(IPropertyStore s, uint pid, string val) {
      var k = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid);
      var v = new PropVariant(); v.vt = 31; v.p = Marshal.StringToCoTaskMemUni(val);
      s.SetValue(ref k, ref v); Marshal.FreeCoTaskMem(v.p);
    }
    public static void Apply(IntPtr hwnd, string id, string name, string icon, string cmd) {
      Guid iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"); IPropertyStore s;
      if (SHGetPropertyStoreForWindow(hwnd, ref iid, out s) != 0 || s == null) return;
      Set(s, 5, id); if (cmd != null) Set(s, 2, cmd); Set(s, 4, name); if (icon != null) Set(s, 3, icon);
      s.Commit(); Marshal.ReleaseComObject(s);
    }
  }
}
"@
} catch { Write-Log ($_ | Out-String) }

# Своё имя приложения для панели задач (иначе Windows показывает значок PowerShell)
try {
    Add-Type -Namespace ZapretGui -Name Shell -MemberDefinition '[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);'
    [void][ZapretGui.Shell]::SetCurrentProcessExplicitAppUserModelID('Zapret.GUI')
} catch { Write-Log ($_ | Out-String) }

# ======================= Поиск папки zapret =======================
function Find-ZapretFolders {
    $home_ = [Environment]::GetFolderPath('UserProfile')
    $dirs = @(
        $GuiDir,
        (Split-Path -Parent $GuiDir),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Zapret'),
        [Environment]::GetFolderPath('Desktop'),
        (Join-Path $home_ 'Desktop'),
        (Join-Path $home_ 'OneDrive\Desktop'),
        (Join-Path $home_ 'Downloads'),
        'C:\'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | ForEach-Object { (Resolve-Path -LiteralPath $_).Path } | Select-Object -Unique
    $found = @()
    foreach ($d in $dirs) {
        Get-ChildItem -LiteralPath $d -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'zapret*' } | ForEach-Object {
            $cands = @($_) + @(Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'zapret*' })
            foreach ($c in $cands) {
                if (Test-Path -LiteralPath (Join-Path $c.FullName 'bin\winws.exe')) {
                    $ver = [version]'0.0'
                    if ($c.Name -match '(\d+(\.\d+)+)') { try { $ver = [version]$matches[1] } catch {} }
                    $found += [PSCustomObject]@{ Path = $c.FullName; Name = $c.Name; Version = $ver; Time = $c.CreationTime }
                }
            }
        }
    }
    return @($found | Sort-Object Path -Unique | Sort-Object Version, Time)
}

$Settings = @{ strategy = 'general' }
if (Test-Path -LiteralPath $SettingsFile) {
    try {
        $j = Get-Content -LiteralPath $SettingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($p in $j.PSObject.Properties) { $Settings[$p.Name] = $p.Value }
    } catch {}
}
$Mon = [hashtable]::Synchronized(@{ Stop = $false; At = 0L; Winws = $false; WinwsName = $null; Svc = $null; SvcName = $null; Tg = $false; TgConn = $false; Fast = $false; Idle = $false; Wake = $false })
function Save-Settings { try { [IO.File]::WriteAllText($SettingsFile, ($Settings | ConvertTo-Json), $utf8) } catch {} }


# ======================= Тема, акцентный цвет, язык =======================
$script:Lang   = if ($Settings.lang) { "$($Settings.lang)" } else { 'ru' }
$script:Theme  = if ($Settings.theme) { "$($Settings.theme)" } else { 'dark' }
$script:Accent = if ($Settings.accent) { "$($Settings.accent)".ToUpper() } else { '#3DDC97' }

function Hex-Rgb([string]$h) { $h = $h.TrimStart('#'); if ($h.Length -eq 8) { $h = $h.Substring(2) }; @([Convert]::ToInt32($h.Substring(0, 2), 16), [Convert]::ToInt32($h.Substring(2, 2), 16), [Convert]::ToInt32($h.Substring(4, 2), 16)) }
function Mix([string]$a, [string]$b, [double]$wa) {
    $x = Hex-Rgb $a; $y = Hex-Rgb $b
    '#' + (-join (0..2 | ForEach-Object { ([int][Math]::Round($x[$_] * $wa + $y[$_] * (1 - $wa))).ToString('X2') }))
}

# Тёмная тема — исходные цвета. Для светлой — замены.
$LightMap = @{
    '#121419' = '#F4F6F9'; '#252933' = '#DDE1E8'; '#1A1D24' = '#FFFFFF'; '#22262F' = '#EEF1F5'; '#2E3340' = '#D5DAE2'
    '#2A2E38' = '#E3E7EE'; '#262A33' = '#E6EAF0'; '#232731' = '#E1E5EC'; '#171A21' = '#FFFFFF'; '#171A20' = '#F0F2F6'
    '#1E2129' = '#F3F5F8'; '#20232B' = '#F1F3F7'; '#232833' = '#E8ECF2'; '#3A3F4D' = '#C7CDD8'
    '#E6E8EE' = '#1C2028'; '#8A90A0' = '#6B7280'; '#5A6070' = '#9AA1AE'; '#9AA3B5' = '#5B6372'; '#B8BDC9' = '#4A5160'; '#C9CED8' = '#4A5160'
    '#C0090A0D' = '#66101418'; '#B0090A0D' = '#55101418'
    '#3A2328' = '#FDE7EA'; '#FF8C99' = '#D9364B'; '#C9606F' = '#C93A50'
    '#2B5664' = '#9BDDEF'; '#CFEFF8' = '#2B5664'
}
$ColorMap = New-Object System.Collections.Hashtable ([StringComparer]::OrdinalIgnoreCase)
if ($script:Theme -eq 'light') { foreach ($k in $LightMap.Keys) { $ColorMap[$k] = $LightMap[$k] } }
# цвета, которые зависят от акцента
$A = $script:Accent
if ($script:Theme -eq 'light') {
    $tint = Mix $A '#FFFFFF' 0.14
    foreach ($k in '#1B3329', '#18302A', '#1E2B26', '#1B2B25', '#1A2621') { $ColorMap[$k] = $tint }
    $ColorMap['#2D5A47'] = Mix $A '#FFFFFF' 0.45
    $ColorMap['#5A9C80'] = Mix $A '#000000' 0.65
    $ColorMap['#7FE8B8'] = Mix $A '#000000' 0.65
    $ColorMap['#BFEBD6'] = Mix $A '#000000' 0.55
} else {
    $tint = Mix $A '#171A21' 0.16
    foreach ($k in '#1B3329', '#18302A', '#1E2B26', '#1B2B25', '#1A2621') { $ColorMap[$k] = $tint }
    $ColorMap['#2D5A47'] = Mix $A '#171A21' 0.38
    $ColorMap['#5A9C80'] = Mix $A '#171A21' 0.6
    $ColorMap['#7FE8B8'] = Mix $A '#FFFFFF' 0.6
    $ColorMap['#BFEBD6'] = Mix $A '#FFFFFF' 0.3
}
$ColorMap['#2BB57A'] = Mix $A '#000000' 0.8
$ColorMap['#3DDC97'] = $A

function Map-Color([string]$hex) { if ($ColorMap.ContainsKey($hex)) { $ColorMap[$hex] } else { $hex } }
function Color-Of([string]$hex) { [System.Windows.Media.ColorConverter]::ConvertFromString((Map-Color $hex)) }
function Apply-Theme([string]$text) { [regex]::Replace($text, '#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b', { param($m) Map-Color $m.Value }) }

# ---------- перевод ----------
$TrExact = New-Object System.Collections.Hashtable ([StringComparer]::Ordinal)
$TrPairs = @(
    @('Подключить Telegram на телефоне по QR-коду', 'Connect Telegram on your phone via QR code'),
    @('Telegram на телефоне', 'Telegram on your phone'),
    @('1. Телефон должен быть в той же Wi-Fi сети, что и компьютер', '1. Your phone must be on the same Wi-Fi as the computer'),
    @('2. Наведи камеру телефона на код', '2. Point the phone camera at the code'),
    @('3. В Telegram нажми «Подключить прокси»', '3. In Telegram tap "Connect proxy"'),
    @('Готовлю…', 'Preparing…'),
    @('Скопировать ссылку', 'Copy link'),
    @('Не нашёл адрес компьютера в домашней сети', 'Couldn''t find this computer''s home network address'),
    @('Ссылка скопирована — можно отправить её себе на телефон', 'Link copied — you can send it to your phone'),
    @('Работает · Telegram подключён ✓', 'Running · Telegram connected ✓'),
    @('Подключён к Telegram', 'Connected to Telegram'),
    @('Открыть Zapret', 'Open Zapret'),
    @('Включить zapret', 'Turn zapret on'),
    @('Выключить zapret', 'Turn zapret off'),
    @('Включить Telegram-прокси', 'Turn Telegram proxy on'),
    @('Выключить Telegram-прокси', 'Turn Telegram proxy off'),
    @('Выйти из Zapret', 'Quit Zapret'),
    @('Свернуть в трей', 'Minimize to tray'),
    @('Zapret работает в трее. Нажми на значок, чтобы открыть, правая кнопка — меню.', 'Zapret is in the tray. Click the icon to open it, right-click for the menu.'),
    @('Zapret · работает', 'Zapret · running'),
    @('Zapret · выключен', 'Zapret · off'),
    @('Подобрать за меня', 'Pick for me'),
    @('Быстро проверить все стратегии на моих сайтах и включить лучшую', 'Quickly test all strategies on my sites and turn on the best one'),
    @('Нечего проверять — добавь свои сайты', 'Nothing to test — add your sites'),
    @('Подбираю лучшую стратегию для твоих сайтов…', 'Finding the best strategy for your sites…'),
    @('Основное', 'Basics'),
    @('Поделиться', 'Share'),
    @('Вставить', 'Paste'),
    @('Выбрать сайты и скопировать их, чтобы отправить другу', 'Pick sites and copy them to send to a friend'),
    @('Добавить сайты из скопированного списка', 'Add sites from a copied list'),
    @('Поделиться сайтами', 'Share sites'),
    @('Отметь, чем поделиться. Список скопируется — останется вставить его в сообщение.', 'Tick what to share. The list will be copied — just paste it into a message.'),
    @('Скопировать', 'Copy'),
    @('Добавить сайты', 'Add sites'),
    @('Вот что нашлось в скопированном тексте. Отметь, что добавить к себе.', 'Here is what was found in the copied text. Tick what to add.'),
    @('Сохранить в файл', 'Save to file'),
    @('Снять все', 'Clear all'),
    @('Выбрать все', 'Select all'),
    @('Текстовый файл', 'Text file'),
    @('Пока нечем делиться — добавь сайты', 'Nothing to share yet — add some sites'),
    @('Сначала скопируй список сайтов, который тебе прислали', 'First copy the site list you were sent'),
    @('В скопированном тексте не нашлось адресов сайтов', 'No site addresses found in the copied text'),
    @('Импорт списка из файла', 'Import a list from a file'),
    @('Вставить список из буфера обмена', 'Paste a list from the clipboard'),
    @('Экспорт всех моих сайтов в файл', 'Export all my sites to a file'),
    @('Экспорт группы', 'Export group'),
    @('Копировать', 'Copy'),
    @('Список Zapret', 'Zapret list'),
    @('Текст', 'Text'),
    @('Списки сайтов', 'Site lists'),
    @('Все файлы', 'All files'),
    @('В списке не нашлось адресов сайтов', 'No site addresses found in the list'),
    @('Пока нечего экспортировать', 'Nothing to export yet'),
    @('Буфер обмена пуст', 'The clipboard is empty'),
    @('Русский', 'Русский'),
    @('Обход', 'Bypass'),
    @('Запуск', 'Startup'),
    @('Вид', 'Look'),
    @('Инструменты', 'Tools'),
    @('О программе', 'About'),
    @('как zapret обходит блокировки', 'how zapret bypasses blocks'),
    @('как открывается приложение', 'how the app opens'),
    @('приложения и zapret', 'app and zapret'),
    @('применяется сразу, окно перезапустится', 'applies instantly, the window will restart'),
    @('обслуживание zapret и приложения', 'zapret and app maintenance'),
    @('служба, статус и проверка zapret', 'service, status and zapret checks'),
    @('списки, батники и логи', 'lists, bat files and logs'),
    @('если вышла новая версия zapret', 'when a new zapret version is out'),
    @('скачать новую версию с GitHub', 'get the new version from GitHub'),
    @('Страница на GitHub', 'GitHub page'),
    @('описание, релизы и обновления', 'description, releases and updates'),
    @('Настройки', 'Settings'),
    @('Выключен', 'Off'),
    @('Работает', 'Running'),
    @('Нажми, чтобы включить', 'Click to turn on'),
    @('Стратегия', 'Strategy'),
    @('Подключить', 'Connect'),
    @('Настроить прокси в Telegram Desktop', 'Set up the proxy in Telegram Desktop'),
    @('Тест стратегий', 'Strategy test'),
    @('Обновления', 'Updates'),
    @('Мои сайты', 'My sites'),
    @('например: google.com', 'e.g. google.com'),
    @('Добавить', 'Add'),
    @('Перезапустить', 'Restart'),
    @('Изменения применятся после перезапуска', 'Changes apply after a restart'),
    @('Обновить', 'Update'),
    @('Позже', 'Later'),
    @('Игровой фильтр', 'Game filter'),
    @('обход для игр (порты 1024+)', 'bypass for games (ports 1024+)'),
    @('IPSet фильтр', 'IPSet filter'),
    @('обход по списку IP-адресов', 'bypass by IP list'),
    @('Показывать окно winws', 'Show winws window'),
    @('чёрное окно с логом; обычно не нужно', 'black log window; usually not needed'),
    @('Запускать без запроса прав', 'Run without admin prompt'),
    @('Windows не будет спрашивать разрешение при запуске', 'Windows won''t ask for permission on launch'),
    @('Проверять обновления при запуске', 'Check for updates on launch'),
    @('ИНСТРУМЕНТЫ', 'TOOLS'),
    @('Сервис и диагностика', 'Service & diagnostics'),
    @('Открыть папку zapret', 'Open zapret folder'),
    @('Перенести настройки из старой версии', 'Move settings from an old version'),
    @('Проверить обновления приложения', 'Check for app updates'),
    @('Папка zapret', 'zapret folder'),
    @('Версия', 'Version'),
    @('Приложение', 'App'),
    @('Проверяю, какая стратегия лучше открывает твои сайты', 'Finding the strategy that opens your sites best'),
    @('ЧТО ПРОВЕРЯТЬ', 'WHAT TO CHECK'),
    @('КАКИЕ СТРАТЕГИИ', 'WHICH STRATEGIES'),
    @('Запустить тест', 'Run test'),
    @('РЕЗУЛЬТАТЫ · нажми на строку, чтобы увидеть сайты', 'RESULTS · click a row to see the sites'),
    @('Перенос настроек', 'Move settings'),
    @('Твои сайты, группы и настройки переедут в новую версию zapret', 'Your sites, groups and settings will move to the new zapret version'),
    @('ОТКУДА · старая', 'FROM · old'),
    @('КУДА · новая', 'TO · new'),
    @('Новая версия zapret не найдена', 'No new zapret version found'),
    @('Скачай новую версию и распакуй её в папку Zapret рядом с текущей. Потом открой это окно снова.', 'Download the new version and unpack it into the Zapret folder next to the current one. Then open this window again.'),
    @('Открыть папку Zapret', 'Open Zapret folder'),
    @('Посмотреть демо', 'Watch demo'),
    @('Перенести', 'Move'),
    @('Готово! Оригиналы файлов автора сохранены как .bak', 'Done! The author''s original files are saved as .bak'),
    @('TCP и UDP', 'TCP and UDP'),
    @('Только TCP', 'TCP only'),
    @('Только UDP', 'UDP only'),
    @('По списку', 'By list'),
    @('Все IP', 'All IPs'),
    @('Запуск…', 'Starting…'),
    @('Остановка…', 'Stopping…'),
    @('Не удалось запустить', 'Failed to start'),
    @('winws.exe не запустился. Попробуй другую стратегию.', 'winws.exe didn''t start. Try another strategy.'),
    @('winws.exe не запустился. Попробуй другую стратегию или включи «Показывать окно winws», чтобы увидеть ошибку.', 'winws.exe didn''t start. Try another strategy or turn on "Show winws window" to see the error.'),
    @('Нужны права администратора — запусти через Zapret.bat', 'Admin rights needed — launch via Zapret.bat'),
    @('Выбери стратегию', 'Choose a strategy'),
    @('Служба остановлена. Она снова запустится после перезагрузки ПК.', 'Service stopped. It will start again after a reboot.'),
    @('Работает как служба', 'Running as a service'),
    @('DNS-серверы', 'DNS servers'),
    @('Twitch и смайлы', 'Twitch & emotes'),
    @('Прочее', 'Other'),
    @('Другое', 'Other'),
    @('Удалить', 'Remove'),
    @('встроенный список zapret · только просмотр', 'built-in zapret list · read only'),
    @('Удалить группу', 'Delete group'),
    @('МОИ', 'MINE'),
    @('Пока пусто. Добавь сайт, который не открывается.', 'Empty for now. Add a site that doesn''t open.'),
    @('ВСТРОЕННЫЕ В ZAPRET', 'BUILT INTO ZAPRET'),
    @('Нет сохранённого списка IP. Обнови его: «Сервис и диагностика» → пункт 8.', 'No saved IP list. Update it: "Service & diagnostics" → item 8.'),
    @('Готово: Zapret будет открываться без запроса прав', 'Done: Zapret will open without an admin prompt'),
    @('Отключено: Windows снова будет спрашивать разрешение', 'Disabled: Windows will ask for permission again'),
    @('Все', 'All'),
    @('Выбери хотя бы одну группу и одну стратегию', 'Pick at least one group and one strategy'),
    @('zapret работает как служба. Удали службу в «Сервис и диагностика», чтобы тестировать.', 'zapret runs as a service. Remove the service in "Service & diagnostics" to test.'),
    @('Идёт тест стратегий…', 'Strategy test in progress…'),
    @('Остановить', 'Stop'),
    @('★ лучшая', '★ best'),
    @('не запустилась', 'failed to start'),
    @('Включить', 'Turn on'),
    @('Новый тест', 'New test'),
    @('Останавливаю…', 'Stopping…'),
    @('Мои сайты и группы', 'My sites and groups'),
    @('Исключения', 'Exclusions'),
    @('Игровой фильтр и автообновление', 'Game filter and auto-update'),
    @('Сайты, вписанные в списки zapret', 'Sites added to zapret lists'),
    @('list-general, list-google, исключения', 'list-general, list-google, exclusions'),
    @('Сайты для теста', 'Test sites'),
    @('Меню выбора групп в тесте', 'Group menu in the test'),
    @('файлов: 2', 'files: 2'),
    @('настройки перенесены', 'settings moved'),
    @('в новой версии уже всё есть', 'the new version already has everything'),
    @('добавлено: 5 сайтов', 'added: 5 sites'),
    @('меню вставлено', 'menu added'),
    @('нечего переносить', 'nothing to move'),
    @('используются стандартные', 'defaults are used'),
    @('уже есть', 'already there'),
    @('теста нет в новой версии', 'no test in the new version'),
    @('автор сильно изменил тест — меню не вставилось', 'the author changed the test a lot — menu not added'),
    @('Выбери разные версии: откуда и куда', 'Pick different versions: from and to'),
    @('Переношу…', 'Moving…'),
    @('Готово', 'Done'),
    @('Это было демо — файлы не менялись. Так всё пройдёт при настоящем переносе.', 'That was a demo — no files changed. A real move looks just like this.'),
    @('Перезапускаю zapret на новой версии…', 'Restarting zapret on the new version…'),
    @('Перезапусти приложение, чтобы перейти на новую версию', 'Restart the app to switch to the new version'),
    @('v-next (демо)', 'v-next (demo)'),
    @('Telegram-прокси запущен. Нажми «Подключить», чтобы настроить Telegram Desktop.', 'Telegram proxy started. Click "Connect" to set up Telegram Desktop.'),
    @('Скачиваю tg-ws-proxy…', 'Downloading tg-ws-proxy…'),
    @('Не удалось скачать', 'Download failed'),
    @('Сначала включи Telegram-прокси', 'Turn on the Telegram proxy first'),
    @('Telegram не открылся. Ссылка скопирована — отправь её себе в «Избранное» и нажми.', 'Telegram didn''t open. The link is copied — send it to your Saved Messages and click it.'),
    @('Открываю Telegram — нажми там «Подключить прокси».', 'Opening Telegram — click "Connect proxy" there.'),
    @('Скачиваю…', 'Downloading…'),
    @('Повторить', 'Retry'),
    @('Устанавливаю…', 'Installing…'),
    @('Проверяю обновления приложения…', 'Checking for app updates…'),
    @('Проверяю…', 'Checking…'),
    @('Нет связи', 'No connection'),
    @('Версия актуальна ✓', 'Up to date ✓'),
    @('Тема', 'Theme'),
    @('Тёмная', 'Dark'),
    @('Светлая', 'Light'),
    @('Акцентный цвет', 'Accent color'),
    @('Язык', 'Language'),
    @('внешний вид окна', 'window appearance'),
    @('цвет кнопок и подсветки', 'color of buttons and highlights'),
    @('язык интерфейса', 'interface language'),
    @('ВНЕШНИЙ ВИД', 'APPEARANCE'),
    @('Перезапускаю приложение…', 'Restarting the app…'),
    @('тестовая сборка', 'test build'),
    @('Удалить Zapret?', 'Remove Zapret?')
)
foreach ($pp in $TrPairs) { $TrExact[$pp[0]] = $pp[1] }

function Plural-En([int]$n) { if ($n -eq 1) { "$n site" } else { "$n sites" } }
$TrRx = @(
    @('^Подобрал лучшую стратегию: (.+) — включаю$', 'Best strategy found: $1 — turning it on'),
    @('^Выбрано: (\d+) сайт(а|ов)? из (\d+)$', { param($m) "Selected: $(Plural-En ([int]$m[1])) of $($m[3])" }),
    @('^Скопировано: (\d+) сайт(а|ов)?\. Вставь в сообщение другу \(Ctrl\+V\)$', { param($m) "Copied: $(Plural-En ([int]$m[1])). Paste it into a message (Ctrl+V)" }),
    @('^Добавлено: (\d+) сайт(а|ов)?$', { param($m) "Added: $(Plural-En ([int]$m[1]))" }),
    @('^Все сайты из списка у тебя уже есть \((\d+)\)$', 'You already have all sites from the list ($1)'),
    @('^Сохранено: (.+)$', 'Saved: $1'),
    @('^Все сайты из списка уже есть \((\d+)\)$', 'All sites from the list are already there ($1)'),
    @('^Импортировано: (\d+) сайт(а|ов)?( · уже были: (\d+))?$', { param($m) 'Imported: ' + (Plural-En ([int]$m[1])) + $(if ($m[4]) { " · already there: $($m[4])" } else { '' }) }),
    @('^Группа (.+) скопирована — её можно вставить кнопкой «Вставить из буфера»$', 'Group $1 copied — paste it with the "Paste" button'),
    @('^(\d+) сайт(а|ов)?$', { param($m) Plural-En ([int]$m[1]) }),
    @('^Стратегия: (.+)$', 'Strategy: $1'),
    @('^zapret включён · (.+)$', 'zapret is on · $1'),
    @('^Работает как служба · (.+)$', 'Running as a service · $1'),
    @('^Работает · (.+)$', 'Running · $1'),
    @('^Добавить сайт в группу (.+)$', 'Add a site to group $1'),
    @('^Удалён (.+)$', 'Removed $1'),
    @('^Группа (.+) удалена$', 'Group $1 deleted'),
    @('^«(.+)» не похоже на адрес сайта$', '"$1" doesn''t look like a site address'),
    @('^(.+) уже есть в группе (.+)$', '$1 is already in group $2'),
    @('^Добавлен (.+) → (.+)$', 'Added $1 → $2'),
    @('^Игровой фильтр: (.+)$', { param($m) 'Game filter: ' + (T $m[1]) }),
    @('^IPSet: (.+)$', { param($m) 'IPSet: ' + (T $m[1]) }),
    @('^(\d+) стратегий × (\d+) сайтов · примерно (\d+) мин$', '$1 strategies × $2 sites · about $3 min'),
    @('^Тест остановлен · проверено (\d+) из (\d+)$', 'Test stopped · checked $1 of $2'),
    @('^Готово за (\d+) мин (\d+) с · лучшая: (.+)$', 'Done in $1 min $2 s · best: $3'),
    @('^Ошибка теста: (.+)$', 'Test error: $1'),
    @('^Тест завершён\. Лучшая стратегия: (.+)$', 'Test finished. Best strategy: $1'),
    @('^Стратегия (\d+) из (\d+) · (.+) · (запускаю|проверяю сайты|перепроверяю)(?: · осталось ~(\d+) (мин|с))?$', {
        param($m)
        $ph = @{ 'запускаю' = 'starting'; 'проверяю сайты' = 'checking sites'; 'перепроверяю' = 'rechecking' }[$m[4]]
        $s = "Strategy $($m[1]) of $($m[2]) · $($m[3]) · $ph"
        if ($m[5]) { $s += " · ~$($m[5]) $(if ($m[6] -eq 'мин') { 'min' } else { 's' }) left" }
        $s }),
    @('^перенесено (\d+) сайт(а|ов)?$', { param($m) 'moved ' + (Plural-En ([int]$m[1])) }),
    @('^файлов: (\d+)$', 'files: $1'),
    @('^добавлено: (\d+) сайт(а|ов)?$', { param($m) 'added: ' + (Plural-En ([int]$m[1])) }),
    @('^Ошибка переноса: (.*)$', 'Move error: $1'),
    @('^Скачиваю tg-ws-proxy · (\d+)%$', 'Downloading tg-ws-proxy · $1%'),
    @('^Не удалось скачать tg-ws-proxy с GitHub: (.*)$', 'Couldn''t download tg-ws-proxy from GitHub: $1'),
    @('^Доступна новая версия приложения (.+)$', 'A new app version is available: $1'),
    @('^Не удалось проверить обновления приложения: (.*)$', 'Couldn''t check for app updates: $1'),
    @('^У тебя последняя версия приложения \((.+)\)$', 'You have the latest app version ($1)'),
    @('^Не удалось скачать обновление: (.*)$', 'Couldn''t download the update: $1'),
    @('^Скачать (.+)$', 'Download $1'),
    @('^Вышла новая версия zapret: (.+)\. Распакуй её в папку Zapret и нажми ⚙ → «Перенести настройки»\.$', 'New zapret version: $1. Unpack it into the Zapret folder and click ⚙ → "Move settings".'),
    @('^Использую (.+)\. Найдено папок zapret: (\d+)\.$', 'Using $1. zapret folders found: $2.'),
    @('^Удалить группу «(.+)» \((.+)\)\?$', { param($m) "Delete group ""$($m[1])"" ($(T $m[2]))?" }),
    @('^Ошибка: (.*)$', 'Error: $1'),
    @('^(.+)  ·  без прав администратора$', '$1  ·  no admin rights'),
    @('^Zapret GUI (.+) · тестовая сборка$', 'Zapret GUI $1 · test build')
)
$script:trMissing = New-Object System.Collections.Generic.HashSet[string]
function T([string]$s) {
    if ($script:Lang -ne 'en' -or -not $s -or $s -notmatch '[А-Яа-яЁё]') { return $s }
    if ($TrExact.ContainsKey($s)) { return $TrExact[$s] }
    foreach ($r in $TrRx) {
        $mm = [regex]::Match($s, $r[0])
        if ($mm.Success) {
            if ($r[1] -is [scriptblock]) { $g = @($mm.Groups | ForEach-Object { $_.Value }); return (& $r[1] $g) }
            return [regex]::Replace($s, $r[0], $r[1])
        }
    }
    if ($script:trMissing.Add($s)) { try { Add-Content -LiteralPath (Join-Path $GuiDir 'untranslated.txt') -Value $s -Encoding UTF8 } catch {} }
    return $s
}


# Надёжный перевод: обход всего дерева окна (и открытых всплывающих окон)
function Translate-Tree($root) {
    if ($script:Lang -ne 'en' -or -not $root) { return }
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    $stack = New-Object System.Collections.Stack
    $stack.Push($root)
    $budget = 4000
    while ($stack.Count -and $budget-- -gt 0) {
        $el = $stack.Pop()
        if (-not $seen.Add([System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($el))) { continue }
        try {
            if ($el -is [System.Windows.Controls.TextBlock]) {
                if ($el.Inlines.Count -gt 1) {
                    foreach ($run in @($el.Inlines)) { if ($run -is [System.Windows.Documents.Run] -and $run.Text -match '[А-Яа-яЁё]') { $n = T $run.Text; if ($n -ne $run.Text) { $run.Text = $n } } }
                } elseif ($el.Text -match '[А-Яа-яЁё]') { $n = T $el.Text; if ($n -ne $el.Text) { $el.Text = $n } }
                continue
            }
            if ($el -is [System.Windows.Controls.ContentControl] -and $el.Content -is [string] -and $el.Content -match '[А-Яа-яЁё]') {
                $n = T $el.Content; if ($n -ne $el.Content) { $el.Content = $n }
            }
            if ($el -is [System.Windows.FrameworkElement] -and $el.ToolTip -is [string] -and $el.ToolTip -match '[А-Яа-яЁё]') {
                $n = T $el.ToolTip; if ($n -ne $el.ToolTip) { $el.ToolTip = $n }
            }
            # элементы выпадающих списков, которые ещё не показаны на экране
            if ($el -is [System.Windows.Controls.ComboBox]) {
                foreach ($it in $el.Items) { if ($it -is [System.Windows.Controls.ComboBoxItem]) { $stack.Push($it) } }
            }
            if ($el -is [System.Windows.Media.Visual]) {
                $cnt = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($el)
                for ($i = 0; $i -lt $cnt; $i++) { $stack.Push([System.Windows.Media.VisualTreeHelper]::GetChild($el, $i)) }
            }
        } catch {}
    }
}


$AllZapret = Find-ZapretFolders
if ($AllZapret.Count -eq 0) {
    [System.Windows.MessageBox]::Show("Не нашёл папку zapret (на рабочем столе, в Загрузках или на диске C:).`nПоложи папку zapret-discord-youtube рядом и запусти снова.", 'Zapret', 'OK', 'Warning') | Out-Null
    exit 1
}
$Z = $AllZapret[-1]
# Если zapret лежит в папке самого приложения (…\Zapret\zapret-*), берём только его
$OwnHome = if ((Split-Path -Leaf $GuiDir) -eq 'app') { Split-Path -Parent $GuiDir } else { $null }
if ($OwnHome) {
    $own = @($AllZapret | Where-Object { (Split-Path -Parent $_.Path) -eq $OwnHome })
    if ($own.Count) { $Z = $own[-1]; $AllZapret = @($own) + @($AllZapret | Where-Object { (Split-Path -Parent $_.Path) -ne $OwnHome }) | Sort-Object Path -Unique | Sort-Object Version, Time; $Z = $own[-1] }
}
$Root = $Z.Path
$Lists = Join-Path $Root 'lists'
$Utils = Join-Path $Root 'utils'
$UserListFile = Join-Path $Lists 'list-general-user.txt'
$GameFile = Join-Path $Utils 'game_filter.enabled'
$UpdFile = Join-Path $Utils 'check_updates.enabled'
$IpsetFile = Join-Path $Lists 'ipset-all.txt'
$IpsetBackup = "$IpsetFile.backup"

$LocalVersion = $null
try {
    $m = Select-String -LiteralPath (Join-Path $Root 'service.bat') -Pattern 'set "LOCAL_VERSION=([^"]+)"' | Select-Object -First 1
    if ($m) { $LocalVersion = $m.Matches[0].Groups[1].Value }
} catch {}
if (-not $LocalVersion) { $LocalVersion = "$($Z.Version)" }

$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)


# ======================= Всё в одной папке =======================
# Новая раскладка:  Рабочий стол\Zapret\  Zapret.bat, app\ (это приложение), tools\ (перенос настроек), zapret-discord-youtube-X\
$InHome = ((Split-Path -Leaf $GuiDir) -eq 'app')
$HomeDir = if ($InHome) { Split-Path -Parent $GuiDir } else { Join-Path ([Environment]::GetFolderPath('Desktop')) 'Zapret' }

function Invoke-Consolidate {
    $target = $HomeDir
    $wasOn = [bool](Get-Process -Name winws -ErrorAction SilentlyContinue)
    Get-Process -Name winws -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    foreach ($d in @($target, (Join-Path $target 'app'), (Join-Path $target 'tools'))) { if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d | Out-Null } }

    # 1) сам zapret
    $newRoot = Join-Path $target (Split-Path -Leaf $Root)
    if ($Root -ne $newRoot) {
        if (Test-Path -LiteralPath $newRoot) { throw "В папке Zapret уже есть $(Split-Path -Leaf $Root)" }
        Move-Item -LiteralPath $Root -Destination $newRoot
    }
    # 2) приложение
    Copy-Item -LiteralPath (Join-Path $GuiDir 'zapret-gui.ps1') -Destination (Join-Path $target 'app') -Force
    if (Test-Path -LiteralPath $SettingsFile) { Copy-Item -LiteralPath $SettingsFile -Destination (Join-Path $target 'app') -Force }
    $launcher = "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command `"Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList '-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File \`"%~dp0app\zapret-gui.ps1\`"'`"`r`n"
    [IO.File]::WriteAllText((Join-Path $target 'Zapret.bat'), $launcher, [Text.Encoding]::ASCII)
    # 3) перенос настроек
    $oldMig = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Перенос настроек zapret'
    if (Test-Path -LiteralPath $oldMig) {
        Get-ChildItem -LiteralPath $oldMig -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $target 'tools') -Force }
    }
    # 4) ярлык на рабочем столе
    try {
        $sh = New-Object -ComObject WScript.Shell
        $lnk = $sh.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Zapret.lnk'))
        $lnk.TargetPath = Join-Path $target 'Zapret.bat'
        $lnk.WorkingDirectory = $target
        $ico = Join-Path $GuiDir 'zapret.ico'
        if (Test-Path -LiteralPath $ico) { Copy-Item -LiteralPath $ico -Destination (Join-Path $target 'app') -Force; $lnk.IconLocation = (Join-Path $target 'app\zapret.ico') + ',0' }
        else { $lnk.IconLocation = "$env:SystemRoot\System32\imageres.dll,-1033" }
        $lnk.WindowStyle = 7
        $lnk.Save()
    } catch {}
    # 5) запустить приложение из нового места, старые папки удалить после закрытия
    $flag = if ($wasOn) { '-AutoStart' } else { '' }
    Release-Instance
    Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File `"$(Join-Path $target 'app\zapret-gui.ps1')`" $flag" -WindowStyle Hidden
    $cleanup = "timeout /t 3 /nobreak >nul & rmdir /s /q `"$GuiDir`""
    if (Test-Path -LiteralPath $oldMig) { $cleanup += " & rmdir /s /q `"$oldMig`"" }
    Start-Process cmd.exe -ArgumentList "/c $cleanup" -WindowStyle Hidden
    $script:consolidated = $true
}



# ======================= Своя программа Zapret.exe =======================
# Приложение работает внутри Zapret.exe (а не powershell.exe), поэтому в диспетчере задач, на панели задач
# и в списке процессов оно называется «Zapret» и показывается со своим значком.
$HostExe = Join-Path $GuiDir 'Zapret.exe'
$InOwnHost = ([Diagnostics.Process]::GetCurrentProcess().ProcessName -eq 'Zapret')
function Get-HostVersion { try { [Diagnostics.FileVersionInfo]::GetVersionInfo($HostExe).FileVersion } catch { $null } }
function Build-HostExe {
    $ver = ($AppVersion -replace '[^0-9.]', '') + '.0'
    $src = @"
using System; using System.IO; using System.Reflection; using System.Threading;
using System.Management.Automation; using System.Management.Automation.Runspaces;
[assembly: AssemblyTitle("Zapret")]
[assembly: AssemblyProduct("Zapret")]
[assembly: AssemblyDescription("Zapret by COFFIOK")]
[assembly: AssemblyCompany("COFFIOK")]
[assembly: AssemblyCopyright("COFFIOK")]
[assembly: AssemblyVersion("$ver")]
[assembly: AssemblyFileVersion("$ver")]
static class ZapretHost {
    [STAThread]
    static int Main(string[] args) {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string script = Path.Combine(dir, "zapret-gui.ps1");
        InitialSessionState iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        iss.ApartmentState = ApartmentState.STA;
        iss.ThreadOptions = PSThreadOptions.UseCurrentThread;
        using (Runspace rs = RunspaceFactory.CreateRunspace(iss)) {
            rs.Open();
            using (PowerShell ps = PowerShell.Create()) {
                ps.Runspace = rs;
                ps.AddCommand(script);
                foreach (string a in args) { if (a.StartsWith("-")) ps.AddParameter(a.TrimStart('-')); }
                try { ps.Invoke(); } catch (Exception ex) { File.AppendAllText(Path.Combine(dir, "gui-error.log"), DateTime.Now + " host: " + ex + Environment.NewLine); }
            }
        }
        return 0;
    }
}
"@
    $manifest = Join-Path $env:TEMP 'zapret-host.manifest'
    [IO.File]::WriteAllText($manifest, '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><assemblyIdentity version="1.0.0.0" name="Zapret.GUI"/><trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="requireAdministrator" uiAccess="false"/></requestedPrivileges></security></trustInfo><application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings><dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware></windowsSettings></application></assembly>', [Text.Encoding]::UTF8)
    $tmpExe = Join-Path $env:TEMP ('Zapret-' + [guid]::NewGuid().ToString('N') + '.exe')
    $cp = New-Object System.CodeDom.Compiler.CompilerParameters
    $cp.GenerateExecutable = $true; $cp.OutputAssembly = $tmpExe; $cp.GenerateInMemory = $false
    [void]$cp.ReferencedAssemblies.Add('System.dll'); [void]$cp.ReferencedAssemblies.Add('System.Core.dll')
    [void]$cp.ReferencedAssemblies.Add([PSObject].Assembly.Location)
    $ico = Join-Path $GuiDir 'zapret.ico'
    $cp.CompilerOptions = "/target:winexe /optimize /win32manifest:`"$manifest`"" + $(if (Test-Path -LiteralPath $ico) { " /win32icon:`"$ico`"" } else { '' })
    $prov = New-Object Microsoft.CSharp.CSharpCodeProvider
    $res = $prov.CompileAssemblyFromSource($cp, $src)
    if ($res.Errors.HasErrors) { throw ('Zapret.exe: ' + (($res.Errors | Where-Object { -not $_.IsWarning } | ForEach-Object { $_.ToString() }) -join '; ')) }
    Copy-Item -LiteralPath $tmpExe -Destination $HostExe -Force
    Remove-Item -LiteralPath $tmpExe, $manifest -Force -ErrorAction SilentlyContinue
}
function Ensure-HostExe {
    if (-not $IsAdmin) { return $false }
    $want = ($AppVersion -replace '[^0-9.]', '') + '.0'
    if ((Test-Path -LiteralPath $HostExe) -and (Get-HostVersion) -eq $want) { return $true }
    if ($InOwnHost) { return $true }   # уже работаем внутри exe — не трогаем файл
    try { Build-HostExe; return $true } catch { Write-Log ($_ | Out-String); return $false }
}
$HostReady = Ensure-HostExe

# ======================= Запуск без запроса прав =======================
# Задание планировщика с наивысшими правами: ярлык запускает его, и Windows больше не спрашивает разрешение
$TaskName = 'ZapretGUI'
$LauncherBat = "@echo off`r`nschtasks /run /tn `"$TaskName`" >nul 2>&1 && exit /b`r`nif exist `"%~dp0app\Zapret.exe`" ( start `"`" `"%~dp0app\Zapret.exe`" & exit /b )`r`npowershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command `"Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList '-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File \`"%~dp0app\zapret-gui.ps1\`"'`"`r`n"
function Get-ElevTask { try { Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop } catch { $null } }
function Enable-NoPrompt {
    $ps1 = Join-Path $GuiDir 'zapret-gui.ps1'
    if ($HostReady -and (Test-Path -LiteralPath $HostExe)) { $exe = $HostExe; $arg = '' }
    else { $exe = 'powershell.exe'; $arg = "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File `"$ps1`"" }
    $ex = Get-ElevTask
    if (-not ($ex -and $ex.Actions[0].Execute -eq $exe -and "$($ex.Actions[0].Arguments)" -eq $arg)) {
        $act = if ($arg) { New-ScheduledTaskAction -Execute $exe -Argument $arg -WorkingDirectory $GuiDir } else { New-ScheduledTaskAction -Execute $exe -WorkingDirectory $GuiDir }
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $pr = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $act -Principal $pr -Settings $set -Description 'Запуск приложения Zapret без запроса прав администратора' -Force | Out-Null
    }
    if ($InHome) {
        $bat = Join-Path $HomeDir 'Zapret.bat'
        $cur = if (Test-Path -LiteralPath $bat) { [IO.File]::ReadAllText($bat) } else { '' }
        if ($cur -ne $LauncherBat) { [IO.File]::WriteAllText($bat, $LauncherBat, [Text.Encoding]::ASCII) }
    }
}
function Disable-NoPrompt {
    if (Get-ElevTask) { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false }
}

# ======================= Интерфейс =======================
$xamlText = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Zapret" Width="920" Height="660" WindowStyle="None" AllowsTransparency="True"
        Background="Transparent" ResizeMode="CanMinimize" WindowStartupLocation="CenterScreen"
        FontFamily="Segoe UI" Foreground="#E6E8EE" UseLayoutRounding="True">
  <Window.Resources>
    <SolidColorBrush x:Key="Accent" Color="#3DDC97"/>
    <SolidColorBrush x:Key="Card" Color="#1A1D24"/>
    <SolidColorBrush x:Key="Field" Color="#22262F"/>
    <SolidColorBrush x:Key="Line" Color="#2E3340"/>
    <SolidColorBrush x:Key="Muted" Color="#8A90A0"/>

    <Style x:Key="Btn" TargetType="Button">
      <Setter Property="Background" Value="#22262F"/>
      <Setter Property="Foreground" Value="#E6E8EE"/>
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="Padding" Value="14,9"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="RenderTransformOrigin" Value="0.5,0.5"/>
      <Setter Property="RenderTransform"><Setter.Value><ScaleTransform/></Setter.Value></Setter>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="10" Padding="{TemplateBinding Padding}" BorderBrush="#2E3340" BorderThickness="1">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Bd" Property="BorderBrush" Value="#3DDC97"/>
              </Trigger>
              <Trigger Property="IsPressed" Value="True">
                <Setter Property="RenderTransform"><Setter.Value><ScaleTransform ScaleX="0.96" ScaleY="0.96"/></Setter.Value></Setter>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Opacity" Value="0.4"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="AccentBtn" TargetType="Button" BasedOn="{StaticResource Btn}">
      <Setter Property="Background" Value="#3DDC97"/>
      <Setter Property="Foreground" Value="#0F1115"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>

    <Style x:Key="TitleBtn" TargetType="Button">
      <Setter Property="Foreground" Value="#8A90A0"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="Bd" Background="Transparent" CornerRadius="8" Width="34" Height="28">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Bd" Property="Background" Value="#2A2E38"/>
                <Setter Property="Foreground" Value="#FFFFFF"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType="ComboBoxItem">
      <Setter Property="Foreground" Value="#E6E8EE"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ComboBoxItem">
            <Border x:Name="Bd" Background="Transparent" CornerRadius="7" Padding="12,7" Margin="4,1">
              <ContentPresenter/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsHighlighted" Value="True">
                <Setter TargetName="Bd" Property="Background" Value="#2A2E38"/>
              </Trigger>
              <Trigger Property="IsSelected" Value="True">
                <Setter Property="Foreground" Value="#3DDC97"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType="ComboBox">
      <Setter Property="Foreground" Value="#E6E8EE"/>
      <Setter Property="Height" Value="38"/>
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ComboBox">
            <Grid>
              <ToggleButton Focusable="False" ClickMode="Press"
                            IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                <ToggleButton.Template>
                  <ControlTemplate TargetType="ToggleButton">
                    <Border x:Name="Bd" Background="#22262F" CornerRadius="10" BorderBrush="#2E3340" BorderThickness="1">
                      <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,14,0"
                            Data="M0,0 L5,5 L10,0" Stroke="#8A90A0" StrokeThickness="1.6"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Bd" Property="BorderBrush" Value="#3DDC97"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </ToggleButton.Template>
              </ToggleButton>
              <ContentPresenter IsHitTestVisible="False" Content="{TemplateBinding SelectionBoxItem}"
                                Margin="14,0,34,0" VerticalAlignment="Center" HorizontalAlignment="Left"/>
              <Popup IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True"
                     Focusable="False" PopupAnimation="Fade">
                <Border Background="#22262F" CornerRadius="10" BorderBrush="#2E3340" BorderThickness="1"
                        Margin="0,4,0,0" MinWidth="{TemplateBinding ActualWidth}" MaxHeight="320" Padding="0,4">
                  <ScrollViewer VerticalScrollBarVisibility="Auto"><ItemsPresenter/></ScrollViewer>
                </Border>
              </Popup>
            </Grid>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="Toggle" TargetType="CheckBox">
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="CheckBox">
            <Border x:Name="Track" Width="44" Height="24" CornerRadius="12" Background="#2E3340">
              <Ellipse x:Name="Knob" Width="18" Height="18" Fill="#E6E8EE" HorizontalAlignment="Left" Margin="3,0,0,0">
                <Ellipse.RenderTransform><TranslateTransform x:Name="KnobT" X="0"/></Ellipse.RenderTransform>
              </Ellipse>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsChecked" Value="True">
                <Trigger.EnterActions>
                  <BeginStoryboard><Storyboard>
                    <DoubleAnimation Storyboard.TargetName="KnobT" Storyboard.TargetProperty="X" To="20" Duration="0:0:0.18"/>
                    <ColorAnimation Storyboard.TargetName="Track" Storyboard.TargetProperty="(Border.Background).(SolidColorBrush.Color)" To="#3DDC97" Duration="0:0:0.18"/>
                  </Storyboard></BeginStoryboard>
                </Trigger.EnterActions>
                <Trigger.ExitActions>
                  <BeginStoryboard><Storyboard>
                    <DoubleAnimation Storyboard.TargetName="KnobT" Storyboard.TargetProperty="X" To="0" Duration="0:0:0.18"/>
                    <ColorAnimation Storyboard.TargetName="Track" Storyboard.TargetProperty="(Border.Background).(SolidColorBrush.Color)" To="#2E3340" Duration="0:0:0.18"/>
                  </Storyboard></BeginStoryboard>
                </Trigger.ExitActions>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="Chip" TargetType="ToggleButton">
      <Setter Property="Foreground" Value="#9AA3B5"/>
      <Setter Property="FontSize" Value="12.5"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Margin" Value="0,0,6,6"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ToggleButton">
            <Border x:Name="Bd" Background="#22262F" BorderBrush="#2E3340" BorderThickness="1" CornerRadius="14" Padding="12,5">
              <ContentPresenter VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsChecked" Value="True">
                <Setter TargetName="Bd" Property="BorderBrush" Value="#3DDC97"/>
                <Setter Property="Foreground" Value="#E6E8EE"/>
                <Trigger.EnterActions>
                  <BeginStoryboard><Storyboard>
                    <ColorAnimation Storyboard.TargetName="Bd" Storyboard.TargetProperty="(Border.Background).(SolidColorBrush.Color)" To="#1B3329" Duration="0:0:0.18"/>
                  </Storyboard></BeginStoryboard>
                </Trigger.EnterActions>
                <Trigger.ExitActions>
                  <BeginStoryboard><Storyboard>
                    <ColorAnimation Storyboard.TargetName="Bd" Storyboard.TargetProperty="(Border.Background).(SolidColorBrush.Color)" To="#22262F" Duration="0:0:0.18"/>
                  </Storyboard></BeginStoryboard>
                </Trigger.ExitActions>
              </Trigger>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Opacity" Value="0.85"/>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Opacity" Value="0.4"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="NavItem" TargetType="RadioButton">
      <Setter Property="Foreground" Value="#9AA3B5"/>
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Margin" Value="0,0,0,4"/>
      <Setter Property="GroupName" Value="SetNav"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="RadioButton">
            <Border x:Name="Bd" Background="Transparent" CornerRadius="10" Padding="12,9">
              <Grid>
                <Border x:Name="Ind" Width="3" CornerRadius="1.5" Background="#3DDC97" HorizontalAlignment="Left" Margin="-12,2,0,2" Opacity="0"/>
                <ContentPresenter VerticalAlignment="Center"/>
              </Grid>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Bd" Property="Background" Value="#1E2129"/>
              </Trigger>
              <Trigger Property="IsChecked" Value="True">
                <Setter TargetName="Bd" Property="Background" Value="#22262F"/>
                <Setter TargetName="Ind" Property="Opacity" Value="1"/>
                <Setter Property="Foreground" Value="#E6E8EE"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="SetCard" TargetType="Border">
      <Setter Property="Background" Value="#1E2129"/>
      <Setter Property="CornerRadius" Value="14"/>
      <Setter Property="Padding" Value="16,14"/>
      <Setter Property="BorderBrush" Value="#262A33"/>
      <Setter Property="BorderThickness" Value="1"/>
    </Style>

    <Style x:Key="RowBtn" TargetType="Button">
      <Setter Property="Foreground" Value="#E6E8EE"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Margin" Value="0,0,0,8"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="Bd" Background="#1E2129" CornerRadius="12" Padding="14,10" BorderBrush="#262A33" BorderThickness="1">
              <DockPanel>
                <TextBlock DockPanel.Dock="Right" Text="›" FontSize="18" Foreground="#5A6070" VerticalAlignment="Center"/>
                <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Left"/>
              </DockPanel>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Bd" Property="BorderBrush" Value="#3DDC97"/>
                <Setter TargetName="Bd" Property="Background" Value="#22262F"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="Check" TargetType="CheckBox">
      <Setter Property="Foreground" Value="#E6E8EE"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="CheckBox">
            <StackPanel Orientation="Horizontal" Background="Transparent">
              <Grid Width="18" Height="18" VerticalAlignment="Center">
                <Border x:Name="Box" CornerRadius="5" BorderBrush="#3A3F4D" BorderThickness="1.5" Background="Transparent"/>
                <TextBlock x:Name="Mark" Text="✓" FontSize="12" FontWeight="Bold" Foreground="#0F1115" HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Hidden"/>
              </Grid>
              <ContentPresenter Margin="9,0,0,0" VerticalAlignment="Center"/>
            </StackPanel>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="#3DDC97"/></Trigger>
              <Trigger Property="IsChecked" Value="True">
                <Setter TargetName="Box" Property="Background" Value="#3DDC97"/>
                <Setter TargetName="Box" Property="BorderBrush" Value="#3DDC97"/>
                <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
              </Trigger>
              <Trigger Property="IsChecked" Value="{x:Null}">
                <Setter TargetName="Box" Property="Background" Value="#2D5A47"/>
                <Setter TargetName="Box" Property="BorderBrush" Value="#3DDC97"/>
                <Setter TargetName="Mark" Property="Text" Value="–"/>
                <Setter TargetName="Mark" Property="Foreground" Value="#E6E8EE"/>
                <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="DelBtn" TargetType="Button">
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Foreground" Value="#8A90A0"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="Bd" Background="Transparent" CornerRadius="6" Width="26" Height="26">
              <TextBlock Text="✕" FontSize="12" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{TemplateBinding Foreground}"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Bd" Property="Background" Value="#3A2328"/>
                <Setter Property="Foreground" Value="#FF6B81"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType="ScrollBar">
      <Setter Property="Width" Value="6"/>
      <Setter Property="MinWidth" Value="6"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ScrollBar">
            <Track x:Name="PART_Track" IsDirectionReversed="True">
              <Track.Thumb>
                <Thumb>
                  <Thumb.Template>
                    <ControlTemplate TargetType="Thumb">
                      <Border Background="#3A3F4D" CornerRadius="3"/>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Window.Resources>

  <Grid Margin="14">
    <Grid x:Name="Shell" Opacity="0">
    <Grid.RenderTransform><TranslateTransform x:Name="ShellShift" Y="18"/></Grid.RenderTransform>
    <!-- тень окна отдельно от содержимого: анимации внутри не заставляют её перерисовываться -->
    <Border CornerRadius="20" Background="#121419" IsHitTestVisible="False">
      <Border.Effect><DropShadowEffect BlurRadius="24" ShadowDepth="0" Opacity="0.55" Color="#000000" RenderingBias="Performance"/></Border.Effect>
    </Border>
    <Border x:Name="ShellBody" CornerRadius="20" Background="#121419" BorderBrush="#252933" BorderThickness="1">
      <Grid>
        <Grid.RowDefinitions>
          <RowDefinition Height="48"/>
          <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Заголовок -->
        <Grid x:Name="TitleBar" Grid.Row="0" Background="Transparent" Margin="20,0,10,0">
          <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
            <Ellipse x:Name="TitleDot" Width="9" Height="9" Fill="#5A6070" Margin="0,0,10,0"/>
            <TextBlock Text="ZAPRET" FontWeight="Bold" FontSize="14" />
            <TextBlock x:Name="TitleVer" Text="" Foreground="{StaticResource Muted}" FontSize="12" Margin="10,1,0,0" VerticalAlignment="Center"/>
          </StackPanel>
          <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center">
            <Button x:Name="GearBtn" Style="{StaticResource TitleBtn}" Margin="0,0,4,0" ToolTip="Настройки">
              <TextBlock Text="⚙" FontFamily="Segoe UI Symbol" FontSize="16" RenderTransformOrigin="0.5,0.55">
                <TextBlock.RenderTransform><RotateTransform x:Name="GearRot"/></TextBlock.RenderTransform>
              </TextBlock>
            </Button>
            <Button x:Name="MinBtn" Style="{StaticResource TitleBtn}" ToolTip="Свернуть в трей"><TextBlock Text="—" FontSize="12"/></Button>
            <Button x:Name="CloseBtn" Style="{StaticResource TitleBtn}" Margin="4,0,0,0"><TextBlock Text="✕" FontSize="13"/></Button>
          </StackPanel>
        </Grid>

        <Grid Grid.Row="1" Margin="22,4,22,20">
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="330"/>
            <ColumnDefinition Width="22"/>
            <ColumnDefinition Width="*"/>
          </Grid.ColumnDefinitions>

          <!-- Левая колонка -->
          <Grid Grid.Column="0">
            <Grid.RowDefinitions>
              <RowDefinition Height="*"/>
              <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <StackPanel VerticalAlignment="Center">
              <Button x:Name="PowerBtn" Width="200" Height="200" Cursor="Hand" HorizontalAlignment="Center" Focusable="False">
                <Button.Template>
                  <ControlTemplate TargetType="Button"><ContentPresenter/></ControlTemplate>
                </Button.Template>
                <Grid x:Name="PowerGrid" Width="200" Height="200" RenderTransformOrigin="0.5,0.5">
                  <Grid.RenderTransform><ScaleTransform x:Name="PowerScale"/></Grid.RenderTransform>
                  <Ellipse x:Name="Glow1" Width="160" Height="160" StrokeThickness="3" Stroke="#3DDC97" Opacity="0" RenderTransformOrigin="0.5,0.5">
                    <Ellipse.RenderTransform><ScaleTransform x:Name="Glow1S"/></Ellipse.RenderTransform>
                  </Ellipse>
                  <Ellipse x:Name="Glow2" Width="160" Height="160" StrokeThickness="3" Stroke="#3DDC97" Opacity="0" RenderTransformOrigin="0.5,0.5">
                    <Ellipse.RenderTransform><ScaleTransform x:Name="Glow2S"/></Ellipse.RenderTransform>
                  </Ellipse>
                  <Ellipse Width="176" Height="176" Fill="#171A20" Stroke="#252933" StrokeThickness="1"/>
                  <Ellipse x:Name="Spinner" Width="176" Height="176" StrokeThickness="3" Stroke="#3DDC97"
                           StrokeDashArray="18 40" StrokeDashCap="Round" Opacity="0" RenderTransformOrigin="0.5,0.5">
                    <Ellipse.RenderTransform><RotateTransform x:Name="SpinRot"/></Ellipse.RenderTransform>
                  </Ellipse>
                  <Ellipse Width="150" Height="150">
                    <Ellipse.Fill><SolidColorBrush x:Name="PowerFill" Color="#262A33"/></Ellipse.Fill>
                    <Ellipse.Effect><DropShadowEffect x:Name="PowerShadow" BlurRadius="0" ShadowDepth="0" Color="#3DDC97" Opacity="0.8"/></Ellipse.Effect>
                  </Ellipse>
                  <Grid x:Name="IconWrap" Width="64" Height="64" RenderTransformOrigin="0.5,0.5">
                    <Grid.RenderTransform>
                      <TransformGroup>
                        <ScaleTransform x:Name="PowerIconScale"/>
                        <RotateTransform x:Name="PowerIconRot"/>
                      </TransformGroup>
                    </Grid.RenderTransform>
                    <Viewbox Width="64" Height="64">
                      <Canvas Width="80" Height="80">
                        <Path Data="M 54.1,23.15 A 22,22 0 1 1 25.86,23.15" StrokeThickness="6" StrokeStartLineCap="Round" StrokeEndLineCap="Round">
                          <Path.Stroke><SolidColorBrush x:Name="IconBrush" Color="#8A90A0"/></Path.Stroke>
                        </Path>
                        <Path Data="M 40,12 L 40,38" StrokeThickness="6" StrokeStartLineCap="Round" StrokeEndLineCap="Round">
                          <Path.Stroke><SolidColorBrush x:Name="IconBrush2" Color="#8A90A0"/></Path.Stroke>
                        </Path>
                      </Canvas>
                    </Viewbox>
                  </Grid>
                  <Grid x:Name="ZWrap" Width="78" Height="78" Opacity="0" RenderTransformOrigin="0.5,0.5" IsHitTestVisible="False">
                    <Grid.RenderTransform>
                      <TransformGroup>
                        <ScaleTransform x:Name="ZScale" ScaleX="0.3" ScaleY="0.3"/>
                        <ScaleTransform x:Name="ZFlip"/>
                      </TransformGroup>
                    </Grid.RenderTransform>
                    <Viewbox>
                      <Canvas Width="80" Height="80">
                        <Path Data="M 16,14 L 64,14 L 64,26 L 34,54 L 64,54 L 64,66 L 16,66 L 16,54 L 46,26 L 16,26 Z" StrokeLineJoin="Round" StrokeThickness="2">
                          <Path.Fill><SolidColorBrush x:Name="ZBrush" Color="#3DDC97"/></Path.Fill>
                          <Path.Stroke><SolidColorBrush x:Name="ZStroke" Color="#3DDC97"/></Path.Stroke>
                        </Path>
                      </Canvas>
                    </Viewbox>
                  </Grid>
                </Grid>
              </Button>

              <TextBlock x:Name="StatusText" Text="Выключен" FontSize="26" FontWeight="SemiBold" HorizontalAlignment="Center" Margin="0,14,0,0"/>
              <TextBlock x:Name="SubText" Text="Нажми, чтобы включить" Foreground="{StaticResource Muted}" FontSize="13" HorizontalAlignment="Center" Margin="0,4,0,0" TextAlignment="Center" TextWrapping="Wrap"/>

              <TextBlock Text="Стратегия" Foreground="{StaticResource Muted}" FontSize="12" Margin="2,26,0,6"/>
              <ComboBox x:Name="StrategyBox"/>

              <!-- Telegram (tg-ws-proxy) -->
              <Border x:Name="TgCard" Background="#1A1D24" CornerRadius="14" Padding="12,0,12,0" Height="60" Margin="0,14,0,0" BorderBrush="#232731" BorderThickness="1" SnapsToDevicePixels="True" UseLayoutRounding="True" TextOptions.TextFormattingMode="Display">
                <Grid VerticalAlignment="Center">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                  <Grid Width="36" Height="36" Margin="0,0,12,0" VerticalAlignment="Center">
                    <Rectangle x:Name="TgDot" RadiusX="10" RadiusY="10"/>
                    <TextBlock x:Name="TgPlane" Text="&#xE724;" FontFamily="Segoe MDL2 Assets" FontSize="16" HorizontalAlignment="Center" VerticalAlignment="Center" Margin="-2,1,0,0"/>
                  </Grid>
                  <StackPanel Grid.Column="1" VerticalAlignment="Center">
                    <TextBlock Text="Telegram" FontSize="14" FontWeight="SemiBold" Margin="0,0,0,2"/>
                    <StackPanel Orientation="Horizontal">
                      <Ellipse x:Name="TgLed" Width="6" Height="6" Margin="0,1,6,0" VerticalAlignment="Center" Fill="#8A90A0"/>
                      <TextBlock x:Name="TgStatus" Text="Выключен" Foreground="{StaticResource Muted}" FontSize="12" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                    </StackPanel>
                  </StackPanel>
                  <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
                    <Button x:Name="TgPhone" Style="{StaticResource Btn}" Width="32" Height="32" Padding="0" Margin="0,0,6,0" Visibility="Collapsed" ToolTip="Подключить Telegram на телефоне по QR-коду">
                      <TextBlock Text="&#xE8EA;" FontFamily="Segoe MDL2 Assets" FontSize="14" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Button>
                    <Button x:Name="TgConnect" Style="{StaticResource Btn}" Content="Подключить" Height="32" Padding="12,0" FontSize="12" Margin="0,0,6,0" Visibility="Collapsed" ToolTip="Настроить прокси в Telegram Desktop"/>
                    <CheckBox x:Name="TgToggle" Style="{StaticResource Toggle}" Margin="4,0,0,0" VerticalAlignment="Center"/>
                  </StackPanel>
                </Grid>
              </Border>
            </StackPanel>

            <StackPanel Grid.Row="1">
              <UniformGrid Columns="2">
                <Button x:Name="TestBtn" Style="{StaticResource Btn}" Margin="0,0,4,0" Content="Тест стратегий"/>
                <Button x:Name="UpdateBtn" Style="{StaticResource Btn}" Margin="4,0,0,0">
                  <Grid>
                    <TextBlock x:Name="UpdText" Text="Обновления" HorizontalAlignment="Center"/>
                    <Border x:Name="UpdTrack" Height="3" VerticalAlignment="Bottom" Margin="-6,0,-6,-6" CornerRadius="1.5" Background="#2A2E38" ClipToBounds="True" Opacity="0">
                      <Grid>
                        <Rectangle x:Name="UpdBar" Width="40" HorizontalAlignment="Left" RadiusX="1.5" RadiusY="1.5" Fill="#3DDC97">
                          <Rectangle.RenderTransform><TranslateTransform x:Name="UpdBarT" X="-40"/></Rectangle.RenderTransform>
                        </Rectangle>
                        <Rectangle x:Name="UpdFill" RadiusX="1.5" RadiusY="1.5" Fill="#3DDC97" RenderTransformOrigin="0,0.5">
                          <Rectangle.RenderTransform><ScaleTransform x:Name="UpdFillS" ScaleX="0"/></Rectangle.RenderTransform>
                        </Rectangle>
                      </Grid>
                    </Border>
                  </Grid>
                </Button>
              </UniformGrid>
            </StackPanel>
          </Grid>

          <!-- Правая колонка -->
          <Grid Grid.Column="2">
            <Grid.RowDefinitions>
              <RowDefinition Height="*"/>
              <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <Border Grid.Row="0" Background="{StaticResource Card}" CornerRadius="16" Padding="18" BorderBrush="#232731" BorderThickness="1">
              <Grid>
                <Grid.RowDefinitions>
                  <RowDefinition Height="Auto"/>
                  <RowDefinition Height="Auto"/>
                  <RowDefinition Height="*"/>
                </Grid.RowDefinitions>
                <DockPanel>
                  <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Center">
                    <TextBlock x:Name="SitesCount" Foreground="{StaticResource Muted}" FontSize="12" VerticalAlignment="Center" Margin="0,0,8,0"/>
                    <Button x:Name="ShareBtn" Style="{StaticResource Btn}" Padding="10,5" FontSize="12" Margin="0,0,6,0" ToolTip="Выбрать сайты и скопировать их, чтобы отправить другу">
                      <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE72D;" FontFamily="Segoe MDL2 Assets" FontSize="12" Margin="0,1,6,0"/><TextBlock Text="Поделиться"/></StackPanel>
                    </Button>
                    <Button x:Name="PasteBtn" Style="{StaticResource Btn}" Padding="10,5" FontSize="12" ToolTip="Добавить сайты из скопированного списка">
                      <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE77F;" FontFamily="Segoe MDL2 Assets" FontSize="12" Margin="0,1,6,0"/><TextBlock Text="Вставить"/></StackPanel>
                    </Button>
                  </StackPanel>
                  <TextBlock Text="Мои сайты" FontSize="16" FontWeight="SemiBold" VerticalAlignment="Center"/>
                </DockPanel>
                <Grid Grid.Row="1" Margin="0,12,0,10">
                  <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                  </Grid.ColumnDefinitions>
                  <Border Background="#22262F" CornerRadius="10" BorderBrush="#2E3340" BorderThickness="1" Height="38">
                    <Grid>
                      <TextBlock x:Name="DomainHint" Text="например: google.com" Foreground="#5A6070" Margin="14,0,0,0" VerticalAlignment="Center" IsHitTestVisible="False"/>
                      <TextBox x:Name="DomainBox" Background="Transparent" BorderThickness="0" Foreground="#E6E8EE" CaretBrush="#3DDC97"
                               Margin="10,0" VerticalAlignment="Center" FontSize="13"/>
                    </Grid>
                  </Border>
                  <Button x:Name="AddBtn" Grid.Column="1" Style="{StaticResource AccentBtn}" Content="Добавить" Margin="8,0,0,0"/>
                </Grid>
                <ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                  <StackPanel x:Name="SitesPanel" Margin="0,0,6,0"/>
                </ScrollViewer>
              </Grid>
            </Border>


            <Border x:Name="ApplyBar" Grid.Row="1" Background="#1E2B26" CornerRadius="12" Padding="14,8" Margin="0,10,0,0"
                    BorderBrush="#2D5A47" BorderThickness="1" Visibility="Collapsed" Opacity="0">
              <DockPanel>
                <Button x:Name="ApplyBtn" DockPanel.Dock="Right" Style="{StaticResource AccentBtn}" Padding="12,6" Content="Перезапустить"/>
                <TextBlock Text="Изменения применятся после перезапуска" VerticalAlignment="Center" Foreground="#BFEBD6" FontSize="12" TextWrapping="Wrap"/>
              </DockPanel>
            </Border>
          </Grid>
        </Grid>

        <!-- Плашка обновления приложения -->
        <Border x:Name="AppUpdBar" Grid.Row="1" VerticalAlignment="Top" HorizontalAlignment="Center" Margin="0,-6,0,0"
                Background="#1B2B25" CornerRadius="14" Padding="16,10" BorderBrush="#3DDC97" BorderThickness="1" Visibility="Collapsed" Opacity="0">
          <Border.Effect><DropShadowEffect BlurRadius="22" ShadowDepth="0" Color="#3DDC97" Opacity="0.35"/></Border.Effect>
          <Border.RenderTransform><TranslateTransform x:Name="AppUpdShift" Y="-30"/></Border.RenderTransform>
          <StackPanel>
            <StackPanel Orientation="Horizontal">
              <Ellipse Width="8" Height="8" Fill="#3DDC97" VerticalAlignment="Center" Margin="0,0,10,0"/>
              <TextBlock x:Name="AppUpdText" FontSize="13" VerticalAlignment="Center" Margin="0,0,14,0"/>
              <Button x:Name="AppUpdBtn" Style="{StaticResource AccentBtn}" Content="Обновить" Padding="14,5"/>
              <Button x:Name="AppUpdLater" Style="{StaticResource TitleBtn}" Margin="6,0,0,0" ToolTip="Позже"><TextBlock Text="✕" FontSize="12"/></Button>
            </StackPanel>
            <Border x:Name="AppUpdTrack" Height="4" CornerRadius="2" Background="#22262F" Margin="0,8,0,0" Visibility="Collapsed">
              <Border x:Name="AppUpdFill" CornerRadius="2" Background="#3DDC97" RenderTransformOrigin="0,0.5">
                <Border.RenderTransform><ScaleTransform x:Name="AppUpdFillS" ScaleX="0"/></Border.RenderTransform>
              </Border>
            </Border>
          </StackPanel>
        </Border>

        <!-- Панель настроек -->
        <Grid x:Name="SettingsOverlay" Grid.RowSpan="2" Visibility="Collapsed">
          <Border x:Name="Dim" Background="#B0090A0D" CornerRadius="20" Opacity="0"/>
          <Border x:Name="SettingsPanel" HorizontalAlignment="Right" Width="600" Background="#171A21" CornerRadius="0,20,20,0"
                  BorderBrush="#2A2E38" BorderThickness="1,0,0,0">
            <Border.RenderTransform><TranslateTransform x:Name="SettingsShift" X="620"/></Border.RenderTransform>
            <Border.Effect><DropShadowEffect BlurRadius="30" ShadowDepth="0" Opacity="0.6"/></Border.Effect>
            <Grid Margin="22,18,22,20">
              <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
              <DockPanel Margin="0,0,0,18">
                <Button x:Name="SettingsClose" DockPanel.Dock="Right" Style="{StaticResource TitleBtn}"><TextBlock Text="✕" FontSize="13"/></Button>
                <TextBlock Text="Настройки" FontSize="20" FontWeight="SemiBold" VerticalAlignment="Center"/>
              </DockPanel>
              <Grid Grid.Row="1">
                <Grid.ColumnDefinitions><ColumnDefinition Width="168"/><ColumnDefinition Width="20"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>

                <!-- Вкладки -->
                <StackPanel x:Name="SetNav">
                  <RadioButton x:Name="Nav0" Style="{StaticResource NavItem}" Tag="0" IsChecked="True">
                    <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE774;" FontFamily="Segoe MDL2 Assets" FontSize="15" Width="26" VerticalAlignment="Center"/><TextBlock Text="Обход" VerticalAlignment="Center"/></StackPanel>
                  </RadioButton>
                  <RadioButton x:Name="Nav1" Style="{StaticResource NavItem}" Tag="1">
                    <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE7E8;" FontFamily="Segoe MDL2 Assets" FontSize="15" Width="26" VerticalAlignment="Center"/><TextBlock Text="Запуск" VerticalAlignment="Center"/></StackPanel>
                  </RadioButton>
                  <RadioButton x:Name="Nav2" Style="{StaticResource NavItem}" Tag="2">
                    <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE790;" FontFamily="Segoe MDL2 Assets" FontSize="15" Width="26" VerticalAlignment="Center"/><TextBlock Text="Вид" VerticalAlignment="Center"/></StackPanel>
                  </RadioButton>
                  <RadioButton x:Name="Nav3" Style="{StaticResource NavItem}" Tag="3">
                    <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE90F;" FontFamily="Segoe MDL2 Assets" FontSize="15" Width="26" VerticalAlignment="Center"/><TextBlock Text="Инструменты" VerticalAlignment="Center"/></StackPanel>
                  </RadioButton>
                  <RadioButton x:Name="Nav4" Style="{StaticResource NavItem}" Tag="4">
                    <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE946;" FontFamily="Segoe MDL2 Assets" FontSize="15" Width="26" VerticalAlignment="Center"/><TextBlock Text="О программе" VerticalAlignment="Center"/></StackPanel>
                  </RadioButton>
                </StackPanel>

                <!-- Содержимое вкладок -->
                <Grid x:Name="SetPages" Grid.Column="2">
                  <!-- 0: Обход -->
                  <StackPanel x:Name="SetPage0">
                    <TextBlock Text="Обход" FontSize="15" FontWeight="SemiBold" Margin="0,0,0,4"/>
                    <TextBlock Text="как zapret обходит блокировки" Foreground="{StaticResource Muted}" FontSize="11.5" Margin="0,0,0,16"/>
                    <Border Style="{StaticResource SetCard}">
                      <StackPanel>
                        <Grid Margin="0,0,0,12">
                          <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="150"/></Grid.ColumnDefinitions>
                          <StackPanel VerticalAlignment="Center">
                            <TextBlock Text="Игровой фильтр" FontSize="13"/>
                            <TextBlock Text="обход для игр (порты 1024+)" Foreground="{StaticResource Muted}" FontSize="11"/>
                          </StackPanel>
                          <ComboBox x:Name="GameBox" Grid.Column="1">
                            <ComboBoxItem Tag="disabled">Выключен</ComboBoxItem>
                            <ComboBoxItem Tag="all">TCP и UDP</ComboBoxItem>
                            <ComboBoxItem Tag="tcp">Только TCP</ComboBoxItem>
                            <ComboBoxItem Tag="udp">Только UDP</ComboBoxItem>
                          </ComboBox>
                        </Grid>
                        <Grid Margin="0,0,0,12">
                          <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="150"/></Grid.ColumnDefinitions>
                          <StackPanel VerticalAlignment="Center">
                            <TextBlock Text="IPSet фильтр" FontSize="13"/>
                            <TextBlock Text="обход по списку IP-адресов" Foreground="{StaticResource Muted}" FontSize="11"/>
                          </StackPanel>
                          <ComboBox x:Name="IpsetBox" Grid.Column="1">
                            <ComboBoxItem Tag="loaded">По списку</ComboBoxItem>
                            <ComboBoxItem Tag="none">Выключен</ComboBoxItem>
                            <ComboBoxItem Tag="any">Все IP</ComboBoxItem>
                          </ComboBox>
                        </Grid>
                        <Grid>
                          <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                          <StackPanel VerticalAlignment="Center">
                            <TextBlock Text="Показывать окно winws" FontSize="13"/>
                            <TextBlock Text="чёрное окно с логом; обычно не нужно" Foreground="{StaticResource Muted}" FontSize="11"/>
                          </StackPanel>
                          <CheckBox x:Name="WinToggle" Grid.Column="1" Style="{StaticResource Toggle}" VerticalAlignment="Center"/>
                        </Grid>
                      </StackPanel>
                    </Border>
                  </StackPanel>

                  <!-- 1: Запуск -->
                  <StackPanel x:Name="SetPage1" Visibility="Collapsed">
                    <TextBlock Text="Запуск" FontSize="15" FontWeight="SemiBold" Margin="0,0,0,4"/>
                    <TextBlock Text="как открывается приложение" Foreground="{StaticResource Muted}" FontSize="11.5" Margin="0,0,0,16"/>
                    <Border Style="{StaticResource SetCard}">
                      <StackPanel>
                        <Grid Margin="0,0,0,12">
                          <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                          <StackPanel VerticalAlignment="Center">
                            <TextBlock Text="Запускать без запроса прав" FontSize="13"/>
                            <TextBlock Text="Windows не будет спрашивать разрешение при запуске" Foreground="{StaticResource Muted}" FontSize="11" TextWrapping="Wrap"/>
                          </StackPanel>
                          <CheckBox x:Name="NoPromptToggle" Grid.Column="1" Style="{StaticResource Toggle}" VerticalAlignment="Center" Margin="10,0,0,0"/>
                        </Grid>
                        <Grid>
                          <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                          <StackPanel VerticalAlignment="Center">
                            <TextBlock Text="Проверять обновления при запуске" FontSize="13"/>
                            <TextBlock Text="приложения и zapret" Foreground="{StaticResource Muted}" FontSize="11"/>
                          </StackPanel>
                          <CheckBox x:Name="UpdToggle" Grid.Column="1" Style="{StaticResource Toggle}" VerticalAlignment="Center" Margin="10,0,0,0"/>
                        </Grid>
                      </StackPanel>
                    </Border>
                  </StackPanel>

                  <!-- 2: Вид -->
                  <StackPanel x:Name="SetPage2" Visibility="Collapsed">
                    <TextBlock Text="Вид" FontSize="15" FontWeight="SemiBold" Margin="0,0,0,4"/>
                    <TextBlock Text="применяется сразу, окно перезапустится" Foreground="{StaticResource Muted}" FontSize="11.5" Margin="0,0,0,16"/>
                    <Border Style="{StaticResource SetCard}">
                      <StackPanel>
                        <Grid Margin="0,0,0,12">
                          <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="150"/></Grid.ColumnDefinitions>
                          <TextBlock Text="Тема" FontSize="13" VerticalAlignment="Center"/>
                          <ComboBox x:Name="ThemeBox" Grid.Column="1">
                            <ComboBoxItem Tag="dark">Тёмная</ComboBoxItem>
                            <ComboBoxItem Tag="light">Светлая</ComboBoxItem>
                          </ComboBox>
                        </Grid>
                        <Grid Margin="0,0,0,12">
                          <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="150"/></Grid.ColumnDefinitions>
                          <TextBlock Text="Язык" FontSize="13" VerticalAlignment="Center"/>
                          <ComboBox x:Name="LangBox" Grid.Column="1">
                            <ComboBoxItem Tag="ru">Русский</ComboBoxItem>
                            <ComboBoxItem Tag="en">English</ComboBoxItem>
                          </ComboBox>
                        </Grid>
                        <TextBlock Text="Акцентный цвет" FontSize="13" Margin="0,2,0,8"/>
                        <WrapPanel x:Name="AccentPanel" Margin="-4,0,0,0"/>
                      </StackPanel>
                    </Border>
                  </StackPanel>

                  <!-- 3: Инструменты -->
                  <StackPanel x:Name="SetPage3" Visibility="Collapsed">
                    <TextBlock Text="Инструменты" FontSize="15" FontWeight="SemiBold" Margin="0,0,0,4"/>
                    <TextBlock Text="обслуживание zapret и приложения" Foreground="{StaticResource Muted}" FontSize="11.5" Margin="0,0,0,16"/>
                    <Button x:Name="ServiceBtn" Style="{StaticResource RowBtn}">
                      <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE90F;" FontFamily="Segoe MDL2 Assets" FontSize="16" Width="30" VerticalAlignment="Center" Foreground="#3DDC97"/>
                        <StackPanel><TextBlock Text="Сервис и диагностика" FontSize="13"/><TextBlock Text="служба, статус и проверка zapret" Foreground="{StaticResource Muted}" FontSize="11"/></StackPanel></StackPanel>
                    </Button>
                    <Button x:Name="FolderBtn" Style="{StaticResource RowBtn}">
                      <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE838;" FontFamily="Segoe MDL2 Assets" FontSize="16" Width="30" VerticalAlignment="Center" Foreground="#3DDC97"/>
                        <StackPanel><TextBlock Text="Открыть папку zapret" FontSize="13"/><TextBlock Text="списки, батники и логи" Foreground="{StaticResource Muted}" FontSize="11"/></StackPanel></StackPanel>
                    </Button>
                    <Button x:Name="MigrateBtn" Style="{StaticResource RowBtn}">
                      <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE8AB;" FontFamily="Segoe MDL2 Assets" FontSize="16" Width="30" VerticalAlignment="Center" Foreground="#3DDC97"/>
                        <StackPanel><TextBlock Text="Перенести настройки из старой версии" FontSize="13"/><TextBlock Text="если вышла новая версия zapret" Foreground="{StaticResource Muted}" FontSize="11"/></StackPanel></StackPanel>
                    </Button>
                    <Button x:Name="AppCheckBtn" Style="{StaticResource RowBtn}">
                      <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE895;" FontFamily="Segoe MDL2 Assets" FontSize="16" Width="30" VerticalAlignment="Center" Foreground="#3DDC97"/>
                        <StackPanel><TextBlock Text="Проверить обновления приложения" FontSize="13"/><TextBlock Text="скачать новую версию с GitHub" Foreground="{StaticResource Muted}" FontSize="11"/></StackPanel></StackPanel>
                    </Button>
                  </StackPanel>

                  <!-- 4: О программе -->
                  <StackPanel x:Name="SetPage4" Visibility="Collapsed">
                    <TextBlock Text="О программе" FontSize="15" FontWeight="SemiBold" Margin="0,0,0,16"/>
                    <Border Style="{StaticResource SetCard}" Margin="0,0,0,10">
                      <StackPanel>
                        <TextBlock FontSize="18" FontWeight="SemiBold">
                          <Run Text="Zapret GUI "/><Run Text="by " Foreground="#8A90A0" FontWeight="Normal" FontSize="14"/><Run Text="COFFIOK" Foreground="#3DDC97" FontWeight="Bold" FontSize="14"/>
                        </TextBlock>
                        <TextBlock x:Name="InfoApp" FontSize="12" Foreground="{StaticResource Muted}" Margin="0,4,0,12"/>
                        <TextBlock Text="Версия" Foreground="{StaticResource Muted}" FontSize="11"/>
                        <TextBlock x:Name="InfoVer" FontSize="12.5" Margin="0,2,0,10"/>
                        <TextBlock Text="Папка zapret" Foreground="{StaticResource Muted}" FontSize="11"/>
                        <TextBlock x:Name="InfoPath" FontSize="12.5" TextWrapping="Wrap" Margin="0,2,0,0"/>
                      </StackPanel>
                    </Border>
                    <Button x:Name="RepoBtn" Style="{StaticResource RowBtn}">
                      <StackPanel Orientation="Horizontal"><TextBlock Text="&#xE774;" FontFamily="Segoe MDL2 Assets" FontSize="16" Width="30" VerticalAlignment="Center" Foreground="#3DDC97"/>
                        <StackPanel><TextBlock Text="Страница на GitHub" FontSize="13"/><TextBlock Text="описание, релизы и обновления" Foreground="{StaticResource Muted}" FontSize="11"/></StackPanel></StackPanel>
                    </Button>
                    <TextBlock Text="zapret, tg-ws-proxy — Flowseal · bol-van · WinDivert — basil00" Foreground="#5A6070" FontSize="11" Margin="2,8,0,0" TextWrapping="Wrap"/>
                  </StackPanel>
                </Grid>
              </Grid>
            </Grid>
          </Border>
        </Grid>

        <!-- Тест стратегий -->
        <Grid x:Name="TestOverlay" Grid.RowSpan="2" Visibility="Collapsed">
          <Border x:Name="TestDim" Background="#C0090A0D" CornerRadius="20" Opacity="0"/>
          <Border x:Name="TestPanel" Margin="26,22,26,22" Background="#171A21" CornerRadius="18" BorderBrush="#2A2E38" BorderThickness="1"
                  Opacity="0" RenderTransformOrigin="0.5,0.5">
            <Border.RenderTransform><ScaleTransform x:Name="TestScale" ScaleX="0.94" ScaleY="0.94"/></Border.RenderTransform>
            <Border.Effect><DropShadowEffect BlurRadius="30" ShadowDepth="0" Opacity="0.6"/></Border.Effect>
            <Grid Margin="26,18,26,20">
              <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
              </Grid.RowDefinitions>
              <DockPanel Margin="0,0,0,14">
                <Button x:Name="TestClose" DockPanel.Dock="Right" Style="{StaticResource TitleBtn}"><TextBlock Text="✕" FontSize="13"/></Button>
                <StackPanel>
                  <TextBlock Text="Тест стратегий" FontSize="20" FontWeight="SemiBold"/>
                  <TextBlock Text="Проверяю, какая стратегия лучше открывает твои сайты" Foreground="{StaticResource Muted}" FontSize="12" Margin="0,2,0,0"/>
                </StackPanel>
              </DockPanel>

              <StackPanel x:Name="TestSetup" Grid.Row="1">
                <TextBlock Text="ЧТО ПРОВЕРЯТЬ" FontSize="11" FontWeight="SemiBold" Foreground="#5A6070" Margin="2,0,0,8"/>
                <WrapPanel x:Name="GroupChips" Margin="0,0,0,10"/>
                <TextBlock Text="КАКИЕ СТРАТЕГИИ" FontSize="11" FontWeight="SemiBold" Foreground="#5A6070" Margin="2,0,0,8"/>
                <ScrollViewer MaxHeight="170" VerticalScrollBarVisibility="Auto">
                  <WrapPanel x:Name="StratChips"/>
                </ScrollViewer>
              </StackPanel>

              <Grid Grid.Row="2" Margin="0,14,0,12">
                <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                <StackPanel VerticalAlignment="Center" Margin="0,0,16,0">
                  <TextBlock x:Name="TestStatus" FontSize="12.5" Foreground="#B8BDC9" TextTrimming="CharacterEllipsis"/>
                  <Border Height="8" CornerRadius="4" Background="#22262F" Margin="0,8,0,0" ClipToBounds="True">
                    <Grid>
                      <Border x:Name="TestBar" CornerRadius="4" RenderTransformOrigin="0,0.5">
                        <Border.Background>
                          <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                            <GradientStop Color="#2BB57A" Offset="0"/>
                            <GradientStop Color="#3DDC97" Offset="1"/>
                          </LinearGradientBrush>
                        </Border.Background>
                        <Border.RenderTransform><ScaleTransform x:Name="TestBarScale" ScaleX="0"/></Border.RenderTransform>
                      </Border>
                      <Rectangle x:Name="TestShimmer" Width="120" HorizontalAlignment="Left" Opacity="0" IsHitTestVisible="False">
                        <Rectangle.Fill>
                          <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                            <GradientStop Color="#00FFFFFF" Offset="0"/>
                            <GradientStop Color="#55FFFFFF" Offset="0.5"/>
                            <GradientStop Color="#00FFFFFF" Offset="1"/>
                          </LinearGradientBrush>
                        </Rectangle.Fill>
                        <Rectangle.RenderTransform><TranslateTransform x:Name="TestShimmerT" X="-120"/></Rectangle.RenderTransform>
                      </Rectangle>
                    </Grid>
                  </Border>
                </StackPanel>
                <Button x:Name="RunBtn" Grid.Column="1" Style="{StaticResource AccentBtn}" Content="Запустить тест" Padding="20,10" MinWidth="150"/>
              </Grid>

              <Grid Grid.Row="3">
                <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
                <TextBlock x:Name="ResultsHead" Text="РЕЗУЛЬТАТЫ · нажми на строку, чтобы увидеть сайты" FontSize="11" FontWeight="SemiBold" Foreground="#5A6070" Margin="2,0,0,8" Visibility="Collapsed"/>
                <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
                  <StackPanel x:Name="TestResults" Margin="0,0,6,0"/>
                </ScrollViewer>
              </Grid>
            </Grid>
          </Border>
        </Grid>

        <!-- Перенос настроек -->
        <Grid x:Name="MigOverlay" Grid.RowSpan="2" Visibility="Collapsed">
          <Border x:Name="MigDim" Background="#C0090A0D" CornerRadius="20" Opacity="0"/>
          <Border x:Name="MigPanel" Width="600" Margin="0,22" Background="#171A21" CornerRadius="18" BorderBrush="#2A2E38" BorderThickness="1"
                  Opacity="0" RenderTransformOrigin="0.5,0.5">
            <Border.RenderTransform><ScaleTransform x:Name="MigScale" ScaleX="0.94" ScaleY="0.94"/></Border.RenderTransform>
            <Border.Effect><DropShadowEffect BlurRadius="30" ShadowDepth="0" Opacity="0.6"/></Border.Effect>
            <Grid Margin="26,18,26,20">
              <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/>
              </Grid.RowDefinitions>
              <DockPanel Margin="0,0,0,16">
                <Button x:Name="MigClose" DockPanel.Dock="Right" Style="{StaticResource TitleBtn}"><TextBlock Text="✕" FontSize="13"/></Button>
                <StackPanel>
                  <TextBlock Text="Перенос настроек" FontSize="20" FontWeight="SemiBold"/>
                  <TextBlock Text="Твои сайты, группы и настройки переедут в новую версию zapret" Foreground="{StaticResource Muted}" FontSize="12" Margin="0,2,0,0"/>
                </StackPanel>
              </DockPanel>

              <Grid x:Name="MigCards" Grid.Row="1" Margin="0,0,0,14">
                <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="90"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <Border Background="#1E2129" CornerRadius="14" Padding="14,12" BorderBrush="#2A2E38" BorderThickness="1">
                  <StackPanel>
                    <TextBlock Text="ОТКУДА · старая" FontSize="10.5" FontWeight="SemiBold" Foreground="#5A6070" Margin="2,0,0,8"/>
                    <ComboBox x:Name="MigFromBox"/>
                  </StackPanel>
                </Border>
                <Canvas Grid.Column="1" Height="10" Width="60" ClipToBounds="True" VerticalAlignment="Center" HorizontalAlignment="Center">
                  <StackPanel Orientation="Horizontal">
                    <StackPanel.RenderTransform><TranslateTransform x:Name="MigDotsT" X="-40"/></StackPanel.RenderTransform>
                    <Ellipse Width="6" Height="6" Fill="#3DDC97" Margin="0,2,14,2" Opacity="0.35"/>
                    <Ellipse Width="6" Height="6" Fill="#3DDC97" Margin="0,2,14,2" Opacity="0.6"/>
                    <Ellipse Width="6" Height="6" Fill="#3DDC97" Margin="0,2,14,2" Opacity="1"/>
                    <Ellipse Width="6" Height="6" Fill="#3DDC97" Margin="0,2,14,2" Opacity="0.35"/>
                    <Ellipse Width="6" Height="6" Fill="#3DDC97" Margin="0,2,14,2" Opacity="0.6"/>
                    <Ellipse Width="6" Height="6" Fill="#3DDC97" Margin="0,2,14,2" Opacity="1"/>
                  </StackPanel>
                </Canvas>
                <Border Grid.Column="2" Background="#1A2621" CornerRadius="14" Padding="14,12" BorderBrush="#2D5A47" BorderThickness="1">
                  <Border.Effect><DropShadowEffect x:Name="MigToGlow" Color="#3DDC97" BlurRadius="0" ShadowDepth="0" Opacity="0.7"/></Border.Effect>
                  <StackPanel>
                    <TextBlock Text="КУДА · новая" FontSize="10.5" FontWeight="SemiBold" Foreground="#5A9C80" Margin="2,0,0,8"/>
                    <ComboBox x:Name="MigToBox"/>
                  </StackPanel>
                </Border>
              </Grid>


              <ScrollViewer x:Name="MigScroll" Grid.Row="2" VerticalScrollBarVisibility="Auto">
                <StackPanel x:Name="MigSteps" Margin="0,0,6,0"/>
              </ScrollViewer>
              <StackPanel x:Name="MigEmpty" Grid.Row="2" Panel.ZIndex="5" VerticalAlignment="Center" HorizontalAlignment="Center" Visibility="Collapsed">
                <TextBlock Text="📦" FontFamily="Segoe UI Emoji" FontSize="40" HorizontalAlignment="Center"/>
                <TextBlock Text="Новая версия zapret не найдена" FontSize="15" FontWeight="SemiBold" HorizontalAlignment="Center" Margin="0,10,0,4"/>
                <TextBlock Text="Скачай новую версию и распакуй её в папку Zapret рядом с текущей. Потом открой это окно снова." Foreground="{StaticResource Muted}"
                           FontSize="12.5" TextWrapping="Wrap" TextAlignment="Center" MaxWidth="380"/>
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,16,0,0">
                  <Button x:Name="MigOpenFolder" Style="{StaticResource Btn}" Content="Открыть папку Zapret" Margin="0,0,8,0"/>
                  <Button x:Name="MigDemo" Style="{StaticResource AccentBtn}" Content="Посмотреть демо"/>
                </StackPanel>
              </StackPanel>

              <DockPanel Grid.Row="3" Margin="0,14,0,0">
                <Button x:Name="MigRun" DockPanel.Dock="Right" Style="{StaticResource AccentBtn}" Content="Перенести" Padding="22,10" MinWidth="140"/>
                <TextBlock x:Name="MigDone" Text="Готово! Оригиналы файлов автора сохранены как .bak" Foreground="#7FE8B8" FontSize="12.5"
                           VerticalAlignment="Center" Visibility="Collapsed" Opacity="0" TextWrapping="Wrap"/>
              </DockPanel>
            </Grid>
          </Border>
        </Grid>

        <!-- Поделиться / Вставить -->
        <Grid x:Name="ShareOverlay" Grid.RowSpan="2" Visibility="Collapsed">
          <Border x:Name="ShareDim" Background="#C0090A0D" CornerRadius="20" Opacity="0"/>
          <Border x:Name="SharePanel" Width="560" Margin="0,22" Background="#171A21" CornerRadius="18" BorderBrush="#2A2E38" BorderThickness="1"
                  Opacity="0" RenderTransformOrigin="0.5,0.5">
            <Border.RenderTransform><ScaleTransform x:Name="ShareScale" ScaleX="0.94" ScaleY="0.94"/></Border.RenderTransform>
            <Border.Effect><DropShadowEffect BlurRadius="30" ShadowDepth="0" Opacity="0.6"/></Border.Effect>
            <Grid Margin="26,18,26,20">
              <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
              <DockPanel Margin="0,0,0,12">
                <Button x:Name="ShareClose" DockPanel.Dock="Right" Style="{StaticResource TitleBtn}"><TextBlock Text="✕" FontSize="13"/></Button>
                <StackPanel>
                  <TextBlock x:Name="ShareTitle" FontSize="20" FontWeight="SemiBold"/>
                  <TextBlock x:Name="ShareSub" Foreground="{StaticResource Muted}" FontSize="12" Margin="0,2,0,0" TextWrapping="Wrap"/>
                </StackPanel>
              </DockPanel>
              <DockPanel Grid.Row="1" Margin="2,0,8,8">
                <TextBlock x:Name="ShareAll" DockPanel.Dock="Right" Text="Снять все" Foreground="#7FE8B8" FontSize="12" Cursor="Hand"/>
                <TextBlock x:Name="ShareCount" Foreground="{StaticResource Muted}" FontSize="12"/>
              </DockPanel>
              <Border Grid.Row="2" Background="#1E2129" CornerRadius="14" BorderBrush="#262A33" BorderThickness="1" Padding="6">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                  <StackPanel x:Name="ShareList" Margin="8,6,10,6"/>
                </ScrollViewer>
              </Border>
              <DockPanel Grid.Row="3" Margin="0,14,0,0">
                <Button x:Name="ShareGo" DockPanel.Dock="Right" Style="{StaticResource AccentBtn}" Padding="22,10" MinWidth="150"/>
                <TextBlock/>
              </DockPanel>
            </Grid>
          </Border>
        </Grid>

        <!-- QR для телефона -->
        <Grid x:Name="QrOverlay" Grid.RowSpan="2" Visibility="Collapsed">
          <Border x:Name="QrDim" Background="#C0090A0D" CornerRadius="20" Opacity="0"/>
          <Border x:Name="QrPanel" Width="440" VerticalAlignment="Center" Background="#171A21" CornerRadius="18" BorderBrush="#2A2E38" BorderThickness="1"
                  Opacity="0" RenderTransformOrigin="0.5,0.5">
            <Border.RenderTransform><ScaleTransform x:Name="QrScale" ScaleX="0.94" ScaleY="0.94"/></Border.RenderTransform>
            <Border.Effect><DropShadowEffect BlurRadius="30" ShadowDepth="0" Opacity="0.6"/></Border.Effect>
            <StackPanel Margin="26,18,26,22">
              <DockPanel Margin="0,0,0,10">
                <Button x:Name="QrClose" DockPanel.Dock="Right" Style="{StaticResource TitleBtn}"><TextBlock Text="✕" FontSize="13"/></Button>
                <TextBlock Text="Telegram на телефоне" FontSize="20" FontWeight="SemiBold" VerticalAlignment="Center"/>
              </DockPanel>
              <TextBlock Text="1. Телефон должен быть в той же Wi-Fi сети, что и компьютер" Foreground="#B8BDC9" FontSize="12.5" TextWrapping="Wrap" Margin="0,0,0,3"/>
              <TextBlock Text="2. Наведи камеру телефона на код" Foreground="#B8BDC9" FontSize="12.5" TextWrapping="Wrap" Margin="0,0,0,3"/>
              <TextBlock Text="3. В Telegram нажми «Подключить прокси»" Foreground="#B8BDC9" FontSize="12.5" TextWrapping="Wrap" Margin="0,0,0,14"/>
              <Border x:Name="QrBox" Background="#FFFFFF" CornerRadius="14" Padding="14" Width="250" Height="250" HorizontalAlignment="Center" RenderTransformOrigin="0.5,0.5">
                <Border.RenderTransform><ScaleTransform x:Name="QrBoxS"/></Border.RenderTransform>
                <Grid>
                  <Viewbox Stretch="Uniform"><Path x:Name="QrPath" Fill="#000000"/></Viewbox>
                  <TextBlock x:Name="QrWait" Text="Готовлю…" Foreground="#5A6070" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Grid>
              </Border>
              <TextBlock x:Name="QrAddr" Foreground="{StaticResource Muted}" FontSize="11.5" HorizontalAlignment="Center" Margin="0,10,0,12"/>
              <Button x:Name="QrCopy" Style="{StaticResource Btn}" Content="Скопировать ссылку" HorizontalAlignment="Center"/>
            </StackPanel>
          </Border>
        </Grid>

        <!-- Уведомление -->
        <Border x:Name="Toast" Grid.Row="1" VerticalAlignment="Bottom" HorizontalAlignment="Center" Margin="0,0,0,18"
                Background="#2A2E38" CornerRadius="12" Padding="18,10" Opacity="0" IsHitTestVisible="False" MaxWidth="560">
          <Border.Effect><DropShadowEffect BlurRadius="16" ShadowDepth="0" Opacity="0.5"/></Border.Effect>
          <Border.RenderTransform><TranslateTransform x:Name="ToastShift" Y="30"/></Border.RenderTransform>
          <TextBlock x:Name="ToastText" FontSize="13" TextWrapping="Wrap"/>
        </Border>
      </Grid>
    </Border>
    </Grid>
  </Grid>
</Window>
'@
$xamlText = Apply-Theme $xamlText
[xml]$xaml = $xamlText

$reader = New-Object System.Xml.XmlNodeReader $xaml
$win = [Windows.Markup.XamlReader]::Load($reader)
$ui = @{}
$xaml.SelectNodes('//*[@*[local-name()="Name"]]') | ForEach-Object {
    $n = $_.GetAttribute('Name', 'http://schemas.microsoft.com/winfx/2006/xaml')
    if ($n) { $ui[$n] = $win.FindName($n) }
}

# ======================= Анимации =======================
function Ease { $e = New-Object System.Windows.Media.Animation.CubicEase; $e.EasingMode = 'EaseOut'; $e }
function Anim-Double($target, $prop, $to, $ms = 250, $from = $null, [switch]$Forever, $beginMs = 0, [switch]$NoEase, [switch]$AutoReverse, $Easing = $null, [switch]$Low) {
    $a = New-Object System.Windows.Media.Animation.DoubleAnimation
    $a.To = $to
    if ($null -ne $from) { $a.From = $from }
    $a.Duration = New-Object System.Windows.Duration ([TimeSpan]::FromMilliseconds($ms))
    if ($beginMs) { $a.BeginTime = [TimeSpan]::FromMilliseconds($beginMs) }
    if ($Forever) { $a.RepeatBehavior = [System.Windows.Media.Animation.RepeatBehavior]::Forever }
    # медленные фоновые анимации — 30 кадров/с: глазу незаметно, а ПК грузит меньше
    if ($Low) { [System.Windows.Media.Animation.Timeline]::SetDesiredFrameRate($a, 30) }
    if ($AutoReverse) { $a.AutoReverse = $true }
    if ($Easing) { $a.EasingFunction = $Easing } elseif (-not $NoEase) { $a.EasingFunction = Ease }
    $target.BeginAnimation($prop, $a)
}
function Anim-Color($brush, $hex, $ms = 350) {
    $a = New-Object System.Windows.Media.Animation.ColorAnimation
    $a.To = Color-Of $hex
    $a.Duration = New-Object System.Windows.Duration ([TimeSpan]::FromMilliseconds($ms))
    $a.EasingFunction = Ease
    $brush.BeginAnimation([System.Windows.Media.SolidColorBrush]::ColorProperty, $a)
}
function Stop-Anim($target, $prop) { $target.BeginAnimation($prop, $null) }

$OP = [System.Windows.UIElement]::OpacityProperty
$SX = [System.Windows.Media.ScaleTransform]::ScaleXProperty
$SY = [System.Windows.Media.ScaleTransform]::ScaleYProperty

$script:toastTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:toastTimer.Interval = [TimeSpan]::FromSeconds(3.2)
$script:toastTimer.Add_Tick({
    $script:toastTimer.Stop()
    Anim-Double $ui.Toast $OP 0 300
    Anim-Double $ui.ToastShift ([System.Windows.Media.TranslateTransform]::YProperty) 30 300
})
function Show-Toast($text, [switch]$Err) {
    $ui.ToastText.Text = T $text
    $ui.ToastText.Foreground = if ($Err) { Brush '#FF5E62' } else { Brush '#E6E8EE' }
    Anim-Double $ui.Toast $OP 1 250
    Anim-Double $ui.ToastShift ([System.Windows.Media.TranslateTransform]::YProperty) 0 300
    $script:toastTimer.Stop(); $script:toastTimer.Start()
}
function Safe([scriptblock]$b) {
    try { & $b } catch { Write-Log ($_ | Out-String); Show-Toast ("Ошибка: " + $_.Exception.Message) -Err }
}

# ======================= Состояние zapret =======================
function Get-ServiceState {
    try {
        $s = Get-Service -Name 'zapret' -ErrorAction Stop
        return "$($s.Status)"
    } catch { return $null }
}
function Get-RunningInfo {
    $p = @(Get-Process -Name 'winws' -ErrorAction SilentlyContinue)
    if ($p.Count -eq 0) { return $null }
    $name = $null
    foreach ($x in $p) { if ($x.MainWindowTitle -match '^zapret:\s*(.+)$') { $name = $matches[1].Trim() } }
    return @{ Name = $name }
}

$script:state = 'unknown'   # off | on | starting | stopping
$script:startDeadline = $null
$script:pendingApply = $false

# ---- Буква Z на кнопке ----
$script:zShown = $false
$AngP = [System.Windows.Media.RotateTransform]::AngleProperty
function Back-Ease { $e = New-Object System.Windows.Media.Animation.BackEase; $e.EasingMode = 'EaseOut'; $e.Amplitude = 0.5; $e }
function Sine-Ease { $e = New-Object System.Windows.Media.Animation.SineEase; $e.EasingMode = 'EaseInOut'; $e }
function Show-Z([switch]$Spin) {
    if (-not $script:zShown) {
        $script:zShown = $true
        Anim-Double $ui.IconWrap $OP 0 200
        Anim-Double $ui.PowerIconScale $SX 0.3 260; Anim-Double $ui.PowerIconScale $SY 0.3 260
        Anim-Double $ui.PowerIconRot $AngP 120 260
        Anim-Double $ui.ZWrap $OP 1 260 0 -beginMs 120
        Anim-Double $ui.ZScale $SX 1 520 0.3 -beginMs 120 -Easing (Back-Ease)
        Anim-Double $ui.ZScale $SY 1 520 0.3 -beginMs 120 -Easing (Back-Ease)
    }
    if ($Spin) {
        # вращение "монеткой" вокруг вертикальной оси
        Anim-Double $ui.ZFlip $SX -1 420 1 -Forever -AutoReverse -Easing (Sine-Ease)
    } else {
        # плавно останавливаем вращение
        Anim-Double $ui.ZFlip $SX 1 450 -Easing (Back-Ease)
    }
}
function Hide-Z {
    if (-not $script:zShown) { return }
    $script:zShown = $false
    Anim-Double $ui.ZFlip $SX 1 300
    Anim-Double $ui.ZScale $SX 0.3 280; Anim-Double $ui.ZScale $SY 0.3 280
    Anim-Double $ui.ZWrap $OP 0 240
    Anim-Double $ui.IconWrap $OP 1 300 0 -beginMs 150
    Anim-Double $ui.PowerIconScale $SX 1 520 0.3 -beginMs 150 -Easing (Back-Ease)
    Anim-Double $ui.PowerIconScale $SY 1 520 0.3 -beginMs 150 -Easing (Back-Ease)
    Anim-Double $ui.PowerIconRot $AngP 0 520 -120 -beginMs 150
}
function Set-ZColor($hex) { Anim-Color $ui.ZBrush $hex 300; Anim-Color $ui.ZStroke $hex 300 }

function Set-Visual($mode, $detail) {
    if ($script:state -eq $mode -and $mode -ne 'on') { return }
    $prev = $script:state
    $script:state = $mode
    if ($prev -ne $mode) { $script:visAt = [DateTime]::UtcNow.Ticks; if ($Mon) { $Mon.Wake = $true }; if ($script:tray) { try { $script:tray.Text = T $(if ($mode -eq 'on') { 'Zapret · работает' } else { 'Zapret · выключен' }) } catch {} } }
    switch ($mode) {
        'on' {
            $ui.StatusText.Text = T $('Работает')
            $ui.SubText.Text = T $($detail)
            if ($prev -ne 'on') {
                Set-ZColor '#0F1115'
                Show-Z
                Anim-Color $ui.PowerFill '#3DDC97'
                Anim-Color $ui.IconBrush '#0F1115'; Anim-Color $ui.IconBrush2 '#0F1115'
                $ui.TitleDot.Fill = $win.FindResource('Accent')
                Anim-Double $ui.PowerShadow ([System.Windows.Media.Effects.DropShadowEffect]::BlurRadiusProperty) 40 500
                Stop-Anim $ui.Spinner $OP; $ui.Spinner.Opacity = 0
                foreach ($g in @(@($ui.Glow1, $ui.Glow1S, 0), @($ui.Glow2, $ui.Glow2S, 1100))) {
                    Anim-Double $g[0] $OP 0 2200 0.7 -Forever -beginMs $g[2] -NoEase -Low
                    Anim-Double $g[1] $SX 1.38 2200 1 -Forever -beginMs $g[2] -Low
                    Anim-Double $g[1] $SY 1.38 2200 1 -Forever -beginMs $g[2] -Low
                }
            }
        }
        'off' {
            $ui.StatusText.Text = T $('Выключен')
            Hide-Z
            $ui.SubText.Text = T $(if ($detail) { $detail } else { 'Нажми, чтобы включить' })
            Anim-Color $ui.PowerFill '#262A33'
            Anim-Color $ui.IconBrush '#8A90A0'; Anim-Color $ui.IconBrush2 '#8A90A0'
            $ui.TitleDot.Fill = New-Object System.Windows.Media.SolidColorBrush ((Color-Of '#5A6070'))
            Anim-Double $ui.PowerShadow ([System.Windows.Media.Effects.DropShadowEffect]::BlurRadiusProperty) 0 400
            foreach ($g in @($ui.Glow1, $ui.Glow2)) { Stop-Anim $g $OP; $g.Opacity = 0 }
            Stop-Anim $ui.Spinner $OP; $ui.Spinner.Opacity = 0; Stop-Anim $ui.SpinRot ([System.Windows.Media.RotateTransform]::AngleProperty)
        }
        { $_ -in 'starting', 'stopping' } {
            $ui.StatusText.Text = T $(if ($mode -eq 'starting') { 'Запуск…' } else { 'Остановка…' })
            if ($mode -eq 'starting') { Set-ZColor '#3DDC97' }
            Show-Z -Spin
            $ui.SubText.Text = T $($detail)
            Anim-Double $ui.Spinner $OP 1 200
            Anim-Double $ui.SpinRot ([System.Windows.Media.RotateTransform]::AngleProperty) 360 900 0 -Forever -NoEase
        }
    }
}

function Refresh-State {
    if ($script:testing) { return }
    if ($script:state -eq 'starting' -or $script:state -eq 'stopping') {
        $Mon.Fast = $true
        $r = Get-RunningInfo
        if ($script:state -eq 'starting' -and $r) {
            $n = if ($r.Name) { $r.Name } else { $Settings.strategy }
            Set-Visual 'on' "Стратегия: $n"
            Show-Toast "zapret включён · $n"
        } elseif ($script:state -eq 'stopping' -and -not $r) {
            Set-Visual 'off'
        } elseif ((Get-Date) -gt $script:startDeadline) {
            if ($script:state -eq 'starting') {
                Set-Visual 'off' 'Не удалось запустить'
                Show-Toast ('winws.exe не запустился. Попробуй другую стратегию' + $(if ($script:hiddenStart) { ' или включи «Показывать окно winws», чтобы увидеть ошибку.' } else { '.' })) -Err
            } else { $script:state = 'x'; Refresh-State }
        }
        return
    }
    $Mon.Fast = $false
    if ($Mon.At -le $script:visAt) { return }   # ждём свежие данные от монитора
    $svc = $Mon.Svc
    $r = if ($Mon.Winws) { @{ Name = $Mon.WinwsName } } else { $null }
    if ($svc -eq 'Running') {
        $n = $Mon.SvcName
        Set-Visual 'on' ("Работает как служба" + $(if ($n) { " · $n" } else { '' }))
    } elseif ($r) {
        $n = if ($r.Name) { $r.Name } else { $Settings.strategy }
        Set-Visual 'on' "Стратегия: $n"
    } else {
        Set-Visual 'off'
    }
}

function Start-Zapret {
    if (-not $IsAdmin) { Show-Toast 'Нужны права администратора — запусти через Zapret.bat' -Err; return }
    $item = $ui.StrategyBox.SelectedItem
    if (-not $item) { Show-Toast 'Выбери стратегию' -Err; return }
    $bat = Join-Path $Root ($item.Tag)
    Set-Visual 'starting' $item.Content
    $script:startDeadline = (Get-Date).AddSeconds(12)
    $args_ = $null
    if (-not $Settings.showWindow) { $args_ = Get-WinwsArgs $bat }
    if ($args_) {
        # Без окна: запускаем winws.exe напрямую с параметрами из батника
        Prepare-UserLists
        try { Start-Process -FilePath 'netsh.exe' -ArgumentList 'interface tcp set global timestamps=enabled' -WindowStyle Hidden | Out-Null } catch {}
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = "$Root\bin\winws.exe"
        $psi.Arguments = $args_
        $psi.WorkingDirectory = "$Root\bin"
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        [void][System.Diagnostics.Process]::Start($psi)
        $script:hiddenStart = $true
    } else {
        $env:NO_UPDATE_CHECK = '1'
        Start-Process -FilePath 'cmd.exe' -ArgumentList "/c `"`"$bat`"`"" -WorkingDirectory $Root -WindowStyle Hidden | Out-Null
        $script:hiddenStart = $false
    }
}
function Prepare-UserLists {
    $f = Join-Path $Lists 'ipset-exclude-user.txt'
    if (-not (Test-Path -LiteralPath $f)) { [IO.File]::WriteAllText($f, "203.0.113.113/32`r`n", [Text.Encoding]::ASCII) }
    if (-not (Test-Path -LiteralPath $UserListFile)) { Write-UserGroups (New-Object System.Collections.Specialized.OrderedDictionary) }
    $f = Join-Path $Lists 'list-exclude-user.txt'
    if (-not (Test-Path -LiteralPath $f)) { [IO.File]::WriteAllText($f, "domain.example.abc`r`n", [Text.Encoding]::ASCII) }
}
# Собрать командную строку winws из батника стратегии (как это сделал бы cmd)
function Get-WinwsArgs([string]$batPath) {
    $lines = [IO.File]::ReadAllLines($batPath)
    $buf = $null
    foreach ($l in $lines) {
        if ($null -eq $buf) {
            if ($l -match 'start\s+"zapret[^"]*"\s+/min\s+"%BIN%winws\.exe"(.*)$') { $buf = $matches[1] } else { continue }
        } else { $buf += $l }
        if ($buf.TrimEnd().EndsWith('^')) { $buf = $buf.TrimEnd(); $buf = $buf.Substring(0, $buf.Length - 1) + ' ' }
        else { break }
    }
    if ($null -eq $buf) { return $null }

    $g = Read-Game
    $tcp = '12'; $udp = '12'
    switch ($g.Mode) { 'all' { $tcp = $g.Tcp; $udp = $g.Udp } 'tcp' { $tcp = $g.Tcp } 'udp' { $udp = $g.Udp } }
    $vars = @{ 'BIN' = "$Root\bin\"; 'LISTS' = $Lists + '\'; 'GameFilterTCP' = $tcp; 'GameFilterUDP' = $udp; 'GameFilter' = $(if ($g.Mode -eq 'udp') { $udp } else { $tcp }) }
    $s = [regex]::Replace($buf, '%(\w+)%', { param($m) if ($vars.ContainsKey($m.Groups[1].Value)) { $vars[$m.Groups[1].Value] } else { $m.Value } })
    if ($s -match '%\w+%') { return $null }

    # Каретки вне кавычек cmd убирает (^! -> !)
    $sb = New-Object System.Text.StringBuilder
    $inQ = $false
    for ($i = 0; $i -lt $s.Length; $i++) {
        $c = $s[$i]
        if ($c -eq '"') { $inQ = -not $inQ; [void]$sb.Append($c); continue }
        if ($c -eq '^' -and -not $inQ -and $i + 1 -lt $s.Length) { $i++; [void]$sb.Append($s[$i]); continue }
        [void]$sb.Append($c)
    }
    return $sb.ToString().Trim()
}

function Stop-Zapret([switch]$Quiet) {
    if (-not $IsAdmin) { Show-Toast 'Нужны права администратора — запусти через Zapret.bat' -Err; return }
    Set-Visual 'stopping' ''
    $script:startDeadline = (Get-Date).AddSeconds(8)
    if ((Get-ServiceState) -eq 'Running') {
        Stop-Service -Name 'zapret' -Force -ErrorAction SilentlyContinue
        if (-not $Quiet) { Show-Toast 'Служба остановлена. Она снова запустится после перезагрузки ПК.' }
    }
    Get-Process -Name 'winws' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
function Restart-Zapret {
    Stop-Zapret -Quiet
    $t = New-Object System.Windows.Threading.DispatcherTimer
    $t.Interval = [TimeSpan]::FromMilliseconds(400)
    $script:restartTimer = $t
    $t.Add_Tick({
        if (-not (Get-RunningInfo)) {
            $script:restartTimer.Stop()
            $script:state = 'off'
            Start-Zapret
        }
    })
    $t.Start()
}

function Mark-Changed {
    if ($script:state -eq 'on' -and -not $script:pendingApply) {
        $script:pendingApply = $true
        $ui.ApplyBar.Visibility = 'Visible'
        Anim-Double $ui.ApplyBar $OP 1 300 0
        $ui.ApplyBar.RenderTransform = New-Object System.Windows.Media.TranslateTransform
        Anim-Double $ui.ApplyBar.RenderTransform ([System.Windows.Media.TranslateTransform]::YProperty) 0 350 14
    }
}
function Hide-Apply {
    $script:pendingApply = $false
    Anim-Double $ui.ApplyBar $OP 0 200
    $ui.ApplyBar.Visibility = 'Collapsed'
}

# ======================= Мои сайты (группы) =======================
$Placeholder = 'domain.example.abc'
$script:expanded = @{}

function Get-BaseDomain([string]$d) {
    $p = $d.Trim('.').Split('.')
    if ($p.Count -le 2) { return ($p -join '.') }
    return ($p[-2..-1] -join '.')
}
# Группы хранятся в list-general-user.txt строками "# @group Имя" (winws их игнорирует как комментарии)
function Read-UserGroups {
    $groups = New-Object System.Collections.Specialized.OrderedDictionary
    if (-not (Test-Path -LiteralPath $UserListFile)) { return $groups }
    $cur = $null
    foreach ($l in (Get-Content -LiteralPath $UserListFile -Encoding UTF8)) {
        $s = $l.Trim()
        if ($s -match '^#\s*@group\s+(.+)$') { $cur = $matches[1].Trim(); if (-not $groups.Contains($cur)) { $groups[$cur] = New-Object System.Collections.Generic.List[string] }; continue }
        if (-not $s -or $s.StartsWith('#') -or $s -eq $Placeholder) { continue }
        $g = $cur
        if (-not $g) { $g = Find-GroupFor $groups $s }
        if (-not $groups.Contains($g)) { $groups[$g] = New-Object System.Collections.Generic.List[string] }
        if (-not $groups[$g].Contains($s)) { $groups[$g].Add($s) }
    }
    return $groups
}
function Write-UserGroups($groups) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# Never leave this file empty'); $lines.Add($Placeholder)
    foreach ($k in @($groups.Keys)) {
        if ($groups[$k].Count -eq 0) { continue }
        $lines.Add("# @group $k")
        foreach ($d in $groups[$k]) { $lines.Add($d) }
    }
    [IO.File]::WriteAllText($UserListFile, (($lines -join "`r`n") + "`r`n"), $utf8)
}
function Read-UserList { $g = Read-UserGroups; return @(foreach ($k in $g.Keys) { $g[$k] }) }
function Write-UserList([string[]]$domains) {
    $g = Read-UserGroups
    foreach ($k in @($g.Keys)) { $keep = @($g[$k] | Where-Object { $domains -contains $_ }); $g[$k] = New-Object System.Collections.Generic.List[string]; foreach ($d in $keep) { $g[$k].Add($d) } }
    Write-UserGroups $g
}
function Find-GroupFor($groups, [string]$dom) {
    $base = Get-BaseDomain $dom
    foreach ($k in $groups.Keys) { foreach ($d in $groups[$k]) { if ((Get-BaseDomain $d) -eq $base) { return $k } } }
    # Похожие домены одного сервиса (например, example.com / exampleapp.com) - в одну группу
    $lbl = $base.Split('.')[0]
    foreach ($k in $groups.Keys) {
        foreach ($d in $groups[$k]) {
            $o = (Get-BaseDomain $d).Split('.')[0]
            if ($o.Length -ge 4 -and $lbl.Length -ge 4 -and ($lbl.StartsWith($o) -or $o.StartsWith($lbl))) { return $k }
        }
    }
    $name = $base.Split('.')[0]
    if ($name.Length -gt 0) { $name = $name.Substring(0, 1).ToUpper() + $name.Substring(1) }
    return $name
}

# Встроенные списки zapret - только просмотр
$BuiltinRules = @(
    @('DNS-серверы', '(^|\.)dns\.|^doh\.|-dns\.|freedns|^dns\.'),
    @('Discord', 'discord|^dis\.gd$'),
    @('YouTube', 'youtu|ytimg|ggpht|googlevideo|^yt[0-9-]|jnn-pa'),
    @('Google', 'google|gstatic'),
    @('Cloudflare', 'cloudflare|cloudfront|encryptedsni'),
    @('Twitch и смайлы', 'frankerfacez|ffzap|betterttv|7tv|live-video')
)
function Get-BuiltinGroups {
    $groups = New-Object System.Collections.Specialized.OrderedDictionary
    foreach ($r in $BuiltinRules) { $groups[$r[0]] = New-Object System.Collections.Generic.List[string] }
    $groups['Прочее'] = New-Object System.Collections.Generic.List[string]
    foreach ($f in @('list-general.txt', 'list-google.txt')) {
        $p = Join-Path $Lists $f
        if (-not (Test-Path -LiteralPath $p)) { continue }
        foreach ($l in (Get-Content -LiteralPath $p -Encoding UTF8)) {
            $s = $l.Trim().TrimStart('^')
            if (-not $s -or $s.StartsWith('#')) { continue }
            $g = 'Прочее'
            foreach ($r in $BuiltinRules) { if ($s -match $r[1]) { $g = $r[0]; break } }
            if (-not $groups[$g].Contains($s)) { $groups[$g].Add($s) }
        }
    }
    foreach ($k in @($groups.Keys)) { if ($groups[$k].Count -eq 0) { $groups.Remove($k) } }
    return $groups
}

function Plural([int]$n) {
    $m10 = $n % 10; $m100 = $n % 100
    if ($m10 -eq 1 -and $m100 -ne 11) { return "$n сайт" }
    if ($m10 -ge 2 -and $m10 -le 4 -and ($m100 -lt 12 -or $m100 -gt 14)) { return "$n сайта" }
    return "$n сайтов"
}
$GroupColors = @{ 'DNS-серверы' = '#F5B44A'; 'Discord' = '#8C9BFF'; 'YouTube' = '#FF5E62'; 'Google' = '#4DA3FF'; 'Cloudflare' = '#FF9A4D'; 'Twitch и смайлы' = '#B57BFF'; 'Прочее' = '#9AA3B5' }
$UserPalette = @('#3DDC97', '#5ED3F3', '#FFB86B', '#FF7AB6', '#B69CFF', '#7EE081', '#FFD166', '#6EC1FF')
function Get-GroupColor([string]$name, [bool]$builtin) {
    if ($builtin -and $GroupColors.ContainsKey($name)) { return $GroupColors[$name] }
    $h = 0; foreach ($ch in $name.ToCharArray()) { $h = ($h * 31 + [int]$ch) % 100003 }
    return $UserPalette[$h % $UserPalette.Count]
}
function Brush($hex) { New-Object System.Windows.Media.SolidColorBrush (Color-Of $hex) }

function New-SectionLabel($text) {
    $t = New-Object System.Windows.Controls.TextBlock
    $t.Text = T $($text); $t.FontSize = 11; $t.FontWeight = 'SemiBold'; $t.Foreground = Brush '#5A6070'
    $t.Margin = '4,10,0,6'
    return $t
}

function New-GroupRow([string]$key, [string]$name, $domains, [bool]$editable, [int]$index = 0) {
    $color = Get-GroupColor $name (-not $editable)
    $wrap = New-Object System.Windows.Controls.StackPanel
    $wrap.Margin = '0,0,0,4'

    $head = New-Object System.Windows.Controls.Border
    $head.CornerRadius = 10; $head.Padding = '12,9,8,9'; $head.Cursor = 'Hand'
    $head.Background = Brush '#20232B'
    $head.BorderThickness = '3,0,0,0'
    $head.BorderBrush = Brush '#00000000'
    $g = New-Object System.Windows.Controls.Grid
    foreach ($w in @('Auto', '*', 'Auto', 'Auto')) {
        $c = New-Object System.Windows.Controls.ColumnDefinition
        $c.Width = if ($w -eq '*') { New-Object System.Windows.GridLength(1, 'Star') } else { [System.Windows.GridLength]::Auto }
        $g.ColumnDefinitions.Add($c)
    }
    $dot = New-Object System.Windows.Shapes.Ellipse
    $dot.Width = 8; $dot.Height = 8; $dot.Margin = '0,0,11,0'; $dot.VerticalAlignment = 'Center'
    $dot.Fill = Brush $color
    $glow = New-Object System.Windows.Media.Effects.DropShadowEffect
    $glow.Color = (Color-Of $color); $glow.BlurRadius = 10; $glow.ShadowDepth = 0; $glow.Opacity = 0.9
    $dot.Effect = $glow
    $nm = New-Object System.Windows.Controls.TextBlock
    $nm.Text = T $($name); $nm.FontSize = 13.5; $nm.FontWeight = 'SemiBold'; $nm.Foreground = Brush '#E6E8EE'; $nm.VerticalAlignment = 'Center'
    $nm.TextTrimming = 'CharacterEllipsis'
    [System.Windows.Controls.Grid]::SetColumn($nm, 1)
    $cnt = New-Object System.Windows.Controls.TextBlock
    $cnt.Text = T $(Plural $domains.Count); $cnt.FontSize = 12; $cnt.Foreground = Brush '#8A90A0'; $cnt.VerticalAlignment = 'Center'; $cnt.Margin = '8,0,8,0'
    [System.Windows.Controls.Grid]::SetColumn($cnt, 2)
    $dots = New-Object System.Windows.Controls.Border
    $dots.Width = 30; $dots.Height = 24; $dots.CornerRadius = 7
    $dt = New-Object System.Windows.Controls.TextBlock
    $dt.Text = T $('⋯'); $dt.FontSize = 16; $dt.Foreground = Brush '#8A90A0'; $dt.HorizontalAlignment = 'Center'; $dt.VerticalAlignment = 'Center'; $dt.Margin = '0,-3,0,0'
    $dots.Child = $dt
    [System.Windows.Controls.Grid]::SetColumn($dots, 3)
    foreach ($x in @($dot, $nm, $cnt, $dots)) { [void]$g.Children.Add($x) }
    $head.Child = $g

    # Раскрывающийся список сайтов
    $body = New-Object System.Windows.Controls.StackPanel
    $body.Margin = '14,4,0,6'
    $body.RenderTransform = New-Object System.Windows.Media.TranslateTransform
    foreach ($d in $domains) {
        $row = New-Object System.Windows.Controls.DockPanel
        $row.Margin = '0,1,0,1'
        if ($editable) {
            $del = New-Object System.Windows.Controls.Button
            $del.Style = $win.FindResource('DelBtn'); $del.ToolTip = 'Удалить'
            $del.Tag = @{ Group = $name; Domain = $d }
            $del.Add_Click($script:onDelDomain)
            [System.Windows.Controls.DockPanel]::SetDock($del, 'Right')
            [void]$row.Children.Add($del)
        }
        $tx = New-Object System.Windows.Controls.TextBlock
        $tx.Text = T $($d); $tx.FontSize = 12.5; $tx.Foreground = Brush '#B8BDC9'; $tx.VerticalAlignment = 'Center'; $tx.Margin = '6,3,0,3'
        $tx.TextTrimming = 'CharacterEllipsis'
        [void]$row.Children.Add($tx)
        [void]$body.Children.Add($row)
    }
    if (-not $editable) {
        $ro = New-Object System.Windows.Controls.TextBlock
        $ro.Text = T $('встроенный список zapret · только просмотр'); $ro.FontSize = 11; $ro.Foreground = Brush '#5A6070'; $ro.Margin = '6,6,0,2'
        [void]$body.Children.Add($ro)
    }
    if ($editable) {
        $foot = New-Object System.Windows.Controls.Grid
        $foot.Margin = '0,6,0,2'
        foreach ($w in @('*', 'Auto')) {
            $c = New-Object System.Windows.Controls.ColumnDefinition
            $c.Width = if ($w -eq '*') { New-Object System.Windows.GridLength(1, 'Star') } else { [System.Windows.GridLength]::Auto }
            $foot.ColumnDefinitions.Add($c)
        }
        $bx = New-Object System.Windows.Controls.Border
        $bx.Background = Brush '#22262F'; $bx.CornerRadius = 8; $bx.BorderBrush = Brush '#2E3340'; $bx.BorderThickness = 1; $bx.Height = 32
        $tb = New-Object System.Windows.Controls.TextBox
        $tb.Background = [System.Windows.Media.Brushes]::Transparent; $tb.BorderThickness = 0; $tb.Foreground = Brush '#E6E8EE'
        $tb.CaretBrush = Brush '#3DDC97'; $tb.Margin = '8,0'; $tb.VerticalAlignment = 'Center'; $tb.FontSize = 12.5
        $tb.ToolTip = "Добавить сайт в группу $name"
        $tb.Tag = @{ Group = $name; Box = $tb }
        $tb.Add_KeyDown($script:onGroupKey)
        $bx.Child = $tb
        $add = New-Object System.Windows.Controls.Button
        $add.Style = $win.FindResource('AccentBtn'); $add.Content = T $('+'); $add.Padding = '12,4'; $add.Margin = '6,0,0,0'
        $add.Tag = @{ Group = $name; Box = $tb }
        $add.Add_Click($script:onGroupAdd)
        [System.Windows.Controls.Grid]::SetColumn($add, 1)
        [void]$foot.Children.Add($bx); [void]$foot.Children.Add($add)
        [void]$body.Children.Add($foot)

        $rm = New-Object System.Windows.Controls.TextBlock
        $rm.Text = T $('Удалить группу'); $rm.FontSize = 11.5; $rm.Foreground = Brush '#C9606F'; $rm.Cursor = 'Hand'; $rm.Margin = '2,8,0,0'
        $rm.HorizontalAlignment = 'Left'
        $rm.Tag = @{ Group = $name; Count = $domains.Count }
        $rm.Add_MouseLeftButtonUp($script:onDelGroup)
        [void]$body.Children.Add($rm)
    }

    $isOpen = [bool]$script:expanded[$key]
    $body.Visibility = if ($isOpen) { 'Visible' } else { 'Collapsed' }
    if ($isOpen) { $dots.Background = Brush '#2E3340'; $dt.Foreground = Brush '#3DDC97' }

    if ($isOpen) { $head.BorderBrush = Brush $color }
    $head.Tag = @{ Key = $key; Body = $body; Dots = $dots; DotsText = $dt; Head = $head; Color = $color }
    $head.Add_MouseLeftButtonUp($script:onToggleGroup)
    $head.Add_MouseEnter({ Anim-Color $this.Background '#2A2E38' 160 })
    $head.Add_MouseLeave({ Anim-Color $this.Background '#20232B' 220 })

    # Плавное появление строк по очереди
    if ($script:animateRows) {
        $wrap.Opacity = 0
        $wrap.RenderTransform = New-Object System.Windows.Media.TranslateTransform
        Anim-Double $wrap $OP 1 320 0 -beginMs ($index * 40)
        Anim-Double $wrap.RenderTransform ([System.Windows.Media.TranslateTransform]::YProperty) 0 380 12 -beginMs ($index * 40)
    }

    [void]$wrap.Children.Add($head)
    [void]$wrap.Children.Add($body)
    return $wrap
}

$script:onToggleGroup = {
    param($s, $e)
    Safe {
        $t = $s.Tag
        $open = -not [bool]$script:expanded[$t.Key]
        $script:expanded[$t.Key] = $open
        if ($open) {
            $t.Body.Visibility = 'Visible'
            Anim-Double $t.Body.RenderTransform ([System.Windows.Media.TranslateTransform]::YProperty) 0 260 -8
            $t.Dots.Background = Brush '#2E3340'; $t.DotsText.Foreground = Brush $t.Color
            $t.Head.BorderBrush = Brush $t.Color
            $i = 0
            foreach ($ch in $t.Body.Children) {
                $ch.Opacity = 0
                Anim-Double $ch $OP 1 220 0 -beginMs ([Math]::Min($i * 18, 300))
                $i++
            }
        } else {
            $t.Body.Visibility = 'Collapsed'
            $t.Dots.Background = [System.Windows.Media.Brushes]::Transparent; $t.DotsText.Foreground = Brush '#8A90A0'
            $t.Head.BorderBrush = Brush '#00000000'
        }
    }
}
$script:onDelDomain = {
    param($s, $e)
    Safe {
        $t = $s.Tag
        $g = Read-UserGroups
        if ($g.Contains($t.Group)) { [void]$g[$t.Group].Remove($t.Domain) }
        Write-UserGroups $g
        Refresh-Sites
        Show-Toast "Удалён $($t.Domain)"
        Mark-Changed
    }
}
$script:onGroupAdd = { param($s, $e) Safe { Add-Site $s.Tag.Box.Text $s.Tag.Group } }
$script:onGroupKey = { param($s, $e) if ($e.Key -eq 'Return') { Safe { Add-Site $s.Tag.Box.Text $s.Tag.Group } } }
$script:onDelGroup = {
    param($s, $e)
    Safe {
        $t = $s.Tag
        $r = [System.Windows.MessageBox]::Show((T "Удалить группу «$($t.Group)» ($(Plural $t.Count))?"), 'Zapret', 'YesNo', 'Question')
        if ($r -ne 'Yes') { return }
        $g = Read-UserGroups
        $g.Remove($t.Group)
        Write-UserGroups $g
        $script:expanded.Remove("u:$($t.Group)")
        Refresh-Sites
        Show-Toast "Группа $($t.Group) удалена"
        Mark-Changed
    }
}

function Refresh-Sites {
    $panel = $ui.SitesPanel
    $panel.Children.Clear()
    $script:animateRows = -not $script:rowsShown
    $script:rowsShown = $true
    $ug = Read-UserGroups
    $total = 0; foreach ($k in $ug.Keys) { $total += $ug[$k].Count }
    $ui.SitesCount.Text = T $(Plural $total)
    [void]$panel.Children.Add((New-SectionLabel 'МОИ'))
    if ($ug.Count -eq 0) {
        $e = New-Object System.Windows.Controls.TextBlock
        $e.Text = T $('Пока пусто. Добавь сайт, который не открывается.'); $e.Foreground = Brush '#5A6070'; $e.Margin = '4,2,0,8'; $e.TextWrapping = 'Wrap'
        [void]$panel.Children.Add($e)
    }
    $idx = 0
    foreach ($k in $ug.Keys) { [void]$panel.Children.Add((New-GroupRow "u:$k" $k $ug[$k] $true $idx)); $idx++ }
    $bg = Get-BuiltinGroups
    if ($bg.Count -gt 0) {
        [void]$panel.Children.Add((New-SectionLabel 'ВСТРОЕННЫЕ В ZAPRET'))
        foreach ($k in $bg.Keys) { [void]$panel.Children.Add((New-GroupRow "b:$k" $k $bg[$k] $false $idx)); $idx++ }
    }
}

function Add-Site([string]$raw, [string]$group = $null) {
    $raw = "$raw".Trim().ToLower()
    if (-not $raw) { return }
    $dom = $raw -replace '^[a-z]+://', '' -replace '[/?#].*$', '' -replace ':\d+$', '' -replace '^www\.', ''
    if ($dom -notmatch '^([a-z0-9а-яё-]+\.)+[a-zа-яё0-9-]{2,}$') { Show-Toast "«$raw» не похоже на адрес сайта" -Err; return }
    $g = Read-UserGroups
    foreach ($k in $g.Keys) { if ($g[$k].Contains($dom)) { Show-Toast "$dom уже есть в группе $k"; Clear-AddBoxes; return } }
    if (-not $group) { $group = Find-GroupFor $g $dom }
    if (-not $g.Contains($group)) { $g[$group] = New-Object System.Collections.Generic.List[string] }
    $g[$group].Add($dom)
    Write-UserGroups $g
    $script:expanded["u:$group"] = $true
    Clear-AddBoxes
    Refresh-Sites
    Show-Toast "Добавлен $dom → $group"
    Mark-Changed
}
function Clear-AddBoxes { $ui.DomainBox.Text = '' }

# ======================= Настройки =======================
function Read-Game {
    $mode = 'disabled'; $tcp = '1024-65535'; $udp = '1024-65535'
    if (Test-Path -LiteralPath $GameFile) {
        foreach ($l in (Get-Content -LiteralPath $GameFile)) {
            if ($l -match '^\s*(\w+)\s*=\s*(.*?)\s*$') {
                switch ($matches[1].ToLower()) { 'mode' { $mode = $matches[2] } 'tcp' { if ($matches[2]) { $tcp = $matches[2] } } 'udp' { if ($matches[2]) { $udp = $matches[2] } } }
            } elseif ($l.Trim() -match '^(all|tcp|udp)$') { $mode = $matches[1] }
        }
    }
    return @{ Mode = $mode.ToLower(); Tcp = $tcp; Udp = $udp }
}
function Save-Game($mode) {
    $g = Read-Game
    [IO.File]::WriteAllText($GameFile, "mode=$mode`r`ntcp=$($g.Tcp)`r`nudp=$($g.Udp)`r`n", [Text.Encoding]::ASCII)
}
function Get-Ipset {
    if (-not (Test-Path -LiteralPath $IpsetFile)) { return 'any' }
    $lines = @(Get-Content -LiteralPath $IpsetFile)
    if ($lines.Count -eq 0) { return 'any' }
    if ($lines -contains '203.0.113.113/32') { return 'none' }
    return 'loaded'
}
function Set-Ipset($target) {
    $cur = Get-Ipset
    if ($cur -eq $target) { return $true }
    if ($target -eq 'loaded') {
        if (-not (Test-Path -LiteralPath $IpsetBackup)) {
            Show-Toast 'Нет сохранённого списка IP. Обнови его: «Сервис и диагностика» → пункт 8.' -Err
            return $false
        }
        Move-Item -LiteralPath $IpsetBackup -Destination $IpsetFile -Force
        return $true
    }
    if ($cur -eq 'loaded') { Copy-Item -LiteralPath $IpsetFile -Destination $IpsetBackup -Force }
    if ($target -eq 'none') { [IO.File]::WriteAllText($IpsetFile, "203.0.113.113/32`r`n", [Text.Encoding]::ASCII) }
    else { [IO.File]::WriteAllText($IpsetFile, '', [Text.Encoding]::ASCII) }
    return $true
}
function Select-ByTag($combo, $tag) {
    foreach ($it in $combo.Items) { if ($it.Tag -eq $tag) { $combo.SelectedItem = $it; return } }
}

# ======================= Заполнение =======================
$script:loading = $true
$ui.TitleVer.Text = T $("v$LocalVersion")

# ---- значок окна и ярлыка ----
$IconFile = Join-Path $GuiDir 'zapret.ico'
if (Test-Path -LiteralPath $IconFile) {
    try {
        # читаем значок в память, чтобы файл не оставался занятым (иначе установщик не сможет его обновить)
        $icoStream = New-Object System.IO.MemoryStream (, [IO.File]::ReadAllBytes($IconFile))
        $icoDec = [System.Windows.Media.Imaging.BitmapDecoder]::Create($icoStream, 'None', 'OnLoad')
        $win.Icon = $icoDec.Frames | Sort-Object PixelWidth -Descending | Select-Object -First 1
    } catch { Write-Log ($_ | Out-String) }
    try {
        $lnkPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Zapret.lnk'
        if (Test-Path -LiteralPath $lnkPath) {
            $sh = New-Object -ComObject WScript.Shell
            $lnk = $sh.CreateShortcut($lnkPath)
            if ($lnk.IconLocation -notlike "$IconFile*") { $lnk.IconLocation = "$IconFile,0"; $lnk.Save() }
        }
    } catch { Write-Log ($_ | Out-String) }
}
$ui.InfoPath.Text = T $($Root)
$ui.InfoVer.Text = T $("zapret $LocalVersion")
$ui.InfoApp.Text = T $("Zapret GUI v$AppVersion · тестовая сборка")
if (-not $IsAdmin) { $ui.TitleVer.Text += '  ·  без прав администратора' }

$bats = Get-ChildItem -LiteralPath $Root -Filter '*.bat' | Where-Object { $_.Name -notlike 'service*' } |
    Sort-Object { [Regex]::Replace($_.Name, '(\d+)', { $args[0].Value.PadLeft(8, '0') }) }
foreach ($b in $bats) {
    $it = New-Object System.Windows.Controls.ComboBoxItem
    $it.Content = T $($b.BaseName)
    $it.Tag = $b.Name
    [void]$ui.StrategyBox.Items.Add($it)
}
Select-ByTag $ui.StrategyBox "$($Settings.strategy).bat"
if (-not $ui.StrategyBox.SelectedItem -and $ui.StrategyBox.Items.Count) { $ui.StrategyBox.SelectedIndex = 0 }

Select-ByTag $ui.GameBox (Read-Game).Mode
if (-not $ui.GameBox.SelectedItem) { $ui.GameBox.SelectedIndex = 0 }
Select-ByTag $ui.IpsetBox (Get-Ipset)
$ui.UpdToggle.IsChecked = (Test-Path -LiteralPath $UpdFile)
$ui.WinToggle.IsChecked = [bool]$Settings.showWindow
if ($IsAdmin -and -not $Settings.noPromptOff) { try { Enable-NoPrompt } catch { Write-Log ($_ | Out-String) } }
$ui.NoPromptToggle.IsChecked = [bool](Get-ElevTask)
Refresh-Sites
$script:loading = $false

# ======================= События =======================
$ui.TitleBar.Add_MouseLeftButtonDown({ try { $win.DragMove() } catch {} })
$ui.MinBtn.Add_Click({ Safe { Hide-ToTray } })
$ui.CloseBtn.Add_Click({
    Anim-Double $ui.Shell $OP 0 180
    $t = New-Object System.Windows.Threading.DispatcherTimer
    $t.Interval = [TimeSpan]::FromMilliseconds(190)
    $t.Add_Tick({ $win.Close() }); $t.Start()
})


# ---- Внешний вид: тема, акцент, язык (применяются перезапуском окна) ----
$AccentChoices = @('#3DDC97', '#5ED3F3', '#4DA3FF', '#B57BFF', '#FF7AB6', '#FF9A4D', '#FFD166')
function Restart-App {
    Save-Settings
    Show-Toast 'Перезапускаю приложение…'
    Release-Instance
    if ($HostReady -and (Test-Path -LiteralPath $HostExe)) { Start-Process -FilePath $HostExe }
    else { Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File `"$PSCommandPath`"" -WindowStyle Hidden }
    $script:rsT = New-Object System.Windows.Threading.DispatcherTimer
    $script:rsT.Interval = [TimeSpan]::FromMilliseconds(700)
    $script:rsT.Add_Tick({ $script:rsT.Stop(); $win.Close() })
    $script:rsT.Start()
}
function Fill-Appearance {
    $script:loading = $true
    Select-ByTag $ui.ThemeBox $script:Theme
    Select-ByTag $ui.LangBox $script:Lang
    $ui.AccentPanel.Children.Clear()
    foreach ($c in $AccentChoices) {
        $b = New-Object System.Windows.Controls.Button
        $b.Width = 28; $b.Height = 28; $b.Margin = '4,0,4,4'; $b.Cursor = 'Hand'; $b.Tag = $c; $b.ToolTip = $c
        $tpl = [Windows.Markup.XamlReader]::Parse('<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button"><Grid><Ellipse x:Name="Ring" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Stroke="{TemplateBinding BorderBrush}" StrokeThickness="2"/><Ellipse Margin="4" Fill="{TemplateBinding Background}"/></Grid></ControlTemplate>')
        $b.Template = $tpl
        $b.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($c))
        $b.BorderBrush = if ($c -eq $script:Accent) { Brush '#E6E8EE' } else { [System.Windows.Media.Brushes]::Transparent }
        $b.RenderTransformOrigin = '0.5,0.5'; $b.RenderTransform = New-Object System.Windows.Media.ScaleTransform
        $b.Add_MouseEnter({ Anim-Double $this.RenderTransform $SX 1.18 160; Anim-Double $this.RenderTransform $SY 1.18 160 })
        $b.Add_MouseLeave({ Anim-Double $this.RenderTransform $SX 1 160; Anim-Double $this.RenderTransform $SY 1 160 })
        $b.Add_Click({ Safe { if ($this.Tag -ne $script:Accent) { $Settings.accent = $this.Tag; Restart-App } } })
        [void]$ui.AccentPanel.Children.Add($b)
    }
    $script:loading = $false
}
$ui.ThemeBox.Add_SelectionChanged({ Safe { if ($script:loading) { return }; $v = $ui.ThemeBox.SelectedItem.Tag; if ($v -ne $script:Theme) { $Settings.theme = $v; Restart-App } } })
$ui.LangBox.Add_SelectionChanged({ Safe { if ($script:loading) { return }; $v = $ui.LangBox.SelectedItem.Tag; if ($v -ne $script:Lang) { $Settings.lang = $v; Restart-App } } })
Fill-Appearance

# ---- Панель настроек ----
$RotP = [System.Windows.Media.RotateTransform]::AngleProperty
$TX = [System.Windows.Media.TranslateTransform]::XProperty
function Show-SetPage([int]$i, [switch]$NoAnim) {
    for ($k = 0; $k -lt 5; $k++) { $ui["SetPage$k"].Visibility = 'Collapsed' }
    $pg = $ui["SetPage$i"]; $pg.Visibility = 'Visible'
    $script:setPage = $i
    if ($NoAnim) { return }
    $j = 0
    foreach ($ch in $pg.Children) {
        $ch.Opacity = 0
        $ch.RenderTransform = New-Object System.Windows.Media.TranslateTransform
        Anim-Double $ch $OP 1 260 0 -beginMs ($j * 45)
        Anim-Double $ch.RenderTransform $TX 0 320 18 -beginMs ($j * 45)
        $j++
    }
}
function Open-Settings {
    $ui.SettingsOverlay.Visibility = 'Visible'
    Anim-Double $ui.Dim $OP 1 260 0
    Anim-Double $ui.SettingsShift $TX 0 400 620
    Anim-Double $ui.GearRot $RotP 180 500
    $i = 0
    foreach ($nv in $ui.SetNav.Children) {
        $nv.Opacity = 0; $nv.RenderTransform = New-Object System.Windows.Media.TranslateTransform
        Anim-Double $nv $OP 1 260 0 -beginMs (120 + $i * 40)
        Anim-Double $nv.RenderTransform $TX 0 320 -14 -beginMs (120 + $i * 40)
        $i++
    }
    Show-SetPage $(if ($script:setPage) { $script:setPage } else { 0 })
}
function Close-Settings {
    Anim-Double $ui.Dim $OP 0 220
    Anim-Double $ui.SettingsShift $TX 620 300
    Anim-Double $ui.GearRot $RotP 0 450
    $script:closeT = New-Object System.Windows.Threading.DispatcherTimer
    $script:closeT.Interval = [TimeSpan]::FromMilliseconds(290)
    $script:closeT.Add_Tick({ $script:closeT.Stop(); $ui.SettingsOverlay.Visibility = 'Collapsed' })
    $script:closeT.Start()
}
$ui.GearBtn.Add_Click({ Safe { if ($ui.SettingsOverlay.Visibility -eq 'Visible') { Close-Settings } else { Open-Settings } } })
$ui.GearBtn.Add_MouseEnter({ if ($ui.SettingsOverlay.Visibility -ne 'Visible') { Anim-Double $ui.GearRot $RotP 60 400 } })
$ui.GearBtn.Add_MouseLeave({ if ($ui.SettingsOverlay.Visibility -ne 'Visible') { Anim-Double $ui.GearRot $RotP 0 400 } })
$ui.SettingsClose.Add_Click({ Close-Settings })
foreach ($k in 0..4) { $ui["Nav$k"].Add_Checked({ Safe { if ($ui.SettingsOverlay.Visibility -eq 'Visible') { Show-SetPage ([int]$this.Tag) } } }) }
$ui.RepoBtn.Add_Click({ Safe { Start-Process "https://github.com/$UpdateRepo" } })
$ui.Dim.Add_MouseLeftButtonUp({ Close-Settings })
$win.Add_KeyDown({ param($s, $e) if ($e.Key -eq 'Escape' -and $ui.SettingsOverlay.Visibility -eq 'Visible') { Close-Settings } })

$ui.PowerBtn.Add_MouseEnter({ Anim-Double $ui.PowerScale $SX 1.04 200; Anim-Double $ui.PowerScale $SY 1.04 200 })
$ui.PowerBtn.Add_MouseLeave({ Anim-Double $ui.PowerScale $SX 1 200; Anim-Double $ui.PowerScale $SY 1 200 })
$ui.PowerBtn.Add_PreviewMouseLeftButtonDown({ Anim-Double $ui.PowerScale $SX 0.95 90; Anim-Double $ui.PowerScale $SY 0.95 90 })
$ui.PowerBtn.Add_PreviewMouseLeftButtonUp({ Anim-Double $ui.PowerScale $SX 1.04 160; Anim-Double $ui.PowerScale $SY 1.04 160 })
$ui.PowerBtn.Add_Click({ Safe {
    switch ($script:state) {
        'on' { Stop-Zapret; Hide-Apply }
        'off' { Start-Zapret }
    }
} })

$ui.StrategyBox.Add_SelectionChanged({ Safe {
    if ($script:loading) { return }
    $it = $ui.StrategyBox.SelectedItem
    if (-not $it) { return }
    $Settings.strategy = $it.Content; Save-Settings
    if ($script:state -eq 'on' -and (Get-ServiceState) -ne 'Running') { Hide-Apply; Restart-Zapret }
} })

$ui.DomainBox.Add_TextChanged({ $ui.DomainHint.Visibility = if ($ui.DomainBox.Text) { 'Collapsed' } else { 'Visible' } })
$ui.DomainBox.Add_KeyDown({ param($s, $e) if ($e.Key -eq 'Return') { Safe { Add-Site $ui.DomainBox.Text } } })
$ui.AddBtn.Add_Click({ Safe { Add-Site $ui.DomainBox.Text } })

$ui.GameBox.Add_SelectionChanged({ Safe {
    if ($script:loading) { return }
    Save-Game $ui.GameBox.SelectedItem.Tag
    Show-Toast "Игровой фильтр: $($ui.GameBox.SelectedItem.Content)"
    Mark-Changed
} })
$ui.IpsetBox.Add_SelectionChanged({ Safe {
    if ($script:loading) { return }
    if (Set-Ipset $ui.IpsetBox.SelectedItem.Tag) {
        Show-Toast "IPSet: $($ui.IpsetBox.SelectedItem.Content)"
        Mark-Changed
    } else {
        $script:loading = $true; Select-ByTag $ui.IpsetBox (Get-Ipset); $script:loading = $false
    }
} })
$ui.UpdToggle.Add_Click({ Safe {
    if ($ui.UpdToggle.IsChecked) { [IO.File]::WriteAllText($UpdFile, "ENABLED `r`n", [Text.Encoding]::ASCII) }
    elseif (Test-Path -LiteralPath $UpdFile) { Remove-Item -LiteralPath $UpdFile -Force }
} })
$ui.NoPromptToggle.Add_Click({ Safe {
    if ($ui.NoPromptToggle.IsChecked) { $Settings.noPromptOff = $false; Enable-NoPrompt; Show-Toast 'Готово: Zapret будет открываться без запроса прав' }
    else { $Settings.noPromptOff = $true; Disable-NoPrompt; Show-Toast 'Отключено: Windows снова будет спрашивать разрешение' }
    Save-Settings
} })
$ui.WinToggle.Add_Click({ Safe {
    $Settings.showWindow = [bool]$ui.WinToggle.IsChecked; Save-Settings
    Mark-Changed
} })
$ui.ApplyBtn.Add_Click({ Safe { Hide-Apply; Restart-Zapret } })

$ui.ServiceBtn.Add_Click({ Safe { Start-Process -FilePath (Join-Path $Root 'service.bat') -WorkingDirectory $Root } })
$ui.FolderBtn.Add_Click({ Safe { Start-Process explorer.exe -ArgumentList "`"$(if ($InHome) { $HomeDir } else { $Root })`"" } })
$ui.MigrateBtn.Add_Click({ Safe { Close-Settings; Open-Migrate } })

# ======================= Тест стратегий (внутри окна) =======================
$script:testing = $false
$script:testSync = $null

# Фоновая проверка: запускается в отдельном runspace, чтобы окно не зависало
$TestWorker = {
    param($sync, $strategies, $targets, $bin, $timeout)
    function Kill-Winws {
        Get-Process -Name winws -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 20; $i++) { if (-not (Get-Process -Name winws -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 100 }
        Start-Sleep -Milliseconds 200
    }
    # одна проверка = один запуск curl (HTTP, TLS1.2 или TLS1.3); все проверки идут параллельно
    $curlCheck = {
        param($url, $label, $extra, $to)
        $curlExe = Join-Path $env:SystemRoot 'System32\curl.exe'
        if (-not (Test-Path $curlExe)) { $curlExe = 'curl.exe' }
        $curlArgs = @("-I", "-s", "-m", "$to", "--connect-timeout", "$([Math]::Min(2, $to))", "-o", "NUL", "-w", "%{http_code}", "--show-error") + $extra + @($url)
        try {
            $err = ''
            $null = & $curlExe @curlArgs 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $err += $_.Exception.Message + ' ' } }
            $code = $LASTEXITCODE
            if ($err -match 'Could not resolve host|certificate|self[- ]?signed') { return @{ Tok = "${label}:SSL"; Diag = "$label exit=$code $err" } }
            if (($code -eq 35) -or ($err -match 'not supported|unsupported|Unknown option|schannel')) { return @{ Tok = "${label}:UNSUP"; Diag = '' } }
            if ($code -eq 0) { return @{ Tok = "${label}:OK"; Diag = '' } }
            return @{ Tok = "${label}:ERR"; Diag = "$label exit=$code $err" }
        } catch { return @{ Tok = "${label}:ERR"; Diag = "$label exception: $($_.Exception.Message)" } }
    }
    $pingCheck = {
        param($host_)
        try { $pg = New-Object System.Net.NetworkInformation.Ping; $r = $pg.Send($host_, 1000); $pg.Dispose(); if ($r.Status -eq 'Success') { "$($r.RoundtripTime) ms" } else { 'Timeout' } } catch { 'Timeout' }
    }
    $kinds = @(
        @{ Label = 'HTTP';   Args = @('--http1.1') },
        @{ Label = 'TLS1.2'; Args = @('--tlsv1.2', '--tls-max', '1.2') },
        @{ Label = 'TLS1.3'; Args = @('--tlsv1.3', '--tls-max', '1.3') }
    )
    $pool = [runspacefactory]::CreateRunspacePool(1, 32); $pool.Open()
    function Run-Checks {
        $jobs = @()
        foreach ($t in $targets) {
            if ($t.Url) {
                foreach ($k in $kinds) {
                    $p = [powershell]::Create().AddScript($curlCheck).AddArgument($t.Url).AddArgument($k.Label).AddArgument($k.Args).AddArgument($timeout)
                    $p.RunspacePool = $pool; $jobs += @{ P = $p; H = $p.BeginInvoke(); T = $t; Kind = 'curl'; Order = [array]::IndexOf(@('HTTP', 'TLS1.2', 'TLS1.3'), $k.Label) }
                }
            }
            if ($t.Ping) {
                $p = [powershell]::Create().AddScript($pingCheck).AddArgument($t.Ping)
                $p.RunspacePool = $pool; $jobs += @{ P = $p; H = $p.BeginInvoke(); T = $t; Kind = 'ping' }
            }
        }
        $byT = @{}
        foreach ($t in $targets) { $byT[$t.Name] = @{ Name = $t.Name; Group = $t.Group; Tokens = @('', '', ''); Ping = 'n/a'; IsUrl = [bool]$t.Url; Diag = @() } }
        $deadline = (Get-Date).AddSeconds($timeout + 6)
        foreach ($j in $jobs) {
            $r = $byT[$j.T.Name]
            try {
                $left = [int][Math]::Max(100, ($deadline - (Get-Date)).TotalMilliseconds)
                if (-not $j.H.AsyncWaitHandle.WaitOne($left)) { $j.P.Stop(); if ($j.Kind -eq 'curl') { $r.Tokens[$j.Order] = "$(@('HTTP','TLS1.2','TLS1.3')[$j.Order]):ERR" } else { $r.Ping = 'Timeout' } }
                else {
                    $out = $j.P.EndInvoke($j.H)
                    if ($j.Kind -eq 'curl') { $o = $out | Select-Object -Last 1; $r.Tokens[$j.Order] = $o.Tok; if ($o.Diag) { $r.Diag += $o.Diag } }
                    else { $r.Ping = "$($out | Select-Object -Last 1)" }
                }
            } catch {}
            $j.P.Dispose()
        }
        $res = @()
        foreach ($t in $targets) { $r = $byT[$t.Name]; $r.Tokens = @($r.Tokens | Where-Object { $_ }); $r.Diag = ($r.Diag -join ' | '); $res += $r }
        return $res
    }
    $first = $true
    try {
        foreach ($s in $strategies) {
            if ($sync.Cancel) { break }
            $sync.Current = $s.Name; $sync.Phase = 'start'; $sync.StartedAt = Get-Date
            Kill-Winws
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = "$bin\winws.exe"; $psi.Arguments = $s.Args; $psi.WorkingDirectory = $bin
            $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
            [void][System.Diagnostics.Process]::Start($psi)
            $up = $false
            for ($i = 0; $i -lt 50; $i++) { if (Get-Process -Name winws -ErrorAction SilentlyContinue) { $up = $true; break }; Start-Sleep -Milliseconds 100 }
            if (-not $up) {
                [void]$sync.Results.Add(@{ Name = $s.Name; File = $s.File; Failed = $true; OK = 0; Total = 0; PingOK = 0; PingTotal = 0; Details = @() })
                $sync.Done++; continue
            }
            # первой стратегии нужно больше времени: драйвер WinDivert загружается
            if ($first) { Start-Sleep -Milliseconds 2500 } else { Start-Sleep -Milliseconds 700 }
            if (-not (Get-Process -Name winws -ErrorAction SilentlyContinue)) {
                [void]$sync.Results.Add(@{ Name = $s.Name; File = $s.File; Failed = $true; OK = 0; Total = 0; PingOK = 0; PingTotal = 0; Details = @() })
                $sync.Done++; $first = $false; continue
            }
            $sync.Phase = 'test'
            for ($attempt = 1; $attempt -le 2; $attempt++) {
                $res = Run-Checks
                $ok = 0; $total = 0; $pok = 0; $ptot = 0
                foreach ($r in $res) {
                    foreach ($tk in $r.Tokens) { if ($tk -notmatch 'UNSUP') { $total++ }; if ($tk -match ':OK$') { $ok++ } }
                    if ($r.Ping -ne 'n/a') { $ptot++; if ($r.Ping -ne 'Timeout') { $pok++ } }
                }
                # перепроверка только у первой стратегии (там 0 бывает из-за загрузки драйвера)
                if ($ok -gt 0 -or $total -eq 0 -or -not $first -or $sync.Cancel) { break }
                $sync.Phase = 'retry'
                Start-Sleep -Milliseconds 2000
            }
            $first = $false
            [void]$sync.Results.Add(@{ Name = $s.Name; File = $s.File; Failed = $false; OK = $ok; Total = $total; PingOK = $pok; PingTotal = $ptot; Details = $res })
            $sync.Done++
        }
    } catch { $sync.Error = "$_" }
    finally { try { $pool.Close(); $pool.Dispose() } catch {}; Kill-Winws; $sync.Finished = $true }
}

function Get-TestTargets {
    $file = Join-Path $Utils 'targets.txt'
    $list = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $file)) { return $list }
    $grp = 'Другое'
    foreach ($l in (Get-Content -LiteralPath $file -Encoding UTF8)) {
        if ($l -match '^\s*###\s*(.+?)\s*$') { $grp = $matches[1]; continue }
        if ($l -match '^\s*(\w+)\s*=\s*"(.+)"\s*$') {
            $v = $matches[2]
            if ($v -like 'PING:*') { $list.Add(@{ Name = $matches[1]; Url = $null; Ping = ($v -replace '^PING:\s*', ''); Group = $grp }) }
            else { $list.Add(@{ Name = $matches[1]; Url = $v; Ping = ($v -replace '^https?://', '' -replace '/.*$', ''); Group = $grp }) }
        }
    }
    return $list
}

function New-Chip([string]$text, $tag, [bool]$checked, $color = $null) {
    $c = New-Object System.Windows.Controls.Primitives.ToggleButton
    $c.Style = $win.FindResource('Chip')
    $c.Tag = $tag
    $c.IsChecked = $checked
    if ($color) {
        $sp = New-Object System.Windows.Controls.StackPanel; $sp.Orientation = 'Horizontal'
        $d = New-Object System.Windows.Shapes.Ellipse; $d.Width = 7; $d.Height = 7; $d.Fill = Brush $color; $d.Margin = '0,0,7,0'; $d.VerticalAlignment = 'Center'
        $tb = New-Object System.Windows.Controls.TextBlock; $tb.Text = T $($text); $tb.VerticalAlignment = 'Center'
        [void]$sp.Children.Add($d); [void]$sp.Children.Add($tb)
        $c.Content = $sp
    } else { $c.Content = T $($text) }
    return $c
}
function Short-Name([string]$n) { if ($n -match '^general\s*\((.+)\)$') { return $matches[1] } return $n }

function Fill-TestSetup {
    $ui.GroupChips.Children.Clear(); $ui.StratChips.Children.Clear()
    $tg = Get-TestTargets
    $groups = @(); foreach ($t in $tg) { if ($groups -notcontains $t.Group) { $groups += $t.Group } }
    $all = New-Chip 'Все' '*' $true
    $all.Add_Click({ $v = $this.IsChecked; foreach ($ch in $ui.GroupChips.Children) { $ch.IsChecked = $v } ; Update-TestHint })
    [void]$ui.GroupChips.Children.Add($all)
    foreach ($g in $groups) {
        $cnt = @($tg | Where-Object { $_.Group -eq $g }).Count
        $c = New-Chip "$g · $cnt" $g $true (Get-GroupColor $g $false)
        $c.Add_Click({ Sync-AllChip $ui.GroupChips; Update-TestHint })
        [void]$ui.GroupChips.Children.Add($c)
    }
    $all2 = New-Chip 'Все' '*' $true
    $all2.Add_Click({ $v = $this.IsChecked; foreach ($ch in $ui.StratChips.Children) { $ch.IsChecked = $v }; Update-TestHint })
    [void]$ui.StratChips.Children.Add($all2)
    foreach ($it in $ui.StrategyBox.Items) {
        $c = New-Chip (Short-Name $it.Content) $it.Tag $true
        $c.Add_Click({ Sync-AllChip $ui.StratChips; Update-TestHint })
        [void]$ui.StratChips.Children.Add($c)
    }
    Update-TestHint
}
function Sync-AllChip($panel) {
    $rest = @($panel.Children | Select-Object -Skip 1)
    $panel.Children[0].IsChecked = (@($rest | Where-Object { -not $_.IsChecked }).Count -eq 0)
}
function Get-Picked($panel) { @($panel.Children | Select-Object -Skip 1 | Where-Object { $_.IsChecked } | ForEach-Object { $_.Tag }) }
function Update-TestHint {
    $g = Get-Picked $ui.GroupChips; $s = Get-Picked $ui.StratChips
    $tg = @(Get-TestTargets | Where-Object { $g -contains $_.Group })
    $sec = [int]($s.Count * (5 + [Math]::Ceiling($tg.Count / 30.0)) + 3)
    $ui.TestStatus.Text = T $(if ($s.Count -eq 0 -or $tg.Count -eq 0) { 'Выбери хотя бы одну группу и одну стратегию' } else { "$($s.Count) стратегий × $($tg.Count) сайтов · примерно $([Math]::Max(1, [Math]::Round($sec / 60))) мин" })
    $ui.RunBtn.IsEnabled = ($s.Count -gt 0 -and $tg.Count -gt 0)
}

function Open-Test {
    if ($script:testing) { } else { Fill-TestSetup }
    $ui.TestOverlay.Visibility = 'Visible'
    Anim-Double $ui.TestDim $OP 1 250 0
    Anim-Double $ui.TestPanel $OP 1 280 0
    Anim-Double $ui.TestScale $SX 1 420 0.94 -Easing (Back-Ease)
    Anim-Double $ui.TestScale $SY 1 420 0.94 -Easing (Back-Ease)
}
function Close-Test {
    Anim-Double $ui.TestDim $OP 0 200
    Anim-Double $ui.TestPanel $OP 0 200
    Anim-Double $ui.TestScale $SX 0.96 220; Anim-Double $ui.TestScale $SY 0.96 220
    $script:testCloseT = New-Object System.Windows.Threading.DispatcherTimer
    $script:testCloseT.Interval = [TimeSpan]::FromMilliseconds(220)
    $script:testCloseT.Add_Tick({ $script:testCloseT.Stop(); $ui.TestOverlay.Visibility = 'Collapsed' })
    $script:testCloseT.Start()
}

function Start-Test([array]$TargetsOverride, [switch]$AutoPick) {
    if (-not $IsAdmin) { Show-Toast 'Нужны права администратора — запусти через Zapret.bat' -Err; return }
    if ((Get-ServiceState) -eq 'Running') { Show-Toast 'zapret работает как служба. Удали службу в «Сервис и диагностика», чтобы тестировать.' -Err; return }
    $script:autoPick = [bool]$AutoPick
    if ($TargetsOverride) {
        $targets = @($TargetsOverride)
        $stratFiles = @($ui.StrategyBox.Items | ForEach-Object { $_.Tag })
    } else {
        $groups = Get-Picked $ui.GroupChips
        $targets = @(Get-TestTargets | Where-Object { $groups -contains $_.Group })
        $stratFiles = Get-Picked $ui.StratChips
    }
    $strategies = @()
    foreach ($f in $stratFiles) {
        $a = Get-WinwsArgs (Join-Path $Root $f)
        if ($a) { $strategies += @{ Name = [IO.Path]::GetFileNameWithoutExtension($f); File = $f; Args = $a } }
    }
    if ($strategies.Count -eq 0 -or $targets.Count -eq 0) { return }

    # запомнить, что было до теста
    $script:testWasOn = ($script:state -eq 'on')
    $script:testing = $true
    $script:testShown = 0
    $script:testTotal = $strategies.Count
    $script:testBegan = Get-Date
    $ui.PowerBtn.IsEnabled = $false; $ui.StrategyBox.IsEnabled = $false
    Set-Visual 'off' 'Идёт тест стратегий…'

    $sync = [hashtable]::Synchronized(@{ Done = 0; Current = ''; Phase = ''; Finished = $false; Cancel = $false; Error = $null; Results = [System.Collections.ArrayList]::Synchronized((New-Object System.Collections.ArrayList)) })
    $script:testSync = $sync
    $rs = [runspacefactory]::CreateRunspace(); $rs.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    [void]$ps.AddScript($TestWorker.ToString()).AddArgument($sync).AddArgument($strategies).AddArgument($targets).AddArgument("$Root\bin").AddArgument(3)
    $script:testPs = $ps
    $script:testHandle = $ps.BeginInvoke()

    $ui.TestResults.Children.Clear()
    Anim-Double $ui.TestSetup $OP 0 200
    $ui.TestSetup.Visibility = 'Collapsed'
    $ui.ResultsHead.Visibility = 'Visible'
    $ui.RunBtn.Content = T $('Остановить')
    $ui.TestBarScale.ScaleX = 0
    Anim-Double $ui.TestShimmer $OP 1 300
    Anim-Double $ui.TestShimmerT ([System.Windows.Media.TranslateTransform]::XProperty) 700 1400 -120 -Forever -NoEase
    $script:testTimer.Start()
}

function New-ResultRow($r, [bool]$best, [int]$rank) {
    $wrap = New-Object System.Windows.Controls.StackPanel; $wrap.Margin = '0,0,0,6'
    $b = New-Object System.Windows.Controls.Border
    $b.CornerRadius = 12; $b.Padding = '14,10'; $b.Cursor = 'Hand'
    $b.Background = Brush $(if ($best) { '#18302A' } else { '#1E2129' })
    $b.BorderThickness = 1; $b.BorderBrush = Brush $(if ($best) { '#3DDC97' } else { '#262A33' })
    $g = New-Object System.Windows.Controls.Grid
    foreach ($w in @('*', '150', 'Auto', 'Auto')) {
        $c = New-Object System.Windows.Controls.ColumnDefinition
        $c.Width = if ($w -eq '*') { New-Object System.Windows.GridLength(1, 'Star') } elseif ($w -eq 'Auto') { [System.Windows.GridLength]::Auto } else { New-Object System.Windows.GridLength([double]$w) }
        $g.ColumnDefinitions.Add($c)
    }
    $nameSp = New-Object System.Windows.Controls.StackPanel; $nameSp.Orientation = 'Horizontal'; $nameSp.VerticalAlignment = 'Center'
    if ($rank -gt 0) {
        $rk = New-Object System.Windows.Controls.TextBlock; $rk.Text = T $("$rank"); $rk.Width = 22; $rk.FontSize = 12; $rk.Foreground = Brush '#5A6070'; $rk.VerticalAlignment = 'Center'
        [void]$nameSp.Children.Add($rk)
    }
    $nm = New-Object System.Windows.Controls.TextBlock; $nm.Text = T $($r.Name); $nm.FontSize = 13.5; $nm.FontWeight = 'SemiBold'; $nm.Foreground = Brush '#E6E8EE'; $nm.VerticalAlignment = 'Center'
    [void]$nameSp.Children.Add($nm)
    if ($best) {
        $bd = New-Object System.Windows.Controls.Border; $bd.Background = Brush '#3DDC97'; $bd.CornerRadius = 8; $bd.Padding = '8,1'; $bd.Margin = '10,0,0,0'
        $bt = New-Object System.Windows.Controls.TextBlock; $bt.Text = T $('★ лучшая'); $bt.FontSize = 11; $bt.FontWeight = 'SemiBold'; $bt.Foreground = Brush '#0F1115'
        $bd.Child = $bt; [void]$nameSp.Children.Add($bd)
    }
    [void]$g.Children.Add($nameSp)

    $ratio = if ($r.Total -gt 0) { $r.OK / $r.Total } else { 0 }
    $col = if ($r.Failed) { '#FF5E62' } elseif ($ratio -ge 0.85) { '#3DDC97' } elseif ($ratio -ge 0.5) { '#FFB86B' } else { '#FF5E62' }
    $track = New-Object System.Windows.Controls.Border; $track.Height = 6; $track.CornerRadius = 3; $track.Background = Brush '#2A2E38'; $track.VerticalAlignment = 'Center'; $track.Margin = '10,0'
    $fill = New-Object System.Windows.Controls.Border; $fill.CornerRadius = 3; $fill.Background = Brush $col; $fill.HorizontalAlignment = 'Stretch'
    $fill.RenderTransformOrigin = '0,0.5'; $sc = New-Object System.Windows.Media.ScaleTransform; $sc.ScaleX = 0; $fill.RenderTransform = $sc
    $track.Child = $fill
    [System.Windows.Controls.Grid]::SetColumn($track, 1); [void]$g.Children.Add($track)
    Anim-Double $sc $SX $ratio 700 0 -beginMs 150

    $txt = New-Object System.Windows.Controls.TextBlock; $txt.FontSize = 12; $txt.VerticalAlignment = 'Center'; $txt.Width = 175; $txt.TextAlignment = 'Right'
    $txt.Text = T $(if ($r.Failed) { 'не запустилась' } else { "$($r.OK)/$($r.Total) · $([int]($ratio * 100))%" + $(if ($r.PingTotal) { " · ping $($r.PingOK)/$($r.PingTotal)" } else { '' }) })
    $txt.Foreground = Brush $col
    [System.Windows.Controls.Grid]::SetColumn($txt, 2); [void]$g.Children.Add($txt)

    if ($best) {
        $ap = New-Object System.Windows.Controls.Button; $ap.Style = $win.FindResource('AccentBtn'); $ap.Content = T $('Включить'); $ap.Padding = '12,5'; $ap.Margin = '12,0,0,0'
        $ap.Tag = $r.File
        $ap.Add_Click({ Safe { Apply-Strategy $this.Tag } })
        [System.Windows.Controls.Grid]::SetColumn($ap, 3); [void]$g.Children.Add($ap)
    }
    $b.Child = $g

    # подробности по сайтам
    $det = New-Object System.Windows.Controls.WrapPanel; $det.Margin = '14,6,0,4'; $det.Visibility = 'Collapsed'
    foreach ($d in $r.Details) {
        $okAll = $true; $any = $false
        foreach ($tk in $d.Tokens) { if ($tk -notmatch 'UNSUP') { $any = $true; if ($tk -notmatch ':OK$') { $okAll = $false } } }
        if (-not $d.IsUrl) { $any = $true; $okAll = ($d.Ping -ne 'Timeout') }
        $pill = New-Object System.Windows.Controls.Border; $pill.CornerRadius = 8; $pill.Padding = '8,3'; $pill.Margin = '0,0,6,6'
        $pill.Background = Brush $(if ($okAll) { '#18302A' } else { '#3A2328' })
        $pt = New-Object System.Windows.Controls.TextBlock; $pt.FontSize = 11.5
        $pt.Foreground = Brush $(if ($okAll) { '#7FE8B8' } else { '#FF8C99' })
        $pt.Text = T $("$($d.Name)  " + (($d.Tokens | ForEach-Object { $_ -replace ':OK$', ' ✓' -replace ':(ERR|SSL)$', ' ✕' -replace ':UNSUP$', ' –' }) -join '  ') + $(if ($d.Ping -and $d.Ping -ne 'n/a') { "  · $($d.Ping)" } else { '' }))
        $pill.Child = $pt
        [void]$det.Children.Add($pill)
    }
    $b.Tag = $det
    $b.Add_MouseLeftButtonUp({ $d = $this.Tag; if ($d.Visibility -eq 'Visible') { $d.Visibility = 'Collapsed' } else { $d.Visibility = 'Visible'; Anim-Double $d $OP 1 250 0 } })
    [void]$wrap.Children.Add($b); [void]$wrap.Children.Add($det)

    $wrap.Opacity = 0; $wrap.RenderTransform = New-Object System.Windows.Media.TranslateTransform
    Anim-Double $wrap $OP 1 300 0 -beginMs ([Math]::Min($rank, 12) * 35)
    Anim-Double $wrap.RenderTransform ([System.Windows.Media.TranslateTransform]::YProperty) 0 360 14 -beginMs ([Math]::Min($rank, 12) * 35)
    return $wrap
}

function Apply-Strategy([string]$file) {
    Close-Test
    Select-ByTag $ui.StrategyBox $file
    if ($script:state -ne 'on') { Start-Zapret }
}

function Finish-Test([bool]$cancelled) {
    $script:testTimer.Stop()
    $script:testing = $false
    $sync = $script:testSync
    try { $script:testPs.EndInvoke($script:testHandle) } catch {}
    try { $script:testPs.Runspace.Close(); $script:testPs.Dispose() } catch {}
    Anim-Double $ui.TestShimmer $OP 0 300
    $ui.PowerBtn.IsEnabled = $true; $ui.StrategyBox.IsEnabled = $true
    $ui.RunBtn.Content = T $('Новый тест')
    $ui.RunBtn.IsEnabled = $true

    $res = @($sync.Results | Sort-Object @{ Expression = { if ($_.Failed) { -1 } else { $_.OK } }; Descending = $true }, @{ Expression = { $_.PingOK }; Descending = $true })
    $ui.TestResults.Children.Clear()
    $rank = 1
    foreach ($r in $res) { [void]$ui.TestResults.Children.Add((New-ResultRow $r ($rank -eq 1 -and -not $r.Failed -and $r.OK -gt 0) $rank)); $rank++ }
    Anim-Double $ui.TestBarScale $SX 1 400
    $el = (Get-Date) - $script:testBegan
    $ui.TestStatus.Text = T $(if ($cancelled) { "Тест остановлен · проверено $($res.Count) из $script:testTotal" } else { "Готово за $([int]$el.TotalMinutes) мин $($el.Seconds) с · лучшая: $(if ($res.Count) { $res[0].Name } else { '—' })" })
    if ($sync.Error) { Show-Toast "Ошибка теста: $($sync.Error)" -Err }

    # диагностика ошибок curl в лог (первые 6)
    try {
        $dl = @(); foreach ($r in $res) { foreach ($d in $r.Details) { if ($d.Diag) { $dl += "$($r.Name) / $($d.Name): $($d.Diag)" } } }
        if ($dl.Count) { Write-Log ("TEST DIAG:`r`n  " + (($dl | Select-Object -First 6) -join "`r`n  ")) }
    } catch {}

    # сохранить результаты как обычный тест
    try {
        $dir = Join-Path $Utils 'test results'
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        $lines = New-Object System.Collections.Generic.List[string]
        foreach ($r in $res) {
            $lines.Add("Config: $($r.File) (Type: standard)  OK $($r.OK)/$($r.Total)")
            foreach ($d in $r.Details) { $lines.Add("  $($d.Name) : $($d.Tokens -join ' ') | Ping: $($d.Ping)") }
        }
        if ($res.Count) { $lines.Add(''); $lines.Add("Best config: $($res[0].File)") }
        [IO.File]::WriteAllLines((Join-Path $dir ("test_results_{0}.txt" -f (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'))), $lines, $utf8)
    } catch {}

    # вернуть zapret как был до теста
    $script:state = 'x'
    if ($script:testWasOn) { $script:state = 'off'; Start-Zapret } else { Refresh-State }
    if (-not $cancelled) { Show-Toast "Тест завершён. Лучшая стратегия: $(if ($res.Count) { $res[0].Name } else { '—' })" }
}

$script:testTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:testTimer.Interval = [TimeSpan]::FromMilliseconds(300)
$script:testTimer.Add_Tick({
    try {
        $sync = $script:testSync
        if (-not $sync) { return }
        $done = [int]$sync.Done
        $frac = if ($script:testTotal) { ($done + $(if ($sync.Phase -eq 'test') { 0.5 } else { 0.1 })) / $script:testTotal } else { 0 }
        if ($frac -gt 1) { $frac = 1 }
        Anim-Double $ui.TestBarScale $SX $frac 450
        $eta = ''
        if ($done -gt 0) {
            $per = ((Get-Date) - $script:testBegan).TotalSeconds / $done
            $left = [int]($per * ($script:testTotal - $done))
            $eta = if ($left -ge 60) { " · осталось ~$([Math]::Round($left / 60)) мин" } else { " · осталось ~$left с" }
        }
        $phase = switch ($sync.Phase) { 'test' { 'проверяю сайты' } 'retry' { 'перепроверяю' } default { 'запускаю' } }
        $ui.TestStatus.Text = T $("Стратегия $([Math]::Min($done + 1, $script:testTotal)) из $($script:testTotal) · $($sync.Current) · $phase$eta")
        while ($script:testShown -lt $sync.Results.Count) {
            $r = $sync.Results[$script:testShown]
            $script:testShown++
            $ui.TestResults.Children.Insert(0, (New-ResultRow $r $false 0))
        }
        if ($sync.Finished) { Finish-Test ([bool]$sync.Cancel) }
    } catch { Write-Log ($_ | Out-String) }
})

$ui.TestBtn.Add_Click({ Safe { Open-Test } })
$ui.TestClose.Add_Click({ Safe { Close-Test } })
$ui.TestDim.Add_MouseLeftButtonUp({ if (-not $script:testing) { Close-Test } })
$ui.RunBtn.Add_Click({ Safe {
    if ($script:testing) {
        $script:testSync.Cancel = $true
        $ui.RunBtn.IsEnabled = $false
        $ui.RunBtn.Content = T $('Останавливаю…')
    } elseif ($ui.RunBtn.Content -eq (T 'Новый тест')) {
        $ui.RunBtn.Content = T $('Запустить тест')
        $ui.ResultsHead.Visibility = 'Collapsed'
        $ui.TestResults.Children.Clear()
        $ui.TestSetup.Visibility = 'Visible'; Anim-Double $ui.TestSetup $OP 1 250 0
        $ui.TestBarScale.ScaleX = 0
        Fill-TestSetup
    } else { Start-Test }
} })


# ======================= Перенос настроек (внутри окна) =======================
$oldParse = @'
    $rawTargets = New-OrderedDict
    if (Test-Path $targetsFile) {
        Get-Content $targetsFile | ForEach-Object {
            if ($_ -match '^\s*(\w+)\s*=\s*"(.+)"\s*$') {
                Add-OrSet -dict $rawTargets -key $matches[1] -val $matches[2]
            }
        }
    }

'@

$newParse = @'
    $rawTargets = New-OrderedDict
    $targetGroupOf = @{}
    $groupNames = New-Object System.Collections.Generic.List[string]
    $currentGroup = "Other"
    if (Test-Path $targetsFile) {
        Get-Content $targetsFile -Encoding UTF8 | ForEach-Object {
            if ($_ -match '^\s*###\s*(.+?)\s*$') {
                $currentGroup = $matches[1]
            }
            elseif ($_ -match '^\s*(\w+)\s*=\s*"(.+)"\s*$') {
                Add-OrSet -dict $rawTargets -key $matches[1] -val $matches[2]
                $targetGroupOf[$matches[1]] = $currentGroup
                if (-not $groupNames.Contains($currentGroup)) { [void]$groupNames.Add($currentGroup) }
            }
        }
    }

    # Let the user choose which groups (### headers in targets.txt) to test
    if ($rawTargets.Count -gt 0 -and $groupNames.Count -gt 1) {
        $selectedGroups = Read-GroupSelection -groups $groupNames.ToArray() -groupOf $targetGroupOf
        if ($selectedGroups.Count -lt $groupNames.Count) {
            $filtered = New-OrderedDict
            foreach ($key in $rawTargets.Keys) {
                if ($selectedGroups -contains $targetGroupOf[$key]) { $filtered[$key] = $rawTargets[$key] }
            }
            $rawTargets = $filtered
        }
    }

'@

$groupFunc = @'
# Select which target groups to test (groups come from ### headers in targets.txt)
function Read-GroupSelection {
    param([array]$groups, [hashtable]$groupOf)

    $picked = New-Object 'System.Collections.Generic.HashSet[int]'
    while ($true) {
        Write-Host ""
        Write-Host "What to test:" -ForegroundColor Cyan
        Write-Host "  [0] All (start now)" -ForegroundColor Gray
        for ($i = 0; $i -lt $groups.Count; $i++) {
            $g = $groups[$i]
            $cnt = @($groupOf.Keys | Where-Object { $groupOf[$_] -eq $g }).Count
            if ($picked.Contains($i + 1)) {
                Write-Host "  [$($i + 1)] [+] $g ($cnt)" -ForegroundColor Green
            } else {
                Write-Host "  [$($i + 1)] [ ] $g ($cnt)" -ForegroundColor Gray
            }
        }
        $selectionInput = Read-Host "Number to add/remove (e.g. 1 or 1,3). '0' = all. Enter = start"
        $trimmed = $selectionInput.Trim()
        if ($trimmed -eq '0') { return $groups }

        if ($trimmed -eq '') {
            if ($picked.Count -eq 0) {
                Write-Host "Nothing selected yet. Pick groups by number, or '0' for all." -ForegroundColor Yellow
                continue
            }
            $result = @($picked | Sort-Object | ForEach-Object { $groups[$_ - 1] })
            Write-Host "Testing: $($result -join ', ')" -ForegroundColor Green
            return $result
        }

        foreach ($part in ($trimmed -split '[,\s]+')) {
            $nums = @()
            if ($part -match '^(\d+)-(\d+)$') {
                for ($n = [int]$matches[1]; $n -le [int]$matches[2]; $n++) { $nums += $n }
            } elseif ($part -match '^\d+$') {
                $nums += [int]$part
            } else {
                Write-Host "  [WARN] '$part' is not a number. Skipping." -ForegroundColor Yellow
                continue
            }
            foreach ($n in $nums) {
                if ($n -lt 1 -or $n -gt $groups.Count) {
                    Write-Host "  [WARN] No group number $n. Skipping." -ForegroundColor Yellow
                } elseif ($picked.Contains($n)) {
                    [void]$picked.Remove($n)
                } else {
                    [void]$picked.Add($n)
                }
            }
        }
    }
}


'@

$MigOldParse = $oldParse -replace "`r`n", "`n"; $MigNewParse = $newParse -replace "`r`n", "`n"; $MigGroupFunc = $groupFunc -replace "`r`n", "`n"

function Mig-Lines($path) { if (-not (Test-Path -LiteralPath $path)) { return @() }; @([IO.File]::ReadAllText($path, $utf8) -split "`r?`n") }
function Mig-Write($path, [string[]]$lines) { [IO.File]::WriteAllText($path, ($lines -join "`r`n"), $utf8) }
function Mig-Backup($path) { if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination "$path.bak" -Force } }
function Mig-Copy([string[]]$rels) {
    $n = 0
    foreach ($f in $rels) {
        $src = Join-Path $script:migFrom $f
        if (Test-Path -LiteralPath $src) {
            $dst = Join-Path $script:migTo $f
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
            Copy-Item -LiteralPath $src -Destination $dst -Force; $n++
        }
    }
    return $n
}
function Mig-MergeLists {
    $added = 0
    foreach ($f in @('lists\list-general.txt', 'lists\list-google.txt', 'lists\list-exclude.txt', 'lists\ipset-exclude.txt')) {
        $src = Join-Path $script:migFrom $f; $dst = Join-Path $script:migTo $f
        if (-not (Test-Path -LiteralPath $src) -or -not (Test-Path -LiteralPath $dst)) { continue }
        $newLines = Mig-Lines $dst
        $set = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        $newLines | ForEach-Object { [void]$set.Add($_.Trim()) }
        $missing = @(Mig-Lines $src | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') -and -not $set.Contains($_) } | Select-Object -Unique)
        if ($missing.Count) { Mig-Backup $dst; Mig-Write $dst (@($newLines | Where-Object { $_ -ne '' }) + $missing + @('')); $added += $missing.Count }
    }
    return $added
}
function Mig-Targets {
    $srcT = Join-Path $script:migFrom 'utils\targets.txt'; $dstT = Join-Path $script:migTo 'utils\targets.txt'
    if (-not (Test-Path -LiteralPath $srcT) -or -not (Test-Path -LiteralPath $dstT)) { return 0 }
    $newT = Mig-Lines $dstT
    $haveKeys = @{}; $haveUrls = @{}
    foreach ($l in $newT) { if ($l -match '^\s*(\w+)\s*=\s*"(.+)"\s*$') { $haveKeys[$matches[1]] = 1; $haveUrls[$matches[2].ToLower()] = 1 } }
    $group = $null; $lastGroup = $null; $add = New-Object System.Collections.Generic.List[string]; $count = 0
    foreach ($l in (Mig-Lines $srcT)) {
        if ($l -match '^\s*###\s*(.+?)\s*$') { $group = $matches[1]; continue }
        if ($l -match '^\s*(\w+)\s*=\s*"(.+)"\s*$') {
            if ($haveKeys.ContainsKey($matches[1]) -or $haveUrls.ContainsKey($matches[2].ToLower())) { continue }
            if ($group -and $group -ne $lastGroup) { $add.Add(''); $add.Add("### $group"); $lastGroup = $group }
            $add.Add($l.Trim()); $count++
        }
    }
    if ($count) {
        Mig-Backup $dstT
        $out = @($newT); while ($out.Count -gt 0 -and $out[-1] -eq '') { $out = $out[0..($out.Count - 2)] }
        Mig-Write $dstT ($out + $add + @(''))
    }
    return $count
}
function Mig-PatchTest {
    $ps1 = Join-Path $script:migTo 'utils\test zapret.ps1'
    if (-not (Test-Path -LiteralPath $ps1)) { return 'none' }
    $text = [IO.File]::ReadAllText($ps1, $utf8) -replace "`r`n", "`n"
    if ($text.Contains('function Read-GroupSelection')) { return 'already' }
    $lm = [regex]::Match($text, '(?m)^while \(\$true\) \{\n')
    if (-not ($text.Contains($MigOldParse) -and $lm.Success)) { return 'fail' }
    Mig-Backup $ps1
    $text = $text.Replace($MigOldParse, $MigNewParse)
    $lm = [regex]::Match($text, '(?m)^while \(\$true\) \{\n')
    $text = $text.Insert($lm.Index, $MigGroupFunc)
    [IO.File]::WriteAllText($ps1, ($text -replace "`n", "`r`n"), $utf8)
    return 'ok'
}

$MigSteps = @(
    @{ Title = 'Мои сайты и группы'; Sub = 'list-general-user.txt' },
    @{ Title = 'Исключения'; Sub = 'list-exclude-user, ipset-exclude-user' },
    @{ Title = 'Игровой фильтр и автообновление'; Sub = 'game_filter, check_updates' },
    @{ Title = 'Сайты, вписанные в списки zapret'; Sub = 'list-general, list-google, исключения' },
    @{ Title = 'Сайты для теста'; Sub = 'targets.txt' },
    @{ Title = 'Меню выбора групп в тесте'; Sub = 'test zapret.ps1' }
)
function Run-MigStep([int]$i) {
    if ($script:migDemo) {
        $u = (Read-UserList).Count
        return @(
            @{ S = 'ok'; T = "перенесено $(Plural $u)" },
            @{ S = 'ok'; T = 'файлов: 2' },
            @{ S = 'ok'; T = 'настройки перенесены' },
            @{ S = 'skip'; T = 'в новой версии уже всё есть' },
            @{ S = 'ok'; T = 'добавлено: 5 сайтов' },
            @{ S = 'ok'; T = 'меню вставлено' }
        )[$i]
    }
    switch ($i) {
        0 { $n = Mig-Copy @('lists\list-general-user.txt'); if ($n) { @{ S = 'ok'; T = "перенесено $(Plural (Read-UserListFrom (Join-Path $script:migTo 'lists\list-general-user.txt')))" } } else { @{ S = 'skip'; T = 'нечего переносить' } } }
        1 { $n = Mig-Copy @('lists\list-exclude-user.txt', 'lists\ipset-exclude-user.txt'); @{ S = $(if ($n) { 'ok' } else { 'skip' }); T = $(if ($n) { "файлов: $n" } else { 'нечего переносить' }) } }
        2 { $n = Mig-Copy @('utils\game_filter.enabled', 'utils\check_updates.enabled'); @{ S = $(if ($n) { 'ok' } else { 'skip' }); T = $(if ($n) { 'настройки перенесены' } else { 'используются стандартные' }) } }
        3 { $n = Mig-MergeLists; @{ S = $(if ($n) { 'ok' } else { 'skip' }); T = $(if ($n) { "добавлено: $(Plural $n)" } else { 'в новой версии уже всё есть' }) } }
        4 { $n = Mig-Targets; @{ S = $(if ($n) { 'ok' } else { 'skip' }); T = $(if ($n) { "добавлено: $(Plural $n)" } else { 'в новой версии уже всё есть' }) } }
        5 { switch (Mig-PatchTest) { 'ok' { @{ S = 'ok'; T = 'меню вставлено' } } 'already' { @{ S = 'skip'; T = 'уже есть' } } 'none' { @{ S = 'skip'; T = 'теста нет в новой версии' } } default { @{ S = 'warn'; T = 'автор сильно изменил тест — меню не вставилось' } } } }
    }
}
function Read-UserListFrom($path) { @(Get-Content -LiteralPath $path -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') -and $_ -ne $Placeholder }).Count }

# --- построение строк шагов ---
function New-StepRow($step, [int]$i) {
    $b = New-Object System.Windows.Controls.Border
    $b.CornerRadius = 12; $b.Padding = '14,10'; $b.Margin = '0,0,0,6'; $b.Background = Brush '#1E2129'
    $g = New-Object System.Windows.Controls.Grid
    foreach ($w in @('Auto', '*', 'Auto')) { $c = New-Object System.Windows.Controls.ColumnDefinition; $c.Width = if ($w -eq '*') { New-Object System.Windows.GridLength(1, 'Star') } else { [System.Windows.GridLength]::Auto }; $g.ColumnDefinitions.Add($c) }
    # иконка состояния
    $ic = New-Object System.Windows.Controls.Grid; $ic.Width = 26; $ic.Height = 26; $ic.Margin = '0,0,14,0'; $ic.VerticalAlignment = 'Center'
    $ring = New-Object System.Windows.Shapes.Ellipse; $ring.Stroke = Brush '#3A3F4D'; $ring.StrokeThickness = 2
    $spin = New-Object System.Windows.Shapes.Ellipse; $spin.Stroke = Brush '#3DDC97'; $spin.StrokeThickness = 2.5; $spin.Opacity = 0
    $spin.StrokeDashArray = New-Object System.Windows.Media.DoubleCollection (,[double[]]@(6, 6)); $spin.StrokeDashCap = 'Round'
    $spin.RenderTransformOrigin = '0.5,0.5'; $spin.RenderTransform = New-Object System.Windows.Media.RotateTransform
    $done = New-Object System.Windows.Controls.Grid; $done.Opacity = 0; $done.RenderTransformOrigin = '0.5,0.5'; $done.RenderTransform = New-Object System.Windows.Media.ScaleTransform(0.3, 0.3)
    $dc = New-Object System.Windows.Shapes.Ellipse; $dc.Fill = Brush '#3DDC97'
    $glow = New-Object System.Windows.Media.Effects.DropShadowEffect; $glow.Color = (Color-Of '#3DDC97'); $glow.BlurRadius = 12; $glow.ShadowDepth = 0; $glow.Opacity = 0.8; $dc.Effect = $glow
    $mark = New-Object System.Windows.Controls.TextBlock; $mark.Text = T $('✓'); $mark.FontSize = 14; $mark.FontWeight = 'Bold'; $mark.Foreground = Brush '#0F1115'; $mark.HorizontalAlignment = 'Center'; $mark.VerticalAlignment = 'Center'
    [void]$done.Children.Add($dc); [void]$done.Children.Add($mark)
    foreach ($x in @($ring, $spin, $done)) { [void]$ic.Children.Add($x) }
    [void]$g.Children.Add($ic)
    $tx = New-Object System.Windows.Controls.StackPanel; $tx.VerticalAlignment = 'Center'
    $t1 = New-Object System.Windows.Controls.TextBlock; $t1.Text = T $($step.Title); $t1.FontSize = 13.5; $t1.FontWeight = 'SemiBold'; $t1.Foreground = Brush '#9AA3B5'
    $t2 = New-Object System.Windows.Controls.TextBlock; $t2.Text = T $($step.Sub); $t2.FontSize = 11.5; $t2.Foreground = Brush '#5A6070'; $t2.Margin = '0,2,0,0'
    [void]$tx.Children.Add($t1); [void]$tx.Children.Add($t2)
    [System.Windows.Controls.Grid]::SetColumn($tx, 1); [void]$g.Children.Add($tx)
    $b.Child = $g
    $b.Tag = @{ Ring = $ring; Spin = $spin; Done = $done; Circle = $dc; Mark = $mark; Glow = $glow; Title = $t1; Sub = $t2; Box = $b }
    $b.Opacity = 0; $b.RenderTransform = New-Object System.Windows.Media.TranslateTransform
    Anim-Double $b $OP 1 300 0 -beginMs ($i * 60)
    Anim-Double $b.RenderTransform ([System.Windows.Media.TranslateTransform]::XProperty) 0 380 -18 -beginMs ($i * 60)
    return $b
}
function Step-Running($tag) {
    Anim-Double $tag.Spin $OP 1 150
    Anim-Double $tag.Spin.RenderTransform $AngP 360 800 0 -Forever -NoEase
    $tag.Title.Foreground = Brush '#E6E8EE'
    Anim-Color $tag.Box.Background '#232833' 200
}
function Step-Finish($tag, $r) {
    Stop-Anim $tag.Spin.RenderTransform $AngP; Anim-Double $tag.Spin $OP 0 120
    $col = switch ($r.S) { 'ok' { '#3DDC97' } 'warn' { '#FFB86B' } default { '#5A6070' } }
    $tag.Circle.Fill = Brush $col; $tag.Glow.Color = (Color-Of $col)
    $tag.Mark.Text = T $(switch ($r.S) { 'ok' { '✓' } 'warn' { '!' } default { '–' } })
    if ($r.S -eq 'skip') { $tag.Glow.Opacity = 0; $tag.Mark.Foreground = Brush '#C9CED8' }
    Anim-Double $tag.Done $OP 1 200
    Anim-Double $tag.Done.RenderTransform $SX 1 450 0.3 -Easing (Back-Ease)
    Anim-Double $tag.Done.RenderTransform $SY 1 450 0.3 -Easing (Back-Ease)
    $tag.Sub.Text = T $($r.T)
    $tag.Sub.Foreground = Brush $(if ($r.S -eq 'ok') { '#7FE8B8' } elseif ($r.S -eq 'warn') { '#FFB86B' } else { '#8A90A0' })
    $tag.Title.Foreground = Brush $(if ($r.S -eq 'skip') { '#9AA3B5' } else { '#E6E8EE' })
    Anim-Color $tag.Box.Background '#1E2129' 300
}

function Open-Migrate {
    $script:migDemo = $false
    $script:migFolders = @(Find-ZapretFolders)
    $ui.MigSteps.Children.Clear()
    $ui.MigDone.Visibility = 'Collapsed'; $ui.MigDone.Opacity = 0
    $ui.MigRun.Content = T $('Перенести'); $ui.MigRun.IsEnabled = $true
    Stop-Anim $ui.MigToGlow ([System.Windows.Media.Effects.DropShadowEffect]::BlurRadiusProperty); $ui.MigToGlow.BlurRadius = 0
    if ($script:migFolders.Count -lt 2) {
        $ui.MigCards.Visibility = 'Collapsed'; $ui.MigEmpty.Visibility = 'Visible'; $ui.MigRun.Visibility = 'Collapsed'; $ui.MigScroll.Visibility = 'Collapsed'
    } else {
        $ui.MigCards.Visibility = 'Visible'; $ui.MigEmpty.Visibility = 'Collapsed'; $ui.MigRun.Visibility = 'Visible'; $ui.MigScroll.Visibility = 'Visible'
        $script:loading = $true
        foreach ($cb in @($ui.MigFromBox, $ui.MigToBox)) {
            $cb.Items.Clear()
            foreach ($f in $script:migFolders) { $it = New-Object System.Windows.Controls.ComboBoxItem; $it.Content = T $($(if ($f.Version -ne [version]'0.0') { "v$($f.Version)" } else { $f.Name })); $it.Tag = $f.Path; $it.ToolTip = $f.Path; [void]$cb.Items.Add($it) }
        }
        $ui.MigFromBox.SelectedIndex = $script:migFolders.Count - 2
        $ui.MigToBox.SelectedIndex = $script:migFolders.Count - 1
        $script:loading = $false
        $i = 0; foreach ($s in $MigSteps) { [void]$ui.MigSteps.Children.Add((New-StepRow $s $i)); $i++ }
    }
    $ui.MigOverlay.Visibility = 'Visible'
    Anim-Double $ui.MigDim $OP 1 250 0
    Anim-Double $ui.MigPanel $OP 1 280 0
    Anim-Double $ui.MigScale $SX 1 420 0.94 -Easing (Back-Ease)
    Anim-Double $ui.MigScale $SY 1 420 0.94 -Easing (Back-Ease)
    Anim-Double $ui.MigDotsT ([System.Windows.Media.TranslateTransform]::XProperty) 40 1400 -40 -Forever -NoEase
}
function Close-Migrate {
    if ($script:migRunning) { return }
    Anim-Double $ui.MigDim $OP 0 200; Anim-Double $ui.MigPanel $OP 0 200
    Anim-Double $ui.MigScale $SX 0.96 220; Anim-Double $ui.MigScale $SY 0.96 220
    $script:migCloseT = New-Object System.Windows.Threading.DispatcherTimer
    $script:migCloseT.Interval = [TimeSpan]::FromMilliseconds(220)
    $script:migCloseT.Add_Tick({ $script:migCloseT.Stop(); $ui.MigOverlay.Visibility = 'Collapsed'; Stop-Anim $ui.MigDotsT ([System.Windows.Media.TranslateTransform]::XProperty) })
    $script:migCloseT.Start()
}
function Start-Migrate {
    if (-not $script:migDemo) {
        $script:migFrom = $ui.MigFromBox.SelectedItem.Tag; $script:migTo = $ui.MigToBox.SelectedItem.Tag
        if (-not $script:migFrom -or $script:migFrom -eq $script:migTo) { Show-Toast 'Выбери разные версии: откуда и куда' -Err; return }
    }
    $script:migRunning = $true; $script:migIndex = 0; $script:migPhase = 'run'
    $ui.MigRun.IsEnabled = $false; $ui.MigRun.Content = T $('Переношу…')
    $ui.MigFromBox.IsEnabled = $false; $ui.MigToBox.IsEnabled = $false
    Anim-Double $ui.MigDotsT ([System.Windows.Media.TranslateTransform]::XProperty) 40 500 -40 -Forever -NoEase
    $script:migTimer.Start()
}
$script:migTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:migTimer.Interval = [TimeSpan]::FromMilliseconds(380)
$script:migTimer.Add_Tick({
    try {
        $rows = $ui.MigSteps.Children
        if ($script:migIndex -ge $rows.Count) {
            $script:migTimer.Stop(); $script:migRunning = $false
            $ui.MigRun.Content = T $('Готово'); $ui.MigRun.IsEnabled = $true
            $ui.MigFromBox.IsEnabled = $true; $ui.MigToBox.IsEnabled = $true
            Anim-Double $ui.MigDotsT ([System.Windows.Media.TranslateTransform]::XProperty) 40 1400 -40 -Forever -NoEase
            Anim-Double $ui.MigToGlow ([System.Windows.Media.Effects.DropShadowEffect]::BlurRadiusProperty) 34 600 0
            $ui.MigDone.Visibility = 'Visible'; Anim-Double $ui.MigDone $OP 1 400 0
            if ($script:migDemo) { $ui.MigDone.Text = T $('Это было демо — файлы не менялись. Так всё пройдёт при настоящем переносе.') }
            else { $ui.MigDone.Text = T $('Готово! Оригиналы файлов автора сохранены как .bak') }
            if ($script:migDemo) { return }
            if ($script:migTo -eq $Root) { Refresh-Sites; $script:loading = $true; Select-ByTag $ui.GameBox (Read-Game).Mode; $ui.UpdToggle.IsChecked = (Test-Path -LiteralPath $UpdFile); $script:loading = $false }
            return
        }
        $tag = $rows[$script:migIndex].Tag
        if ($script:migPhase -eq 'run') { Step-Running $tag; $script:migPhase = 'finish'; return }
        $r = Run-MigStep $script:migIndex
        Step-Finish $tag $r
        $script:migIndex++; $script:migPhase = 'run'
    } catch {
        $script:migTimer.Stop(); $script:migRunning = $false
        Write-Log ($_ | Out-String); Show-Toast ("Ошибка переноса: " + $_.Exception.Message) -Err
        $ui.MigRun.Content = T $('Перенести'); $ui.MigRun.IsEnabled = $true
    }
})
$ui.MigRun.Add_Click({ Safe {
    if ($ui.MigRun.Content -eq (T 'Готово')) {
        Close-Migrate
        if ($script:migDemo) { return }
        if ($script:migTo -eq $Root -and $script:state -eq 'on') { Show-Toast 'Перезапускаю zapret на новой версии…'; Restart-Zapret }
        elseif ($script:migTo -ne $Root) { Show-Toast 'Перезапусти приложение, чтобы перейти на новую версию' }
    } else { Start-Migrate }
} })
$ui.MigClose.Add_Click({ Close-Migrate })
$ui.MigDim.Add_MouseLeftButtonUp({ Close-Migrate })
$ui.MigDemo.Add_Click({ Safe {
    $script:migDemo = $true
    $ui.MigEmpty.Visibility = 'Collapsed'; $ui.MigCards.Visibility = 'Visible'; $ui.MigRun.Visibility = 'Visible'; $ui.MigScroll.Visibility = 'Visible'
    $ui.MigRun.IsEnabled = $false
    $script:loading = $true
    foreach ($pair in @(@($ui.MigFromBox, "v$LocalVersion"), @($ui.MigToBox, 'v-next (демо)'))) {
        $pair[0].Items.Clear(); $it = New-Object System.Windows.Controls.ComboBoxItem; $it.Content = T $($pair[1]); [void]$pair[0].Items.Add($it); $pair[0].SelectedIndex = 0
    }
    $script:loading = $false
    $ui.MigSteps.Children.Clear()
    $i = 0; foreach ($s in $MigSteps) { [void]$ui.MigSteps.Children.Add((New-StepRow $s $i)); $i++ }
    $script:migDemoT = New-Object System.Windows.Threading.DispatcherTimer
    $script:migDemoT.Interval = [TimeSpan]::FromMilliseconds(700)
    $script:migDemoT.Add_Tick({ $script:migDemoT.Stop(); Start-Migrate })
    $script:migDemoT.Start()
} })
$ui.MigOpenFolder.Add_Click({ Safe { Start-Process explorer.exe -ArgumentList "`"$HomeDir`"" } })




# ======================= Поделиться / Вставить =======================
function Build-TextFromGroups($groups) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# Zapret · список сайтов · by COFFIOK')
    $lines.Add('# Скопируй этот текст и нажми в Zapret «Вставить»')
    foreach ($k in $groups.Keys) {
        $lines.Add(''); $lines.Add("# @group $k")
        foreach ($d in $groups[$k]) { $lines.Add($d) }
    }
    ($lines -join "`r`n") + "`r`n"
}
function Parse-SitesText([string]$text) {
    # разбирает текст в группы; уже добавленные сайты пропускает
    $mine = Read-UserGroups
    $have = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($k in $mine.Keys) { foreach ($d in $mine[$k]) { [void]$have.Add($d) } }
    $out = New-Object System.Collections.Specialized.OrderedDictionary
    $cur = $null; $script:parseSkipped = 0
    foreach ($raw in ($text -split "`r?`n")) {
        $s = $raw.Trim()
        if (-not $s) { continue }
        if ($s -match '^#\s*@group\s+(.+)$') { $cur = $matches[1].Trim(); continue }
        if ($s.StartsWith('#') -or $s -eq $Placeholder) { continue }
        foreach ($tok in ($s -split '[\s,;]+')) {
            $dom = $tok.ToLower() -replace '^[a-z]+://', '' -replace '[/?#].*$', '' -replace ':\d+$', '' -replace '^www\.', ''
            if ($dom -notmatch '^([a-z0-9а-яё-]+\.)+[a-zа-яё0-9-]{2,}$') { continue }
            if ($have.Contains($dom)) { $script:parseSkipped++; continue }
            $grp = if ($cur) { $cur } else { Find-GroupFor $mine $dom }
            if (-not $out.Contains($grp)) { $out[$grp] = New-Object System.Collections.Generic.List[string] }
            if (-not $out[$grp].Contains($dom)) { $out[$grp].Add($dom) }
            [void]$have.Add($dom)
        }
    }
    return $out
}

$script:shareBoxes = @()
function New-ShareCheck([string]$text, $tag, [double]$size) {
    $c = New-Object System.Windows.Controls.CheckBox
    $c.Style = $win.FindResource('Check'); $c.IsChecked = $true; $c.Tag = $tag
    $tb = New-Object System.Windows.Controls.TextBlock; $tb.Text = $text; $tb.FontSize = $size
    $c.Content = $tb
    return $c
}
function Open-Share([string]$mode, $groups) {
    $script:shareMode = $mode
    $ui.ShareList.Children.Clear(); $script:shareBoxes = @()
    if ($mode -eq 'share') {
        $ui.ShareTitle.Text = T 'Поделиться сайтами'
        $ui.ShareSub.Text = T 'Отметь, чем поделиться. Список скопируется — останется вставить его в сообщение.'
        $ui.ShareGo.Content = T 'Скопировать'
    } else {
        $ui.ShareTitle.Text = T 'Добавить сайты'
        $ui.ShareSub.Text = T 'Вот что нашлось в скопированном тексте. Отметь, что добавить к себе.'
        $ui.ShareGo.Content = T 'Добавить'
    }
    $i = 0
    foreach ($k in $groups.Keys) {
        $wrap = New-Object System.Windows.Controls.StackPanel; $wrap.Margin = '0,4,0,8'
        $head = New-Object System.Windows.Controls.StackPanel; $head.Orientation = 'Horizontal'
        $gc = New-ShareCheck $k @{ Kind = 'group'; Group = $k } 13.5
        $gc.Content.FontWeight = 'SemiBold'
        $dot = New-Object System.Windows.Shapes.Ellipse; $dot.Width = 7; $dot.Height = 7; $dot.Fill = Brush (Get-GroupColor $k $false); $dot.Margin = '10,0,6,0'; $dot.VerticalAlignment = 'Center'
        $cnt = New-Object System.Windows.Controls.TextBlock; $cnt.Text = T $(Plural $groups[$k].Count); $cnt.FontSize = 11.5; $cnt.Foreground = Brush '#8A90A0'; $cnt.VerticalAlignment = 'Center'
        [void]$head.Children.Add($gc); [void]$head.Children.Add($dot); [void]$head.Children.Add($cnt)
        $sites = New-Object System.Windows.Controls.WrapPanel; $sites.Margin = '27,6,0,0'
        $kids = @()
        foreach ($d in $groups[$k]) {
            $sc = New-ShareCheck $d @{ Kind = 'site'; Group = $k; Domain = $d; Parent = $gc } 12.5
            $sc.Margin = '0,0,16,6'; $sc.Content.Foreground = Brush '#B8BDC9'
            $sc.Add_Click({ Sync-ShareGroup $this.Tag.Parent; Update-ShareCount })
            [void]$sites.Children.Add($sc); $kids += $sc; $script:shareBoxes += $sc
        }
        $gc.Tag.Kids = $kids
        $gc.Add_Click({ $v = [bool]$this.IsChecked; if ($this.IsChecked -eq $null) { $v = $true; $this.IsChecked = $true }; foreach ($c in $this.Tag.Kids) { $c.IsChecked = $v }; Update-ShareCount })
        [void]$wrap.Children.Add($head); [void]$wrap.Children.Add($sites)
        $wrap.Opacity = 0; $wrap.RenderTransform = New-Object System.Windows.Media.TranslateTransform
        Anim-Double $wrap $OP 1 280 0 -beginMs ([Math]::Min($i, 10) * 45)
        Anim-Double $wrap.RenderTransform ([System.Windows.Media.TranslateTransform]::YProperty) 0 320 10 -beginMs ([Math]::Min($i, 10) * 45)
        [void]$ui.ShareList.Children.Add($wrap); $i++
    }
    Update-ShareCount
    $ui.ShareOverlay.Visibility = 'Visible'
    Anim-Double $ui.ShareDim $OP 1 250 0
    Anim-Double $ui.SharePanel $OP 1 280 0
    Anim-Double $ui.ShareScale $SX 1 420 0.94 -Easing (Back-Ease)
    Anim-Double $ui.ShareScale $SY 1 420 0.94 -Easing (Back-Ease)
}
function Close-Share {
    Anim-Double $ui.ShareDim $OP 0 200; Anim-Double $ui.SharePanel $OP 0 200
    Anim-Double $ui.ShareScale $SX 0.96 220; Anim-Double $ui.ShareScale $SY 0.96 220
    $script:shareCloseT = New-Object System.Windows.Threading.DispatcherTimer
    $script:shareCloseT.Interval = [TimeSpan]::FromMilliseconds(220)
    $script:shareCloseT.Add_Tick({ $script:shareCloseT.Stop(); $ui.ShareOverlay.Visibility = 'Collapsed' })
    $script:shareCloseT.Start()
}
function Sync-ShareGroup($gc) {
    $on = @($gc.Tag.Kids | Where-Object { $_.IsChecked }).Count
    $gc.IsChecked = if ($on -eq 0) { $false } elseif ($on -eq $gc.Tag.Kids.Count) { $true } else { $null }
}
function Get-ShareSelection {
    $sel = New-Object System.Collections.Specialized.OrderedDictionary
    foreach ($c in $script:shareBoxes) {
        if (-not $c.IsChecked) { continue }
        $g = $c.Tag.Group
        if (-not $sel.Contains($g)) { $sel[$g] = New-Object System.Collections.Generic.List[string] }
        $sel[$g].Add($c.Tag.Domain)
    }
    return $sel
}
function Update-ShareCount {
    $n = @($script:shareBoxes | Where-Object { $_.IsChecked }).Count
    $ui.ShareCount.Text = T "Выбрано: $(Plural $n) из $($script:shareBoxes.Count)"
    $ui.ShareGo.IsEnabled = ($n -gt 0)
    $ui.ShareAll.Text = T $(if ($n -eq $script:shareBoxes.Count) { 'Снять все' } else { 'Выбрать все' })
}
$ui.ShareAll.Add_MouseLeftButtonUp({
    $all = (@($script:shareBoxes | Where-Object { $_.IsChecked }).Count -ne $script:shareBoxes.Count)
    foreach ($c in $script:shareBoxes) { $c.IsChecked = $all }
    foreach ($w in $ui.ShareList.Children) { $w.Children[0].Children[0].IsChecked = $all }
    Update-ShareCount
})
$ui.ShareClose.Add_Click({ Close-Share })
$ui.ShareDim.Add_MouseLeftButtonUp({ Close-Share })
$ui.ShareGo.Add_Click({ Safe {
    $sel = Get-ShareSelection
    $n = 0; foreach ($k in $sel.Keys) { $n += $sel[$k].Count }
    if ($script:shareMode -eq 'share') {
        [System.Windows.Clipboard]::SetText((Build-TextFromGroups $sel))
        Close-Share
        Show-Toast "Скопировано: $(Plural $n). Вставь в сообщение другу (Ctrl+V)"
    } else {
        $g = Read-UserGroups
        foreach ($k in $sel.Keys) {
            if (-not $g.Contains($k)) { $g[$k] = New-Object System.Collections.Generic.List[string] }
            foreach ($d in $sel[$k]) { if (-not $g[$k].Contains($d)) { $g[$k].Add($d) } }
            $script:expanded["u:$k"] = $true
        }
        Write-UserGroups $g
        Close-Share
        $script:rowsShown = $false; Refresh-Sites
        Show-Toast "Добавлено: $(Plural $n)"
        Mark-Changed
    }
} })
$ui.ShareBtn.Add_Click({ Safe {
    $g = Read-UserGroups
    if ($g.Count -eq 0) { Show-Toast 'Пока нечем делиться — добавь сайты' -Err; return }
    Open-Share 'share' $g
} })
$ui.PasteBtn.Add_Click({ Safe {
    $txt = [System.Windows.Clipboard]::GetText()
    if (-not $txt) { Show-Toast 'Сначала скопируй список сайтов, который тебе прислали' -Err; return }
    $parsed = Parse-SitesText $txt
    if ($parsed.Count -eq 0) {
        if ($script:parseSkipped) { Show-Toast "Все сайты из списка у тебя уже есть ($($script:parseSkipped))" } else { Show-Toast 'В скопированном тексте не нашлось адресов сайтов' -Err }
        return
    }
    Open-Share 'import' $parsed
} })

# ======================= Telegram (tg-ws-proxy от Flowseal) =======================
$TgDir = Join-Path $(if ($InHome) { $HomeDir } else { $GuiDir }) 'telegram'
$TgExe = Join-Path $TgDir 'TgWsProxy_windows.exe'
$TgData = Join-Path $TgDir 'TgWsProxy_data'
$TgCfg = Join-Path $TgData 'config.json'
$TgUrl = 'https://github.com/Flowseal/tg-ws-proxy/releases/latest/download/TgWsProxy_windows.exe'
$script:tgState = 'off'
$ui.TgDot.Fill = Brush '#2A2E38'; $ui.TgPlane.Foreground = Brush '#8A90A0'; $ui.TgLed.Fill = Brush '#8A90A0'

function Get-TgProc { @(Get-Process -Name 'TgWsProxy_windows' -ErrorAction SilentlyContinue) }
function Get-TgConfig {
    if (-not (Test-Path -LiteralPath $TgCfg)) { return $null }
    try { Get-Content -LiteralPath $TgCfg -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $null }
}
function Ensure-TgConfig {
    # портативный режим: настройки лежат рядом с exe; без окна приветствия и своей проверки обновлений
    New-Item -ItemType Directory -Force -Path $TgData | Out-Null
    if (-not (Test-Path -LiteralPath $TgCfg)) {
        $b = New-Object byte[] 16; (New-Object Security.Cryptography.RNGCryptoServiceProvider).GetBytes($b)
        $secret = -join ($b | ForEach-Object { $_.ToString('x2') })
        $cfg = [ordered]@{ host = '127.0.0.1'; port = 1443; secret = $secret; check_updates = $false; language = 'ru' }
        [IO.File]::WriteAllText($TgCfg, ($cfg | ConvertTo-Json), $utf8)
    }
    $marker = Join-Path $TgData '.first_run_done_mtproto'
    if (-not (Test-Path -LiteralPath $marker)) { [IO.File]::WriteAllText($marker, '') }
}
function Get-TgLink {
    $c = Get-TgConfig
    if (-not $c) { return $null }
    $h = if ($c.host -and $c.host -ne '0.0.0.0') { $c.host } else { '127.0.0.1' }
    $port = if ($c.port) { $c.port } else { 1443 }
    "tg://proxy?server=$h&port=$port&secret=dd$($c.secret)"
}
function Set-TgVisual($mode, $text) {
    $script:tgState = $mode
    $ui.TgStatus.Text = T $($text)
    switch ($mode) {
        'on' {
            $script:tgVisAt = [DateTime]::UtcNow.Ticks; $Mon.Wake = $true
            Anim-Color $ui.TgDot.Fill '#5ED3F3'; Anim-Color $ui.TgPlane.Foreground '#0F1115'; Anim-Color $ui.TgLed.Fill '#5ED3F3'
            $ui.TgCard.BorderBrush = Brush '#2B5664'
            if ($ui.TgConnect.Visibility -ne 'Visible') { $ui.TgConnect.Visibility = 'Visible'; Anim-Double $ui.TgConnect $OP 1 250 0 }
            if ($ui.TgPhone.Visibility -ne 'Visible') { $ui.TgPhone.Visibility = 'Visible'; Anim-Double $ui.TgPhone $OP 1 250 0 }
        }
        'busy' { $script:tgVisAt = [DateTime]::UtcNow.Ticks; $Mon.Wake = $true; Anim-Color $ui.TgDot.Fill '#2B5664'; Anim-Color $ui.TgPlane.Foreground '#CFEFF8'; Anim-Color $ui.TgLed.Fill '#5ED3F3' }
        default {
            $script:tgVisAt = [DateTime]::UtcNow.Ticks; $Mon.Wake = $true; $script:tgIsConnected = $false
            Anim-Color $ui.TgDot.Fill '#2A2E38'; Anim-Color $ui.TgPlane.Foreground '#8A90A0'; Anim-Color $ui.TgLed.Fill '#8A90A0'
            $ui.TgCard.BorderBrush = Brush '#232731'
            $ui.TgConnect.Visibility = 'Collapsed'; $ui.TgPhone.Visibility = 'Collapsed'
        }
    }
}
function Start-Tg {
    if (-not (Test-Path -LiteralPath $TgExe)) { Start-TgDownload; return }
    Ensure-TgConfig
    if (-not (Get-TgProc)) { Start-Process -FilePath $TgExe -WorkingDirectory $TgDir -WindowStyle Hidden | Out-Null }
    $Settings.tgEnabled = $true; Save-Settings
    $port = (Get-TgConfig).port; if (-not $port) { $port = 1443 }
    Set-TgVisual 'on' "Работает · 127.0.0.1:$port"
    if (-not $Settings.tgConnected) {
        Show-Toast 'Telegram-прокси запущен. Нажми «Подключить», чтобы настроить Telegram Desktop.'
        Anim-Double $ui.TgConnect $OP 0.4 500 1 -Forever -AutoReverse -Low
    }
}
function Stop-Tg {
    $script:qrReady = $false
    Get-TgProc | Stop-Process -Force -ErrorAction SilentlyContinue
    $Settings.tgEnabled = $false; Save-Settings
    Set-TgVisual 'off' 'Выключен'
}
function Start-TgDownload {
    Set-TgVisual 'busy' 'Скачиваю tg-ws-proxy…'
    $ui.TgToggle.IsEnabled = $false
    New-Item -ItemType Directory -Force -Path $TgDir | Out-Null
    $script:tgDl = [hashtable]::Synchronized(@{ Done = $false; Got = 0; Total = 0; Error = $null; File = "$TgExe.part" })
    $rs = [runspacefactory]::CreateRunspace(); $rs.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    [void]$ps.AddScript({
        param($sync, $url)
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $req = [Net.HttpWebRequest]::Create($url); $req.UserAgent = 'ZapretGUI'; $req.AllowAutoRedirect = $true
            $resp = $req.GetResponse(); $sync.Total = $resp.ContentLength
            $in = $resp.GetResponseStream(); $fs = [IO.File]::Create($sync.File); $buf = New-Object byte[] 65536
            while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) { $fs.Write($buf, 0, $n); $sync.Got += $n }
            $fs.Close(); $in.Close(); $resp.Close()
        } catch { $sync.Error = $_.Exception.Message } finally { $sync.Done = $true }
    }).AddArgument($script:tgDl).AddArgument($TgUrl)
    $script:tgDlPs = $ps; [void]$ps.BeginInvoke()
}

# ======================= Фоновый монитор =======================
# Все проверки (процессы, служба, подключение Telegram) идут в отдельном потоке,
# окно только читает готовые значения — так интерфейс не подтормаживает.
$script:visAt = 0L; $script:tgVisAt = 0L
function Start-Monitor {
    $rs = [runspacefactory]::CreateRunspace(); $rs.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    [void]$ps.AddScript({
        param($m, $cfgPath)
        $i = 0; $port = 1443
        while (-not $m.Stop) {
            $t0 = [DateTime]::UtcNow.Ticks
            try {
                $p = [System.Diagnostics.Process]::GetProcessesByName('winws')
                $name = $null
                foreach ($x in $p) { try { if ($x.MainWindowTitle -match '^zapret:\s*(.+)$') { $name = $matches[1].Trim() } } catch {}; $x.Dispose() }
                $svc = $null; $svcName = $null
                try { $s = New-Object System.ServiceProcess.ServiceController 'zapret'; $svc = "$($s.Status)"; $s.Dispose() } catch { $svc = $null }
                if ($svc -eq 'Running') { try { $svcName = (Get-ItemProperty 'HKLM:\System\CurrentControlSet\Services\zapret' -ErrorAction Stop).'zapret-discord-youtube' } catch {} }
                $tgp = [System.Diagnostics.Process]::GetProcessesByName('TgWsProxy_windows'); $tg = $tgp.Length -gt 0; foreach ($x in $tgp) { $x.Dispose() }
                $conn = $false
                if ($tg) {
                    if ($i % 5 -eq 0) { try { $c = [IO.File]::ReadAllText($cfgPath) | ConvertFrom-Json; if ($c.port) { $port = [int]$c.port } } catch {} }
                    # клиент Telegram держит соединение с прокси на этом компьютере
                    $hasClient = $false
                    foreach ($nm in 'Telegram', 'AyuGram', 'Kotatogram', '64Gram', 'Unigram') { $q = [System.Diagnostics.Process]::GetProcessesByName($nm); if ($q.Length) { $hasClient = $true }; foreach ($x in $q) { $x.Dispose() }; if ($hasClient) { break } }
                    if ($hasClient) {
                        foreach ($cn in [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpConnections()) {
                            if ($cn.RemoteEndPoint.Port -eq $port -and $cn.State -eq 'Established' -and [System.Net.IPAddress]::IsLoopback($cn.RemoteEndPoint.Address)) { $conn = $true; break }
                        }
                    }
                }
                $m.Winws = $p.Length -gt 0; $m.WinwsName = $name; $m.Svc = $svc; $m.SvcName = $svcName; $m.Tg = $tg; $m.TgConn = $conn
                $m.At = $t0
            } catch {}
            $i++
            # переключение — часто; окно открыто — раз в 3 с; свёрнуто/в фоне — раз в 10 с
            $wait = if ($m.Fast) { 300 } elseif ($m.Idle) { 10000 } else { 3000 }
            for ($k = 0; $k -lt $wait -and -not $m.Stop -and -not ($m.Fast -and $k -ge 300) -and -not ($m.Wake); $k += 100) { Start-Sleep -Milliseconds 100 }
            $m.Wake = $false
        }
    }).AddArgument($Mon).AddArgument($TgCfg)
    $script:monPs = $ps; [void]$ps.BeginInvoke()
}
Add-Type -AssemblyName System.ServiceProcess -ErrorAction SilentlyContinue
$script:tgTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:tgTimer.Interval = [TimeSpan]::FromMilliseconds(1000)
$script:tgTimer.Add_Tick({
    try {
        if ($script:tgDl) {
            $d = $script:tgDl
            if ($d.Total -gt 0) { $ui.TgStatus.Text = T $("Скачиваю tg-ws-proxy · $([int](100 * $d.Got / $d.Total))%") }
            if ($d.Done) {
                $script:tgDl = $null; $ui.TgToggle.IsEnabled = $true
                if ($d.Error -or -not (Test-Path -LiteralPath $d.File) -or (Get-Item -LiteralPath $d.File).Length -lt 100000) {
                    Remove-Item -LiteralPath $d.File -Force -ErrorAction SilentlyContinue
                    $ui.TgToggle.IsChecked = $false; Set-TgVisual 'off' 'Не удалось скачать'
                    Show-Toast "Не удалось скачать tg-ws-proxy с GitHub: $($d.Error)" -Err
                } else {
                    Move-Item -LiteralPath $d.File -Destination $TgExe -Force
                    Start-Tg
                }
            }
            return
        }
        # данные от фонового монитора; свежие — только если сняты после последнего переключения
        if ($Mon.At -le $script:tgVisAt) { return }
        Update-TgConnected
        # следим, не закрыли ли прокси из трея
        $running = [bool]$Mon.Tg
        if ($script:tgState -eq 'on' -and -not $running) { $ui.TgToggle.IsChecked = $false; Set-TgVisual 'off' 'Выключен' }
        elseif ($script:tgState -eq 'off' -and $running) { $ui.TgToggle.IsChecked = $true; $port = (Get-TgConfig).port; if (-not $port) { $port = 1443 }; Set-TgVisual 'on' "Работает · 127.0.0.1:$port" }
    } catch { Write-Log ($_ | Out-String) }
})
# ---- QR-код (байтовый режим, уровень коррекции L, версии 1–6) ----
function New-QrMatrix([string]$text) {
    $data = [Text.Encoding]::UTF8.GetBytes($text)
    $caps = @{ 1 = @(19, 7, 1); 2 = @(34, 10, 1); 3 = @(55, 15, 1); 4 = @(80, 20, 1); 5 = @(108, 26, 1); 6 = @(136, 18, 2) }
    $ver = 0
    foreach ($v in 1..6) { if ((4 + 8 + 8 * $data.Length) -le $caps[$v][0] * 8) { $ver = $v; break } }
    if (-not $ver) { throw 'QR: слишком длинный текст' }
    $dcw = $caps[$ver][0]; $ecn = $caps[$ver][1]; $blocks = $caps[$ver][2]
    # биты данных
    $bits = New-Object System.Collections.Generic.List[int]
    function AddBits([int]$val, [int]$len) { for ($i = $len - 1; $i -ge 0; $i--) { $bits.Add(($val -shr $i) -band 1) } }
    AddBits 4 4; AddBits $data.Length 8
    foreach ($b in $data) { AddBits $b 8 }
    $max = $dcw * 8
    for ($i = 0; $i -lt 4 -and $bits.Count -lt $max; $i++) { $bits.Add(0) }
    while ($bits.Count % 8) { $bits.Add(0) }
    $cw = New-Object System.Collections.Generic.List[int]
    for ($i = 0; $i -lt $bits.Count; $i += 8) { $v = 0; for ($j = 0; $j -lt 8; $j++) { $v = ($v -shl 1) -bor $bits[$i + $j] }; $cw.Add($v) }
    $pad = @(0xEC, 0x11); $k = 0
    while ($cw.Count -lt $dcw) { $cw.Add($pad[$k % 2]); $k++ }
    # Рид — Соломон в GF(256)
    $exp = New-Object int[] 512; $log = New-Object int[] 256; $x = 1
    for ($i = 0; $i -lt 255; $i++) { $exp[$i] = $x; $log[$x] = $i; $x = $x -shl 1; if ($x -band 0x100) { $x = $x -bxor 0x11D } }
    for ($i = 255; $i -lt 512; $i++) { $exp[$i] = $exp[$i - 255] }
    $gen = @(1)
    for ($i = 0; $i -lt $ecn; $i++) {
        $ng = New-Object int[] ($gen.Count + 1)
        for ($j = 0; $j -lt $gen.Count; $j++) {
            $ng[$j] = $ng[$j] -bxor $gen[$j]
            if ($gen[$j]) { $ng[$j + 1] = $ng[$j + 1] -bxor $exp[$log[$gen[$j]] + $i] }
        }
        $gen = $ng
    }
    $per = [int]($dcw / $blocks)
    $dBlocks = @(); $eBlocks = @()
    for ($bI = 0; $bI -lt $blocks; $bI++) {
        $blk = @($cw.GetRange($bI * $per, $per))
        $rem = New-Object int[] $ecn
        foreach ($c in $blk) {
            $f = $c -bxor $rem[0]
            for ($j = 0; $j -lt $ecn - 1; $j++) { $rem[$j] = $rem[$j + 1] }
            $rem[$ecn - 1] = 0
            if ($f) { for ($j = 0; $j -lt $ecn; $j++) { if ($gen[$j + 1]) { $rem[$j] = $rem[$j] -bxor $exp[$log[$gen[$j + 1]] + $log[$f]] } } }
        }
        $dBlocks += , $blk; $eBlocks += , $rem
    }
    $final = New-Object System.Collections.Generic.List[int]
    for ($i = 0; $i -lt $per; $i++) { foreach ($b in $dBlocks) { $final.Add($b[$i]) } }
    for ($i = 0; $i -lt $ecn; $i++) { foreach ($b in $eBlocks) { $final.Add($b[$i]) } }
    # матрица
    $n = 17 + 4 * $ver
    $m = New-Object 'int[,]' $n, $n; $fn = New-Object 'bool[,]' $n, $n
    function SetF([int]$r, [int]$c, [int]$v) { $m[$r, $c] = $v; $fn[$r, $c] = $true }
    foreach ($o in @(@(0, 0), @(0, ($n - 7)), @(($n - 7), 0))) {
        for ($r = -1; $r -le 7; $r++) { for ($c = -1; $c -le 7; $c++) {
            $rr = $o[0] + $r; $cc = $o[1] + $c
            if ($rr -lt 0 -or $cc -lt 0 -or $rr -ge $n -or $cc -ge $n) { continue }
            $in = ($r -ge 0 -and $r -le 6 -and $c -ge 0 -and $c -le 6) -and (($r -eq 0 -or $r -eq 6 -or $c -eq 0 -or $c -eq 6) -or ($r -ge 2 -and $r -le 4 -and $c -ge 2 -and $c -le 4))
            SetF $rr $cc ([int]$in)
        } }
    }
    for ($i = 8; $i -lt $n - 8; $i++) { SetF 6 $i ([int](($i % 2) -eq 0)); SetF $i 6 ([int](($i % 2) -eq 0)) }
    $al = @{ 2 = 18; 3 = 22; 4 = 26; 5 = 30; 6 = 34 }
    if ($ver -ge 2) {
        $a = $al[$ver]
        for ($r = -2; $r -le 2; $r++) { for ($c = -2; $c -le 2; $c++) { SetF ($a + $r) ($a + $c) ([int]([Math]::Max([Math]::Abs($r), [Math]::Abs($c)) -ne 1)) } }
    }
    SetF ($n - 8) 8 1
    for ($i = 0; $i -lt 9; $i++) { if (-not $fn[8, $i]) { SetF 8 $i 0 }; if (-not $fn[$i, 8]) { SetF $i 8 0 } }
    for ($i = 0; $i -lt 8; $i++) { SetF 8 ($n - 1 - $i) 0; SetF ($n - 1 - $i) 8 0 }
    SetF ($n - 8) 8 1
    # размещение данных змейкой
    $allBits = New-Object System.Collections.Generic.List[int]
    foreach ($c in $final) { for ($i = 7; $i -ge 0; $i--) { $allBits.Add(($c -shr $i) -band 1) } }
    $idx = 0; $up = $true
    for ($col = $n - 1; $col -gt 0; $col -= 2) {
        if ($col -eq 6) { $col-- }
        for ($t = 0; $t -lt $n; $t++) {
            $r = if ($up) { $n - 1 - $t } else { $t }
            foreach ($cc in @($col, ($col - 1))) {
                if (-not $fn[$r, $cc]) { $m[$r, $cc] = $(if ($idx -lt $allBits.Count) { $allBits[$idx] } else { 0 }); $idx++ }
            }
        }
        $up = -not $up
    }
    # маски + форматная информация, выбираем лучшую
    $best = $null; $bestScore = [int]::MaxValue
    for ($mask = 0; $mask -lt 8; $mask++) {
        $q = $m.Clone()
        for ($r = 0; $r -lt $n; $r++) { for ($c = 0; $c -lt $n; $c++) {
            if ($fn[$r, $c]) { continue }
            $inv = switch ($mask) { 0 { ($r + $c) % 2 -eq 0 } 1 { $r % 2 -eq 0 } 2 { $c % 3 -eq 0 } 3 { ($r + $c) % 3 -eq 0 }
                4 { ([Math]::Floor($r / 2) + [Math]::Floor($c / 3)) % 2 -eq 0 } 5 { (($r * $c) % 2 + ($r * $c) % 3) -eq 0 }
                6 { ((($r * $c) % 2 + ($r * $c) % 3) % 2) -eq 0 } 7 { ((($r + $c) % 2 + ($r * $c) % 3) % 2) -eq 0 } }
            if ($inv) { $q[$r, $c] = 1 - $q[$r, $c] }
        } }
        $fmt = (1 -shl 3) -bor $mask   # уровень L = 01
        $v = $fmt -shl 10
        for ($i = 14; $i -ge 10; $i--) { if (($v -shr $i) -band 1) { $v = $v -bxor (0x537 -shl ($i - 10)) } }
        $fb = (($fmt -shl 10) -bor $v) -bxor 0x5412
        for ($i = 0; $i -lt 15; $i++) {
            $bit = ($fb -shr $i) -band 1
            if ($i -lt 6) { $q[$i, 8] = $bit } elseif ($i -lt 8) { $q[($i + 1), 8] = $bit } else { $q[($n - 15 + $i), 8] = $bit }
            if ($i -lt 8) { $q[8, ($n - 1 - $i)] = $bit } elseif ($i -lt 9) { $q[8, (15 - $i)] = $bit } else { $q[8, (14 - $i)] = $bit }
        }
        $q[($n - 8), 8] = 1
        # штраф (упрощённо: правила 1 и 2 и баланс)
        $score = 0; $dark = 0
        for ($r = 0; $r -lt $n; $r++) {
            $runR = 1; $runC = 1
            for ($c = 0; $c -lt $n; $c++) {
                $dark += $q[$r, $c]
                if ($c -gt 0) {
                    if ($q[$r, $c] -eq $q[$r, ($c - 1)]) { $runR++ } else { if ($runR -ge 5) { $score += $runR - 2 }; $runR = 1 }
                    if ($q[$c, $r] -eq $q[($c - 1), $r]) { $runC++ } else { if ($runC -ge 5) { $score += $runC - 2 }; $runC = 1 }
                }
                if ($r -lt $n - 1 -and $c -lt $n - 1) { $s = $q[$r, $c] + $q[$r, ($c + 1)] + $q[($r + 1), $c] + $q[($r + 1), ($c + 1)]; if ($s -eq 0 -or $s -eq 4) { $score += 3 } }
            }
            if ($runR -ge 5) { $score += $runR - 2 }; if ($runC -ge 5) { $score += $runC - 2 }
        }
        $score += [Math]::Floor([Math]::Abs($dark * 100 / ($n * $n) - 50) / 5) * 10
        if ($score -lt $bestScore) { $bestScore = $score; $best = $q }
    }
    return , $best
}

# ---- Telegram: телефон по QR и «уже подключён» ----
function Get-LanIp {
    try {
        $cfg = Get-NetIPConfiguration -ErrorAction Stop | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } | Select-Object -First 1
        if ($cfg) { return @($cfg.IPv4Address)[0].IPAddress }
    } catch {}
    try { return (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1).IPAddress } catch { return $null }
}
function Enable-TgLan {
    # разрешить подключения к прокси из домашней сети (только с адресов своей подсети)
    $c = Get-TgConfig
    $changed = $false
    if ($c -and $c.host -ne '0.0.0.0') {
        $c.host = '0.0.0.0'
        [IO.File]::WriteAllText($TgCfg, ($c | ConvertTo-Json -Depth 5), $utf8)
        $changed = $true
    }
    try {
        if (-not (Get-NetFirewallRule -DisplayName 'Zapret Telegram proxy' -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName 'Zapret Telegram proxy' -Direction Inbound -Program $TgExe -Action Allow -Profile Any -RemoteAddress LocalSubnet | Out-Null
        }
    } catch { Write-Log ($_ | Out-String) }
    if ($changed -and (Get-TgProc)) {
        Get-TgProc | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 400
        Start-Process -FilePath $TgExe -WorkingDirectory $TgDir -WindowStyle Hidden | Out-Null
    }
}
function Draw-Qr($q) {
    $n = $q.GetLength(0)
    $sg = New-Object System.Windows.Media.StreamGeometry
    $ctx = $sg.Open()
    for ($r = 0; $r -lt $n; $r++) { for ($c = 0; $c -lt $n; $c++) {
        if ($q[$r, $c]) {
            $ctx.BeginFigure((New-Object System.Windows.Point ([double]$c), ([double]$r)), $true, $true)
            $ctx.LineTo((New-Object System.Windows.Point ([double]($c + 1.02)), ([double]$r)), $false, $false)
            $ctx.LineTo((New-Object System.Windows.Point ([double]($c + 1.02)), ([double]($r + 1.02))), $false, $false)
            $ctx.LineTo((New-Object System.Windows.Point ([double]$c), ([double]($r + 1.02))), $false, $false)
        }
    } }
    # уголки рамки, чтобы код не обрезался по краям
    $ctx.BeginFigure((New-Object System.Windows.Point 0, 0), $true, $true); $ctx.LineTo((New-Object System.Windows.Point 0.001, 0), $false, $false)
    $ctx.BeginFigure((New-Object System.Windows.Point ([double]$n), ([double]$n)), $true, $true); $ctx.LineTo((New-Object System.Windows.Point ([double]($n - 0.001)), ([double]$n)), $false, $false)
    $ctx.Close(); $sg.Freeze()
    $ui.QrPath.Data = $sg
    $ui.QrWait.Visibility = 'Collapsed'
    $ui.QrPath.Opacity = 0
    Anim-Double $ui.QrPath $OP 1 300 0
    Anim-Double $ui.QrBoxS $SX 1 450 0.88 -Easing (Back-Ease); Anim-Double $ui.QrBoxS $SY 1 450 0.88 -Easing (Back-Ease)
}
function Open-Qr {
    if (-not (Get-TgProc)) { Show-Toast 'Сначала включи Telegram-прокси' -Err; return }
    $ui.QrOverlay.Visibility = 'Visible'
    Anim-Double $ui.QrDim $OP 1 250 0; Anim-Double $ui.QrPanel $OP 1 280 0
    Anim-Double $ui.QrScale $SX 1 420 0.94 -Easing (Back-Ease); Anim-Double $ui.QrScale $SY 1 420 0.94 -Easing (Back-Ease)
    # код уже готов — показываем сразу
    if ($script:qrReady -and $script:qrLink) { return }
    $ui.QrPath.Data = $null; $ui.QrWait.Visibility = 'Visible'; $ui.QrAddr.Text = ''
    # всё тяжёлое (адрес в сети, брандмауэр, построение кода) — в фоне, чтобы анимация не тормозила
    $script:qrJob = [hashtable]::Synchronized(@{ Done = $false; Error = $null; Link = $null; Addr = $null; Matrix = $null })
    $rs = [runspacefactory]::CreateRunspace(); $rs.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    $qrSrc = "function New-QrMatrix {`n" + ${function:New-QrMatrix}.ToString() + "`n}`n"
    [void]$ps.AddScript("param(`$sync, `$cfgPath, `$exe, `$dir, `$needLan)`n" + $qrSrc + @'

try {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $ip = $null
    try { $cfg = Get-NetIPConfiguration -ErrorAction Stop | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } | Select-Object -First 1; if ($cfg) { $ip = @($cfg.IPv4Address)[0].IPAddress } } catch {}
    if (-not $ip) { try { $ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1).IPAddress } catch {} }
    if (-not $ip) { throw 'Не нашёл адрес компьютера в домашней сети' }
    $c = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($needLan) {
        if ($c.host -ne '0.0.0.0') {
            $c.host = '0.0.0.0'
            [IO.File]::WriteAllText($cfgPath, ($c | ConvertTo-Json -Depth 5), $utf8)
            Get-Process -Name 'TgWsProxy_windows' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 400
            Start-Process -FilePath $exe -WorkingDirectory $dir -WindowStyle Hidden | Out-Null
        }
        try {
            if (-not (Get-NetFirewallRule -DisplayName 'Zapret Telegram proxy' -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -DisplayName 'Zapret Telegram proxy' -Direction Inbound -Program $exe -Action Allow -Profile Any -RemoteAddress LocalSubnet | Out-Null
            }
        } catch {}
    }
    $port = if ($c.port) { $c.port } else { 1443 }
    $sync.Link = "https://t.me/proxy?server=$ip&port=$port&secret=dd$($c.secret)"
    $sync.Addr = "${ip}:$port"
    $sync.Matrix = New-QrMatrix $sync.Link
} catch { $sync.Error = $_.Exception.Message } finally { $sync.Done = $true }
'@).AddArgument($script:qrJob).AddArgument($TgCfg).AddArgument($TgExe).AddArgument($TgDir).AddArgument((-not $Settings.tgLanReady))
    $script:qrPs = $ps; [void]$ps.BeginInvoke()
    $script:qrPoll = New-Object System.Windows.Threading.DispatcherTimer
    $script:qrPoll.Interval = [TimeSpan]::FromMilliseconds(100)
    $script:qrPoll.Add_Tick({
        $j = $script:qrJob
        if (-not $j -or -not $j.Done) { return }
        $script:qrPoll.Stop(); $script:qrJob = $null
        try { $script:qrPs.Dispose() } catch {}
        if ($j.Error) { $ui.QrWait.Text = T $j.Error; Show-Toast $j.Error -Err; return }
        if (-not $Settings.tgLanReady) { $Settings.tgLanReady = $true; Save-Settings }
        $script:qrLink = $j.Link
        $ui.QrAddr.Text = $j.Addr
        Safe { Draw-Qr $j.Matrix }
        $script:qrReady = $true
    })
    $script:qrPoll.Start()
}
function Close-Qr {
    Anim-Double $ui.QrDim $OP 0 200; Anim-Double $ui.QrPanel $OP 0 200
    $script:qrCloseT = New-Object System.Windows.Threading.DispatcherTimer
    $script:qrCloseT.Interval = [TimeSpan]::FromMilliseconds(210)
    $script:qrCloseT.Add_Tick({ $script:qrCloseT.Stop(); $ui.QrOverlay.Visibility = 'Collapsed' })
    $script:qrCloseT.Start()
}
$ui.TgPhone.Add_Click({ Safe { Open-Qr } })
$ui.QrClose.Add_Click({ Close-Qr })
$ui.QrDim.Add_MouseLeftButtonUp({ Close-Qr })
$ui.QrCopy.Add_Click({ Safe { if ($script:qrLink) { [System.Windows.Clipboard]::SetText($script:qrLink); Show-Toast 'Ссылка скопирована — можно отправить её себе на телефон' } } })

# Подключён ли Telegram Desktop к прокси (есть соединение Telegram → 127.0.0.1:порт)
function Test-TgConnected {
    try {
        $c = Get-TgConfig; $port = if ($c.port) { [int]$c.port } else { 1443 }
        foreach ($x in @(Get-NetTCPConnection -RemotePort $port -State Established -ErrorAction Stop)) {
            $pn = (Get-Process -Id $x.OwningProcess -ErrorAction SilentlyContinue).ProcessName
            if ($pn -match '^(Telegram|AyuGram|Kotatogram|64Gram|Unigram)') { return $true }
        }
    } catch {}
    return $false
}
$script:tgConnShown = $null; $script:tgConnShownState = 0L
$script:tgIsConnected = $false
function Update-TgConnected {
    if ($script:tgState -ne 'on') { return }
    $now = [bool]$Mon.TgConn
    if ($null -ne $script:tgConnShown -and $now -eq $script:tgConnShown -and $script:tgConnShownState -eq $script:tgVisAt) { return }
    $script:tgConnShown = $now; $script:tgConnShownState = $script:tgVisAt
    $port = (Get-TgConfig).port; if (-not $port) { $port = 1443 }
    if ($now) {
        $ui.TgStatus.Text = T "Подключён к Telegram"
        Anim-Color $ui.TgLed.Fill '#3DDC97'
        if ($ui.TgConnect.Visibility -eq 'Visible') { Stop-Anim $ui.TgConnect $OP; Anim-Double $ui.TgConnect $OP 0 250; $script:tgHideT = New-Object System.Windows.Threading.DispatcherTimer; $script:tgHideT.Interval = [TimeSpan]::FromMilliseconds(260); $script:tgHideT.Add_Tick({ $script:tgHideT.Stop(); if ($script:tgIsConnected) { $ui.TgConnect.Visibility = 'Collapsed' } }); $script:tgHideT.Start() }
        if (-not $Settings.tgConnected) { $Settings.tgConnected = $true; Save-Settings }
    } else {
        $ui.TgStatus.Text = T "Работает · 127.0.0.1:$port"
        Anim-Color $ui.TgLed.Fill '#5ED3F3'
        if ($ui.TgConnect.Visibility -ne 'Visible') { $ui.TgConnect.Visibility = 'Visible'; Anim-Double $ui.TgConnect $OP 1 250 0 }
    }
    $script:tgIsConnected = $now
}

$ui.TgToggle.Add_Click({ Safe { if ($ui.TgToggle.IsChecked) { Start-Tg } else { Stop-Tg } } })
$ui.TgConnect.Add_Click({ Safe {
    $link = Get-TgLink
    if (-not $link) { Show-Toast 'Сначала включи Telegram-прокси' -Err; return }
    Stop-Anim $ui.TgConnect $OP; $ui.TgConnect.Opacity = 1
    $Settings.tgConnected = $true; Save-Settings
    try { Start-Process $link } catch { Set-Clipboard -Value $link; Show-Toast 'Telegram не открылся. Ссылка скопирована — отправь её себе в «Избранное» и нажми.' -Err; return }
    Show-Toast 'Открываю Telegram — нажми там «Подключить прокси».'
} })

# ======================= Обновления приложения =======================
$script:appUpd = $null
function Start-AppUpdateCheck([switch]$Manual) {
    $script:appTimer.Start()
    $script:appCheckManual = [bool]$Manual
    $script:appChk = [hashtable]::Synchronized(@{ Done = $false; Tag = $null; Url = $null; Error = $null })
    $rs = [runspacefactory]::CreateRunspace(); $rs.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    [void]$ps.AddScript({
        param($sync, $repo)
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $wc = New-Object Net.WebClient; $wc.Headers.Add('User-Agent', 'ZapretGUI')
            try {
                $j = $wc.DownloadString("https://api.github.com/repos/$repo/releases/latest") | ConvertFrom-Json
                $a = $j.assets | Where-Object { $_.name -like '*.exe' } | Select-Object -First 1
                $sync.Tag = $j.tag_name; if ($a) { $sync.Url = $a.browser_download_url }
            } catch {
                $r = [Net.HttpWebRequest]::Create("https://github.com/$repo/releases/latest"); $r.UserAgent = 'ZapretGUI'; $r.AllowAutoRedirect = $false
                $resp = $r.GetResponse(); $loc = $resp.Headers['Location']; $resp.Close()
                if ($loc -match '/tag/([^/?#]+)') { $sync.Tag = $matches[1]; $sync.Url = "https://github.com/$repo/releases/download/$($sync.Tag)/ZapretSetup.exe" }
                else { throw 'релизов пока нет' }
            }
        } catch { $sync.Error = $_.Exception.Message } finally { $sync.Done = $true }
    }).AddArgument($script:appChk).AddArgument($UpdateRepo)
    $script:appChkPs = $ps; [void]$ps.BeginInvoke()
}
function Parse-Ver([string]$s) { $s = "$s".Trim() -replace '^[vV]', ''; try { [version]$s } catch { [version]'0.0' } }
function Show-AppUpdate($tag, $url) {
    $script:appUpd = @{ Tag = $tag; Url = $url }
    $ui.AppUpdText.Text = T $("Доступна новая версия приложения $tag")
    $ui.AppUpdBtn.Content = T $('Обновить'); $ui.AppUpdBtn.IsEnabled = $true; $ui.AppUpdTrack.Visibility = 'Collapsed'
    $ui.AppUpdBar.Visibility = 'Visible'
    Anim-Double $ui.AppUpdBar $OP 1 300 0
    Anim-Double $ui.AppUpdShift ([System.Windows.Media.TranslateTransform]::YProperty) 10 520 -30 -Easing (Back-Ease)
}
function Hide-AppUpdate {
    Anim-Double $ui.AppUpdBar $OP 0 200
    Anim-Double $ui.AppUpdShift ([System.Windows.Media.TranslateTransform]::YProperty) -30 250
    $script:appHideT = New-Object System.Windows.Threading.DispatcherTimer
    $script:appHideT.Interval = [TimeSpan]::FromMilliseconds(260)
    $script:appHideT.Add_Tick({ $script:appHideT.Stop(); $ui.AppUpdBar.Visibility = 'Collapsed' })
    $script:appHideT.Start()
}
function Start-AppDownload {
    if (-not $script:appUpd -or -not $script:appUpd.Url) { return }
    $script:appTimer.Start()
    $ui.AppUpdBtn.IsEnabled = $false; $ui.AppUpdBtn.Content = T $('Скачиваю…')
    $ui.AppUpdTrack.Visibility = 'Visible'; $ui.AppUpdFillS.ScaleX = 0
    $script:appDl = [hashtable]::Synchronized(@{ Done = $false; Got = 0; Total = 0; Error = $null; File = (Join-Path $env:TEMP "ZapretSetup-$($script:appUpd.Tag).exe") })
    $rs = [runspacefactory]::CreateRunspace(); $rs.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    [void]$ps.AddScript({
        param($sync, $url)
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $req = [Net.HttpWebRequest]::Create($url); $req.UserAgent = 'ZapretGUI'; $req.AllowAutoRedirect = $true
            $resp = $req.GetResponse(); $sync.Total = $resp.ContentLength
            $in = $resp.GetResponseStream(); $fs = [IO.File]::Create($sync.File); $buf = New-Object byte[] 65536
            while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) { $fs.Write($buf, 0, $n); $sync.Got += $n }
            $fs.Close(); $in.Close(); $resp.Close()
        } catch { $sync.Error = $_.Exception.Message } finally { $sync.Done = $true }
    }).AddArgument($script:appDl).AddArgument($script:appUpd.Url)
    $script:appDlPs = $ps; [void]$ps.BeginInvoke()
}
$script:appTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:appTimer.Interval = [TimeSpan]::FromMilliseconds(400)
$script:appTimer.Add_Tick({
    try {
        if ($script:appChk -and $script:appChk.Done) {
            $c = $script:appChk; $script:appChk = $null
            if ($c.Error -or -not $c.Tag) {
                if ($script:appCheckManual) { Show-Toast "Не удалось проверить обновления приложения: $($c.Error)" -Err }
            } elseif ((Parse-Ver $c.Tag) -gt (Parse-Ver $AppVersion)) {
                Show-AppUpdate $c.Tag $c.Url
            } elseif ($script:appCheckManual) { Show-Toast "У тебя последняя версия приложения (v$AppVersion)" }
        }
        if ($script:appDl) {
            $d = $script:appDl
            if ($d.Total -gt 0) { Anim-Double $ui.AppUpdFillS $SX ([Math]::Min(1, $d.Got / $d.Total)) 250; $ui.AppUpdBtn.Content = T $("$([int](100 * $d.Got / $d.Total))%") }
            if ($d.Done) {
                $script:appDl = $null
                if ($d.Error) { Show-Toast "Не удалось скачать обновление: $($d.Error)" -Err; $ui.AppUpdBtn.Content = T $('Повторить'); $ui.AppUpdBtn.IsEnabled = $true }
                else {
                    $ui.AppUpdBtn.Content = T $('Устанавливаю…')
                    Start-Process -FilePath $d.File -ArgumentList '-Auto'
                    $script:appCloseT = New-Object System.Windows.Threading.DispatcherTimer
                    $script:appCloseT.Interval = [TimeSpan]::FromMilliseconds(800)
                    $script:appCloseT.Add_Tick({ $script:appCloseT.Stop(); $win.Close() })
                    $script:appCloseT.Start()
                }
            }
        }
    } catch { Write-Log ($_ | Out-String) }
    # таймер нужен только пока идёт проверка или скачивание обновления
    if (-not $script:appChk -and -not $script:appDl) { $script:appTimer.Stop() }
})
$ui.AppUpdBtn.Add_Click({ Safe { Start-AppDownload } })
$ui.AppUpdLater.Add_Click({ Hide-AppUpdate })
$ui.AppCheckBtn.Add_Click({ Safe { Close-Settings; Show-Toast 'Проверяю обновления приложения…'; Start-AppUpdateCheck -Manual } })

# ======================= Обновления =======================
$script:latest = $null
$ui.UpdateBtn.Add_Click({ Safe {
    if ($script:latest -and $script:latest -ne $LocalVersion) {
        Start-Process 'https://github.com/Flowseal/zapret-discord-youtube/releases/latest'
    } else { Start-UpdateCheck }
} })
function Start-UpdateCheck {
    $ui.UpdText.Text = T $('Проверяю…')
    $ui.UpdFillS.ScaleX = 0; $ui.UpdBar.Opacity = 1
    Anim-Double $ui.UpdTrack $OP 1 200
    Anim-Double $ui.UpdBarT ([System.Windows.Media.TranslateTransform]::XProperty) 170 1100 -40 -Forever -Easing (Sine-Ease)
    $script:updJob = [hashtable]::Synchronized(@{ Done = $false; V = '' })
    $rs = [runspacefactory]::CreateRunspace(); $rs.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    [void]$ps.AddScript({
        param($sync)
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $wc = New-Object Net.WebClient; $wc.Headers.Add('Cache-Control', 'no-cache'); $wc.Headers.Add('User-Agent', 'ZapretGUI')
            $sync.V = $wc.DownloadString('https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt').Trim()
        } catch { $sync.V = '' } finally { $sync.Done = $true }
    }).AddArgument($script:updJob)
    $script:updPs = $ps; [void]$ps.BeginInvoke()
}

# ======================= Таймер =======================
$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(1000)
$timer.Add_Tick({
    try { Refresh-State } catch { Write-Log ($_ | Out-String) }
    if ($script:updJob -and $script:updJob.Done) {
        $v = "$($script:updJob.V)".Trim(); $script:updJob = $null
        Stop-Anim $ui.UpdBarT ([System.Windows.Media.TranslateTransform]::XProperty); $ui.UpdBar.Opacity = 0
        $ui.UpdFill.Fill = Brush $(if ($v) { '#3DDC97' } else { '#FF5E62' })
        Anim-Double $ui.UpdFillS $SX 1 350 0
        Anim-Double $ui.UpdTrack $OP 0 400 -beginMs 900
        if (-not $v) { $ui.UpdText.Text = T $('Нет связи') }
        elseif ($v -eq $LocalVersion) { $script:latest = $v; $ui.UpdText.Text = T $('Версия актуальна ✓') }
        else {
            $script:latest = $v
            $ui.UpdText.Text = T $("Скачать $v")
            $ui.UpdateBtn.Style = $win.FindResource('AccentBtn')
            Show-Toast "Вышла новая версия zapret: $v. Распакуй её в папку Zapret и нажми ⚙ → «Перенести настройки»."
        }
    }
})

$win.Add_SourceInitialized({
    try {
        $hwnd = (New-Object System.Windows.Interop.WindowInteropHelper $win).Handle
        $bat = if ($InHome) { Join-Path $HomeDir 'Zapret.bat' } else { Join-Path $GuiDir 'Zapret.bat' }
        $ico = Join-Path $GuiDir 'zapret.ico'
        [ZapretGuiShell.TaskbarId]::Apply($hwnd, 'Zapret.GUI', 'Zapret', $(if (Test-Path -LiteralPath $ico) { "$ico,0" } else { $null }), $(if (Test-Path -LiteralPath $bat) { "`"$bat`"" } else { $null }))
    } catch { Write-Log ($_ | Out-String) }
})
$win.Add_Loaded({
    if ($script:Lang -eq 'en') {
        $win.Title = 'Zapret'
        Translate-Tree $win
        $script:trTimer = New-Object System.Windows.Threading.DispatcherTimer
        $script:trTimer.Interval = [TimeSpan]::FromMilliseconds(2000)
        $script:trTimer.Add_Tick({
            if (-not $win.IsActive -or $win.WindowState -eq 'Minimized') { return }
            Translate-Tree $win
            # всплывающие списки и подсказки живут в своих окнах
            foreach ($pp in @($ui.StrategyBox, $ui.GameBox, $ui.IpsetBox, $ui.ThemeBox, $ui.LangBox, $ui.MigFromBox, $ui.MigToBox)) { if ($pp -and $pp.IsDropDownOpen) { foreach ($it in $pp.Items) { Translate-Tree $it } } }
        })
        $script:trTimer.Start()
    }
    Anim-Double $ui.Shell $OP 1 380 0
    Anim-Double $ui.ShellShift ([System.Windows.Media.TranslateTransform]::YProperty) 0 450 18
    # первый замер — сразу, дальше всё делает фоновый монитор
    try {
        $Mon.Svc = Get-ServiceState
        if ($Mon.Svc -eq 'Running') { try { $Mon.SvcName = (Get-ItemProperty 'HKLM:\System\CurrentControlSet\Services\zapret' -ErrorAction Stop).'zapret-discord-youtube' } catch {} }
        $r0 = Get-RunningInfo; $Mon.Winws = [bool]$r0; if ($r0) { $Mon.WinwsName = $r0.Name }
        $Mon.Tg = [bool](Get-TgProc)
        $Mon.At = [DateTime]::UtcNow.Ticks
    } catch {}
    Refresh-State
    Start-Monitor
    $timer.Start()
    if ($AutoStart -and $script:state -eq 'off') { Start-Zapret }
    if (Get-TgProc) { $ui.TgToggle.IsChecked = $true; $p_ = (Get-TgConfig).port; if (-not $p_) { $p_ = 1443 }; Set-TgVisual 'on' "Работает · 127.0.0.1:$p_" }
    elseif ($Settings.tgEnabled -and (Test-Path -LiteralPath $TgExe)) { $ui.TgToggle.IsChecked = $true; Start-Tg }
    $script:tgTimer.Start()
    if ($Settings.autoStartOnce) { $Settings.autoStartOnce = $false; Save-Settings; if ($script:state -eq 'off') { Start-Zapret } }
    if (Test-Path -LiteralPath $UpdFile) { Start-AppUpdateCheck }
    if (Test-Path -LiteralPath $UpdFile) { Start-UpdateCheck } else { $ui.UpdText.Text = T $('Обновления') }
    if ($AllZapret.Count -gt 1) {
        Show-Toast "Использую $($Z.Name). Найдено папок zapret: $($AllZapret.Count)."
    }
})

if (-not $InHome -and $IsAdmin -and -not $Settings.noConsolidate) {
    $q = [System.Windows.MessageBox]::Show("Собрать всё в одну папку?`n`nzapret, это приложение и перенос настроек переедут в:`n$HomeDir`n`nНа рабочем столе появится ярлык Zapret. Если zapret сейчас работает, он перезапустится.", 'Zapret', 'YesNo', 'Question')
    if ($q -eq 'Yes') {
        try { Invoke-Consolidate } catch {
            Write-Log ($_ | Out-String)
            [System.Windows.MessageBox]::Show("Не получилось перенести: $($_.Exception.Message)`nВсё осталось на старых местах.", 'Zapret', 'OK', 'Warning') | Out-Null
        }
    } else { $Settings.noConsolidate = $true; Save-Settings }
}
if ($script:consolidated) { exit 0 }
if (-not $InOwnHost -and $HostReady -and (Test-Path -LiteralPath $HostExe) -and $InHome) {
    # первый запуск после сборки: сразу открываемся уже как Zapret.exe
    Release-Instance
    if ($AutoStart) { Start-Process -FilePath $HostExe -ArgumentList '-AutoStart' } else { Start-Process -FilePath $HostExe }
    exit 0
}


# ======================= Трей =======================
function Pause-Glow {
    foreach ($g in @($ui.Glow1, $ui.Glow2)) { Stop-Anim $g $OP; $g.Opacity = 0 }
    foreach ($s in @($ui.Glow1S, $ui.Glow2S)) { Stop-Anim $s $SX; Stop-Anim $s $SY }
    $script:glowPaused = $true
}
function Resume-Glow {
    if (-not $script:glowPaused) { return }
    $script:glowPaused = $false
    if ($script:state -ne 'on') { return }
    foreach ($g in @(@($ui.Glow1, $ui.Glow1S, 0), @($ui.Glow2, $ui.Glow2S, 1100))) {
        Anim-Double $g[0] $OP 0 2200 0.7 -Forever -beginMs $g[2] -NoEase -Low
        Anim-Double $g[1] $SX 1.38 2200 1 -Forever -beginMs $g[2] -Low
        Anim-Double $g[1] $SY 1.38 2200 1 -Forever -beginMs $g[2] -Low
    }
}
function Show-FromTray {
    $win.Show()
    if ($win.WindowState -eq 'Minimized') { $win.WindowState = 'Normal' }
    [void]$win.Activate(); $win.Topmost = $true; $win.Topmost = $false
    Resume-Glow
    $Mon.Wake = $true
}
function Hide-ToTray {
    Pause-Glow
    $win.Hide()
    if (-not $Settings.trayHintShown) {
        $Settings.trayHintShown = $true; Save-Settings
        try { $script:tray.ShowBalloonTip(4000, 'Zapret', (T 'Zapret работает в трее. Нажми на значок, чтобы открыть, правая кнопка — меню.'), [System.Windows.Forms.ToolTipIcon]::None) } catch {}
    }
}
$script:tray = New-Object System.Windows.Forms.NotifyIcon
try {
    $trayMs = New-Object System.IO.MemoryStream (, [IO.File]::ReadAllBytes($IconFile))
    $script:tray.Icon = New-Object System.Drawing.Icon($trayMs, [System.Windows.Forms.SystemInformation]::SmallIconSize)
} catch { $script:tray.Icon = [System.Drawing.SystemIcons]::Application }
$script:tray.Text = 'Zapret'
$trayMenu = New-Object System.Windows.Forms.ContextMenuStrip
$trayMenu.Font = New-Object System.Drawing.Font('Segoe UI', 9.5)
$trayMenu.ShowImageMargin = $false
$miOpen = New-Object System.Windows.Forms.ToolStripMenuItem (T 'Открыть Zapret')
$miOpen.Font = New-Object System.Drawing.Font('Segoe UI', 9.5, [System.Drawing.FontStyle]::Bold)
$miZ = New-Object System.Windows.Forms.ToolStripMenuItem 'zapret'
$miTg = New-Object System.Windows.Forms.ToolStripMenuItem 'Telegram'
$miExit = New-Object System.Windows.Forms.ToolStripMenuItem (T 'Выйти из Zapret')
[void]$trayMenu.Items.Add($miOpen)
[void]$trayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$trayMenu.Items.Add($miZ)
[void]$trayMenu.Items.Add($miTg)
[void]$trayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$trayMenu.Items.Add($miExit)
$trayMenu.Add_Opening({
    try {
        $miZ.Text = T $(if ($script:state -eq 'on') { 'Выключить zapret' } else { 'Включить zapret' })
        $miZ.Enabled = ($script:state -eq 'on' -or $script:state -eq 'off')
        $miTg.Text = T $(if ($ui.TgToggle.IsChecked) { 'Выключить Telegram-прокси' } else { 'Включить Telegram-прокси' })
        $miTg.Enabled = [bool]$ui.TgToggle.IsEnabled
    } catch {}
})
$miOpen.Add_Click({ Safe { Show-FromTray } })
$miZ.Add_Click({ Safe {
    switch ($script:state) { 'on' { Stop-Zapret; Hide-Apply } 'off' { Start-Zapret } }
} })
$miTg.Add_Click({ Safe {
    $ui.TgToggle.IsChecked = -not [bool]$ui.TgToggle.IsChecked
    if ($ui.TgToggle.IsChecked) { Start-Tg } else { Stop-Tg }
} })
$miExit.Add_Click({ $win.Close() })
$script:tray.ContextMenuStrip = $trayMenu
$script:tray.Add_MouseClick({ param($s, $e) if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) { Safe { Show-FromTray } } })
$script:tray.Visible = $true
# повторный запуск Zapret — показываем это окно (проверка раз в секунду, ничего не стоит)
$script:showT = New-Object System.Windows.Threading.DispatcherTimer
$script:showT.Interval = [TimeSpan]::FromSeconds(1)
$script:showT.Add_Tick({ try { if ($script:showEvent.WaitOne(0)) { Show-FromTray } } catch {} })
$script:showT.Start()

# окно свёрнуто или в фоне — почти ничего не делаем
function Set-AppIdle([bool]$idle) {
    $Mon.Idle = $idle
    if ($win.WindowState -eq 'Minimized') { $timer.Stop(); $script:tgTimer.Stop(); if ($script:trTimer) { $script:trTimer.Stop() }; Pause-Glow }
    else {
        if (-not $timer.IsEnabled) { $timer.Start() }
        if (-not $script:tgTimer.IsEnabled) { $script:tgTimer.Start() }
        if ($script:trTimer -and -not $script:trTimer.IsEnabled) { $script:trTimer.Start(); Translate-Tree $win }
        if (-not $idle) { $Mon.Wake = $true; if ($win.IsVisible) { Resume-Glow } }   # вернулись в окно — сразу свежие данные
    }
}
$win.Add_Activated({ try { Set-AppIdle $false } catch {} })
$win.Add_Deactivated({ try { Set-AppIdle $true } catch {} })
$win.Add_StateChanged({ try { Set-AppIdle ($win.WindowState -eq 'Minimized' -or -not $win.IsActive) } catch {} })
$script:mainFrame = New-Object System.Windows.Threading.DispatcherFrame
$win.Add_Closed({ $script:mainFrame.Continue = $false })
$win.Show()
[System.Windows.Threading.Dispatcher]::PushFrame($script:mainFrame)
try { $script:tray.Visible = $false; $script:tray.Dispose() } catch {}
Release-Instance
$Mon.Stop = $true
$timer.Stop()
}
catch {
    Write-Log ($_ | Out-String)
    try { [System.Windows.MessageBox]::Show("Ошибка запуска:`n$($_.Exception.Message)`n`nПодробности в gui-error.log", 'Zapret', 'OK', 'Error') | Out-Null } catch {}
}
