# Zapret GUI - графическая оболочка для zapret-discord-youtube
# Запуск: Zapret.bat (сам попросит права администратора)

param([switch]$AutoStart)
$ErrorActionPreference = 'Stop'
# ---- версия приложения и где искать обновления ----
$AppVersion = '1.0.0'
$UpdateRepo = 'COFFIOK/zapret-gui'   # GitHub: владелец/репозиторий, в Releases лежит ZapretSetup.exe
$GuiDir = $PSScriptRoot
$LogFile = Join-Path $GuiDir 'gui-error.log'
$SettingsFile = Join-Path $GuiDir 'gui-settings.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Write-Log($msg) {
    try { Add-Content -LiteralPath $LogFile -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg) -Encoding UTF8 } catch {}
}

try {
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms

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
function Save-Settings { try { [IO.File]::WriteAllText($SettingsFile, ($Settings | ConvertTo-Json), $utf8) } catch {} }

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
    Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File `"$(Join-Path $target 'app\zapret-gui.ps1')`" $flag" -WindowStyle Hidden
    $cleanup = "timeout /t 3 /nobreak >nul & rmdir /s /q `"$GuiDir`""
    if (Test-Path -LiteralPath $oldMig) { $cleanup += " & rmdir /s /q `"$oldMig`"" }
    Start-Process cmd.exe -ArgumentList "/c $cleanup" -WindowStyle Hidden
    $script:consolidated = $true
}


