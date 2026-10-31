param([string]$SelfPath, [switch]$Auto)
# ====== Установщик Zapret GUI ======
$ErrorActionPreference = 'Stop'
$AppName = 'Zapret'
$AppVersion = '2.0.1'

# --- права администратора (один запрос) ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $cmd = "& ([ScriptBlock]::Create((Get-Content -LiteralPath '$($SelfPath -replace "'", "''")' -Raw -Encoding UTF8))) -SelfPath '$($SelfPath -replace "'", "''")'"
    try { Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-Command', $cmd } catch {}
    exit
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.IO.Compression.FileSystem
try {
    Add-Type -Namespace ZapretSetup -Name Shell -MemberDefinition '[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);'
    [void][ZapretSetup.Shell]::SetCurrentProcessExplicitAppUserModelID('Zapret.Setup')
} catch {}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$TempDir = Join-Path $env:TEMP ('zapret-setup-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $TempDir | Out-Null
function Save-Payload([string]$name, [string]$dest) { [IO.File]::WriteAllBytes($dest, [Convert]::FromBase64String($Payload[$name])) }
$IconTmp = Join-Path $TempDir 'zapret.ico'
Save-Payload 'zapret.ico' $IconTmp

# Уже установлено?
$UninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ZapretGUI'
$Existing = $null
try { $Existing = (Get-ItemProperty -LiteralPath $UninstKey -ErrorAction Stop).InstallLocation } catch {}
if (-not $Existing) {
    # приложение могло быть собрано вручную, без установщика (например, на рабочем столе)
    foreach ($cand in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Zapret'), 'C:\Zapret')) {
        if ((Test-Path -LiteralPath (Join-Path $cand 'app\Zapret.exe')) -or (Test-Path -LiteralPath (Join-Path $cand 'app\zapret-gui.ps1'))) { $Existing = $cand; break }
    }
}
# ---- поиск уже установленного zapret ----
function Find-ExistingZapret {
    $home_ = [Environment]::GetFolderPath('UserProfile')
    $roots = @($Existing, 'C:\Zapret', [Environment]::GetFolderPath('Desktop'), (Join-Path $home_ 'Desktop'), (Join-Path $home_ 'OneDrive\Desktop'),
               (Join-Path $home_ 'Downloads'), [Environment]::GetFolderPath('MyDocuments'), $home_, 'C:\', $env:ProgramFiles, ${env:ProgramFiles(x86)}, 'D:\', 'E:\') |
             Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
    $cands = New-Object System.Collections.Generic.List[string]
    # запущенный winws и служба zapret
    try { Get-Process -Name winws -ErrorAction Stop | ForEach-Object { if ($_.Path) { $cands.Add((Split-Path (Split-Path $_.Path -Parent) -Parent)) } } } catch {}
    try {
        $ip = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\zapret' -ErrorAction Stop).ImagePath
        if ($ip -match '"?([^"]+\\bin\\winws\.exe)') { $cands.Add((Split-Path (Split-Path $matches[1] -Parent) -Parent)) }
    } catch {}
    foreach ($r in $roots) {
        $cands.Add($r)
        foreach ($d in @(Get-ChildItem -LiteralPath $r -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'zapret' })) {
            $cands.Add($d.FullName)
            foreach ($d2 in @(Get-ChildItem -LiteralPath $d.FullName -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'zapret' })) { $cands.Add($d2.FullName) }
        }
    }
    $found = @()
    foreach ($c in ($cands | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath (Join-Path $c 'bin\winws.exe'))) { continue }
        $ver = [version]'0.0'
        try { $m = Select-String -LiteralPath (Join-Path $c 'service.bat') -Pattern 'set "LOCAL_VERSION=([^"]+)"' | Select-Object -First 1; if ($m) { $ver = [version]$m.Matches[0].Groups[1].Value } } catch {}
        if ($ver -eq [version]'0.0' -and (Split-Path -Leaf $c) -match '(\d+(\.\d+)+)') { try { $ver = [version]$matches[1] } catch {} }
        $sites = 0
        try { $sites = @(Get-Content -LiteralPath (Join-Path $c 'lists\list-general-user.txt') -ErrorAction Stop | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('#') -and $_.Trim() -ne 'domain.example.abc' }).Count } catch {}
        $found += [PSCustomObject]@{ Path = (Resolve-Path -LiteralPath $c).Path; Version = $ver; Sites = $sites }
    }
    return @($found | Sort-Object Path -Unique | Sort-Object Version, Sites -Descending)
}
$FoundZapret = @(Find-ExistingZapret)

