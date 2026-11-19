#requires -Version 5.1
<#
    Starts OBSIDIAN from an already built folder.

    Prefer the double-click launcher SPUSTIT-OBSIDIAN.cmd: Windows blocks .ps1
    files by default (ExecutionPolicy), while the .cmd wrapper bypasses that.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $MyInvocation.MyCommand.Path)

$candidates = @(
    'artifacts\publish-selfcontained\OBSIDIAN\OBSIDIAN.exe',
    'artifacts\publish\OBSIDIAN\OBSIDIAN.exe',
    'src\OBSIDIAN\bin\x64\Release\net10.0-windows\OBSIDIAN.exe',
    'src\OBSIDIAN\bin\Release\net10.0-windows\OBSIDIAN.exe',
    'src\OBSIDIAN\bin\Debug\net10.0-windows\OBSIDIAN.exe'
)

$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $exe) {
    Write-Host 'No build found. Building Debug output…' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug -SkipNative
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $exe = 'src\OBSIDIAN\bin\Debug\net10.0-windows\OBSIDIAN.exe'
}

if (-not (Test-Path $exe)) {
    throw 'OBSIDIAN.exe not found. Run SPUSTIT-OBSIDIAN.cmd or DIAGNOSTIKA-OBSIDIAN.cmd.'
}

Write-Host "Starting $exe" -ForegroundColor Green
Start-Process (Join-Path $PSScriptRoot $exe) -ArgumentList $Arguments
