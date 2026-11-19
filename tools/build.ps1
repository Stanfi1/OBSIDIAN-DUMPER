<#
    Serialised build / test helper for OBSIDIAN.

    Several agents (and humans) share this working tree, so every build is
    serialised through a global mutex. Never run `dotnet build` directly from a
    parallel script: two MSBuild processes writing the same obj/ folders produce
    phantom errors. Use this script instead.

    Examples:
      pwsh -File tools/build.ps1                        # whole solution, Debug
      pwsh -File tools/build.ps1 -Target app            # WPF app only
      pwsh -File tools/build.ps1 -Target core -Release  # engine, Release
      pwsh -File tools/build.ps1 -Target tests -RunTests
#>
param(
    [ValidateSet('sln', 'app', 'core', 'cli', 'tests', 'payload', 'native')]
    [string]$Target = 'sln',

    [switch]$Release,
    [switch]$RunTests,
    [switch]$NoRestore,
    [string]$ExtraArgs = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

function Resolve-Project {
    param([string]$Name)
    switch ($Name) {
        'sln' { return 'Obsidian.Dumper.sln' }
        'app' { return 'src/OBSIDIAN/OBSIDIAN.csproj' }
        'core' { return 'src/Obsidian.Dumper.Core/Obsidian.Dumper.Core.csproj' }
        'cli' { return 'src/Obsidian.Dumper.Cli/Obsidian.Dumper.Cli.csproj' }
        'tests' { return 'tests/Obsidian.Dumper.Tests/Obsidian.Dumper.Tests.csproj' }
        'payload' { return 'src/Obsidian.Dumper.Payload/Obsidian.Dumper.Payload.csproj' }
        'native' { return '' }
        default { throw "Unknown target $Name" }
    }
}

$config = if ($Release) { 'Release' } else { 'Debug' }
$mutex = [System.Threading.Mutex]::new($false, 'Global\OBSIDIAN-BUILD-LOCK')
$acquired = $false
try {
    try {
        $acquired = $mutex.WaitOne([TimeSpan]::FromMinutes(15))
    }
    catch [System.Threading.AbandonedMutexException] {
        $acquired = $true
    }
    if (-not $acquired) {
        Write-Error 'Timed out waiting for the shared build lock.'
        exit 2
    }

    Write-Host "== OBSIDIAN build: $Target ($config)" -ForegroundColor Cyan

    if ($Target -eq 'native') {
        # `pwsh` is not guaranteed to exist on build machines; the native script
        # only needs Windows PowerShell 5.1 features, so prefer whatever runs us.
        $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
        & $shell -NoProfile -ExecutionPolicy Bypass -File 'src/Obsidian.Dumper.Native/build-native.ps1'
        exit $LASTEXITCODE
    }

    $project = Resolve-Project $Target
    $buildArgs = @('build', $project, '-c', $config, '-nologo', '-v', 'minimal', '-clp:NoSummary')
    if ($NoRestore) { $buildArgs += '--no-restore' }
    if ($ExtraArgs) { $buildArgs += $ExtraArgs.Split(' ') }

    & dotnet @buildArgs
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host "BUILD FAILED (exit $code)" -ForegroundColor Red
        exit $code
    }
    Write-Host 'BUILD OK' -ForegroundColor Green

    if ($RunTests) {
        Write-Host '== running engine verification harness' -ForegroundColor Cyan
        & dotnet run --project 'tests/Obsidian.Dumper.Tests/Obsidian.Dumper.Tests.csproj' -c $config --no-build
        $testCode = $LASTEXITCODE
        if ($testCode -ne 0) {
            Write-Host "TESTS FAILED (exit $testCode)" -ForegroundColor Red
            exit $testCode
        }
        Write-Host 'TESTS OK' -ForegroundColor Green
    }

    exit 0
}
finally {
    if ($acquired) {
        try { $mutex.ReleaseMutex() } catch { }
    }
    $mutex.Dispose()
}