# стратегия, которая сейчас запущена (окно winws или служба)
$DetectedStrategy = $null
try { Get-Process -Name winws -ErrorAction Stop | ForEach-Object { if ($_.MainWindowTitle -match '^zapret:\s*(.+)$') { $DetectedStrategy = $matches[1].Trim() } } } catch {}
if (-not $DetectedStrategy) { try { $s = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\zapret' -ErrorAction Stop).'zapret-discord-youtube'; if ($s) { $DetectedStrategy = [IO.Path]::GetFileNameWithoutExtension($s) } } catch {} }

$DefaultDir = if ($Existing -and (Test-Path -LiteralPath $Existing)) { $Existing } else { 'C:\Zapret' }

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Установка Zapret" Width="580" Height="620" WindowStyle="None" AllowsTransparency="True"
        Background="Transparent" ResizeMode="NoResize" WindowStartupLocation="CenterScreen"
        FontFamily="Segoe UI" Foreground="#E6E8EE">
  <Window.Resources>
    <Style x:Key="Btn" TargetType="Button">
      <Setter Property="Background" Value="#22262F"/>
      <Setter Property="Foreground" Value="#E6E8EE"/>
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="Padding" Value="16,9"/>
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
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="#3DDC97"/></Trigger>
              <Trigger Property="IsPressed" Value="True"><Setter Property="RenderTransform"><Setter.Value><ScaleTransform ScaleX="0.96" ScaleY="0.96"/></Setter.Value></Setter></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
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
            <Border x:Name="Bd" Background="Transparent" CornerRadius="8" Width="34" Height="28"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="#2A2E38"/><Setter Property="Foreground" Value="#FFFFFF"/></Trigger>
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
              <Ellipse Width="18" Height="18" Fill="#E6E8EE" HorizontalAlignment="Left" Margin="3,0,0,0">
                <Ellipse.RenderTransform><TranslateTransform x:Name="KnobT" X="0"/></Ellipse.RenderTransform>
              </Ellipse>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsChecked" Value="True">
                <Trigger.EnterActions><BeginStoryboard><Storyboard>
                  <DoubleAnimation Storyboard.TargetName="KnobT" Storyboard.TargetProperty="X" To="20" Duration="0:0:0.18"/>
                  <ColorAnimation Storyboard.TargetName="Track" Storyboard.TargetProperty="(Border.Background).(SolidColorBrush.Color)" To="#3DDC97" Duration="0:0:0.18"/>
                </Storyboard></BeginStoryboard></Trigger.EnterActions>
                <Trigger.ExitActions><BeginStoryboard><Storyboard>
                  <DoubleAnimation Storyboard.TargetName="KnobT" Storyboard.TargetProperty="X" To="0" Duration="0:0:0.18"/>
                  <ColorAnimation Storyboard.TargetName="Track" Storyboard.TargetProperty="(Border.Background).(SolidColorBrush.Color)" To="#2E3340" Duration="0:0:0.18"/>
                </Storyboard></BeginStoryboard></Trigger.ExitActions>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Window.Resources>

  <Grid Margin="14">
    <Border x:Name="Shell" CornerRadius="20" Background="#121419" BorderBrush="#252933" BorderThickness="1" Opacity="0">
      <Border.Effect><DropShadowEffect BlurRadius="24" ShadowDepth="0" Opacity="0.55"/></Border.Effect>
      <Border.RenderTransform><TranslateTransform x:Name="ShellShift" Y="18"/></Border.RenderTransform>
      <Grid>
        <Grid.RowDefinitions><RowDefinition Height="44"/><RowDefinition Height="*"/></Grid.RowDefinitions>
        <Grid x:Name="TitleBar" Background="Transparent" Margin="20,0,10,0">
          <TextBlock FontSize="12" VerticalAlignment="Center">
            <Run Text="УСТАНОВКА ZAPRET" FontWeight="Bold" Foreground="#8A90A0"/><Run Text="   by " Foreground="#5A6070"/><Run Text="COFFIOK" FontWeight="Bold" Foreground="#3DDC97"/>
          </TextBlock>
          <Button x:Name="CloseBtn" Style="{StaticResource TitleBtn}" HorizontalAlignment="Right"><TextBlock Text="✕" FontSize="13"/></Button>
        </Grid>

        <Grid Grid.Row="1" Margin="32,0,32,24">
          <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
          <!-- Логотип -->
          <Grid Grid.Row="0" VerticalAlignment="Top" HorizontalAlignment="Center" Margin="0,4,0,0" Width="96" Height="96" RenderTransformOrigin="0.5,0.5">
            <Grid.RenderTransform><ScaleTransform x:Name="LogoScale" ScaleX="0.6" ScaleY="0.6"/></Grid.RenderTransform>
            <Ellipse x:Name="LogoRing" Width="96" Height="96" Stroke="#3DDC97" StrokeThickness="3" Opacity="0" RenderTransformOrigin="0.5,0.5">
              <Ellipse.RenderTransform><ScaleTransform x:Name="LogoRingS"/></Ellipse.RenderTransform>
            </Ellipse>
            <Grid x:Name="Logo" Width="96" Height="96" RenderTransformOrigin="0.5,0.5">
              <Grid.RenderTransform><ScaleTransform x:Name="LogoFlip"/></Grid.RenderTransform>
              <!-- логотип приложения (тот же значок, что у Zapret) -->
              <Image x:Name="LogoImg" Width="96" Height="96" RenderOptions.BitmapScalingMode="HighQuality">
                <Image.Effect><DropShadowEffect Color="#3DDC97" BlurRadius="28" ShadowDepth="0" Opacity="0.45"/></Image.Effect>
              </Image>
            </Grid>
          </Grid>

          <!-- Экран 1: настройки -->
          <ScrollViewer x:Name="PageSetup" Grid.Row="1" Margin="0,14,-10,0" VerticalScrollBarVisibility="Auto">
          <StackPanel Margin="0,0,10,0">
            <TextBlock x:Name="Headline" Text="Установка Zapret" FontSize="24" FontWeight="SemiBold" HorizontalAlignment="Center"/>
            <TextBlock x:Name="Subline" Text="Удобное окно для zapret-discord-youtube. Сам zapret скачается с GitHub автора." Foreground="#8A90A0" FontSize="12.5"
                       HorizontalAlignment="Center" TextAlignment="Center" TextWrapping="Wrap" Margin="0,6,0,18" MaxWidth="400"/>
            <TextBlock Text="ПАПКА УСТАНОВКИ" FontSize="11" FontWeight="SemiBold" Foreground="#5A6070" Margin="2,0,0,6"/>
            <Grid Margin="0,0,0,18">
              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
              <Border Background="#22262F" CornerRadius="10" BorderBrush="#2E3340" BorderThickness="1" Height="38">
                <TextBox x:Name="DirBox" Background="Transparent" BorderThickness="0" Foreground="#E6E8EE" CaretBrush="#3DDC97" Margin="10,0" VerticalAlignment="Center" FontSize="13"/>
              </Border>
              <Button x:Name="BrowseBtn" Grid.Column="1" Style="{StaticResource Btn}" Content="Изменить" Margin="8,0,0,0"/>
            </Grid>
            <TextBlock Text="ZAPRET" FontSize="11" FontWeight="SemiBold" Foreground="#5A6070" Margin="2,0,0,6"/>
            <ComboBox x:Name="SrcBox" Margin="0,0,0,4"/>
            <TextBlock x:Name="SrcHint" Text="" Foreground="#5A6070" FontSize="11" Margin="2,0,0,16" TextWrapping="Wrap"/>
            <Grid Margin="0,0,0,12">
              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
              <TextBlock Text="Ярлык на рабочем столе" FontSize="13" VerticalAlignment="Center"/>
              <CheckBox x:Name="OptDesktop" Grid.Column="1" Style="{StaticResource Toggle}" IsChecked="True"/>
            </Grid>
            <Grid Margin="0,0,0,12">
              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
              <StackPanel>
                <TextBlock Text="Запускать без запроса прав" FontSize="13"/>
                <TextBlock Text="Windows не будет каждый раз спрашивать разрешение" Foreground="#8A90A0" FontSize="11"/>
              </StackPanel>
              <CheckBox x:Name="OptNoPrompt" Grid.Column="1" Style="{StaticResource Toggle}" IsChecked="True" VerticalAlignment="Center"/>
            </Grid>
          </StackPanel>
          </ScrollViewer>

          <!-- Экран 2: ход установки -->
          <Grid x:Name="PageProgress" Grid.Row="1" Margin="0,14,0,0" Visibility="Collapsed">
            <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
            <StackPanel>
              <TextBlock x:Name="ProgTitle" Text="Устанавливаю…" FontSize="20" FontWeight="SemiBold" HorizontalAlignment="Center"/>
              <TextBlock x:Name="ProgText" Text="" Foreground="#8A90A0" FontSize="12" HorizontalAlignment="Center" TextAlignment="Center" Margin="0,4,0,10" TextWrapping="Wrap"/>
              <Border Height="6" CornerRadius="3" Background="#22262F" ClipToBounds="True" Margin="0,0,0,14">
                <Border x:Name="Bar" CornerRadius="3" Background="#3DDC97" RenderTransformOrigin="0,0.5">
                  <Border.RenderTransform><ScaleTransform x:Name="BarS" ScaleX="0"/></Border.RenderTransform>
                </Border>
              </Border>
            </StackPanel>
            <StackPanel x:Name="Steps" Grid.Row="1"/>
          </Grid>

          <!-- Кнопки -->
          <DockPanel Grid.Row="2" Margin="0,14,0,0">
            <Button x:Name="MainBtn" DockPanel.Dock="Right" Style="{StaticResource AccentBtn}" Content="Установить" Padding="26,11" MinWidth="150"/>
            <Button x:Name="SecondBtn" DockPanel.Dock="Right" Style="{StaticResource Btn}" Content="Выбрать архив" Margin="0,0,8,0" Visibility="Collapsed"/>
            <TextBlock x:Name="FootText" Text="v1.1.0 · zapret, tg-ws-proxy — Flowseal · bol-van" Foreground="#5A6070" FontSize="11" VerticalAlignment="Center" TextWrapping="Wrap"/>
          </DockPanel>
        </Grid>
      </Grid>
    </Border>
  </Grid>
</Window>
'@
$win = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$ui = @{}
$xaml.SelectNodes('//*[@*[local-name()="Name"]]') | ForEach-Object { $n = $_.GetAttribute('Name', 'http://schemas.microsoft.com/winfx/2006/xaml'); if ($n) { $ui[$n] = $win.FindName($n) } }

# ---------- анимации ----------
$OP = [System.Windows.UIElement]::OpacityProperty
$SX = [System.Windows.Media.ScaleTransform]::ScaleXProperty
$SY = [System.Windows.Media.ScaleTransform]::ScaleYProperty
$AngP = [System.Windows.Media.RotateTransform]::AngleProperty
function EaseOut { $e = New-Object System.Windows.Media.Animation.CubicEase; $e.EasingMode = 'EaseOut'; $e }
function BackOut { $e = New-Object System.Windows.Media.Animation.BackEase; $e.EasingMode = 'EaseOut'; $e.Amplitude = 0.5; $e }
function SineIO { $e = New-Object System.Windows.Media.Animation.SineEase; $e.EasingMode = 'EaseInOut'; $e }
function Anim($target, $prop, $to, $ms = 250, $from = $null, [switch]$Forever, $beginMs = 0, $Easing = $null, [switch]$AutoReverse, [switch]$NoEase) {
    $a = New-Object System.Windows.Media.Animation.DoubleAnimation
    $a.To = $to; if ($null -ne $from) { $a.From = $from }
    $a.Duration = New-Object System.Windows.Duration ([TimeSpan]::FromMilliseconds($ms))
    if ($beginMs) { $a.BeginTime = [TimeSpan]::FromMilliseconds($beginMs) }
    if ($Forever) { $a.RepeatBehavior = [System.Windows.Media.Animation.RepeatBehavior]::Forever }
    if ($AutoReverse) { $a.AutoReverse = $true }
    if ($Easing) { $a.EasingFunction = $Easing } elseif (-not $NoEase) { $a.EasingFunction = EaseOut }
    $target.BeginAnimation($prop, $a)
}
function Brush($hex) { New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex)) }

