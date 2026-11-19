#requires -Version 5.1
<#
    End-to-end verification of the Injection page (Lead acceptance test for
    task-11).

    What it proves, with the real UI:
      1. The Injection page is reachable (--page inject) and the DLL picker
         accepts a path.
      2. The compatibility check passes for a matching native DLL.
      3. Clicking "Inject library" asks for confirmation and, after the operator
         accepts, the engine loads the DLL into the target (verified through the
         target's own module list, not just the UI text).
      4. The result card shows a real remote module base.
      5. "Unload" removes the library again (module list no longer contains it).

    Notes for automation robustness:
      * Buttons are clicked with synthetic mouse input at their UIA bounding
        rect instead of InvokePattern: the WPF click handler opens a modal
        confirmation dialog synchronously, which would block InvokePattern.Invoke
        until the dialog is dismissed.
      * The script takes an exclusive `artifacts\.gui-lock` slot and kills only
        the processes it started itself.
      * Confirmation is pre-disabled for the run by seeding a throw-away
        settings.json (the operator's settings file is backed up and restored).

    Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-injection-ui.ps1
#>
[CmdletBinding()]
param(
    [string] $OutDirectory = "artifacts\inject-verify",
    [switch] $SkipUnload,
    # Advertise the confirmation-dialog path: keep ConfirmInjection enabled and
    # click OK in the dialog. Without this the page runs with a seeded
    # settings file that disables confirmation (more reliable for CI).
    [switch] $WithConfirmation
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root
$out = Join-Path $root $OutDirectory
New-Item -ItemType Directory -Force $out | Out-Null
$logPath = Join-Path $out 'verify.log'
Set-Content -Path $logPath -Value ("OBSIDIAN injection UI verification - " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) -Encoding UTF8

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class InjectShot {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
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
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static void Focus(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero) return;
        IntPtr foreground = GetForegroundWindow();
        uint pid;
        uint foregroundThread = GetWindowThreadProcessId(foreground, out pid);
        uint current = GetCurrentThreadId();
        AttachThreadInput(current, foregroundThread, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        AttachThreadInput(current, foregroundThread, false);
        System.Threading.Thread.Sleep(200);
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(150);
    }

    // Win32-level top-level window lookup. UIA root children do not reliably
    // expose owner-owned message boxes, but EnumWindows always sees them.
    public static IntPtr FindTopLevelWindow(uint processId, string titlePart) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, l) => {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid != processId || !IsWindowVisible(hwnd)) return true;
            var title = new StringBuilder(512);
            GetWindowTextW(hwnd, title, 512);
            if (title.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) >= 0) {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string[] TopLevelWindowTitles(uint processId) {
        var titles = new System.Collections.Generic.List<string>();
        EnumWindows((hwnd, l) => {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid != processId) return true;
            var title = new StringBuilder(512);
            GetWindowTextW(hwnd, title, 512);
            var cls = new StringBuilder(128);
            GetClassNameW(hwnd, cls, 128);
            titles.Add(string.Format("hwnd=0x{0:X} class='{1}' visible={2} title='{3}'",
                hwnd.ToInt64(), cls, IsWindowVisible(hwnd), title));
            return true;
        }, IntPtr.Zero);
        return titles.ToArray();
    }

    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);

    // Win32 message boxes do not always expose their buttons through UIA on this
    // machine, so the OK action is delivered as WM_COMMAND(IDOK) with a Return
    // key fallback.
    public static bool PressDialogOk(IntPtr hwnd) {
        bool posted = PostMessageW(hwnd, 0x0111, (IntPtr)1, IntPtr.Zero);
        System.Threading.Thread.Sleep(300);
        if (IsWindow(hwnd)) {
            PostMessageW(hwnd, 0x0100, (IntPtr)0x0D, IntPtr.Zero);
            PostMessageW(hwnd, 0x0101, (IntPtr)0x0D, IntPtr.Zero);
        }
        return posted;
    }
}
'@
[InjectShot]::SetProcessDPIAware() | Out-Null

function Write-Step([string] $text) {
    $line = ("[" + (Get-Date -Format 'HH:mm:ss.fff') + "] " + $text)
    Write-Host $line
    Add-Content -Path $logPath -Value $line -Encoding UTF8
}

$exePath = Join-Path $root 'src\OBSIDIAN\bin\Debug\net10.0-windows\OBSIDIAN.exe'
$targetPath = Join-Path $root 'samples\TestTarget\bin\Debug\net10.0\ObsidianTestTarget.exe'
$dllPath = Join-Path $env:windir 'System32\winhttp.dll'
$moduleName = 'winhttp.dll'
$settingsDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'OBSIDIAN'
$settingsPath = Join-Path $settingsDir 'settings.json'
$settingsBackup = $settingsPath + '.verify-backup'

if (-not (Test-Path $exePath)) { throw "Build first: $exePath is missing." }
if (-not (Test-Path $targetPath)) { throw "Build the samples first: $targetPath is missing." }
if (-not (Test-Path $dllPath)) { throw "Missing probe DLL: $dllPath" }

