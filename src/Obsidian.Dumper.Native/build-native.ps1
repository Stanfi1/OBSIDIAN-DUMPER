#requires -Version 5.1
<#
    Builds the native injection agent with MSVC.
    Output: src\Obsidian.Dumper.Native\bin\Obsidian.Injector.x64.dll
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [ValidateSet('x64')]
    [string] $Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $here 'bin'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found; Visual Studio is required to build the injector.' }

$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'No Visual Studio installation with the C++ toolchain was found.' }

$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars" }

$source = Join-Path $here 'Obsidian.Injector.cpp'
$def = Join-Path $here 'Obsidian.Injector.def'
$obj = Join-Path $outDir 'Obsidian.Injector.obj'
$dll = Join-Path $outDir 'Obsidian.Injector.x64.dll'

# The injecting process and the payload hosting path are x64-only, so the
# Architecture switch is validated instead of silently ignored.
if ($Architecture -ne 'x64') { throw "Only x64 is supported for the native agent (requested $Architecture)." }

$optimise = if ($Configuration -eq 'Debug') { '/Od /Zi' } else { '/O2' }
$linkFlags = if ($Configuration -eq 'Debug') { '/DEBUG /OPT:NOREF' } else { '/DEBUG:NONE /OPT:REF /OPT:ICF' }

$command = @"
call "$vcvars" >nul
cl /nologo /std:c++17 /EHsc $optimise /MT /W4 /DUNICODE /D_UNICODE /Fo"$obj" /Fe"$dll" /LD "$source" /link /DEF:"$def" $linkFlags
"@

Write-Host "Building native agent ($Architecture, $Configuration)..." -ForegroundColor Cyan
$temp = Join-Path $env:TEMP ("obsidian-native-" + [guid]::NewGuid().ToString('N') + '.cmd')
Set-Content -Path $temp -Value $command -Encoding ASCII
try {
    & cmd.exe /c "`"$temp`""
    if ($LASTEXITCODE -ne 0) { throw "cl.exe failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $temp -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $dll)) { throw "Native agent was not produced at $dll" }
Get-Item $dll | Select-Object FullName, Length, LastWriteTime | Format-List
