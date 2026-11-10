<#
.SYNOPSIS
  End-to-end tests for eero Desktop: builds the app, then drives the real UI through Windows UI Automation
  against the signed-in eero account.

.DESCRIPTION
  READ-ONLY by design: it never restarts, renames, blocks, pauses, or changes settings. The only API calls are the
  GETs the pages already make, plus one GET 2.2/account from the API Console.
  The sign-in screen is tested with an empty, temporary session file (EERO_DESKTOP_SESSION_FILE), so the real saved
  session is never touched. Screenshots and a results log go to tests\results\<timestamp>\.
#>
param(
    [switch]$SkipBuild,
    # Temporarily switch Windows apps to light mode for this run (restored afterwards, even on failure).
    [switch]$LightMode,
    # Test a different build, e.g. the packaged eero\dist\EeroDesktop.exe (use with -SkipBuild).
    [string]$ExePath,
    [int]$PageTimeoutSec = 45
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class E2EWin32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint n);
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr dlg, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int max);
    // A Win32 message box's message is the Static control with id 0xFFFF (UI Automation may not expose it).
    public static string MessageBoxText(IntPtr box) { var sb = new System.Text.StringBuilder(4096); GetWindowText(GetDlgItem(box, 0xFFFF), sb, sb.Capacity); return sb.ToString(); }
    /// <summary>
    /// Runs window measurement + PrintWindow in the system-DPI-aware context. On this PC (150% scaling) that context
    /// sees the window at its true rendered size (e.g. 1800x960), while per-monitor-v2 reports 1200x640 and a capture
    /// sized from it shows only the top-left part.
    /// </summary>
    public static IntPtr EnterCaptureContext() { return SetThreadDpiAwarenessContext((IntPtr)(-2)); /* SYSTEM_AWARE */ }
    public static void LeaveCaptureContext(IntPtr previous) { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, string l, int flags, int timeout, out IntPtr result);
    public static void BroadcastThemeChange() { IntPtr r; SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "ImmersiveColorSet", 2, 5000, out r); }
}
"@
# Per-monitor DPI awareness on this thread, so window rectangles, UI Automation and screenshots all use physical pixels
# (the PowerShell host's own awareness varies between runs).
if ([E2EWin32]::SetThreadDpiAwarenessContext([IntPtr](-4)) -eq [IntPtr]::Zero) { [E2EWin32]::SetProcessDPIAware() | Out-Null }

$Root    = Split-Path $PSScriptRoot -Parent
$Project = Join-Path $Root 'EeroDesktop\EeroDesktop.csproj'
$Exe     = if ($ExePath) { (Resolve-Path $ExePath).Path } else { Join-Path $Root 'EeroDesktop\bin\Debug\net10.0-windows\EeroDesktop.exe' }
$OutDir  = Join-Path $PSScriptRoot ("results\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $OutDir | Out-Null

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$Results = [System.Collections.Generic.List[object]]::new()

function Log([string]$m) { $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m; Write-Host $line; Add-Content "$OutDir\log.txt" $line }

function Test([string]$Name, [scriptblock]$Body) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $detail = & $Body
        $Results.Add([pscustomobject]@{ Test = $Name; Result = 'PASS'; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Detail = "$detail" })
        Log "PASS  $Name  $detail"
    } catch {
        $Results.Add([pscustomobject]@{ Test = $Name; Result = 'FAIL'; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Detail = $_.Exception.Message })
        Log "FAIL  $Name  $($_.Exception.Message)"
    }
}

function Assert([bool]$cond, [string]$msg) { if (-not $cond) { throw $msg } }

function Wait-Until([scriptblock]$Cond, [int]$TimeoutSec, [string]$What) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { $v = & $Cond; if ($v) { return $v } } catch { }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out after ${TimeoutSec}s waiting for $What"
}

function Get-TopWindow([int]$ProcessId, [string]$TitleLike = '*') {
    $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $ProcessId
    $AE::RootElement.FindAll($TS::Children, $cond) | Where-Object { $_.Current.Name -like $TitleLike } | Select-Object -First 1
}

function Find([System.Windows.Automation.AutomationElement]$Parent, [string]$AutomationId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::AutomationIdProperty), $AutomationId
    $Parent.FindFirst($TS::Descendants, $cond)
}

function Find-ByName($Parent, [string]$Name, $ControlType) {
    $c1 = New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), $Name
    $c2 = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $ControlType
    $Parent.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition $c1, $c2))
}

function Get-Value($el) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Set-Value($el, [string]$v) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Get-Rows($grid) { $grid.GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern).Current.RowCount }

function Get-ErrorDialog([int]$ProcessId) {
    # A MessageBox (class #32770) raised by the app means an unhandled error or a failed action.
    $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $ProcessId
    foreach ($w in $AE::RootElement.FindAll($TS::Descendants -bor $TS::Children, $cond)) {
        if ($w.Current.ClassName -eq '#32770') { return $w }
    }
}

function Save-Shot([IntPtr]$Hwnd, [string]$Name) {
    [E2EWin32]::SetForegroundWindow($Hwnd) | Out-Null; Start-Sleep -Milliseconds 400
    $ctx = [E2EWin32]::EnterCaptureContext()   # measure and render at the window's true size, so nothing is cropped
    try {
        $r = New-Object E2EWin32+RECT; [E2EWin32]::GetWindowRect($Hwnd, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
        # PrintWindow renders the window itself (PW_RENDERFULLCONTENT = 2), so other windows or toasts on top don't matter.
        $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
        $ok = [E2EWin32]::PrintWindow($Hwnd, $hdc, 2)
        $g.ReleaseHdc($hdc)
    } finally { [E2EWin32]::LeaveCaptureContext($ctx) }
    if (-not $ok) { $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size) }
    $g.Dispose()
    $path = Join-Path $OutDir "$Name.png"; $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    return $bmp
}

function Has-WindowIcon([IntPtr]$Hwnd) {
    # WM_GETICON: ICON_SMALL = 0, ICON_BIG = 1
    ([E2EWin32]::SendMessage($Hwnd, 0x7F, [IntPtr]0, [IntPtr]0) -ne 0) -or ([E2EWin32]::SendMessage($Hwnd, 0x7F, [IntPtr]1, [IntPtr]0) -ne 0)
}

function Start-App([hashtable]$Env = @{}) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $Exe
    $psi.UseShellExecute = $false
    foreach ($k in $Env.Keys) { $psi.Environment[$k] = $Env[$k] }
    [System.Diagnostics.Process]::Start($psi)
}

function Stop-App($proc) { if ($proc -and -not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null } }

