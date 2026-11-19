#requires -Version 5.1
<#
    Screenshot harness for OBSIDIAN.

    Launches the sample targets, drives the GUI with coordinate clicks (the
    window is pinned to 0,0 so the sidebar coordinates are stable) and captures
    every page with PrintWindow so the result does not depend on window focus.

    IMPORTANT: run this exclusively â€” it starts and stops OBSIDIAN/TestTarget
    processes and therefore must not overlap with another capture or a manual
    session on the same machine.

    Usage:  powershell -ExecutionPolicy Bypass -File tools\ui-capture.ps1
            powershell -ExecutionPolicy Bypass -File tools\ui-capture.ps1 -SkipLight
#>
[CmdletBinding()]
param(
    [string] $OutDirectory = "artifacts\shots",
    # Light-mode captures need OBSIDIAN.exe to support --theme (added with the
    # runtime theme system). Skip them when running against an older build.
    [switch] $SkipLight
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root
$out = Join-Path $root $OutDirectory
New-Item -ItemType Directory -Force $out | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ObsidianShot {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static void Focus(IntPtr hwnd) {
        IntPtr foreground = GetForegroundWindow();
        uint pid;
        uint foregroundThread = GetWindowThreadProcessId(foreground, out pid);
        uint current = GetCurrentThreadId();
        AttachThreadInput(current, foregroundThread, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        AttachThreadInput(current, foregroundThread, false);
        System.Threading.Thread.Sleep(250);
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(150);
    }
}
'@
[ObsidianShot]::SetProcessDPIAware() | Out-Null

$exePath = Join-Path $root 'src\OBSIDIAN\bin\Debug\net10.0-windows\OBSIDIAN.exe'
$targetPath = Join-Path $root 'samples\TestTarget\bin\Debug\net10.0\ObsidianTestTarget.exe'
$openPath = Join-Path $root 'artifacts\test-dumps\target\ObsidianTestTarget.dll'

if (-not (Test-Path $exePath)) {
    throw "Build first: $exePath is missing. Run: pwsh -File tools/build.ps1 -Target app"
}

function Stop-App {
    Get-Process OBSIDIAN -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process ObsidianTestTarget -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

function Start-App([string[]] $Arguments) {
    $process = Start-Process $exePath -ArgumentList $Arguments -PassThru -WindowStyle Normal `
        -RedirectStandardOutput (Join-Path $root 'artifacts\app-out.log') `
        -RedirectStandardError (Join-Path $root 'artifacts\app-err.log')

    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.MainWindowHandle -ne 0) { break }
    }

    Start-Sleep -Milliseconds 800
    $handle = $process.MainWindowHandle
    [ObsidianShot]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, 1600, 1000, 0x0040) | Out-Null
    [ObsidianShot]::Focus($handle)
    return @{ Process = $process; Handle = $handle }
}