# ======================= Запуск без запроса прав =======================
# Задание планировщика с наивысшими правами: ярлык запускает его, и Windows больше не спрашивает разрешение
$TaskName = 'ZapretGUI'
$LauncherBat = "@echo off`r`nschtasks /run /tn `"$TaskName`" >nul 2>&1 && exit /b`r`npowershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command `"Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList '-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File \`"%~dp0app\zapret-gui.ps1\`"'`"`r`n"
function Get-ElevTask { try { Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop } catch { $null } }
function Enable-NoPrompt {
    $ps1 = Join-Path $GuiDir 'zapret-gui.ps1'
    $arg = "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File `"$ps1`""
    $ex = Get-ElevTask
    if (-not ($ex -and $ex.Actions[0].Arguments -eq $arg)) {
        $act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg -WorkingDirectory $GuiDir
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
[xml]$xaml = @'
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
    <Border x:Name="Shell" CornerRadius="20" Background="#121419" BorderBrush="#252933" BorderThickness="1" Opacity="0">
      <Border.Effect><DropShadowEffect BlurRadius="24" ShadowDepth="0" Opacity="0.55" Color="#000000"/></Border.Effect>
      <Border.RenderTransform><TranslateTransform x:Name="ShellShift" Y="18"/></Border.RenderTransform>
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
            <Button x:Name="MinBtn" Style="{StaticResource TitleBtn}"><TextBlock Text="—" FontSize="12"/></Button>
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
                  <TextBlock x:Name="SitesCount" DockPanel.Dock="Right" Foreground="{StaticResource Muted}" FontSize="12" VerticalAlignment="Center"/>
                  <TextBlock Text="Мои сайты" FontSize="16" FontWeight="SemiBold"/>
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
          <Border x:Name="SettingsPanel" HorizontalAlignment="Right" Width="400" Background="#171A21" CornerRadius="0,20,20,0"
                  BorderBrush="#2A2E38" BorderThickness="1,0,0,0">
            <Border.RenderTransform><TranslateTransform x:Name="SettingsShift" X="420"/></Border.RenderTransform>
            <Border.Effect><DropShadowEffect BlurRadius="30" ShadowDepth="0" Opacity="0.6"/></Border.Effect>
            <Grid Margin="26,18,26,22">
              <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
              <DockPanel Margin="0,0,0,22">
                <Button x:Name="SettingsClose" DockPanel.Dock="Right" Style="{StaticResource TitleBtn}"><TextBlock Text="✕" FontSize="13"/></Button>
                <TextBlock Text="Настройки" FontSize="20" FontWeight="SemiBold" VerticalAlignment="Center"/>
              </DockPanel>
              <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto" Margin="0,0,-10,0">
              <StackPanel x:Name="SettingsRows" Margin="0,0,10,0">
                <Grid Margin="0,0,0,14">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="170"/></Grid.ColumnDefinitions>
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
                <Grid Margin="0,0,0,14">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="170"/></Grid.ColumnDefinitions>
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
                <Grid Margin="0,0,0,14">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                  <StackPanel VerticalAlignment="Center">
                    <TextBlock Text="Показывать окно winws" FontSize="13"/>
                    <TextBlock Text="чёрное окно с логом; обычно не нужно" Foreground="{StaticResource Muted}" FontSize="11"/>
                  </StackPanel>
                  <CheckBox x:Name="WinToggle" Grid.Column="1" Style="{StaticResource Toggle}"/>
                </Grid>
                <Grid Margin="0,0,0,14">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                  <StackPanel VerticalAlignment="Center">
                    <TextBlock Text="Запускать без запроса прав" FontSize="13"/>
                    <TextBlock Text="Windows не будет спрашивать разрешение при запуске" Foreground="{StaticResource Muted}" FontSize="11"/>
                  </StackPanel>
                  <CheckBox x:Name="NoPromptToggle" Grid.Column="1" Style="{StaticResource Toggle}"/>
                </Grid>
                <Grid>
                  <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                  <TextBlock Text="Проверять обновления при запуске" FontSize="13" VerticalAlignment="Center"/>
                  <CheckBox x:Name="UpdToggle" Grid.Column="1" Style="{StaticResource Toggle}"/>
                </Grid>
                <StackPanel Margin="0,18,0,0">
                  <TextBlock Text="ИНСТРУМЕНТЫ" FontSize="11" FontWeight="SemiBold" Foreground="#5A6070" Margin="2,0,0,8"/>
                  <Button x:Name="ServiceBtn" Style="{StaticResource Btn}" Margin="0,0,0,6" HorizontalContentAlignment="Left" Content="Сервис и диагностика"/>
                  <Button x:Name="FolderBtn" Style="{StaticResource Btn}" Margin="0,0,0,6" Content="Открыть папку zapret"/>
                  <Button x:Name="MigrateBtn" Style="{StaticResource Btn}" Content="Перенести настройки из старой версии" Margin="0,0,0,6"/>
                  <Button x:Name="AppCheckBtn" Style="{StaticResource Btn}" Content="Проверить обновления приложения" Margin="0,0,0,2"/>
                </StackPanel>
              </StackPanel>
              </ScrollViewer>
              <Border Grid.Row="2" Background="#1E2129" CornerRadius="12" Padding="14,12" Margin="0,14,0,0">
                <StackPanel>
                  <TextBlock Text="Папка zapret" Foreground="{StaticResource Muted}" FontSize="11"/>
                  <TextBlock x:Name="InfoPath" FontSize="12" TextWrapping="Wrap" Margin="0,2,0,8"/>
                  <TextBlock Text="Версия" Foreground="{StaticResource Muted}" FontSize="11"/>
                  <TextBlock x:Name="InfoVer" FontSize="12" Margin="0,2,0,8"/>
                  <TextBlock Text="Приложение" Foreground="{StaticResource Muted}" FontSize="11"/>
                  <TextBlock x:Name="InfoApp" FontSize="12" Margin="0,2,0,0"/>
                  <Border Height="1" Background="#2A2E38" Margin="0,10,0,10"/>
                  <DockPanel>
                    <TextBlock DockPanel.Dock="Right" Text="zapret: Flowseal · bol-van" Foreground="#5A6070" FontSize="11" VerticalAlignment="Center"/>
                    <TextBlock FontSize="12.5" VerticalAlignment="Center">
                      <Run Text="by " Foreground="#8A90A0"/><Run Text="COFFIOK" Foreground="#3DDC97" FontWeight="Bold"/>
                    </TextBlock>
                  </DockPanel>
                </StackPanel>
              </Border>
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
</Window>
'@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$win = [Windows.Markup.XamlReader]::Load($reader)
$ui = @{}
$xaml.SelectNodes('//*[@*[local-name()="Name"]]') | ForEach-Object {
    $n = $_.GetAttribute('Name', 'http://schemas.microsoft.com/winfx/2006/xaml')
    if ($n) { $ui[$n] = $win.FindName($n) }
}

# ======================= Анимации =======================
function Ease { $e = New-Object System.Windows.Media.Animation.CubicEase; $e.EasingMode = 'EaseOut'; $e }
function Anim-Double($target, $prop, $to, $ms = 250, $from = $null, [switch]$Forever, $beginMs = 0, [switch]$NoEase, [switch]$AutoReverse, $Easing = $null) {
    $a = New-Object System.Windows.Media.Animation.DoubleAnimation
    $a.To = $to
    if ($null -ne $from) { $a.From = $from }
    $a.Duration = New-Object System.Windows.Duration ([TimeSpan]::FromMilliseconds($ms))
    if ($beginMs) { $a.BeginTime = [TimeSpan]::FromMilliseconds($beginMs) }
    if ($Forever) { $a.RepeatBehavior = [System.Windows.Media.Animation.RepeatBehavior]::Forever }
    if ($AutoReverse) { $a.AutoReverse = $true }
    if ($Easing) { $a.EasingFunction = $Easing } elseif (-not $NoEase) { $a.EasingFunction = Ease }
    $target.BeginAnimation($prop, $a)
}
function Anim-Color($brush, $hex, $ms = 350) {
    $a = New-Object System.Windows.Media.Animation.ColorAnimation
    $a.To = [System.Windows.Media.ColorConverter]::ConvertFromString($hex)
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
    $ui.ToastText.Text = $text
    $ui.ToastText.Foreground = if ($Err) { [System.Windows.Media.Brushes]::Salmon } else { [System.Windows.Media.Brushes]::WhiteSmoke }
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
    switch ($mode) {
        'on' {
            $ui.StatusText.Text = 'Работает'
            $ui.SubText.Text = $detail
            if ($prev -ne 'on') {
                Set-ZColor '#0F1115'
                Show-Z
                Anim-Color $ui.PowerFill '#3DDC97'
                Anim-Color $ui.IconBrush '#0F1115'; Anim-Color $ui.IconBrush2 '#0F1115'
                $ui.TitleDot.Fill = $win.FindResource('Accent')
                Anim-Double $ui.PowerShadow ([System.Windows.Media.Effects.DropShadowEffect]::BlurRadiusProperty) 40 500
                Stop-Anim $ui.Spinner $OP; $ui.Spinner.Opacity = 0
                foreach ($g in @(@($ui.Glow1, $ui.Glow1S, 0), @($ui.Glow2, $ui.Glow2S, 1100))) {
                    Anim-Double $g[0] $OP 0 2200 0.7 -Forever -beginMs $g[2] -NoEase
                    Anim-Double $g[1] $SX 1.38 2200 1 -Forever -beginMs $g[2]
                    Anim-Double $g[1] $SY 1.38 2200 1 -Forever -beginMs $g[2]
                }
            }
        }
        'off' {
            $ui.StatusText.Text = 'Выключен'
            Hide-Z
            $ui.SubText.Text = if ($detail) { $detail } else { 'Нажми, чтобы включить' }
            Anim-Color $ui.PowerFill '#262A33'
            Anim-Color $ui.IconBrush '#8A90A0'; Anim-Color $ui.IconBrush2 '#8A90A0'
            $ui.TitleDot.Fill = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString('#5A6070'))
            Anim-Double $ui.PowerShadow ([System.Windows.Media.Effects.DropShadowEffect]::BlurRadiusProperty) 0 400
            foreach ($g in @($ui.Glow1, $ui.Glow2)) { Stop-Anim $g $OP; $g.Opacity = 0 }
            Stop-Anim $ui.Spinner $OP; $ui.Spinner.Opacity = 0; Stop-Anim $ui.SpinRot ([System.Windows.Media.RotateTransform]::AngleProperty)
        }
        { $_ -in 'starting', 'stopping' } {
            $ui.StatusText.Text = if ($mode -eq 'starting') { 'Запуск…' } else { 'Остановка…' }
            if ($mode -eq 'starting') { Set-ZColor '#3DDC97' }
            Show-Z -Spin
            $ui.SubText.Text = $detail
            Anim-Double $ui.Spinner $OP 1 200
            Anim-Double $ui.SpinRot ([System.Windows.Media.RotateTransform]::AngleProperty) 360 900 0 -Forever -NoEase
        }
    }
}

function Refresh-State {
    if ($script:testing) { return }
    if ($script:state -eq 'starting' -or $script:state -eq 'stopping') {
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
    $svc = Get-ServiceState
    $r = Get-RunningInfo
    if ($svc -eq 'Running') {
        $n = $null
        try { $n = (Get-ItemProperty 'HKLM:\System\CurrentControlSet\Services\zapret' -ErrorAction Stop).'zapret-discord-youtube' } catch {}
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
function Brush($hex) { New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex)) }

function New-SectionLabel($text) {
    $t = New-Object System.Windows.Controls.TextBlock
    $t.Text = $text; $t.FontSize = 11; $t.FontWeight = 'SemiBold'; $t.Foreground = Brush '#5A6070'
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
    $glow.Color = [System.Windows.Media.ColorConverter]::ConvertFromString($color); $glow.BlurRadius = 10; $glow.ShadowDepth = 0; $glow.Opacity = 0.9
    $dot.Effect = $glow
    $nm = New-Object System.Windows.Controls.TextBlock
    $nm.Text = $name; $nm.FontSize = 13.5; $nm.FontWeight = 'SemiBold'; $nm.Foreground = Brush '#E6E8EE'; $nm.VerticalAlignment = 'Center'
    $nm.TextTrimming = 'CharacterEllipsis'
    [System.Windows.Controls.Grid]::SetColumn($nm, 1)
    $cnt = New-Object System.Windows.Controls.TextBlock
    $cnt.Text = Plural $domains.Count; $cnt.FontSize = 12; $cnt.Foreground = Brush '#8A90A0'; $cnt.VerticalAlignment = 'Center'; $cnt.Margin = '8,0,8,0'
    [System.Windows.Controls.Grid]::SetColumn($cnt, 2)
    $dots = New-Object System.Windows.Controls.Border
    $dots.Width = 30; $dots.Height = 24; $dots.CornerRadius = 7
    $dt = New-Object System.Windows.Controls.TextBlock
    $dt.Text = '⋯'; $dt.FontSize = 16; $dt.Foreground = Brush '#8A90A0'; $dt.HorizontalAlignment = 'Center'; $dt.VerticalAlignment = 'Center'; $dt.Margin = '0,-3,0,0'
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
        $tx.Text = $d; $tx.FontSize = 12.5; $tx.Foreground = Brush '#B8BDC9'; $tx.VerticalAlignment = 'Center'; $tx.Margin = '6,3,0,3'
        $tx.TextTrimming = 'CharacterEllipsis'
        [void]$row.Children.Add($tx)
        [void]$body.Children.Add($row)
    }
    if (-not $editable) {
        $ro = New-Object System.Windows.Controls.TextBlock
        $ro.Text = 'встроенный список zapret · только просмотр'; $ro.FontSize = 11; $ro.Foreground = Brush '#5A6070'; $ro.Margin = '6,6,0,2'
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
        $add.Style = $win.FindResource('AccentBtn'); $add.Content = '+'; $add.Padding = '12,4'; $add.Margin = '6,0,0,0'
        $add.Tag = @{ Group = $name; Box = $tb }
        $add.Add_Click($script:onGroupAdd)
        [System.Windows.Controls.Grid]::SetColumn($add, 1)
        [void]$foot.Children.Add($bx); [void]$foot.Children.Add($add)
        [void]$body.Children.Add($foot)

        $rm = New-Object System.Windows.Controls.TextBlock
        $rm.Text = 'Удалить группу'; $rm.FontSize = 11.5; $rm.Foreground = Brush '#C9606F'; $rm.Cursor = 'Hand'; $rm.Margin = '2,8,0,0'
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
        $r = [System.Windows.MessageBox]::Show("Удалить группу «$($t.Group)» ($(Plural $t.Count))?", 'Zapret', 'YesNo', 'Question')
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
    $ui.SitesCount.Text = Plural $total
    [void]$panel.Children.Add((New-SectionLabel 'МОИ'))
    if ($ug.Count -eq 0) {
        $e = New-Object System.Windows.Controls.TextBlock
        $e.Text = 'Пока пусто. Добавь сайт, который не открывается.'; $e.Foreground = Brush '#5A6070'; $e.Margin = '4,2,0,8'; $e.TextWrapping = 'Wrap'
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
$ui.TitleVer.Text = "v$LocalVersion"

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
$ui.InfoPath.Text = $Root
$ui.InfoVer.Text = "zapret $LocalVersion"
$ui.InfoApp.Text = "Zapret GUI v$AppVersion"
if (-not $IsAdmin) { $ui.TitleVer.Text += '  ·  без прав администратора' }

$bats = Get-ChildItem -LiteralPath $Root -Filter '*.bat' | Where-Object { $_.Name -notlike 'service*' } |
    Sort-Object { [Regex]::Replace($_.Name, '(\d+)', { $args[0].Value.PadLeft(8, '0') }) }
foreach ($b in $bats) {
    $it = New-Object System.Windows.Controls.ComboBoxItem
    $it.Content = $b.BaseName
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
$ui.MinBtn.Add_Click({ $win.WindowState = 'Minimized' })
$ui.CloseBtn.Add_Click({
    Anim-Double $ui.Shell $OP 0 180
    $t = New-Object System.Windows.Threading.DispatcherTimer
    $t.Interval = [TimeSpan]::FromMilliseconds(190)
    $t.Add_Tick({ $win.Close() }); $t.Start()
})

# ---- Панель настроек ----
$RotP = [System.Windows.Media.RotateTransform]::AngleProperty
$TX = [System.Windows.Media.TranslateTransform]::XProperty
function Open-Settings {
    $ui.SettingsOverlay.Visibility = 'Visible'
    Anim-Double $ui.Dim $OP 1 260 0
    Anim-Double $ui.SettingsShift $TX 0 380 420
    Anim-Double $ui.GearRot $RotP 180 500
    $i = 0
    foreach ($ch in $ui.SettingsRows.Children) {
        $ch.Opacity = 0
        $ch.RenderTransform = New-Object System.Windows.Media.TranslateTransform
        Anim-Double $ch $OP 1 300 0 -beginMs (120 + $i * 60)
        Anim-Double $ch.RenderTransform $TX 0 340 24 -beginMs (120 + $i * 60)
        $i++
    }
}
function Close-Settings {
    Anim-Double $ui.Dim $OP 0 220
    Anim-Double $ui.SettingsShift $TX 420 280
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
    function Kill-Winws { Get-Process -Name winws -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }
    $check = {
        param($t, $to)
        $tokens = @(); $diag = @()
        if ($t.Url) {
            $tests = @(
                @{ Label = 'HTTP';   Args = @('--http1.1') },
                @{ Label = 'TLS1.2'; Args = @('--tlsv1.2', '--tls-max', '1.2') },
                @{ Label = 'TLS1.3'; Args = @('--tlsv1.3', '--tls-max', '1.3') }
            )
            $curlExe = Join-Path $env:SystemRoot 'System32\curl.exe'
            if (-not (Test-Path $curlExe)) { $curlExe = 'curl.exe' }
            $baseArgs = @("-I", "-s", "-m", "$to", "--connect-timeout", "$([Math]::Min(2, $to))", "-o", "NUL", "-w", "%{http_code}", "--show-error")
            foreach ($x in $tests) {
                try {
                    $err = ''
                    $curlArgs = $baseArgs + $x.Args + @($t.Url)
                    $out = & $curlExe @curlArgs 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $err += $_.Exception.Message + ' ' } else { $_ } }
                    $code = $LASTEXITCODE
                    if ($err -match 'Could not resolve host|certificate|self[- ]?signed') { $tokens += "$($x.Label):SSL"; $diag += "$($x.Label) exit=$code $err"; continue }
                    if (($code -eq 35) -or ($err -match 'not supported|unsupported|Unknown option|schannel')) { $tokens += "$($x.Label):UNSUP"; continue }
                    if ($code -eq 0) { $tokens += "$($x.Label):OK" } else { $tokens += "$($x.Label):ERR"; $diag += "$($x.Label) exit=$code $err" }
                } catch { $tokens += "$($x.Label):ERR"; $diag += "$($x.Label) exception: $($_.Exception.Message)" }
            }
        }
        $ping = 'n/a'
        if ($t.Ping) {
            try {
                $pg = New-Object System.Net.NetworkInformation.Ping
                $r = $pg.Send($t.Ping, 1000)
                $ping = if ($r.Status -eq 'Success') { "$($r.RoundtripTime) ms" } else { 'Timeout' }
                $pg.Dispose()
            } catch { $ping = 'Timeout' }
        }
        return @{ Name = $t.Name; Group = $t.Group; Tokens = $tokens; Ping = $ping; IsUrl = [bool]$t.Url; Diag = ($diag -join ' | ') }
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
            for ($i = 0; $i -lt 25; $i++) { if (Get-Process -Name winws -ErrorAction SilentlyContinue) { $up = $true; break }; Start-Sleep -Milliseconds 200 }
            if (-not $up) {
                [void]$sync.Results.Add(@{ Name = $s.Name; File = $s.File; Failed = $true; OK = 0; Total = 0; PingOK = 0; PingTotal = 0; Details = @() })
                $sync.Done++; continue
            }
            # первой стратегии нужно больше времени: драйвер WinDivert загружается
            if ($first) { Start-Sleep -Milliseconds 3500; $first = $false } else { Start-Sleep -Milliseconds 1500 }
            $sync.Phase = 'test'
            for ($attempt = 1; $attempt -le 2; $attempt++) {
                $pool = [runspacefactory]::CreateRunspacePool(1, 12); $pool.Open()
                $jobs = @()
                foreach ($t in $targets) {
                    $p = [powershell]::Create().AddScript($check).AddArgument($t).AddArgument($timeout)
                    $p.RunspacePool = $pool
                    $jobs += @{ P = $p; H = $p.BeginInvoke() }
                }
                $res = @()
                foreach ($j in $jobs) {
                    try {
                        if (-not $j.H.AsyncWaitHandle.WaitOne(($timeout * 3 + 5) * 1000)) { $j.P.Stop() }
                        $res += $j.P.EndInvoke($j.H)
                    } catch {}
                    $j.P.Dispose()
                }
                $pool.Close(); $pool.Dispose()
                $ok = 0; $total = 0; $pok = 0; $ptot = 0
                foreach ($r in $res) {
                    foreach ($tk in $r.Tokens) { if ($tk -notmatch 'UNSUP') { $total++ }; if ($tk -match ':OK$') { $ok++ } }
                    if ($r.Ping -ne 'n/a') { $ptot++; if ($r.Ping -ne 'Timeout') { $pok++ } }
                }
                # ни одного ответа - возможно, обход ещё не успел подняться; перепроверим один раз
                if ($ok -gt 0 -or $total -eq 0 -or $sync.Cancel) { break }
                $sync.Phase = 'retry'
                Start-Sleep -Milliseconds 2500
            }
            [void]$sync.Results.Add(@{ Name = $s.Name; File = $s.File; Failed = $false; OK = $ok; Total = $total; PingOK = $pok; PingTotal = $ptot; Details = $res })
            $sync.Done++
        }
    } catch { $sync.Error = "$_" }
    finally { Kill-Winws; $sync.Finished = $true }
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
        $tb = New-Object System.Windows.Controls.TextBlock; $tb.Text = $text; $tb.VerticalAlignment = 'Center'
        [void]$sp.Children.Add($d); [void]$sp.Children.Add($tb)
        $c.Content = $sp
    } else { $c.Content = $text }
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
    $sec = [int]($s.Count * (4 + [Math]::Min(3, [Math]::Ceiling($tg.Count / 12.0)) * 3))
    $ui.TestStatus.Text = if ($s.Count -eq 0 -or $tg.Count -eq 0) { 'Выбери хотя бы одну группу и одну стратегию' } else { "$($s.Count) стратегий × $($tg.Count) сайтов · примерно $([Math]::Max(1, [Math]::Round($sec / 60))) мин" }
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

function Start-Test {
    if (-not $IsAdmin) { Show-Toast 'Нужны права администратора — запусти через Zapret.bat' -Err; return }
    if ((Get-ServiceState) -eq 'Running') { Show-Toast 'zapret работает как служба. Удали службу в «Сервис и диагностика», чтобы тестировать.' -Err; return }
    $groups = Get-Picked $ui.GroupChips
    $targets = @(Get-TestTargets | Where-Object { $groups -contains $_.Group })
    $strategies = @()
    foreach ($f in (Get-Picked $ui.StratChips)) {
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
    [void]$ps.AddScript($TestWorker.ToString()).AddArgument($sync).AddArgument($strategies).AddArgument($targets).AddArgument("$Root\bin").AddArgument(4)
    $script:testPs = $ps
    $script:testHandle = $ps.BeginInvoke()

    $ui.TestResults.Children.Clear()
    Anim-Double $ui.TestSetup $OP 0 200
    $ui.TestSetup.Visibility = 'Collapsed'
    $ui.ResultsHead.Visibility = 'Visible'
    $ui.RunBtn.Content = 'Остановить'
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
        $rk = New-Object System.Windows.Controls.TextBlock; $rk.Text = "$rank"; $rk.Width = 22; $rk.FontSize = 12; $rk.Foreground = Brush '#5A6070'; $rk.VerticalAlignment = 'Center'
        [void]$nameSp.Children.Add($rk)
    }
    $nm = New-Object System.Windows.Controls.TextBlock; $nm.Text = $r.Name; $nm.FontSize = 13.5; $nm.FontWeight = 'SemiBold'; $nm.Foreground = Brush '#E6E8EE'; $nm.VerticalAlignment = 'Center'
    [void]$nameSp.Children.Add($nm)
    if ($best) {
        $bd = New-Object System.Windows.Controls.Border; $bd.Background = Brush '#3DDC97'; $bd.CornerRadius = 8; $bd.Padding = '8,1'; $bd.Margin = '10,0,0,0'
        $bt = New-Object System.Windows.Controls.TextBlock; $bt.Text = '★ лучшая'; $bt.FontSize = 11; $bt.FontWeight = 'SemiBold'; $bt.Foreground = Brush '#0F1115'
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
    $txt.Text = if ($r.Failed) { 'не запустилась' } else { "$($r.OK)/$($r.Total) · $([int]($ratio * 100))%" + $(if ($r.PingTotal) { " · ping $($r.PingOK)/$($r.PingTotal)" } else { '' }) }
    $txt.Foreground = Brush $col
    [System.Windows.Controls.Grid]::SetColumn($txt, 2); [void]$g.Children.Add($txt)

    if ($best) {
        $ap = New-Object System.Windows.Controls.Button; $ap.Style = $win.FindResource('AccentBtn'); $ap.Content = 'Включить'; $ap.Padding = '12,5'; $ap.Margin = '12,0,0,0'
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
        $pt.Text = "$($d.Name)  " + (($d.Tokens | ForEach-Object { $_ -replace ':OK$', ' ✓' -replace ':(ERR|SSL)$', ' ✕' -replace ':UNSUP$', ' –' }) -join '  ') + $(if ($d.Ping -and $d.Ping -ne 'n/a') { "  · $($d.Ping)" } else { '' })
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
    $ui.RunBtn.Content = 'Новый тест'
    $ui.RunBtn.IsEnabled = $true

    $res = @($sync.Results | Sort-Object @{ Expression = { if ($_.Failed) { -1 } else { $_.OK } }; Descending = $true }, @{ Expression = { $_.PingOK }; Descending = $true })
    $ui.TestResults.Children.Clear()
    $rank = 1
    foreach ($r in $res) { [void]$ui.TestResults.Children.Add((New-ResultRow $r ($rank -eq 1 -and -not $r.Failed -and $r.OK -gt 0) $rank)); $rank++ }
    Anim-Double $ui.TestBarScale $SX 1 400
    $el = (Get-Date) - $script:testBegan
    $ui.TestStatus.Text = if ($cancelled) { "Тест остановлен · проверено $($res.Count) из $script:testTotal" } else { "Готово за $([int]$el.TotalMinutes) мин $($el.Seconds) с · лучшая: $(if ($res.Count) { $res[0].Name } else { '—' })" }
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
        $ui.TestStatus.Text = "Стратегия $([Math]::Min($done + 1, $script:testTotal)) из $($script:testTotal) · $($sync.Current) · $phase$eta"
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
        $ui.RunBtn.Content = 'Останавливаю…'
    } elseif ($ui.RunBtn.Content -eq 'Новый тест') {
        $ui.RunBtn.Content = 'Запустить тест'
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
    $glow = New-Object System.Windows.Media.Effects.DropShadowEffect; $glow.Color = [System.Windows.Media.ColorConverter]::ConvertFromString('#3DDC97'); $glow.BlurRadius = 12; $glow.ShadowDepth = 0; $glow.Opacity = 0.8; $dc.Effect = $glow
    $mark = New-Object System.Windows.Controls.TextBlock; $mark.Text = '✓'; $mark.FontSize = 14; $mark.FontWeight = 'Bold'; $mark.Foreground = Brush '#0F1115'; $mark.HorizontalAlignment = 'Center'; $mark.VerticalAlignment = 'Center'
    [void]$done.Children.Add($dc); [void]$done.Children.Add($mark)
    foreach ($x in @($ring, $spin, $done)) { [void]$ic.Children.Add($x) }
    [void]$g.Children.Add($ic)
    $tx = New-Object System.Windows.Controls.StackPanel; $tx.VerticalAlignment = 'Center'
    $t1 = New-Object System.Windows.Controls.TextBlock; $t1.Text = $step.Title; $t1.FontSize = 13.5; $t1.FontWeight = 'SemiBold'; $t1.Foreground = Brush '#9AA3B5'
    $t2 = New-Object System.Windows.Controls.TextBlock; $t2.Text = $step.Sub; $t2.FontSize = 11.5; $t2.Foreground = Brush '#5A6070'; $t2.Margin = '0,2,0,0'
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
    $tag.Circle.Fill = Brush $col; $tag.Glow.Color = [System.Windows.Media.ColorConverter]::ConvertFromString($col)
    $tag.Mark.Text = switch ($r.S) { 'ok' { '✓' } 'warn' { '!' } default { '–' } }
    if ($r.S -eq 'skip') { $tag.Glow.Opacity = 0; $tag.Mark.Foreground = Brush '#C9CED8' }
    Anim-Double $tag.Done $OP 1 200
    Anim-Double $tag.Done.RenderTransform $SX 1 450 0.3 -Easing (Back-Ease)
    Anim-Double $tag.Done.RenderTransform $SY 1 450 0.3 -Easing (Back-Ease)
    $tag.Sub.Text = $r.T
    $tag.Sub.Foreground = Brush $(if ($r.S -eq 'ok') { '#7FE8B8' } elseif ($r.S -eq 'warn') { '#FFB86B' } else { '#8A90A0' })
    $tag.Title.Foreground = Brush $(if ($r.S -eq 'skip') { '#9AA3B5' } else { '#E6E8EE' })
    Anim-Color $tag.Box.Background '#1E2129' 300
}

function Open-Migrate {
    $script:migDemo = $false
    $script:migFolders = @(Find-ZapretFolders)
    $ui.MigSteps.Children.Clear()
    $ui.MigDone.Visibility = 'Collapsed'; $ui.MigDone.Opacity = 0
    $ui.MigRun.Content = 'Перенести'; $ui.MigRun.IsEnabled = $true
    Stop-Anim $ui.MigToGlow ([System.Windows.Media.Effects.DropShadowEffect]::BlurRadiusProperty); $ui.MigToGlow.BlurRadius = 0
    if ($script:migFolders.Count -lt 2) {
        $ui.MigCards.Visibility = 'Collapsed'; $ui.MigEmpty.Visibility = 'Visible'; $ui.MigRun.Visibility = 'Collapsed'; $ui.MigScroll.Visibility = 'Collapsed'
    } else {
        $ui.MigCards.Visibility = 'Visible'; $ui.MigEmpty.Visibility = 'Collapsed'; $ui.MigRun.Visibility = 'Visible'; $ui.MigScroll.Visibility = 'Visible'
        $script:loading = $true
        foreach ($cb in @($ui.MigFromBox, $ui.MigToBox)) {
            $cb.Items.Clear()
            foreach ($f in $script:migFolders) { $it = New-Object System.Windows.Controls.ComboBoxItem; $it.Content = $(if ($f.Version -ne [version]'0.0') { "v$($f.Version)" } else { $f.Name }); $it.Tag = $f.Path; $it.ToolTip = $f.Path; [void]$cb.Items.Add($it) }
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
    $ui.MigRun.IsEnabled = $false; $ui.MigRun.Content = 'Переношу…'
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
            $ui.MigRun.Content = 'Готово'; $ui.MigRun.IsEnabled = $true
            $ui.MigFromBox.IsEnabled = $true; $ui.MigToBox.IsEnabled = $true
            Anim-Double $ui.MigDotsT ([System.Windows.Media.TranslateTransform]::XProperty) 40 1400 -40 -Forever -NoEase
            Anim-Double $ui.MigToGlow ([System.Windows.Media.Effects.DropShadowEffect]::BlurRadiusProperty) 34 600 0
            $ui.MigDone.Visibility = 'Visible'; Anim-Double $ui.MigDone $OP 1 400 0
            if ($script:migDemo) { $ui.MigDone.Text = 'Это было демо — файлы не менялись. Так всё пройдёт при настоящем переносе.' }
            else { $ui.MigDone.Text = 'Готово! Оригиналы файлов автора сохранены как .bak' }
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
        $ui.MigRun.Content = 'Перенести'; $ui.MigRun.IsEnabled = $true
    }
})
$ui.MigRun.Add_Click({ Safe {
    if ($ui.MigRun.Content -eq 'Готово') {
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
        $pair[0].Items.Clear(); $it = New-Object System.Windows.Controls.ComboBoxItem; $it.Content = $pair[1]; [void]$pair[0].Items.Add($it); $pair[0].SelectedIndex = 0
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


# ======================= Обновления приложения =======================
$script:appUpd = $null
function Start-AppUpdateCheck([switch]$Manual) {
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
    $ui.AppUpdText.Text = "Доступна новая версия приложения $tag"
    $ui.AppUpdBtn.Content = 'Обновить'; $ui.AppUpdBtn.IsEnabled = $true; $ui.AppUpdTrack.Visibility = 'Collapsed'
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
    $ui.AppUpdBtn.IsEnabled = $false; $ui.AppUpdBtn.Content = 'Скачиваю…'
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
$script:appTimer.Interval = [TimeSpan]::FromMilliseconds(250)
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
            if ($d.Total -gt 0) { Anim-Double $ui.AppUpdFillS $SX ([Math]::Min(1, $d.Got / $d.Total)) 250; $ui.AppUpdBtn.Content = "$([int](100 * $d.Got / $d.Total))%" }
            if ($d.Done) {
                $script:appDl = $null
                if ($d.Error) { Show-Toast "Не удалось скачать обновление: $($d.Error)" -Err; $ui.AppUpdBtn.Content = 'Повторить'; $ui.AppUpdBtn.IsEnabled = $true }
                else {
                    $ui.AppUpdBtn.Content = 'Устанавливаю…'
                    Start-Process -FilePath $d.File -ArgumentList '-Auto'
                    $script:appCloseT = New-Object System.Windows.Threading.DispatcherTimer
                    $script:appCloseT.Interval = [TimeSpan]::FromMilliseconds(800)
                    $script:appCloseT.Add_Tick({ $script:appCloseT.Stop(); $win.Close() })
                    $script:appCloseT.Start()
                }
            }
        }
    } catch { Write-Log ($_ | Out-String) }
})
$script:appTimer.Start()
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
    $ui.UpdText.Text = 'Проверяю…'
    $ui.UpdFillS.ScaleX = 0; $ui.UpdBar.Opacity = 1
    Anim-Double $ui.UpdTrack $OP 1 200
    Anim-Double $ui.UpdBarT ([System.Windows.Media.TranslateTransform]::XProperty) 170 1100 -40 -Forever -Easing (Sine-Ease)
    $script:updJob = Start-Job -ScriptBlock {
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt' -Headers @{ 'Cache-Control' = 'no-cache' } -UseBasicParsing -TimeoutSec 8).Content.Trim()
        } catch { '' }
    }
}

# ======================= Таймер =======================
$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(800)
$timer.Add_Tick({
    try { Refresh-State } catch { Write-Log ($_ | Out-String) }
    if ($script:updJob -and $script:updJob.State -ne 'Running') {
        $v = "$(Receive-Job $script:updJob -ErrorAction SilentlyContinue)".Trim()
        Remove-Job $script:updJob -Force -ErrorAction SilentlyContinue; $script:updJob = $null
        Stop-Anim $ui.UpdBarT ([System.Windows.Media.TranslateTransform]::XProperty); $ui.UpdBar.Opacity = 0
        $ui.UpdFill.Fill = Brush $(if ($v) { '#3DDC97' } else { '#FF5E62' })
        Anim-Double $ui.UpdFillS $SX 1 350 0
        Anim-Double $ui.UpdTrack $OP 0 400 -beginMs 900
        if (-not $v) { $ui.UpdText.Text = 'Нет связи' }
        elseif ($v -eq $LocalVersion) { $script:latest = $v; $ui.UpdText.Text = 'Версия актуальна ✓' }
        else {
            $script:latest = $v
            $ui.UpdText.Text = "Скачать $v"
            $ui.UpdateBtn.Style = $win.FindResource('AccentBtn')
            Show-Toast "Вышла новая версия zapret: $v. Распакуй её в папку Zapret и нажми ⚙ → «Перенести настройки»."
        }
    }
})

$win.Add_Loaded({
    Anim-Double $ui.Shell $OP 1 380 0
    Anim-Double $ui.ShellShift ([System.Windows.Media.TranslateTransform]::YProperty) 0 450 18
    Refresh-State
    $timer.Start()
    if ($AutoStart -and $script:state -eq 'off') { Start-Zapret }
    if ($Settings.autoStartOnce) { $Settings.autoStartOnce = $false; Save-Settings; if ($script:state -eq 'off') { Start-Zapret } }
    if (Test-Path -LiteralPath $UpdFile) { Start-AppUpdateCheck }
    if (Test-Path -LiteralPath $UpdFile) { Start-UpdateCheck } else { $ui.UpdText.Text = 'Обновления' }
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

[void]$win.ShowDialog()
$timer.Stop()
}
catch {
    Write-Log ($_ | Out-String)
    try { [System.Windows.MessageBox]::Show("Ошибка запуска:`n$($_.Exception.Message)`n`nПодробности в gui-error.log", 'Zapret', 'OK', 'Error') | Out-Null } catch {}
}