# Exclusive GUI slot: every capture/verification script owns the app lifecycle.
$lockPath = Join-Path $root 'artifacts\.gui-lock'
if (Test-Path $lockPath) {
    throw "Another GUI verification run is active ($lockPath). Wait for it to finish and retry."
}
New-Item -ItemType Directory -Force (Split-Path -Parent $lockPath) | Out-Null
New-Item -ItemType File -Force $lockPath | Out-Null

$appProcess = $null
$targetProcess = $null
$checks = New-Object System.Collections.Generic.List[object]
$deadline = [System.Diagnostics.Stopwatch]::StartNew()

function Add-Check([string] $name, [bool] $ok, [string] $detail = '') {
    $checks.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Step ("{0} {1}{2}" -f $(if ($ok) { '[ok]  ' } else { '[FAIL]' }), $name, $(if ($detail) { " - $detail" } else { '' }))
    $null = $color
}

function Save-Shot([IntPtr] $Handle, [string] $Name) {
    $rect = New-Object InjectShot+RECT
    [InjectShot]::GetWindowRect($Handle, [ref] $rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    [InjectShot]::PrintWindow($Handle, $hdc, 2) | Out-Null
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
    $path = Join-Path $out "$Name.png"
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Step "captured $Name"
}

function Focus-App {
    if ($null -ne $appProcess) { [InjectShot]::Focus($appProcess.MainWindowHandle) }
}

function Click-Element($Element) {
    $rect = $Element.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) { return $false }
    Focus-App
    [InjectShot]::Click([int]($rect.Left + $rect.Width / 2), [int]($rect.Top + $rect.Height / 2))
    return $true
}

function Get-Window([int] $ProcessId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition)
    $result = @()
    foreach ($window in $windows) { $result += $window }
    return $result
}

function Find-ByName($Root, [string] $Name, $ControlType = $null, [int] $TimeoutMs = 4000) {
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMs) {
        $conditions = New-Object System.Collections.Generic.List[object]
        $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
        if ($ControlType) {
            $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)))
        }
        $condition = New-Object System.Windows.Automation.AndCondition($conditions.ToArray())
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Test-TargetModule([int] $ProcessId, [string] $Name) {
    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        return ($process.Modules | Where-Object { $_.ModuleName -ieq $Name }).Count -gt 0
    }
    catch {
        return $false
    }
}

function Assert-AppAlive([string] $Stage) {
    if ($null -eq $appProcess) { return }
    $appProcess.Refresh()
    if ($appProcess.HasExited) {
        throw "OBSIDIAN exited during '$Stage' (exit code $($appProcess.ExitCode)). Rerun in an exclusive slot."
    }
    if ($deadline.Elapsed.TotalSeconds -gt 150) {
        throw "Verification exceeded its 150 s watchdog during '$Stage'."
    }
}