function Save-Shot([IntPtr] $Handle, [string] $Name) {
    $rect = New-Object ObsidianShot+RECT
    [ObsidianShot]::GetWindowRect($Handle, [ref] $rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    [ObsidianShot]::PrintWindow($Handle, $hdc, 2) | Out-Null
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()

    $path = Join-Path $out "$Name.png"
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "  captured $Name" -ForegroundColor DarkGray
}

function Click-Nav([IntPtr] $Handle, [string] $Name) {
    <#
        Navigate by accessible name (Theme.xaml sets AutomationProperties.Name on
        the NavItem container). Falls back to a coordinate click only if the
        automation tree cannot resolve the row, so a layout tweak no longer
        silently captures the wrong page.
    #>
    [ObsidianShot]::Focus($Handle)
    $element = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $condition = New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)
    $row = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($row -ne $null) {
        $rect = $row.Current.BoundingRectangle
        if ($rect.Width -gt 0 -and $rect.Height -gt 0) {
            [ObsidianShot]::Click([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
            Start-Sleep -Milliseconds 900
            return
        }
    }

    Write-Host "  nav '$Name' not found by automation; using legacy coordinates" -ForegroundColor Yellow
    $legacy = @{ Targets = 86; Workspace = 117; Inspector = 148; Injection = 179; Activity = 210; Settings = 241 }
    if (-not $legacy.ContainsKey($Name)) {
        Write-Host "  no legacy coordinate for '$Name'" -ForegroundColor Yellow
        return
    }

    $rect = New-Object ObsidianShot+RECT
    [ObsidianShot]::GetWindowRect($Handle, [ref] $rect) | Out-Null
    $scale = ($rect.Right - $rect.Left) / 1600.0
    if ($scale -le 0) { $scale = 1 }
    [ObsidianShot]::Click([int](104 * $scale), [int]($legacy[$Name] * $scale))
    Start-Sleep -Milliseconds 900
}

function Select-Tab([IntPtr] $Handle, [string] $Name) {
    <#
        Select an inspector tab by accessible name. The automation tree is
        populated asynchronously while the page animates and the assembly loads,
        so poll instead of assuming the tab already exists.
    #>
    $element = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)

    $tab = $null
    for ($attempt = 0; $attempt -lt 24; $attempt++) {
        $tab = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
        if ($tab -ne $null) { break }
        Start-Sleep -Milliseconds 250
    }

    if ($tab -eq $null) {
        Write-Host "  tab '$Name' not found" -ForegroundColor Yellow
        return $false
    }

    try {
        $pattern = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $pattern.Select()
    }
    catch {
        $rect = $tab.Current.BoundingRectangle
        if ($rect.Width -le 0) { Write-Host "  tab '$Name' not clickable" -ForegroundColor Yellow; return $false }
        [ObsidianShot]::Click([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
    }

    Start-Sleep -Milliseconds 1200
    return $true
}

$lockPath = Join-Path $root 'artifacts\.gui-lock'
if (Test-Path $lockPath) {
    throw "Another GUI verification run is active ($lockPath). Wait for it to finish and retry."
}
New-Item -ItemType File -Force $lockPath | Out-Null

function Start-TestTarget {
    if (-not (Test-Path $targetPath)) {
        throw "Build the samples first: $targetPath is missing."
    }
    return Start-Process $targetPath -ArgumentList '1800' -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $root 'artifacts\t1.log') `
        -RedirectStandardError (Join-Path $root 'artifacts\t1e.log')
}

# ---------------------------------------------------------------- phase A: targets
Write-Host "Phase A - targets page" -ForegroundColor Cyan
Stop-App
$target = Start-TestTarget
Start-Sleep -Milliseconds 1400

$app = Start-App @('--select', $target.Id)
Start-Sleep -Seconds 2
Save-Shot $app.Handle '01-targets'
Stop-App

# ---------------------------------------------------------------- phase B: pages
Write-Host "Phase B - workspace, inspector, injection, activity, settings" -ForegroundColor Cyan
Start-Sleep -Milliseconds 400
$target = Start-TestTarget
Start-Sleep -Milliseconds 1200

$arguments = @('--attach', $target.Id, '--dump')
if (Test-Path $openPath) { $arguments += @('--open', $openPath) }
$app = Start-App $arguments
Start-Sleep -Seconds 8
Save-Shot $app.Handle '02-workspace'

Click-Nav $app.Handle 'Inspector'
Start-Sleep -Seconds 2
Save-Shot $app.Handle '03-inspector-overview'

if (Select-Tab $app.Handle 'EXPLORER') {
    Start-Sleep -Seconds 2
    Save-Shot $app.Handle '04-inspector-explorer'
}

if (Select-Tab $app.Handle 'STRINGS') {
    Start-Sleep -Seconds 1
    Save-Shot $app.Handle '05-inspector-strings'
}

Click-Nav $app.Handle 'Injection'
Start-Sleep -Seconds 1
Save-Shot $app.Handle '06-injection'

Click-Nav $app.Handle 'Activity'
Start-Sleep -Milliseconds 600
Save-Shot $app.Handle '07-activity'

Click-Nav $app.Handle 'Settings'
Start-Sleep -Milliseconds 900
Save-Shot $app.Handle '08-settings'

Click-Nav $app.Handle 'Targets'
Start-Sleep -Milliseconds 900
Save-Shot $app.Handle '09-targets-live'

Click-Nav $app.Handle 'Workspace'
Start-Sleep -Milliseconds 900
Stop-App

# ---------------------------------------------------------------- phase C: light theme
if (-not $SkipLight) {
    Write-Host "Phase C - light theme" -ForegroundColor Cyan
    Start-Sleep -Milliseconds 400
    $target = Start-TestTarget
    Start-Sleep -Milliseconds 1200

    $lightArguments = @('--theme', 'light', '--attach', $target.Id, '--dump')
    if (Test-Path $openPath) { $lightArguments += @('--open', $openPath) }
    $app = Start-App $lightArguments
    Start-Sleep -Seconds 9
    Save-Shot $app.Handle '10-light-workspace'

    Click-Nav $app.Handle 'Inspector'
    Start-Sleep -Seconds 2
    Save-Shot $app.Handle '11-light-inspector-overview'

    Click-Nav $app.Handle 'Injection'
    Start-Sleep -Seconds 1
    Save-Shot $app.Handle '12-light-injection'

    Click-Nav $app.Handle 'Settings'
    Start-Sleep -Milliseconds 900
    Save-Shot $app.Handle '13-light-settings'

    Click-Nav $app.Handle 'Targets'
    Start-Sleep -Milliseconds 900
    Save-Shot $app.Handle '14-light-targets'
    Stop-App
}

# Leave no stray windows or targets behind: the harness is also used in CI.
Stop-App
Remove-Item $lockPath -Force -ErrorAction SilentlyContinue

Write-Host "Done. Screenshots in $out" -ForegroundColor Green