# Alerts, settings and the known-device list go to a throwaway folder, so the user's real ones are never changed.
$DataDir = Join-Path $OutDir 'data'
$FilesDir = Join-Path $OutDir 'files'   # Backup & Export write here instead of showing Windows file dialogs
$alertsLog = Join-Path $DataDir 'alerts.log'
$AppEnv = @{ EERO_DESKTOP_DATA_DIR = $DataDir; EERO_DESKTOP_MONITOR_SECONDS = '5'; EERO_DESKTOP_TEST_FILE_DIR = $FilesDir; EERO_DESKTOP_DNS_REPEATS = '1' }

Log "eero Desktop E2E — results in $OutDir"
Get-Process EeroDesktop -ErrorAction SilentlyContinue | ForEach-Object {
    Log "Closing running instance (PID $($_.Id))"
    if (Test-Path $Exe) { & $Exe --exit }   # graceful: removes the tray icon
    if (-not $_.WaitForExit(10000)) { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
}

$themeKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$originalTheme = (Get-ItemProperty $themeKey -ErrorAction SilentlyContinue).AppsUseLightTheme
if ($LightMode) {
    Log "Switching Windows apps to light mode for this run (was AppsUseLightTheme=$originalTheme)"
    Set-ItemProperty $themeKey AppsUseLightTheme 1 -Type DWord; [E2EWin32]::BroadcastThemeChange(); Start-Sleep 2
}
try {

# ---------------------------------------------------------------- Build
if (-not $SkipBuild) {
    Test 'Build: Debug build with no warnings or errors' {
        $out = & dotnet build $Project -c Debug -nologo 2>&1 | Out-String
        $out | Set-Content "$OutDir\build.txt"
        Assert ($LASTEXITCODE -eq 0) "dotnet build failed (see build.txt)"
        $warn = [regex]::Match($out, '(\d+) Warning\(s\)').Groups[1].Value
        Assert ($warn -eq '0') "$warn build warning(s) (see build.txt)"
        "0 warnings, 0 errors"
    }
}

Test 'Icon: executable embeds the application icon' {
    Assert (Test-Path $Exe) "EeroDesktop.exe not found at $Exe"
    $count = [E2EWin32]::ExtractIconEx($Exe, -1, $null, $null, 0)
    Assert ($count -ge 1) "EeroDesktop.exe has no icon resources"
    $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($Exe); $bmp = $ico.ToBitmap()
    $c = $bmp.GetPixel([int]($bmp.Width / 2), [int]($bmp.Height * 0.2))
    $bmp.Save("$OutDir\exe-icon.png", [System.Drawing.Imaging.ImageFormat]::Png)
    "$count icon group(s); 32px sample pixel #{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B
}

# ---------------------------------------------------------------- Sign-in screen (isolated empty session)
$tempSession = Join-Path $OutDir 'empty-session.bin'
$login = $null
Test 'Sign-in: shows the sign-in window when there is no saved session' {
    $script:login = Start-App ($AppEnv + @{ EERO_DESKTOP_SESSION_FILE = $tempSession })
    $w = Wait-Until { Get-TopWindow $login.Id 'eero Desktop*Sign In' } 20 'sign-in window'
    $script:loginWin = $w
    Assert ($null -ne (Find $w 'LoginBox')) 'LoginBox missing'
    Assert ($null -ne (Find $w 'SendButton')) 'SendButton missing'
    Assert ($null -ne (Find-ByName $w 'Sign In to eero' $CT::Text)) "Title Case header 'Sign In to eero' missing"
    "title '$($w.Current.Name)'"
}
Test 'Sign-in: window has the app icon' {
    $h = [IntPtr]$loginWin.Current.NativeWindowHandle
    Assert (Has-WindowIcon $h) 'WM_GETICON returned no icon'
    Save-Shot $h '01-sign-in' | ForEach-Object Dispose
    'icon set'
}
Test 'Sign-in: closing the window exits the app' {
    $loginWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Assert ($login.WaitForExit(10000)) 'process still running 10s after closing sign-in window'
    Assert (-not (Test-Path $tempSession)) 'a session file was written without signing in'
    "exit code $($login.ExitCode)"
}
Stop-App $login

# ---------------------------------------------------------------- Main window (real saved session)
$app = Start-App $AppEnv
$main = $null; $hwnd = [IntPtr]::Zero
Test 'Startup: signed-in session opens the main window' {
    $script:main = Wait-Until { $w = Get-TopWindow $app.Id 'eero Desktop'; if ($w -and (Find $w 'Nav')) { $w } } 40 'main window (is the saved session still valid?)'
    $script:hwnd = [IntPtr]$main.Current.NativeWindowHandle
    $status = Find $main 'StatusText'
    Wait-Until { $s = $status.Current.Name; $s -eq 'Ready' -or ($s -and -not $s.EndsWith('…') -and -not $s.StartsWith('Loading')) } $PageTimeoutSec 'initial load' | Out-Null
    Assert (-not $status.Current.Name.StartsWith('Error')) "status bar: $($status.Current.Name)"
    "account '$((Find $main 'AccountText').Current.Name)'"
}
Test 'Startup: main window has the app icon' { Assert (Has-WindowIcon $hwnd) 'WM_GETICON returned no icon'; 'icon set' }
Test 'Layout: window (and title bar) fits inside the screen work area' {
    $r = New-Object E2EWin32+RECT; [E2EWin32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $mi = New-Object E2EWin32+MONITORINFO; $mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($mi)
    [E2EWin32]::GetMonitorInfo([E2EWin32]::MonitorFromWindow($hwnd, 2), [ref]$mi) | Out-Null
    $wa = $mi.rcWork; $tol = 8  # invisible resize borders
    Assert ($r.Top -ge $wa.Top - 1) "title bar above work area (top $($r.Top) < $($wa.Top))"
    Assert ($r.Bottom -le $wa.Bottom + $tol -and $r.Left -ge $wa.Left - $tol -and $r.Right -le $wa.Right + $tol) "window $($r.Left),$($r.Top)-$($r.Right),$($r.Bottom) exceeds work area $($wa.Left),$($wa.Top)-$($wa.Right),$($wa.Bottom)"
    "window $($r.Right - $r.Left)x$($r.Bottom - $r.Top) in work area $($wa.Right - $wa.Left)x$($wa.Bottom - $wa.Top)"
}
Test 'Header: network picker shows a network name, health is shown' {
    $combo = Find $main 'NetworkCombo'
    $sel = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    $name = if ($sel.Count) { $sel[0].Current.Name } else { '' }
    Assert ($name -and $name -notmatch '^/|^https?:|networks/') "network picker shows '$name'"
    $health = (Find $main 'HealthText').Current.Name
    Assert ($health -match '^Internet: \w+, eero Network: \w+') "health text '$health'"
    "network '$name'; $health"
}
Test 'Theme: app colors follow the Windows light/dark setting' {
    $light = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction SilentlyContinue).AppsUseLightTheme
    $mode = if ($light -eq 0) { 'Dark' } else { 'Light' }
    $bmp = Save-Shot $hwnd '00-theme'
    $nav = (Find $main 'Nav').Current.BoundingRectangle
    $r = New-Object E2EWin32+RECT; [E2EWin32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    # Sample the empty sidebar area under the navigation list and the page background next to the status bar.
    $status = (Find $main 'StatusText').Current.BoundingRectangle
    $sx = $bmp.Width / [math]::Max(1, $r.Right - $r.Left)   # 1 unless screen coordinates are DPI-virtualized
    $samples = [ordered]@{
        sidebar = $bmp.GetPixel([int](($nav.Left - $r.Left + 12) * $sx), [int][math]::Min($bmp.Height - 60, ($nav.Bottom - $r.Top + 20) * $sx))
        status  = $bmp.GetPixel([int]($bmp.Width * 0.6), [int][math]::Min($bmp.Height - 4, ($status.Top - $r.Top + $status.Height / 2) * $sx))
    }
    # The title bar follows the theme only when Windows isn't painting it with the accent color.
    $accentTitle = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\DWM' -ErrorAction SilentlyContinue).ColorPrevalence -eq 1
    if (-not $accentTitle) { $samples['title bar'] = $bmp.GetPixel([int]($bmp.Width / 2), 12) }
    $bmp.Dispose()
    foreach ($k in $samples.Keys) {   # neither Fluent theme paints pure black or pure white: that means a bad capture
        $c = $samples[$k]
        Assert (-not (($c.R + $c.G + $c.B) -eq 0 -or ($c.R + $c.G + $c.B) -eq 765)) "screenshot sample '$k' is pure $(if ($c.R) {'white'} else {'black'}) — capture did not hit the app window"
    }
    $lum = @{}; foreach ($k in $samples.Keys) { $c = $samples[$k]; $lum[$k] = [math]::Round((0.2126 * $c.R + 0.7152 * $c.G + 0.0722 * $c.B) / 255, 2) }
    $desc = ($lum.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', '
    $bad = $lum.GetEnumerator() | Where-Object { if ($mode -eq 'Dark') { $_.Value -ge 0.35 } else { $_.Value -le 0.65 } }
    Assert (-not $bad) "Windows is in $mode mode but app luminance is $desc"
    "Windows $mode mode; luminance $desc" + $(if ($accentTitle) { ' (title bar uses the Windows accent color; not checked)' } else { '' })
}

# ---------------------------------------------------------------- Every page loads without errors
$pageChecks = [ordered]@{
    'Overview'             = { param($w) $n = Wait-Until { (Find $w 'NameText').Current.Name } 10 'network name on Overview'; "network '$n'; $((Find $w 'SubText').Current.Name)" }
    'eeros'                = { param($w) $c = Get-Rows (Find $w 'Grid'); Assert ($c -ge 1) 'no eeros listed'; "$c eero(s)" }
    'Devices'              = { param($w) $c = Get-Rows (Find $w 'Grid'); Assert ($c -ge 1) 'no devices listed'; "$c device(s); $((Find $w 'CountText').Current.Name)" }
    'Profiles'             = { param($w) $g = Find $w 'ProfileGrid'; Assert ($null -ne $g) 'ProfileGrid missing'; "$(Get-Rows $g) profile(s)" }
    'Wi-Fi & Guest'        = { param($w) $n = Get-Value (Find $w 'NameBox'); Assert ([bool]$n) 'Wi-Fi name empty'; "Wi-Fi '$n'" }
    'Advanced'             = { param($w) Assert ($null -ne (Find $w 'Toggles')) 'Toggles missing'; Assert ($null -ne (Find $w 'DnsFacts')) 'DnsFacts missing'; 'toggles + DNS shown' }
    'Security Check'       = { param($w) $s = Wait-Until { $t = (Find $w 'SecuritySummary').Current.Name; if ($t) { $t } } 20 'security summary'; $c = Get-Rows (Find $w 'SecurityFindings'); Assert ($c -ge 4) "only $c findings"; "$c findings: $s" }
    'Reservations & Ports' = { param($w) "$(Get-Rows (Find $w 'ReservationGrid')) reservation(s), $(Get-Rows (Find $w 'ForwardGrid')) forward(s), $(Get-Rows (Find $w 'PinholeGrid')) pinhole(s)" }
    'Activity'             = { param($w) "$(Get-Rows (Find $w 'SpeedGrid')) speed test(s)" }
    'History'              = { param($w) foreach ($id in 'SpeedChart', 'SpeedHistoryGrid', 'OutageGrid', 'DnsHistoryChart', 'DnsHistoryGrid') { Assert ($null -ne (Find $w $id)) "$id missing" }; "$(Get-Rows (Find $w 'SpeedHistoryGrid')) saved speed test(s)" }
    'Updates'              = { param($w) Assert ($null -ne (Find $w 'Facts')) 'Facts missing'; "$(Get-Rows (Find $w 'Grid')) eero firmware row(s)" }
    'API Console'          = { param($w) Assert ($null -ne (Find $w 'PathBox')) 'PathBox missing'; 'console ready' }
}

$i = 1
foreach ($page in $pageChecks.Keys) {
    Test "Page: $page loads without errors" {
        Assert ($null -ne $main) 'main window not available'
        $nav = Find $main 'Nav'
        $item = Find-ByName $nav $page $CT::ListItem
        Assert ($null -ne $item) "sidebar item '$page' not found"
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 300
        $status = Find $main 'StatusText'
        Wait-Until { $s = $status.Current.Name; -not ($s.EndsWith('…') -or $s.StartsWith('Loading')) } $PageTimeoutSec "'$page' to finish loading" | Out-Null
        Start-Sleep -Milliseconds 300
        if ($dlg = Get-ErrorDialog $app.Id) {
            $msg = ($dlg.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::Text)) | ForEach-Object { $_.Current.Name }) -join ' '
            $dlg.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
            throw "error dialog: $msg"
        }
        $s = $status.Current.Name
        Assert (-not $s.StartsWith('Error')) "status bar: $s"
        $detail = & $pageChecks[$page] $main
        Save-Shot $hwnd ('{0:00}-{1}' -f (++$script:i), ($page -replace '[^A-Za-z]+', '-')) | ForEach-Object Dispose
        $detail
    }
}

# ---------------------------------------------------------------- Interactions (read-only)
Test 'Devices: search filters the list and clearing it restores it' {
    (Find-ByName (Find $main 'Nav') 'Devices' $CT::ListItem).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $grid = Wait-Until { Find $main 'Grid' } 10 'devices grid'
    $box = Find $main 'SearchBox'
    # The page is disabled while it reloads; wait for the reload to finish before typing.
    Wait-Until { $box.Current.IsEnabled } $PageTimeoutSec 'Devices page to finish reloading' | Out-Null
    $all = Wait-Until { $c = Get-Rows $grid; if ($c -ge 1) { $c } } $PageTimeoutSec 'device rows'
    Set-Value $box 'zz-no-such-device-zz'
    Wait-Until { (Get-Rows $grid) -eq 0 } 5 'filter to empty' | Out-Null
    Set-Value $box ''
    Wait-Until { (Get-Rows $grid) -eq $all } 5 'filter cleared' | Out-Null
    "$all -> 0 -> $all rows"
}
Test 'Devices: "Needs a Name" shows only devices with machine-made names, and turning it off restores the list' {
    $grid = Find $main 'Grid'
    $all = Get-Rows $grid
    $box = Find $main 'NeedsNameOnly'
    $toggle = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $toggle.Toggle(); Start-Sleep -Milliseconds 500
    $unnamed = Get-Rows $grid
    $toggle.Toggle(); Start-Sleep -Milliseconds 500
    $counted = if ((Find $main 'CountText').Current.Name -match '(\d+) need a name') { [int]$Matches[1] } else { 0 }
    Assert ($unnamed -le $counted) "filter showed $unnamed rows but only $counted devices need a name"
    if ($counted -gt 0 -and $counted -lt $all) { Assert ($unnamed -lt $all) 'the filter did not narrow the list' }
    Assert ((Get-Rows $grid) -eq $all) 'list not restored after turning the filter off'
    "$all devices, $unnamed need a name; $((Find $main 'CountText').Current.Name)"
}

Test 'Wi-Fi: password is masked until Show is pressed, and Hide masks it again' {
    (Find-ByName (Find $main 'Nav') 'Wi-Fi & Guest' $CT::ListItem).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $masked = Wait-Until { $m = Find $main 'PasswordBoxMasked'; if ($m -and $m.Current.IsEnabled) { $m } } $PageTimeoutSec 'masked password field'
    Assert ($masked.Current.IsPassword) 'password field is not masked'
    Assert ($null -eq (Find $main 'PasswordBoxPlain')) 'plain-text password is visible before pressing Show'
    $toggle = Find $main 'PasswordBoxToggle'
    Assert ($toggle.Current.Name -eq 'Show') "toggle reads '$($toggle.Current.Name)', expected 'Show'"
    Invoke-El $toggle
    $plain = Wait-Until { Find $main 'PasswordBoxPlain' } 5 'plain-text password after Show'
    $len = (Get-Value $plain).Length
    Assert ($len -ge 8) "revealed password has $len characters"
    Assert ((Find $main 'PasswordBoxToggle').Current.Name -eq 'Hide') 'toggle did not change to Hide'
    Invoke-El (Find $main 'PasswordBoxToggle')
    Wait-Until { $null -eq (Find $main 'PasswordBoxPlain') } 5 'password to be masked again' | Out-Null
    Assert ($null -ne (Find $main 'GuestPasswordBoxToggle')) 'guest password has no Show/Hide button'
    "masked -> shown ($len chars) -> masked; guest password has the toggle too"
}
function Select-Page([string]$name) {
    (Find-ByName (Find $main 'Nav') $name $CT::ListItem).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $status = Find $main 'StatusText'
    Start-Sleep -Milliseconds 300
    Wait-Until { $s = $status.Current.Name; -not ($s.EndsWith('…') -or $s.StartsWith('Loading')) } $PageTimeoutSec "'$name' to load" | Out-Null
}
function Get-OwnedWindow([string]$TitleLike) {
    # WPF dialogs owned by the main window appear under it in the UI Automation tree, not at the top level.
    $winCond = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::Window
    Wait-Until {
        $main.FindAll($TS::Descendants, $winCond) | Where-Object { $_.Current.Name -like $TitleLike } | Select-Object -First 1
    } 10 "window '$TitleLike'"
}
function Test-OwnedWindowOpen([string]$TitleLike) {
    $winCond = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::Window
    [bool]($main.FindAll($TS::Descendants, $winCond) | Where-Object { $_.Current.Name -like $TitleLike })
}

Test 'Advanced: Ad Blocking section and one-at-a-time switches are shown' {
    Select-Page 'Advanced'
    Assert ($null -ne (Find-ByName $main 'Ad Blocking' $CT::Text)) "'Ad Blocking' section missing"
    $status = (Find $main 'AdBlockStatus').Current.Name
    $on = Find $main 'AdGuardOnButton'; $off = Find $main 'AdGuardOffButton'
    $cd = Find $main 'ControlDOnButton'
    Assert ($null -ne $cd) 'Block Ads with Control D button missing'
    $blocking = $status -like 'Ads are being blocked*'
    Assert ($off.Current.IsEnabled -eq $blocking) "Stop Blocking Ads enabled=$($off.Current.IsEnabled) but status '$status'"
    Assert (-not ($on.Current.IsEnabled -and $cd.Current.IsEnabled -and $blocking)) 'the provider in use should not be offered again'
    $expected = 'upnp','sqm','thread','dns_caching','ddns','ad_block','block_malware'
    $missing = $expected | Where-Object { $null -eq (Find $main "Toggle_$_") }
    Assert (-not $missing) "missing switches: $($missing -join ', ')"
    $plusNote = Find-ByName $main 'Needs eero Plus.' $CT::Text
    "$status; switches: $($expected -join ', ')" + $(if ($plusNote) { '; eero Plus items marked' } else { '' })
}
Test 'Profiles: Content Filters dialog lists every filter and Cancel sends nothing' {
    Select-Page 'Profiles'
    Invoke-El (Find-ByName $main 'Content Filters' $CT::Button)
    $dlg = Get-OwnedWindow 'Content Filters*'
    $boxes = @($dlg.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::CheckBox)))
    Assert ($boxes.Count -eq 11) "expected 11 filter checkboxes, found $($boxes.Count)"
    $title = $dlg.Current.Name
    Invoke-El (Find-ByName $dlg 'Cancel' $CT::Button)
    Wait-Until { -not (Test-OwnedWindowOpen 'Content Filters*') } 5 'dialog to close' | Out-Null
    Start-Sleep -Milliseconds 500
    Assert (-not (Find $main 'StatusText').Current.Name.StartsWith('Updating')) 'a change was sent after Cancel'
    "'$title': $($boxes.Count) filters; cancelled"
}
Test 'eeros: Ports window lists ports and protects the internet port' {
    Select-Page 'eeros'
    $grid = Find $main 'Grid'
    $first = $grid.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::DataItem))
    $first.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-El (Find-ByName $main 'Ports' $CT::Button)
    $win = Get-OwnedWindow 'Ports*'
    $pgrid = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::DataGrid))
    $rows = @($pgrid.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::DataItem)))
    Assert ($rows.Count -ge 1) 'no ports listed'
    Save-Shot ([IntPtr]$win.Current.NativeWindowHandle) '21-Ports' | ForEach-Object Dispose
    $wan = $rows | Where-Object { ($_.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -contains 'Internet (WAN)' } | Select-Object -First 1
    $detail = "$($rows.Count) port(s) on '$($win.Current.Name)'"
    if ($wan) {
        $wan.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
        Assert (-not (Find-ByName $win 'Turn Port Off' $CT::Button).Current.IsEnabled) 'Turn Port Off is enabled for the internet port'
        $detail += '; Turn Port Off disabled on the WAN port'
    }
    $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-Until { -not (Test-OwnedWindowOpen 'Ports*') } 5 'Ports window to close' | Out-Null
    $detail
}
Test 'Reservations & Ports: Add Pinhole lists devices and Cancel sends nothing' {
    Select-Page 'Reservations & Ports'
    $before = Get-Rows (Find $main 'PinholeGrid')
    $add = @($main.FindAll($TS::Descendants, (New-Object System.Windows.Automation.AndCondition (New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), 'Add'), (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::Button))))[-1]   # the pinholes' Add button (last on the page)
    # The page disables itself while it loads the device list, which can make UI Automation report an error for the click itself.
    # Press again until the dialog is up (a press can be lost while the page re-enables; once the dialog is open the main window ignores presses).
    Wait-Until { if (-not (Test-OwnedWindowOpen 'Add IPv6 Pinhole')) { try { Invoke-El $add } catch { }; Start-Sleep -Milliseconds 1500 }; Test-OwnedWindowOpen 'Add IPv6 Pinhole' } 20 'Add IPv6 Pinhole dialog' | Out-Null
    $dlg = Get-OwnedWindow 'Add IPv6 Pinhole'
    $combo = $dlg.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::ComboBox))
    Assert ($null -ne $combo) 'device list missing'
    Invoke-El (Find-ByName $dlg 'Cancel' $CT::Button)
    Wait-Until { -not (Test-OwnedWindowOpen 'Add IPv6 Pinhole') } 10 'dialog to close' | Out-Null
    Select-Page 'Settings'; Select-Page 'Reservations & Ports'
    Assert ((Get-Rows (Find $main 'PinholeGrid')) -eq $before) 'pinhole count changed after Cancel'
    "dialog opened with a device list; cancelled; still $before pinhole(s)"
}
function Get-Luminance([string]$Name) {
    $bmp = Save-Shot $hwnd $Name
    $r = New-Object E2EWin32+RECT; [E2EWin32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $nav = (Find $main 'Nav').Current.BoundingRectangle
    $sx = $bmp.Width / [math]::Max(1, $r.Right - $r.Left)
    $c = $bmp.GetPixel([int](($nav.Left - $r.Left + 12) * $sx), [int][math]::Min($bmp.Height - 60, ($nav.Bottom - $r.Top + 20) * $sx))
    $bmp.Dispose()
    [math]::Round((0.2126 * $c.R + 0.7152 * $c.G + 0.0722 * $c.B) / 255, 2)
}
function Select-Radio([string]$Id) { (Find $main $Id).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 800 }
function Toggle-Box([string]$Id) { (Find $main $Id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 300 }
function Read-TestSettings { Get-Content (Join-Path $DataDir 'settings.json') -Raw | ConvertFrom-Json }

# Drives a Windows Save/Open file dialog opened by a button on the current page.
function Close-StrayDialogs {
    $winCond = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::Window
    $byPid = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $app.Id
    $tops = @($AE::RootElement.FindAll($TS::Children, $byPid)) | Where-Object { $_.Current.ClassName -eq '#32770' }
    foreach ($w in @($main.FindAll($TS::Descendants, $winCond)) + @($tops)) {
        try { $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    }
}
# Never type into native dialogs (keystrokes can land in other apps). The app skips its file dialogs when
# EERO_DESKTOP_TEST_FILE_DIR is set, so a button press writes straight into $FilesDir.
# Standard Windows message boxes (#32770) appear under the main window, and Windows reports their buttons as panes
# (no Invoke pattern). Find the button by its control id and send BM_CLICK straight to that button's own handle —
# a message to one specific control, never simulated keystrokes.
function Click-MessageBoxButton([string]$Name) {
    $classCond = New-Object System.Windows.Automation.PropertyCondition ($AE::ClassNameProperty), '#32770'
    $box = $main.FindFirst($TS::Descendants, $classCond)
    if (-not $box) { return $false }
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $c = $walker.GetFirstChild($AE::FromHandle([IntPtr]$box.Current.NativeWindowHandle))
    while ($c) {
        if ($c.Current.Name -eq $Name -and $c.Current.NativeWindowHandle -ne 0) {
            [E2EWin32]::PostMessage([IntPtr]$c.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null # BM_CLICK
            return $true
        }
        $c = $walker.GetNextSibling($c)
    }
    $false
}
function Invoke-SettingsButton([string]$Button) { Invoke-El (Find-ByName $main $Button $CT::Button) }
function Wait-NewFile([string]$Pattern, [int]$TimeoutSec = 20) {
    Wait-Until { Get-ChildItem $FilesDir -Filter $Pattern -ErrorAction SilentlyContinue | Where-Object Length -gt 0 | Sort-Object LastWriteTime | Select-Object -Last 1 } $TimeoutSec "file $Pattern"
}
Test 'Settings: page shows Appearance, Window & Startup, Backup & Export and About' {
    Select-Page 'Settings'
    $missing = 'Appearance', 'Window & Startup', 'Backup & Export', 'About' | Where-Object { $null -eq (Find-ByName $main $_ $CT::Text) }
    Assert (-not $missing) "missing sections: $($missing -join ', ')"
    $ver = (Find $main 'VersionText').Current.Name
    Assert ($ver -match '^Version \d') "version text '$ver'"
    $startup = Find $main 'StartWithWindows'
    $registered = $null -ne (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'eero Desktop' -ErrorAction SilentlyContinue)
    $state = $startup.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On'
    Assert ($state -eq $registered) "Start with Windows shows $state but the Run key exists=$registered"
    Save-Shot $hwnd '22-Settings' | ForEach-Object Dispose
    "$ver; start with Windows = $registered (not changed by tests)"
}
Test 'Settings: Light and Dark switch the theme immediately, then back to the Windows setting' {
    Select-Radio 'ThemeLight'; $light = Get-Luminance '23-theme-light'
    Assert ((Read-TestSettings).Theme -eq 'Light') 'Light not saved'
    Select-Radio 'ThemeDark'; $dark = Get-Luminance '24-theme-dark'
    Assert ((Read-TestSettings).Theme -eq 'Dark') 'Dark not saved'
    Select-Radio 'ThemeSystem'
    Assert ((Read-TestSettings).Theme -eq 'System') 'System not saved'
    Assert ($light -gt 0.65) "Light theme luminance $light"
    Assert ($dark -lt 0.35) "Dark theme luminance $dark"
    "Light $light, Dark $dark, back to Windows setting"
}
Test 'Settings: notification and tray options are saved' {
    $before = Read-TestSettings
    Toggle-Box 'OutageAlerts'; Toggle-Box 'StartInTray'
    $after = Read-TestSettings
    Assert ($after.OutageAlerts -ne $before.OutageAlerts) 'outage alert option not saved'
    Assert ($after.StartInTray -ne $before.StartInTray) 'start in tray option not saved'
    Toggle-Box 'OutageAlerts'; Toggle-Box 'StartInTray'
    $restored = Read-TestSettings
    Assert ($restored.OutageAlerts -eq $before.OutageAlerts -and $restored.StartInTray -eq $before.StartInTray) 'options not restored'
    'OutageAlerts and StartInTray toggled, saved, and restored'
}
Test 'Settings: back up, export snapshot (passwords removed) and devices CSV, then restore' {
    Invoke-SettingsButton 'Back Up App Settings…'
    $backup = Wait-NewFile 'eero Desktop settings*.json'
    $b = Get-Content $backup.FullName -Raw | ConvertFrom-Json
    Assert ($b.format -eq 'eero-desktop-backup' -and $b.settings) 'backup file has the wrong format'
    Assert ($null -ne $b.history -and $null -ne $b.history.speed_tests -and $null -ne $b.history.device_events) 'backup has no history'
    Assert (-not ((Get-Content $backup.FullName -Raw) -match 'user_token|X-User-Token')) 'backup contains a session token'

    Invoke-SettingsButton 'Export Network Snapshot…'
    $snap = Wait-NewFile '*snapshot*.json' 40
    Start-Sleep -Milliseconds 500
    $text = Get-Content $snap.FullName -Raw
    $s = $text | ConvertFrom-Json
    Assert ($s.network -and $s.devices) 'snapshot is missing network or devices'
    $leaks = [regex]::Matches($text, '"[^"]*password[^"]*"\s*:\s*"(?!\(removed\))[^"]+"')
    Assert ($leaks.Count -eq 0) "snapshot contains $($leaks.Count) password value(s)"

    Invoke-SettingsButton 'Export Devices to CSV…'
    $csv = Wait-NewFile '*devices*.csv'
    Start-Sleep -Milliseconds 300
    $rows = @(Import-Csv $csv.FullName)
    Assert ($rows.Count -ge 1 -and $rows[0].PSObject.Properties.Name -contains 'MAC') 'CSV has no device rows'

    # Restore: change an option, restore the backup, confirm, and check the option came back.
    Toggle-Box 'NewDeviceAlerts'
    $changed = (Read-TestSettings).NewDeviceAlerts
    Assert ($changed -ne $b.settings.NewDeviceAlerts) 'option should differ from the backup before restoring'
    Invoke-SettingsButton 'Restore App Settings…'
    # Click Yes until the confirmation is gone: a click that arrives before the box is ready is ignored. The box exists only
# once, so a repeated click can never restore twice.
    Wait-Until { Click-MessageBoxButton 'Yes' } 10 'restore confirmation (Yes button)' | Out-Null
    Wait-Until {
        $box = $main.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::ClassNameProperty), '#32770'))
        if ($box) { Click-MessageBoxButton 'Yes' | Out-Null; Start-Sleep -Milliseconds 400; $false } else { $true }
    } 10 'restore confirmation to close' | Out-Null
    Wait-Until { (Read-TestSettings).NewDeviceAlerts -eq $b.settings.NewDeviceAlerts } 10 'restored setting' | Out-Null
    Assert ((Find $main 'NewDeviceAlerts').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq $(if ($b.settings.NewDeviceAlerts) { 'On' } else { 'Off' })) 'page not refreshed after restore'
    "backup OK; snapshot $([math]::Round($snap.Length / 1KB)) KB, no passwords; CSV $($rows.Count) devices; restore OK"
}
Test 'Settings › About: dnspyre is credited with links to its GitHub source and documentation' {
    Select-Page 'Settings'
    $src = Find $main 'DnspyreSourceLink'; $docs = Find $main 'DnspyreDocsLink'
    Assert ($null -ne $src -and $null -ne $docs) 'dnspyre links missing from About'
    Assert ($null -ne (Find $main 'DnspyreLicenseButton')) 'View dnspyre License button missing'
    $credit = (Find $main 'DnspyreCredit').Current.Name
    $disclaimer = (Find $main 'Disclaimer').Current.Name
    Assert ($disclaimer -match 'not developed, endorsed or supported by eero LLC or Amazon\.com, Inc\.') "disclaimer text: '$disclaimer'"
    "links: $($src.Current.Name), $($docs.Current.Name)"
}
Test 'DNS Benchmark: dnspyre ranks providers, recommends the fastest, shows graphs (nothing switched)' {
    Select-Page 'Advanced'
    Invoke-El (Find-ByName $main 'Benchmark DNS Servers…' $CT::Button)
    $win = Get-OwnedWindow 'DNS Benchmark'
    Invoke-El ($win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::AutomationIdProperty), 'BenchmarkRun')))
    $statusEl = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::AutomationIdProperty), 'BenchmarkStatus'))
    $done = Wait-Until { $s = $statusEl.Current.Name; if ($s -match '^(Finished|Benchmark failed|Benchmark cancelled)') { $s } } 180 'benchmark to finish'
    Assert ($done -like 'Finished*') $done
    $grid = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::AutomationIdProperty), 'BenchmarkGrid'))
    $rows = Get-Rows $grid
    Assert ($rows -ge 5) "only $rows providers listed"
    $note = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::AutomationIdProperty), 'BenchmarkConnection')).Current.Name
    Assert ($note -match 'from this PC') "connection note '$note'"
    $rec = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::AutomationIdProperty), 'BenchmarkRecommendation')).Current.Name
    Assert ($rec -match "^(Fastest:|You're already using the fastest)") "recommendation '$rec'"
    $graphs = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::AutomationIdProperty), 'BenchmarkGraphs'))
    $tabs = @($graphs.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::TabItem)))
    Assert ($tabs.Count -ge 3) "only $($tabs.Count) dnspyre graph tabs"
    Save-Shot ([IntPtr]$win.Current.NativeWindowHandle) '25-DNS-Benchmark' | ForEach-Object Dispose
    $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-Until { -not (Test-OwnedWindowOpen 'DNS Benchmark') } 10 'benchmark window to close' | Out-Null
    "$rows providers; $rec; $($tabs.Count) graphs"
}
Test 'History: speed tests, outages and the DNS benchmark appear in charts, tiles and tables' {
    # Seed one finished internet outage into the throwaway data folder (network id from the monitor's known-devices list).
    $kf = Join-Path $DataDir 'known-devices.json'
    $netId = Wait-Until { if (Test-Path $kf) { (Get-Content $kf -Raw | ConvertFrom-Json).PSObject.Properties.Name | Select-Object -First 1 } } 30 'network id in known-devices.json'
    $hist = Join-Path $DataDir 'history'; New-Item -ItemType Directory -Force $hist | Out-Null
    $down = (Get-Date).ToUniversalTime().AddHours(-3)
    @(
        @{ Network = $netId; Utc = $down.ToString('o'); Kind = 'internet'; Subject = 'Internet'; State = 'down'; Detail = 'E2E seeded outage' }
        @{ Network = $netId; Utc = $down.AddMinutes(12).ToString('o'); Kind = 'internet'; Subject = 'Internet'; State = 'up'; Detail = '' }
    ) | ForEach-Object { ($_ | ConvertTo-Json -Compress) } | Add-Content (Join-Path $hist 'events.jsonl')
    Select-Page 'Settings'; Select-Page 'History'   # reload the page
    $count = Wait-Until { $e = Find $main 'OutageCount'; if ($e -and $e.Current.Name -match '^Internet outages?: [1-9]') { $e.Current.Name } } 30 'outage tile'
    Assert ((Get-Rows (Find $main 'OutageGrid')) -ge 1) 'outage table empty'
    Assert ((Find $main 'OutageDowntime').Current.Name -match '12 min') "downtime tile '$((Find $main 'OutageDowntime').Current.Name)'"
    $dns = Get-Rows (Find $main 'DnsHistoryGrid')
    Assert ($dns -ge 1) 'the DNS benchmark run is not in history'
    $dnsChart = (Find $main 'DnsHistoryChart').Current.Name
    Assert ($dnsChart -match 'points') "DNS chart has no data: '$dnsChart'"
    $speed = Get-Rows (Find $main 'SpeedHistoryGrid')
    Assert ($speed -eq 0 -or (Find $main 'SpeedChart').Current.Name -match 'Download') 'speed chart has no series'
    Save-Shot $hwnd '26-History' | ForEach-Object Dispose
    $scroll = (Find $main 'HistoryScroll').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $scroll.SetScrollPercent(-1, 100); Start-Sleep -Milliseconds 500
    Save-Shot $hwnd '27-History-lower' | ForEach-Object Dispose
    $scroll.SetScrollPercent(-1, 0)
    "$count; $dns DNS run(s); $speed speed test(s)"
}
Test 'History › Devices: devices seen by the monitor are listed with a timeline' {
    Select-Page 'Settings'; Select-Page 'History'
    $grid = Find $main 'DeviceHistoryGrid'
    $rows = Get-Rows $grid
    Assert ($rows -ge 1) 'no devices in History › Devices'
    $first = $grid.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $CT::DataItem))
    $first.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 500
    $events = Get-Rows (Find $main 'DeviceEventsGrid')
    Assert ($events -ge 1) 'selected device has no timeline'
    $online = (Find $main 'DevicesOnline').Current.Name
    "$rows device(s); $online; first device has $events event(s)"
}
Test 'Monthly summary: last month is summarized once, and Settings can show it again' {
    $line = Wait-Until { if (Test-Path $alertsLog) { Get-Content $alertsLog | Where-Object { $_ -match '\sSUMMARY\s' } | Select-Object -First 1 } } 30 'SUMMARY entry in alerts.log'
    Select-Page 'Settings'
    Invoke-El (Find $main 'MonthlySummaryButton')
    $status = Wait-Until { $s = (Find $main 'StatusText').Current.Name; if ($s -match ' on .+: (Speed|Internet|DNS)|Nothing to summarize') { $s } } 10 'summary in the status bar'
    Assert ($status -notmatch 'Nothing to summarize') $status
    ($line -replace '^\S+ \S+\s+SUMMARY\s+', '')
}
Test 'Undo: the button appears for a saved change and asking No sends nothing' {
    $kf = Join-Path $DataDir 'known-devices.json'
    $netId = (Get-Content $kf -Raw | ConvertFrom-Json).PSObject.Properties.Name | Select-Object -First 1
    $undoFile = Join-Path $DataDir 'undo.json'
    # A harmless saved change (UPnP): the test only opens the confirmation and answers No.
    @{ $netId = @{ NetworkId = $netId; Utc = (Get-Date).ToUniversalTime().AddMinutes(-5).ToString('o'); Done = 'UPnP turned on (E2E)'; UndoText = 'Turn UPnP off again';
        Kind = 'toggle'; Args = @{ key = 'upnp'; value = $false }; RestartsAll = $false } } | ConvertTo-Json -Depth 5 | Set-Content $undoFile
    $before = Get-Content $undoFile -Raw
    Invoke-El (Find-ByName $main 'Refresh' $CT::Button)
    $btn = Wait-Until { $u = Find $main 'UndoButton'; if ($u -and -not $u.Current.IsOffscreen) { $u } } 15 'Undo Last Change button'
    Invoke-El $btn
    $text = ''
    try {
        $text = Wait-Until {
            $box = $main.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::ClassNameProperty), '#32770'))
            if ($box) { $t = [E2EWin32]::MessageBoxText([IntPtr]$box.Current.NativeWindowHandle); if ($t) { $t } }
        } 10 'undo confirmation text'
    }
    finally { Wait-Until { Click-MessageBoxButton 'No' } 10 'No button' | Out-Null }   # always answer No, even if a check fails
    Assert ($text -match 'Turn UPnP off again') "confirmation text: $text"
    Wait-Until { -not $main.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::ClassNameProperty), '#32770')) } 10 'confirmation to close' | Out-Null
    Start-Sleep 1
    Assert ((Get-Content $undoFile -Raw) -eq $before) 'undo.json changed although No was chosen'
    Assert (-not (Get-Content $alertsLog | Where-Object { $_ -match '\sCHANGE\s' })) 'a change was logged although No was chosen'
    Remove-Item $undoFile
    'button shown; confirmation explained the undo; No sent nothing'
}
Test 'Settings › About: eero app compatibility check reads Google Play' {
    Select-Page 'Settings'
    Invoke-El (Find $main 'EeroAppCheckButton')
    $text = Wait-Until { $t = (Find $main 'EeroAppStatus').Current.Name; if ($t -match '^(Up to date|eero has released)') { $t } } 30 'eero app status'
    $text
}
Close-StrayDialogs
Test 'API Console: GET 2.2/account returns the account JSON' {
    (Find-ByName (Find $main 'Nav') 'API Console' $CT::ListItem).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $path = Wait-Until { Find $main 'PathBox' } 10 'PathBox'
    $method = Find $main 'MethodCombo'
    $sel = $method.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    Assert ($sel.Count -and $sel[0].Current.Name -eq 'GET') 'method is not GET — refusing to send'
    Set-Value $path '2.2/account'
    Invoke-El (Find-ByName $main 'Send' $CT::Button)
    $result = Find $main 'ResultBox'
    $text = Wait-Until { $t = Get-Value $result; if ($t -match '"networks"') { $t } } 20 'account JSON in the response box'
    Save-Shot $hwnd '20-API-Console-result' | ForEach-Object Dispose
    "$($text.Length) chars; label '$((Find $main 'ResultLabel').Current.Name)'"
}
Test 'Refresh: reloading the network keeps the app healthy' {
    Invoke-El (Find-ByName $main 'Refresh' $CT::Button)
    $status = Find $main 'StatusText'
    Wait-Until { $s = $status.Current.Name; -not ($s.EndsWith('…') -or $s.StartsWith('Loading')) } $PageTimeoutSec 'refresh' | Out-Null
    Assert (-not $status.Current.Name.StartsWith('Error')) "status bar: $($status.Current.Name)"
    "status '$($status.Current.Name)'"
}
# ---------------------------------------------------------------- Background monitor & alerts (throwaway data folder)
$alertsLog = Join-Path $DataDir 'alerts.log'
$knownFile = Join-Path $DataDir 'known-devices.json'
Test 'Monitor: first check records the known devices without alerting' {
    $line = Wait-Until { if (Test-Path $alertsLog) { Get-Content $alertsLog | Where-Object { $_ -match 'BASELINE' } | Select-Object -First 1 } } 30 'BASELINE entry in alerts.log'
    $alerts = @(Get-Content $alertsLog | Where-Object { $_ -match '\sALERT\s' })
    Assert ($alerts.Count -eq 0) "unexpected alert(s) on a healthy network: $($alerts -join ' | ')"
    ($line -replace '^\S+ \S+\s+BASELINE\s+', '')
}
Test 'Alerts: a device never seen before raises a "new device" alert' {
    $known = Get-Content $knownFile -Raw | ConvertFrom-Json
    $netId = ($known.PSObject.Properties | Select-Object -First 1).Name
    $macs = @($known.$netId)
    Assert ($macs.Count -ge 1) 'known-devices.json has no devices'
    $forget = $macs[0]
    $known.$netId = @($macs | Select-Object -Skip 1)       # pretend the first device has never been seen
    $known | ConvertTo-Json -Depth 5 | Set-Content $knownFile
    $alert = Wait-Until { Get-Content $alertsLog | Where-Object { $_ -match 'ALERT\s+New device on' } | Select-Object -Last 1 } 30 "new-device alert for $forget"
    Assert ((Get-Content $knownFile -Raw) -match [regex]::Escape($forget)) 'device was not added back to the known list'
    ($alert -replace '^\S+ \S+\s+ALERT\s+', '')
}

