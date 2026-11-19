#requires -Version 5.1
<#
    Builds OBSIDIAN: native agent, payload, engine, UI and CLI.

    Options:
      -Configuration Release|Debug   (default Release)
      -SkipNative                    skip the MSVC agent build
      -Test                          run the verification harness afterwards
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $SkipNative,
    [switch] $Test
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK (dotnet) is required. Install .NET 10 SDK from https://dotnet.microsoft.com/.'
}

$version = & dotnet --version
Write-Host "OBSIDIAN build · configuration=$Configuration · dotnet=$version" -ForegroundColor Cyan

if (-not $SkipNative) {
    $agent = Join-Path $PSScriptRoot 'src\Obsidian.Dumper.Native\bin\Obsidian.Injector.x64.dll'
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'src\Obsidian.Dumper.Native\build-native.ps1') | Out-Host
        Write-Host "  native agent: $agent" -ForegroundColor DarkGray
    }
    catch {
        Write-Warning "Native agent could not be rebuilt ($($_.Exception.Message)). Deep dump will be unavailable until it exists."
    }
}

& dotnet build (Join-Path $PSScriptRoot 'Obsidian.Dumper.sln') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$app = $null
foreach ($candidate in @(
        (Join-Path $PSScriptRoot "src\OBSIDIAN\bin\x64\$Configuration\net10.0-windows\OBSIDIAN.exe"),
        (Join-Path $PSScriptRoot "src\OBSIDIAN\bin\$Configuration\net10.0-windows\OBSIDIAN.exe"),
        (Join-Path $PSScriptRoot 'src\OBSIDIAN\bin\x64\Debug\net10.0-windows\OBSIDIAN.exe'),
        (Join-Path $PSScriptRoot 'src\OBSIDIAN\bin\Debug\net10.0-windows\OBSIDIAN.exe')))
{
    if (Test-Path $candidate) { $app = $candidate; break }
}

if ($app) {
    Write-Host ''
    Write-Host "Build complete: $app" -ForegroundColor Green
}
else {
    Write-Warning 'Build finished but OBSIDIAN.exe was not found under src\OBSIDIAN\bin.'
}

if ($Test) {
    & dotnet run --project (Join-Path $PSScriptRoot 'tests\Obsidian.Dumper.Tests') -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "Verification failed with exit code $LASTEXITCODE" }
}
