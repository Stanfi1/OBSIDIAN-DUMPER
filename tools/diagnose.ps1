#requires -Version 5.1
<#
    OBSIDIAN launch diagnostics.
    Writes a report to artifacts\diagnostika.txt and prints the essentials.
    Intentionally ASCII-only output so it reads correctly in any console code page.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root
$reportPath = Join-Path $root 'artifacts\diagnostika.txt'
New-Item -ItemType Directory -Force (Split-Path -Parent $reportPath) | Out-Null
$lines = New-Object System.Collections.Generic.List[string]

function Add-Line([string] $text = '') {
    $lines.Add($text)
    Write-Host $text
}

function Add-Section([string] $title) {
    Add-Line ''
    Add-Line ('=== ' + $title + ' ' + ('=' * [Math]::Max(0, 62 - $title.Length)))
}

Add-Line 'OBSIDIAN - diagnostika spustenia'
Add-Line ('cas:        ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Add-Line ('priecinok:  ' + $root)

Add-Section 'Prostredie'
Add-Line ('pouzivatel: ' + [Security.Principal.WindowsIdentity]::GetCurrent().Name)
try {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    Add-Line ('admin:      ' + $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
} catch { }
Add-Line ('OS:         ' + [Environment]::OSVersion.VersionString)
Add-Line ('64-bit OS:  ' + [Environment]::Is64BitOperatingSystem + ' / 64-bit proces: ' + [Environment]::Is64BitProcess)
Add-Line ('PowerShell: ' + $PSVersionTable.PSVersion)
Add-Line ('DPI:        ' + (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -ErrorAction SilentlyContinue).AppliedDPI)

Add-Section '.NET'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    Add-Line ('dotnet:     ' + $dotnet.Source)
    Add-Line ((& dotnet --version 2>&1) -join ' ')
    Add-Line ''
    Add-Line 'SDK:'
    foreach ($sdk in (& dotnet --list-sdks 2>&1)) { Add-Line ('  ' + $sdk) }
    Add-Line ''
    Add-Line 'Runtiny:'
    $runtimes = @(& dotnet --list-runtimes 2>&1)
    foreach ($runtime in $runtimes) { Add-Line ('  ' + $runtime) }
    $desktop = $runtimes | Where-Object { $_ -match 'Microsoft\.WindowsDesktop\.App' }
    Add-Line ''
    Add-Line ('WindowsDesktop runtime: ' + $(if ($desktop) { 'OK' } else { 'CHYBA - .NET Desktop Runtime nie je nainstalovany' }))
} else {
    Add-Line 'dotnet:     NENAJDENY v PATH'
}

Add-Section 'Execution policy'
try {
    Get-ExecutionPolicy -List | ForEach-Object { Add-Line ('  ' + $_.Scope + ' = ' + $_.ExecutionPolicy) }
} catch { Add-Line ('  chyba: ' + $_.Exception.Message) }

Add-Section 'Kandidati na spustenie'
$candidates = @(
    'artifacts\publish-selfcontained\OBSIDIAN\OBSIDIAN.exe',
    'artifacts\publish\OBSIDIAN\OBSIDIAN.exe',
    'src\OBSIDIAN\bin\x64\Release\net10.0-windows\OBSIDIAN.exe',
    'src\OBSIDIAN\bin\Release\net10.0-windows\OBSIDIAN.exe',
    'src\OBSIDIAN\bin\Debug\net10.0-windows\OBSIDIAN.exe'
)

$chosen = $null
foreach ($relative in $candidates) {
    $path = Join-Path $root $relative
    if (Test-Path $path) {
        $item = Get-Item $path
        $sha = (Get-FileHash $path -Algorithm SHA256).Hash.Substring(0, 16)
        Add-Line ('  [OK]    ' + $relative)
        Add-Line ('          ' + $item.Length + ' B, ' + $item.LastWriteTime + ', sha256=' + $sha + '...')
        $zone = Get-Item $path -Stream Zone.Identifier -ErrorAction SilentlyContinue
        if ($zone) { Add-Line '          POZOR: subor ma Zone.Identifier (mark of the web)' }
        if (-not $chosen) { $chosen = $path }
    } else {
        Add-Line ('  [chyba] ' + $relative)
    }
}

if (-not $chosen) {
    Add-Line ''
    Add-Line '  Ziadny build sa nenasiel. Spusti build.ps1 alebo SPUSTIT-OBSIDIAN.cmd.'
}

if ($chosen) {
    Add-Line ''
    Add-Line ('  Pouzijem: ' + $chosen)
    $runtimeConfig = [IO.Path]::ChangeExtension($chosen, '.runtimeconfig.json')
    if (Test-Path $runtimeConfig) {
        Add-Line '  runtimeconfig:'
        foreach ($line in (Get-Content $runtimeConfig)) { Add-Line ('    ' + $line.Trim()) }
    } else {
        Add-Line '  runtimeconfig: (self-contained alebo chyba)'
    }

    Add-Section 'Pokus o spustenie'
    Get-Process OBSIDIAN -ErrorAction SilentlyContinue | ForEach-Object {
        Add-Line ('  uz bezi: pid ' + $_.Id + ', okno="' + $_.MainWindowTitle + '"')
        $script:alreadyRunning = $true
    }

    if (-not $script:alreadyRunning) {
        $started = Get-Date
        & cmd /c start "" "$chosen"
        $process = $null
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 250
            $process = Get-Process OBSIDIAN -ErrorAction SilentlyContinue |
                Where-Object { $_.StartTime -ge $started.AddSeconds(-2) } |
                Select-Object -First 1
            if ($process -and $process.MainWindowHandle -ne 0) { break }
            if ($process -and $process.HasExited) { break }
        }

        if ($process) {
            $process.Refresh()
            if ($process.MainWindowHandle -ne 0) {
                Add-Line ('  VYSLEDOK: OK - pid ' + $process.Id + ', okno "' + $process.MainWindowTitle + '"')
            } else {
                Add-Line ('  VYSLEDOK: proces bezi (pid ' + $process.Id + '), ale zatial nema okno')
            }
        } else {
            Add-Line '  VYSLEDOK: proces sa nespustil alebo hned skoncil'
        }
    }

    Add-Section 'crash.log'
    $crashFiles = Get-ChildItem $root -Recurse -Filter crash.log -ErrorAction SilentlyContinue
    if ($crashFiles) {
        foreach ($file in $crashFiles) {
            Add-Line ('  ' + $file.FullName + ' (' + $file.LastWriteTime + ')')
            Get-Content $file.FullName -TotalCount 25 | ForEach-Object { Add-Line ('    ' + $_) }
        }
    } else {
        Add-Line '  ziadny crash.log'
    }
}