# ---------------------------------------------------------------- Tray, single instance, exit
Test 'Tray: closing the window hides it to the tray and the app keeps running' {
    $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-Until { -not [E2EWin32]::IsWindowVisible($hwnd) } 10 'window to hide' | Out-Null
    Start-Sleep 2
    Assert (-not $app.HasExited) 'app exited instead of staying in the tray'
    'window hidden, process running'
}
Test 'Single instance: launching again reopens the existing window' {
    $second = Start-App $AppEnv
    Assert ($second.WaitForExit(15000)) 'second copy kept running'
    Assert ($second.ExitCode -eq 0) "second copy crashed (exit code $($second.ExitCode))"
    Wait-Until { [E2EWin32]::IsWindowVisible($hwnd) } 10 'existing window to reappear' | Out-Null
    Assert (-not $app.HasExited) 'original app exited'
    "second copy exited (code $($second.ExitCode)); original window shown again"
}
Test 'Exit: "EeroDesktop.exe --exit" quits the running app (session kept)' {
    $exiter = Start-Process $Exe -ArgumentList '--exit' -PassThru -Wait
    Assert ($exiter.ExitCode -eq 0) "the --exit copy crashed (exit code $($exiter.ExitCode))"
    Assert ($app.WaitForExit(15000)) 'app still running 15s after --exit'
    Assert (Test-Path (Join-Path $env:APPDATA 'EeroDesktop\session.bin')) 'saved session was removed'
    "exit code $($app.ExitCode)"
}
Stop-App $app