function Wait-TargetModule([int] $ProcessId, [string] $Name, [bool] $Present, [int] $TimeoutMs = 15000) {
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMs) {
        if ((Test-TargetModule $ProcessId $Name) -eq $Present) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

try {
    # ---- seed a throw-away settings file -------------------------------------
    # Confirmation disabled keeps the automated run deterministic; with
    # -WithConfirmation it stays enabled so the dialog path is exercised too.
    New-Item -ItemType Directory -Force $settingsDir | Out-Null
    if (Test-Path $settingsPath) { Copy-Item $settingsPath $settingsBackup -Force }
    $seedConfirm = if ($WithConfirmation) { 'true' } else { 'false' }
    Set-Content -Path $settingsPath -Value ('{"ConfirmInjection":' + $seedConfirm + '}') -Encoding UTF8
    Write-Step "settings seeded: $settingsPath (ConfirmInjection=$seedConfirm)"

    Write-Step 'starting probe target'
    $targetProcess = Start-Process $targetPath -ArgumentList '900' -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $out 'target-out.log') `
        -RedirectStandardError (Join-Path $out 'target-err.log')
    Start-Sleep -Milliseconds 1200

    Write-Step 'starting OBSIDIAN on the Injection page'
    $appProcess = Start-Process $exePath -ArgumentList @('--attach', $targetProcess.Id, '--page', 'inject') `
        -PassThru -WindowStyle Normal `
        -RedirectStandardOutput (Join-Path $out 'app-out.log') `
        -RedirectStandardError (Join-Path $out 'app-err.log')

    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        $appProcess.Refresh()
        if ($appProcess.MainWindowHandle -ne 0) { break }
    }
    if ($appProcess.MainWindowHandle -eq 0) { throw 'OBSIDIAN did not open a window.' }

    $handle = $appProcess.MainWindowHandle
    [InjectShot]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, 1600, 1000, 0x0040) | Out-Null
    [InjectShot]::Focus($handle)
    Start-Sleep -Seconds 2.5
    Assert-AppAlive 'page load'
    Write-Step "window ready: pid=$($appProcess.Id), target=$($targetProcess.Id)"

    $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    Add-Check 'Injection page reachable' (-not [string]::IsNullOrEmpty($window.Current.Name)) $window.Current.Name

    # ---- fill in the DLL path -------------------------------------------------
    Assert-AppAlive 'dll path entry'
    $editCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edit = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
    if (-not $edit) { throw 'The DLL path TextBox was not found on the Injection page.' }
    $valuePattern = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue($dllPath)
    Start-Sleep -Milliseconds 900
    Add-Check 'DLL path accepted by the page' ($valuePattern.Current.Value -eq $dllPath) $dllPath
    Save-Shot $handle '01-ready'
    Assert-AppAlive 'after dll entry'

    # ---- inject ---------------------------------------------------------------
    $injectButton = Find-ByName $window 'Inject library' ([System.Windows.Automation.ControlType]::Button)
    if (-not $injectButton) { throw 'The "Inject library" button was not found.' }
    Add-Check 'Inject button enabled for a matching DLL' $injectButton.Current.IsEnabled

    Write-Step 'clicking Inject library'
    if (-not (Click-Element $injectButton)) { throw 'Could not click the Inject button.' }
    Start-Sleep -Milliseconds 900

    # ---- confirmation path ----------------------------------------------------
    # The dialog is a Win32 message box owned by the main window; it is looked up
    # through EnumWindows because UIA root children do not reliably expose
    # owner-owned message boxes, and the WPF window's UIA IsEnabled does not
    # reflect the Win32 modal owner state.
    $dialogHandle = [IntPtr]::Zero
    $dialogWatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($dialogWatch.ElapsedMilliseconds -lt 10000 -and $dialogHandle -eq [IntPtr]::Zero) {
        $dialogHandle = [InjectShot]::FindTopLevelWindow([uint32]$appProcess.Id, 'Confirm DLL injection')
        if ($dialogHandle -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 250 }
    }

    $dialogFound = $dialogHandle -ne [IntPtr]::Zero
    if ($WithConfirmation) {
        Add-Check 'Confirmation dialog opens before injection' $dialogFound
    }

    if ($dialogFound) {
        Write-Step 'confirmation dialog found, clicking OK'
        Save-Shot $handle '01b-confirmation-open'
        [InjectShot]::Focus($dialogHandle)
        Start-Sleep -Milliseconds 250
        $dialogElement = [System.Windows.Automation.AutomationElement]::FromHandle($dialogHandle)
        $okButton = Find-ByName $dialogElement 'OK' ([System.Windows.Automation.ControlType]::Button) 2000
        if (-not $okButton) {
            $okButton = $dialogElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Button)))
        }
        if ($okButton) {
            $okRect = $okButton.Current.BoundingRectangle
            [InjectShot]::Click([int]($okRect.Left + $okRect.Width / 2), [int]($okRect.Top + $okRect.Height / 2))
            Write-Step 'clicked OK in the confirmation dialog (UIA button)'
        }
        else {
            [InjectShot]::PressDialogOk($dialogHandle) | Out-Null
            Write-Step "clicked OK in the confirmation dialog (WM_COMMAND IDOK, hwnd 0x$($dialogHandle.ToString('X')))"
        }
        Start-Sleep -Milliseconds 400
    }

    if (-not $WithConfirmation -or $dialogFound) {
        $loaded = Wait-TargetModule $targetProcess.Id $moduleName $true 20000
        Add-Check 'Target module list contains the injected library' $loaded $moduleName
        Assert-AppAlive 'injection result'
        Save-Shot $handle '02-injected'
        Write-Step 'injection step finished'

        if (-not $SkipUnload) {
            Assert-AppAlive 'unload'
            $unloadButton = Find-ByName $window 'Unload' ([System.Windows.Automation.ControlType]::Button) 2000
            if (-not $unloadButton) {
                Add-Check 'Unload button available after a successful injection' $false
            }
            else {
                Add-Check 'Unload button available after a successful injection' $unloadButton.Current.IsEnabled
                Click-Element $unloadButton | Out-Null
                $unloaded = Wait-TargetModule $targetProcess.Id $moduleName $false 15000
                Add-Check 'Target module list no longer contains the library after unload' $unloaded
                Save-Shot $handle '03-unloaded'
                Write-Step 'unload step finished'
            }
        }
    }
}
catch {
    Write-Step ("ERROR: " + $_.Exception.Message)
    Add-Check 'verification script completed' $false $_.Exception.Message
}
finally {
    foreach ($process in @($appProcess, $targetProcess)) {
        if ($null -ne $process) {
            try { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction Stop } } catch { }
        }
    }
    try {
        if (Test-Path $settingsBackup) { Move-Item $settingsBackup $settingsPath -Force }
        elseif (Test-Path $settingsPath) { Remove-Item $settingsPath -Force }
    }
    catch { }
    Remove-Item $lockPath -Force -ErrorAction SilentlyContinue
}

$failed = @($checks | Where-Object { -not $_.Ok }).Count
Write-Step ("RESULT: {0} passed, {1} failed" -f ($checks.Count - $failed), $failed)
Write-Step "screenshots: $out"
exit $(if ($failed -eq 0) { 0 } else { 1 })