try {
    $dec = [System.Windows.Media.Imaging.BitmapDecoder]::Create((New-Object Uri $IconTmp), 'None', 'OnLoad')
    $win.Icon = $dec.Frames | Sort-Object PixelWidth -Descending | Select-Object -First 1
    $ui.LogoImg.Source = $win.Icon
} catch {}
$ui.DirBox.Text = $DefaultDir
function Fill-Src {
    $ui.SrcBox.Items.Clear()
    $sel = $null
    foreach ($f in $FoundZapret) {
        $it = New-Object System.Windows.Controls.ComboBoxItem
        $label = "Найден: v$($f.Version)" + $(if ($f.Sites) { " · $($f.Sites) моих сайтов" } else { '' })
        $sp = New-Object System.Windows.Controls.StackPanel
        $a = New-Object System.Windows.Controls.TextBlock; $a.Text = $label
        $b = New-Object System.Windows.Controls.TextBlock; $b.Text = $f.Path; $b.FontSize = 10.5; $b.Foreground = Brush '#8A90A0'; $b.TextTrimming = 'CharacterEllipsis'
        [void]$sp.Children.Add($a); [void]$sp.Children.Add($b)
        $it.Content = $sp; $it.Tag = $f.Path; $it.ToolTip = $f.Path
        [void]$ui.SrcBox.Items.Add($it)
        if (-not $sel) { $sel = $it }
    }
    $dl = New-Object System.Windows.Controls.ComboBoxItem; $dl.Content = 'Скачать свежий с GitHub'; $dl.Tag = 'download'
    [void]$ui.SrcBox.Items.Add($dl)
    $ui.SrcBox.SelectedItem = $(if ($sel) { $sel } else { $dl })
    $ui.SrcBox.Height = $(if ($sel) { 48 } else { 38 })
}
function Update-SrcHint {
    $tag = $ui.SrcBox.SelectedItem.Tag
    $ui.SrcHint.Text = if ($tag -eq 'download') { 'Скачаю последнюю версию с GitHub автора (Flowseal).' }
                       elseif ((Split-Path -Parent $tag) -eq $ui.DirBox.Text.Trim().TrimEnd('\')) { 'Это твоя текущая установка — использую её как есть, со всеми сайтами и настройками.' }
                       else { 'Скопирую найденный zapret вместе с твоими сайтами, стратегией и настройками. Оригинальная папка останется на месте.' }
}
Fill-Src; Update-SrcHint
$ui.SrcBox.Add_SelectionChanged({ Update-SrcHint; $ui.SrcBox.Height = $(if ($ui.SrcBox.SelectedItem.Tag -eq 'download') { 38 } else { 48 }) })
if ($Existing) {
    $ui.Headline.Text = 'Обновление Zapret'
    $ui.Subline.Text = 'Приложение уже установлено. Обновлю его, твои сайты и настройки останутся на месте.'

    $ui.MainBtn.Content = 'Обновить'
}

# ---------- шаги ----------
$StepDefs = @(
    @{ Title = 'Поиск zapret'; Sub = '' },
    @{ Title = 'Скачивание zapret'; Sub = '' },
    @{ Title = 'Распаковка / копирование zapret'; Sub = '' },
    @{ Title = 'Установка приложения'; Sub = '' },
    @{ Title = 'Ярлыки и меню «Пуск»'; Sub = '' },
    @{ Title = 'Запуск без запроса прав'; Sub = '' }
)
function New-Step($def, $i) {
    $b = New-Object System.Windows.Controls.Border
    $b.CornerRadius = 10; $b.Padding = '12,7'; $b.Margin = '0,0,0,5'; $b.Background = Brush '#1A1D24'
    $g = New-Object System.Windows.Controls.DockPanel
    $ic = New-Object System.Windows.Controls.Grid; $ic.Width = 22; $ic.Height = 22; $ic.Margin = '0,0,12,0'
    $ring = New-Object System.Windows.Shapes.Ellipse; $ring.Stroke = Brush '#3A3F4D'; $ring.StrokeThickness = 2
    $spin = New-Object System.Windows.Shapes.Ellipse; $spin.Stroke = Brush '#3DDC97'; $spin.StrokeThickness = 2.5; $spin.Opacity = 0
    $spin.StrokeDashArray = New-Object System.Windows.Media.DoubleCollection (, [double[]]@(5, 5)); $spin.StrokeDashCap = 'Round'
    $spin.RenderTransformOrigin = '0.5,0.5'; $spin.RenderTransform = New-Object System.Windows.Media.RotateTransform
    $done = New-Object System.Windows.Controls.Grid; $done.Opacity = 0; $done.RenderTransformOrigin = '0.5,0.5'; $done.RenderTransform = New-Object System.Windows.Media.ScaleTransform(0.3, 0.3)
    $dc = New-Object System.Windows.Shapes.Ellipse; $dc.Fill = Brush '#3DDC97'
    $mk = New-Object System.Windows.Controls.TextBlock; $mk.Text = '✓'; $mk.FontSize = 12; $mk.FontWeight = 'Bold'; $mk.Foreground = Brush '#0F1115'; $mk.HorizontalAlignment = 'Center'; $mk.VerticalAlignment = 'Center'
    [void]$done.Children.Add($dc); [void]$done.Children.Add($mk)
    foreach ($x in @($ring, $spin, $done)) { [void]$ic.Children.Add($x) }
    [void]$g.Children.Add($ic)
    $sp = New-Object System.Windows.Controls.StackPanel; $sp.VerticalAlignment = 'Center'
    $t1 = New-Object System.Windows.Controls.TextBlock; $t1.Text = $def.Title; $t1.FontSize = 13; $t1.Foreground = Brush '#9AA3B5'
    $t2 = New-Object System.Windows.Controls.TextBlock; $t2.Text = $def.Sub; $t2.FontSize = 11; $t2.Foreground = Brush '#5A6070'; $t2.TextWrapping = 'Wrap'
    if (-not $def.Sub) { $t2.Visibility = 'Collapsed' }
    [void]$sp.Children.Add($t1); [void]$sp.Children.Add($t2); [void]$g.Children.Add($sp)
    $b.Child = $g
    $b.Tag = @{ Spin = $spin; Done = $done; Circle = $dc; Mark = $mk; T1 = $t1; T2 = $t2 }
    $b.Opacity = 0; $b.RenderTransform = New-Object System.Windows.Media.TranslateTransform
    Anim $b $OP 1 300 0 -beginMs ($i * 60)
    Anim $b.RenderTransform ([System.Windows.Media.TranslateTransform]::XProperty) 0 360 -16 -beginMs ($i * 60)
    return $b
}
function Step-Run($i) {
    $t = $ui.Steps.Children[$i].Tag
    Anim $t.Spin $OP 1 150; Anim $t.Spin.RenderTransform $AngP 360 800 0 -Forever -NoEase
    $t.T1.Foreground = Brush '#E6E8EE'
}
function Step-Note($i, $text) { $t = $ui.Steps.Children[$i].Tag; $t.T2.Text = $text; $t.T2.Visibility = 'Visible' }
function Step-Done($i, $state = 'ok', $text = $null) {
    $t = $ui.Steps.Children[$i].Tag
    $t.Spin.RenderTransform.BeginAnimation($AngP, $null); Anim $t.Spin $OP 0 120
    $col = switch ($state) { 'ok' { '#3DDC97' } 'skip' { '#5A6070' } default { '#FF5E62' } }
    $t.Circle.Fill = Brush $col
    $t.Mark.Text = switch ($state) { 'ok' { '✓' } 'skip' { '–' } default { '✕' } }
    if ($state -ne 'ok') { $t.Mark.Foreground = Brush '#FFFFFF' }
    Anim $t.Done $OP 1 200
    Anim $t.Done.RenderTransform $SX 1 420 0.3 -Easing (BackOut); Anim $t.Done.RenderTransform $SY 1 420 0.3 -Easing (BackOut)
    if ($text) { Step-Note $i $text }
    if ($state -eq 'err') { $t.T2.Foreground = Brush '#FF8C99' }
}
function Set-Bar($frac) { Anim $ui.BarS $SX ([Math]::Min(1, $frac)) 350 }

# ---------- работа ----------
$script:dest = $null
$script:zipPath = $null
$script:manualZip = $null
$script:zapretFolder = $null

function Start-Download {
    $sync = [hashtable]::Synchronized(@{ Done = $false; Error = $null; Got = 0; Total = 0; Url = $null; Tag = $null; Phase = 'api' })
    $script:dl = $sync
    $rs = [runspacefactory]::CreateRunspace(); $rs.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    [void]$ps.AddScript({
        param($sync, $out)
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $wc = New-Object Net.WebClient
            $wc.Headers.Add('User-Agent', 'ZapretSetup')
            $url = $null
            try {
                $json = $wc.DownloadString('https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest') | ConvertFrom-Json
                $asset = $json.assets | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1
                if ($asset) { $sync.Tag = $json.tag_name; $url = $asset.browser_download_url; $sync.Total = [long]$asset.size }
            } catch {}
            if (-not $url) {
                # запасной путь: узнать версию по переадресации страницы последнего релиза
                $r0 = [Net.HttpWebRequest]::Create('https://github.com/Flowseal/zapret-discord-youtube/releases/latest')
                $r0.UserAgent = 'ZapretSetup'; $r0.AllowAutoRedirect = $false
                $resp0 = $r0.GetResponse(); $loc = $resp0.Headers['Location']; $resp0.Close()
                if (-not $loc -or $loc -notmatch '/tag/([^/?#]+)') { throw 'не удалось узнать последнюю версию' }
                $sync.Tag = $matches[1]
                $url = "https://github.com/Flowseal/zapret-discord-youtube/releases/download/$($sync.Tag)/zapret-discord-youtube-$($sync.Tag).zip"
            }
            $sync.Url = $url; $sync.Phase = 'file'
            $req = [Net.HttpWebRequest]::Create($url)
            $req.UserAgent = 'ZapretSetup'; $req.AllowAutoRedirect = $true
            $resp = $req.GetResponse()
            if ($resp.ContentLength -gt 0) { $sync.Total = $resp.ContentLength }
            $in = $resp.GetResponseStream(); $fs = [IO.File]::Create($out)
            $buf = New-Object byte[] 65536
            while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) { $fs.Write($buf, 0, $n); $sync.Got += $n }
            $fs.Close(); $in.Close(); $resp.Close()
        } catch { $sync.Error = $_.Exception.Message }
        finally { $sync.Done = $true }
    }).AddArgument($sync).AddArgument((Join-Path $TempDir 'zapret.zip'))
    $script:dlPs = $ps; $script:dlH = $ps.BeginInvoke()
}