Test 'Startup: "Start in the system tray" opens only the tray icon until asked' {
    $settingsFile = Join-Path $DataDir 'settings.json'
    $s = Get-Content $settingsFile -Raw | ConvertFrom-Json
    $s.StartInTray = $true; $s | ConvertTo-Json | Set-Content $settingsFile
    $trayApp = Start-App $AppEnv
    try {
        # Give it time to sign in and start monitoring (the monitor's first check writes to alerts.log).
        Start-Sleep -Seconds 8
        Assert (-not $trayApp.HasExited) 'app exited instead of starting in the tray'
        $w = Get-TopWindow $trayApp.Id 'eero Desktop'
        Assert ($null -eq $w -or -not [E2EWin32]::IsWindowVisible([IntPtr]$w.Current.NativeWindowHandle)) 'main window is visible at startup'
        $second = Start-App $AppEnv; $second.WaitForExit(15000) | Out-Null
        $shown = Wait-Until { $x = Get-TopWindow $trayApp.Id 'eero Desktop'; if ($x -and [E2EWin32]::IsWindowVisible([IntPtr]$x.Current.NativeWindowHandle)) { $x } } 15 'window to open from the tray'
        Start-Process $Exe -ArgumentList '--exit' -Wait
        Assert ($trayApp.WaitForExit(15000)) 'app still running after --exit'
        'started hidden with only the tray icon; window opened on request; exited cleanly'
    }
    finally {
        Stop-App $trayApp
        $s.StartInTray = $false; $s | ConvertTo-Json | Set-Content $settingsFile
    }
}

} finally {
    if ($LightMode) {
        if ($null -eq $originalTheme) { Remove-ItemProperty $themeKey AppsUseLightTheme -ErrorAction SilentlyContinue }
        else { Set-ItemProperty $themeKey AppsUseLightTheme $originalTheme -Type DWord }
        [E2EWin32]::BroadcastThemeChange()
        Log "Restored Windows app theme (AppsUseLightTheme=$originalTheme)"
    }
}

# ---------------------------------------------------------------- Summary
$Results | Export-Csv "$OutDir\results.csv" -NoTypeInformation
$pass = @($Results | Where-Object Result -eq 'PASS').Count; $fail = @($Results | Where-Object Result -eq 'FAIL').Count
Write-Host ''
$Results | Format-Table Result, Seconds, Test, Detail -AutoSize -Wrap | Out-String -Width 220 | Write-Host
Log "$pass passed, $fail failed — $OutDir"
exit ([int]($fail -gt 0))