Add-Section 'Event log (Application)'
try {
    $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddDays(-1) } -ErrorAction Stop |
        Where-Object { $_.Message -match 'OBSIDIAN' } |
        Select-Object -First 8
    if ($events) {
        foreach ($event in $events) {
            Add-Line ('  ' + $event.TimeCreated + ' [' + $event.ProviderName + ']')
            ($event.Message -split "`n")[0..3] | ForEach-Object { Add-Line ('    ' + $_.Trim()) }
        }
    } else {
        Add-Line '  ziadne zaznamy o OBSIDIAN za 24h'
    }
} catch {
    Add-Line ('  event log nedostupny: ' + $_.Exception.Message)
}

Add-Section 'Nastavenia'
$settingsCandidates = @(
    (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'OBSIDIAN\settings.json'),
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OBSIDIAN\settings.json'),
    (Join-Path $root 'settings.json')
)
foreach ($settings in $settingsCandidates) {
    if (Test-Path $settings) {
        Add-Line ('  ' + $settings)
        Get-Content $settings | ForEach-Object { Add-Line ('    ' + $_) }
    }
}

Add-Section 'Odporucania'
Add-Line '  1. Spusti OBSIDIAN cez SPUSTIT-OBSIDIAN.cmd (dvojklik), nie cez run.ps1.'
Add-Line '     PowerShell moze byt blokovany policy - .cmd to obchadza.'
Add-Line '  2. Ak antivirus/SmartScreen blokuje EXE: More info -> Run anyway,'
Add-Line '     pripadne odblokuj subor: Unblock-File .\artifacts\publish-selfcontained\OBSIDIAN\OBSIDIAN.exe'
Add-Line '  3. Self-contained build v artifacts\publish-selfcontained nepotrebuje .NET runtime.'
Add-Line '  4. Ak build chyba, spusti build.ps1 a posli cely vystup.'

$lines | Set-Content -Path $reportPath -Encoding UTF8
Write-Host ''
Write-Host ('Report ulozeny: ' + $reportPath) -ForegroundColor Green