# закрыть всё, что осталось от прошлых версий приложения (окно на PowerShell, Zapret.exe, тестовая ZapretCS.exe)
function Stop-OldApp {
    try {
        Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction Stop |
            Where-Object { $_.CommandLine -like '*zapret-gui.ps1*' -and $_.ProcessId -ne $PID } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } catch {}
    Get-Process -Name 'Zapret', 'ZapretCS' -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID -and $_.Path -like '*\app\Zapret*.exe' } | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($k = 0; $k -lt 20; $k++) {
        if (-not @(Get-Process -Name 'Zapret', 'ZapretCS' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\app\Zapret*.exe' }).Count) { break }
        Start-Sleep -Milliseconds 150
    }
}
# убрать остатки старой версии (PowerShell) и тестовой сборки
function Remove-OldVersion([string]$app) {
    foreach ($f in 'zapret-gui.ps1', 'untranslated.txt', 'ZapretCS.exe', 'cs-error.log') {
        Remove-Item -LiteralPath (Join-Path $app $f) -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath (Join-Path (Split-Path -Parent $app) 'csharp') -Recurse -Force -ErrorAction SilentlyContinue
    $desk = [Environment]::GetFolderPath('Desktop')
    foreach ($f in 'Zapret (новая версия).lnk', 'ZapretCS-build.cmd', 'ZapretCS-setup.cmd') { Remove-Item -LiteralPath (Join-Path $desk $f) -Force -ErrorAction SilentlyContinue }
    foreach ($tn in 'ZapretCS GUI', 'ZapretCS Autostart', 'ZapretCS Dev') {
        try { Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction Stop } catch {}
    }
    # автозапуск с Windows: если был включён — теперь запускает новую версию
    try {
        if (Get-ScheduledTask -TaskName 'ZapretGUI Autostart' -ErrorAction Stop) {
            $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
            $act = New-ScheduledTaskAction -Execute (Join-Path $app 'Zapret.exe') -Argument '-AutoStart -Tray' -WorkingDirectory $app
            $trg = New-ScheduledTaskTrigger -AtLogOn -User $user; $trg.Delay = 'PT5S'
            $pr = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
            $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Priority 4
            Register-ScheduledTask -TaskName 'ZapretGUI Autostart' -Action $act -Trigger $trg -Principal $pr -Settings $set -Description 'Запуск Zapret вместе с Windows' -Force | Out-Null
        }
    } catch {}
}
function Install-App {
    $app = Join-Path $script:dest 'app'
    New-Item -ItemType Directory -Force -Path $app | Out-Null
    Stop-OldApp
    for ($k = 0; $k -lt 8; $k++) { try { Save-Payload 'Zapret.exe' (Join-Path $app 'Zapret.exe'); break } catch { if ($k -eq 7) { throw }; Start-Sleep -Milliseconds 500 } }
    Remove-OldVersion $app
    $icoDst = Join-Path $app 'zapret.ico'
    $same = $false
    if (Test-Path -LiteralPath $icoDst) { try { $same = ([Convert]::ToBase64String([IO.File]::ReadAllBytes($icoDst)) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($IconTmp))) } catch {} }
    if (-not $same) {
        for ($k = 0; $k -lt 5; $k++) { try { Copy-Item -LiteralPath $IconTmp -Destination $icoDst -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 400 } }
    }
    $launcher = "@echo off`r`nschtasks /run /tn `"ZapretGUI`" >nul 2>&1 && exit /b`r`nstart `"`" `"%~dp0app\Zapret.exe`"`r`n"
    [IO.File]::WriteAllText((Join-Path $script:dest 'Zapret.bat'), $launcher, [Text.Encoding]::ASCII)
    # настройка «без запроса прав» для приложения
    $sf = Join-Path $app 'gui-settings.json'
    $st = @{ strategy = 'general' }
    if (Test-Path -LiteralPath $sf) { try { $j = Get-Content -LiteralPath $sf -Raw -Encoding UTF8 | ConvertFrom-Json; foreach ($pp in $j.PSObject.Properties) { $st[$pp.Name] = $pp.Value } } catch {} }
    $st.noPromptOff = -not [bool]$ui.OptNoPrompt.IsChecked
    if ($DetectedStrategy) { $st.strategy = $DetectedStrategy }
    if ($script:wasRunning) { $st.autoStartOnce = $true }
    [IO.File]::WriteAllText($sf, ($st | ConvertTo-Json), $utf8)
    # деинсталлятор
    $un = @'
$ErrorActionPreference = 'SilentlyContinue'
$dir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Add-Type -AssemblyName PresentationFramework
$r = [System.Windows.MessageBox]::Show("Удалить Zapret?`n`nБудет удалена папка $dir вместе с zapret и твоими списками сайтов.", 'Удаление Zapret', 'YesNo', 'Question')
if ($r -ne 'Yes') { exit }
Get-Process -Name winws | Stop-Process -Force
Get-Process -Name 'Zapret', 'ZapretCS' | Where-Object { $_.Path -like "$dir\*" } | Stop-Process -Force
Get-Process -Name 'TgWsProxy_windows' | Stop-Process -Force
Remove-NetFirewallRule -DisplayName 'Zapret Telegram proxy'
try { Stop-Service zapret -Force; sc.exe delete zapret | Out-Null } catch {}
Unregister-ScheduledTask -TaskName 'ZapretGUI' -Confirm:$false
Unregister-ScheduledTask -TaskName 'ZapretGUI Autostart' -Confirm:$false
foreach ($tn in 'ZapretCS GUI', 'ZapretCS Autostart', 'ZapretCS Dev') { Unregister-ScheduledTask -TaskName $tn -Confirm:$false }
foreach ($l in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Zapret.lnk'), (Join-Path ([Environment]::GetFolderPath('Programs')) 'Zapret.lnk'))) { Remove-Item -LiteralPath $l -Force }
Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ZapretGUI' -Recurse -Force
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$dir`"" -WindowStyle Hidden
[System.Windows.MessageBox]::Show('Zapret удалён.', 'Удаление Zapret') | Out-Null
'@
    [IO.File]::WriteAllText((Join-Path $app 'uninstall.ps1'), $un, (New-Object System.Text.UTF8Encoding($true)))
    $unCmd = "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -Command `"Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \`"%~dp0uninstall.ps1\`"'`"`r`n"
    [IO.File]::WriteAllText((Join-Path $app 'uninstall.cmd'), $unCmd, [Text.Encoding]::ASCII)
    # запись в «Приложения»
    New-Item -Path $UninstKey -Force | Out-Null
    $props = @{ DisplayName = 'Zapret GUI'; DisplayVersion = $AppVersion; DisplayIcon = (Join-Path $app 'zapret.ico'); InstallLocation = $script:dest
                UninstallString = "`"$(Join-Path $app 'uninstall.cmd')`""; Publisher = 'COFFIOK'; NoModify = 1; NoRepair = 1 }
    foreach ($k in $props.Keys) { New-ItemProperty -Path $UninstKey -Name $k -Value $props[$k] -PropertyType $(if ($props[$k] -is [int]) { 'DWord' } else { 'String' }) -Force | Out-Null }
}
function New-Shortcut($path) {
    $sh = New-Object -ComObject WScript.Shell
    $l = $sh.CreateShortcut($path)
    $l.TargetPath = Join-Path $script:dest 'Zapret.bat'; $l.WorkingDirectory = $script:dest
    $l.IconLocation = (Join-Path $script:dest 'app\zapret.ico') + ',0'; $l.WindowStyle = 7; $l.Description = 'Zapret'
    $l.Save()
}
function Register-NoPrompt {
    $app = Join-Path $script:dest 'app'
    $act = New-ScheduledTaskAction -Execute (Join-Path $app 'Zapret.exe') -WorkingDirectory $app
    $pr = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
    $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Priority 4
    Register-ScheduledTask -TaskName 'ZapretGUI' -Action $act -Principal $pr -Settings $set -Description 'Запуск приложения Zapret без запроса прав администратора' -Force | Out-Null
}

# Конечный автомат установки, шаги идут по таймеру — окно не зависает
$script:step = 0; $script:phase = 'start'
$tick = New-Object System.Windows.Threading.DispatcherTimer
$tick.Interval = [TimeSpan]::FromMilliseconds(250)
$tick.Add_Tick({
    try {
        switch ($script:step) {
            0 {
                if ($script:phase -eq 'start') {
                    Step-Run 0
                    if ($script:localSrc) {
                        $f = $FoundZapret | Where-Object { $_.Path -eq $script:localSrc } | Select-Object -First 1
                        Step-Done 0 'ok' "найден на компьютере: v$($f.Version)"; Step-Done 1 'skip' 'скачивать не нужно'; Set-Bar 0.45
                        $script:step = 2; $script:phase = 'start'; return
                    }
                    if ($script:manualZip) { $script:zipPath = $script:manualZip; Step-Done 0 'skip' 'выбран архив вручную'; Step-Done 1 'skip' (Split-Path -Leaf $script:manualZip); Set-Bar 0.45; $script:step = 2; $script:phase = 'start'; return }
                    Start-Download; $script:phase = 'wait'; $ui.ProgText.Text = 'Связываюсь с GitHub…'; return
                }
                if ($script:dl.Phase -eq 'file' -or $script:dl.Done) {
                    if ($script:dl.Error -and -not $script:dl.Tag) { throw "Не удалось связаться с GitHub: $($script:dl.Error)" }
                    Step-Done 0 'ok' "найдена версия $($script:dl.Tag)"; Set-Bar 0.08
                    $script:step = 1; $script:phase = 'start'
                }
            }
            1 {
                if ($script:phase -eq 'start') { Step-Run 1; $script:phase = 'wait' }
                $got = [double]$script:dl.Got; $tot = [double]$script:dl.Total
                if ($tot -gt 0) {
                    Step-Note 1 ('{0:N1} из {1:N1} МБ' -f ($got / 1MB), ($tot / 1MB))
                    Set-Bar (0.08 + 0.37 * ($got / $tot))
                    $ui.ProgText.Text = "Скачиваю zapret $($script:dl.Tag) · $([int](100 * $got / $tot))%"
                }
                if ($script:dl.Done) {
                    if ($script:dl.Error) { throw "Не удалось скачать zapret: $($script:dl.Error)" }
                    $script:zipPath = Join-Path $TempDir 'zapret.zip'
                    Step-Done 1 'ok' ('{0:N1} МБ' -f ($got / 1MB)); $script:step = 2; $script:phase = 'start'
                }
            }
            2 {
                if ($script:phase -eq 'start') { Step-Run 2; $ui.ProgText.Text = $(if ($script:localSrc) { 'Копирую zapret с твоими настройками…' } else { 'Распаковываю…' }); $script:phase = 'go'; return }
                if ($script:localSrc) {
                    New-Item -ItemType Directory -Force -Path $script:dest | Out-Null
                    $name = Split-Path -Leaf $script:localSrc
                    if ($name -notmatch 'zapret') { $name = 'zapret-discord-youtube' }
                    $target = Join-Path $script:dest $name
                    if ((Split-Path -Parent $script:localSrc) -eq $script:dest -or $script:localSrc -eq $target) {
                        $target = $script:localSrc
                        Step-Done 2 'skip' 'уже в папке установки — использую как есть'
                    } else {
                        if (-not (Test-Path -LiteralPath $target)) { New-Item -ItemType Directory -Path $target | Out-Null }
                        Copy-Item -Path (Join-Path $script:localSrc '*') -Destination $target -Recurse -Force
                        Step-Done 2 'ok' "скопирован с настройками: $name"
                    }
                    $script:zapretFolder = $target
                    Set-Bar 0.6; $script:step = 3; $script:phase = 'start'; return
                }
                $ex = Join-Path $TempDir 'unz'
                if (Test-Path -LiteralPath $ex) { Remove-Item -LiteralPath $ex -Recurse -Force }
                [IO.Compression.ZipFile]::ExtractToDirectory($script:zipPath, $ex)
                $top = @(Get-ChildItem -LiteralPath $ex -Directory)
                $src = if ($top.Count -eq 1 -and -not (Test-Path -LiteralPath (Join-Path $ex 'bin\winws.exe'))) { $top[0].FullName } else { $ex }
                if (-not (Test-Path -LiteralPath (Join-Path $src 'bin\winws.exe'))) { throw 'В архиве нет zapret (bin\winws.exe не найден)' }
                $name = Split-Path -Leaf $src
                if ($name -eq 'unz') { $name = 'zapret-discord-youtube' + $(if ($script:dl.Tag) { '-' + $script:dl.Tag } else { '' }) }
                New-Item -ItemType Directory -Force -Path $script:dest | Out-Null
                $target = Join-Path $script:dest $name
                if (Test-Path -LiteralPath $target) {
                    Step-Done 2 'skip' "$name уже установлен"
                } else {
                    Move-Item -LiteralPath $src -Destination $target
                    Step-Done 2 'ok' $name
                }
                $script:zapretFolder = $target
                Set-Bar 0.6; $script:step = 3; $script:phase = 'start'
            }
            3 {
                if ($script:phase -eq 'start') { Step-Run 3; $ui.ProgText.Text = 'Устанавливаю приложение…'; $script:phase = 'go'; return }
                Install-App; Step-Done 3 'ok' (Join-Path $script:dest 'app'); Set-Bar 0.75; $script:step = 4; $script:phase = 'start'
            }
            4 {
                if ($script:phase -eq 'start') { Step-Run 4; $script:phase = 'go'; return }
                New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'Zapret.lnk')
                $made = 'меню «Пуск»'
                if ($ui.OptDesktop.IsChecked) { New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Zapret.lnk'); $made = 'рабочий стол и меню «Пуск»' }
                Step-Done 4 'ok' $made; Set-Bar 0.88; $script:step = 5; $script:phase = 'start'
            }
            5 {
                if ($script:phase -eq 'start') { Step-Run 5; $script:phase = 'go'; return }
                if ($ui.OptNoPrompt.IsChecked) { Register-NoPrompt; Step-Done 5 'ok' 'включено' }
                else { try { Unregister-ScheduledTask -TaskName 'ZapretGUI' -Confirm:$false -ErrorAction Stop } catch {}; Step-Done 5 'skip' 'выключено — Windows будет спрашивать' }
                Set-Bar 1; $script:step = 6
            }
            6 {
                $tick.Stop()
                $ui.ProgTitle.Text = 'Готово!'
                $ui.ProgText.Text = "Zapret установлен в $($script:dest)"
                $ui.MainBtn.Content = 'Запустить Zapret'; $ui.MainBtn.IsEnabled = $true
                $ui.FootText.Text = 'Удалить можно в «Параметры → Приложения»'
                Stop-Spin
                Anim $ui.LogoRing $OP 0 1400 0.8; Anim $ui.LogoRingS $SX 1.6 1400 1; Anim $ui.LogoRingS $SY 1.6 1400 1
                Anim $ui.LogoScale $SX 1 500 1.15 -Easing (BackOut); Anim $ui.LogoScale $SY 1 500 1.15 -Easing (BackOut)
                $script:finished = $true
                if ($Auto) {
                    $ui.ProgText.Text = 'Обновлено! Открываю Zapret…'
                    $script:autoT2 = New-Object System.Windows.Threading.DispatcherTimer
                    $script:autoT2.Interval = [TimeSpan]::FromMilliseconds(1600)
                    $script:autoT2.Add_Tick({ $script:autoT2.Stop(); $ui.MainBtn.RaiseEvent((New-Object System.Windows.RoutedEventArgs ([System.Windows.Controls.Primitives.ButtonBase]::ClickEvent))) })
                    $script:autoT2.Start()
                }
            }
        }
    } catch {
        $tick.Stop(); Stop-Spin
        $i = [Math]::Min($script:step, $ui.Steps.Children.Count - 1)
        Step-Done $i 'err' $_.Exception.Message
        $ui.ProgTitle.Text = 'Что-то пошло не так'
        $ui.ProgText.Text = $_.Exception.Message
        $ui.MainBtn.Content = 'Повторить'; $ui.MainBtn.IsEnabled = $true
        $ui.SecondBtn.Visibility = 'Visible'
        $ui.FootText.Text = 'Нет доступа к GitHub? Скачай архив zapret вручную и нажми «Выбрать архив».'
        $script:failed = $true
    }
})
function Start-Spin { Anim $ui.LogoFlip $SX -1 450 1 -Forever -AutoReverse -Easing (SineIO) }
function Stop-Spin { Anim $ui.LogoFlip $SX 1 450 -Easing (BackOut) }

function Begin-Install {
    $d = $ui.DirBox.Text.Trim().TrimEnd('\')
    if (-not $d) { return }
    if ($d -match '[^\x00-\x7F]' -and $d -ne $Existing) {
        [System.Windows.MessageBox]::Show('В пути есть русские буквы — zapret с такими путями работает плохо. Выбери папку с латинскими буквами, например C:\Zapret.', 'Zapret', 'OK', 'Warning') | Out-Null
        return
    }
    $script:dest = $d
    $script:failed = $false
    $src = $ui.SrcBox.SelectedItem.Tag
    $script:localSrc = if ($src -and $src -ne 'download' -and -not $script:manualZip) { $src } else { $null }
    $script:wasRunning = [bool](Get-Process -Name winws -ErrorAction SilentlyContinue)
    # закрыть открытое окно Zapret, чтобы обновить его файлы
    Stop-OldApp
    Get-Process -Name winws -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $ui.PageSetup.Visibility = 'Collapsed'
    $ui.PageProgress.Visibility = 'Visible'; Anim $ui.PageProgress $OP 1 300 0
    $ui.Steps.Children.Clear()
    $i = 0; foreach ($s in $StepDefs) { [void]$ui.Steps.Children.Add((New-Step $s $i)); $i++ }
    $ui.BarS.ScaleX = 0
    $ui.MainBtn.Content = 'Устанавливаю…'; $ui.MainBtn.IsEnabled = $false
    $ui.SecondBtn.Visibility = 'Collapsed'; $ui.FootText.Text = ''
    $ui.ProgTitle.Text = if ($Existing) { 'Обновляю…' } else { 'Устанавливаю…' }
    Start-Spin
    $script:step = 0; $script:phase = 'start'
    $tick.Start()
}

# ---------- события ----------
$ui.TitleBar.Add_MouseLeftButtonDown({ try { $win.DragMove() } catch {} })
$ui.CloseBtn.Add_Click({ $win.Close() })
$ui.BrowseBtn.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Куда установить Zapret (будет создана папка Zapret)'
    if ($dlg.ShowDialog() -eq 'OK') { $ui.DirBox.Text = Join-Path $dlg.SelectedPath 'Zapret' }
})
$ui.MainBtn.Add_Click({
    if ($script:finished) {
        $bat = Join-Path $script:dest 'Zapret.bat'
        if ($ui.OptNoPrompt.IsChecked) { Start-Process schtasks.exe -ArgumentList '/run /tn "ZapretGUI"' -WindowStyle Hidden }
        else { Start-Process -FilePath (Join-Path $script:dest 'app\Zapret.exe') }
        $win.Close(); return
    }
    Begin-Install
})
$ui.SecondBtn.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter = 'Архив zapret (*.zip)|*.zip'; $dlg.Title = 'Выбери скачанный архив zapret-discord-youtube'
    if ($dlg.ShowDialog() -eq 'OK') { $script:manualZip = $dlg.FileName; Begin-Install }
})
$win.Add_Loaded({
    if ($Auto) {
        # тихое обновление из приложения: сразу ставим с найденными настройками
        $script:autoT = New-Object System.Windows.Threading.DispatcherTimer
        $script:autoT.Interval = [TimeSpan]::FromMilliseconds(900)
        $script:autoT.Add_Tick({ $script:autoT.Stop(); Begin-Install })
        $script:autoT.Start()
    }
    Anim $ui.Shell $OP 1 380 0
    Anim $ui.ShellShift ([System.Windows.Media.TranslateTransform]::YProperty) 0 450 18
    Anim $ui.LogoScale $SX 1 650 0.6 -Easing (BackOut); Anim $ui.LogoScale $SY 1 650 0.6 -Easing (BackOut)
})
$win.Add_Closed({ try { Remove-Item -LiteralPath $TempDir -Recurse -Force -ErrorAction SilentlyContinue } catch {} })
# ---- тихое обновление из приложения: без окна установщика ----
# Приложение само показывает анимацию обновления, здесь только меняем файлы и запускаем его снова.
if ($Auto -and $Existing -and (Test-Path -LiteralPath (Join-Path $Existing 'app'))) {
    try {
        $script:dest = $Existing
        $script:wasRunning = $false
        $hasTask = $false; try { $hasTask = [bool](Get-ScheduledTask -TaskName 'ZapretGUI' -ErrorAction Stop) } catch {}
        $ui.OptNoPrompt.IsChecked = $hasTask
        $ui.OptDesktop.IsChecked = Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Zapret.lnk')
        # ждём, пока приложение закроется само (оно закрывается с анимацией), потом подстраховка
        for ($k = 0; $k -lt 30; $k++) {
            $alive = @(Get-Process -Name 'Zapret', 'ZapretCS' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\app\Zapret*.exe' })
            if (-not $alive.Count) { break }
            Start-Sleep -Milliseconds 200
        }
        Stop-OldApp
        Start-Sleep -Milliseconds 300
        Install-App
        # задача «без запроса прав» у старых версий запускала PowerShell — перенастраиваем на Zapret.exe
        if ($hasTask) { Register-NoPrompt }
        New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'Zapret.lnk')
        if ($ui.OptDesktop.IsChecked) { New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Zapret.lnk') }
        # отметка для приложения: показать «Обновлено»
        [IO.File]::WriteAllText((Join-Path $Existing 'app\updated.flag'), $AppVersion, (New-Object System.Text.UTF8Encoding($false)))
        $exe = Join-Path $Existing 'app\Zapret.exe'
        if ($hasTask) { Start-Process schtasks.exe -ArgumentList '/run /tn "ZapretGUI"' -WindowStyle Hidden }
        else { Start-Process -FilePath $exe }
        try { Remove-Item -LiteralPath $TempDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}
        exit 0
    } catch {
        try { Add-Content -LiteralPath (Join-Path $Existing 'app\gui-error.log') -Value ("[{0}] silent update: {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), ($_ | Out-String)) -Encoding UTF8 } catch {}
        # не вышло тихо — показываем обычный установщик
    }
}
[void]$win.ShowDialog()
