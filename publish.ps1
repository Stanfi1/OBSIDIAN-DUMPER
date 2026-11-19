#requires -Version 5.1
<#
    Publishes a self-contained or framework-dependent OBSIDIAN folder, including
    the managed agent and the native loader.

    Examples:
      .\publish.ps1                       # framework-dependent -> artifacts\publish\OBSIDIAN
      .\publish.ps1 -SelfContained        # portable         -> artifacts\publish-selfcontained\OBSIDIAN
      .\publish.ps1 -SelfContained -NoNative
#>
[CmdletBinding()]
param(
    [string] $Runtime = 'win-x64',
    [switch] $SelfContained,
    [switch] $NoNative,
    # Empty = derived from -SelfContained so the two flavours never overwrite
    # each other (that used to produce a self-contained folder with a
    # framework-dependent name and vice versa).
    [string] $Output = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK (dotnet) is required. Install .NET 10 SDK from https://dotnet.microsoft.com/.'
}

if (-not $Output) {
    $Output = if ($SelfContained) { 'artifacts\publish-selfcontained\OBSIDIAN' } else { 'artifacts\publish\OBSIDIAN' }
}

$outPath = Join-Path $root $Output
if (Test-Path $outPath) { Remove-Item $outPath -Recurse -Force }

# The native loader is optional at runtime (only deep dump needs it), but a
# "complete" publish should try to include it. A missing MSVC toolchain is not
# fatal: the app reports the capability instead of failing to start.
if (-not $NoNative) {
    $nativeScript = Join-Path $root 'src\Obsidian.Dumper.Native\build-native.ps1'
    if (Test-Path $nativeScript) {
        try {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $nativeScript | Out-Host
        }
        catch {
            Write-Warning "Native agent was not rebuilt ($($_.Exception.Message)). Deep dump will be unavailable unless a prebuilt DLL exists."
        }
    }
}

Write-Host "Publishing OBSIDIAN ($Runtime, self-contained=$($SelfContained.IsPresent)) -> $Output" -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'src\OBSIDIAN\OBSIDIAN.csproj') `
    -c Release -r $Runtime --self-contained:$($SelfContained.IsPresent) `
    -o $outPath --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Ship the documents and notices next to the binary.
foreach ($file in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md')) {
    $source = Join-Path $root $file
    if (Test-Path $source) { Copy-Item $source (Join-Path $outPath $file) -Force }
}

# Only the developer architecture write-up travels with the app, plus the
# diagram and the demo clip it references. The audit notes, theme/settings
# contracts and design system are development documents and are intentionally
# not shipped in the public package.
$docsSource = Join-Path $root 'docs'
if (Test-Path $docsSource) {
    $docsTarget = Join-Path $outPath 'docs'
    New-Item -ItemType Directory -Force $docsTarget | Out-Null
    $architecture = Join-Path $docsSource 'ARCHITECTURE.md'
    if (Test-Path $architecture) { Copy-Item $architecture (Join-Path $docsTarget 'ARCHITECTURE.md') -Force }

    $diagramSource = Join-Path $docsSource 'diagram'
    if (Test-Path $diagramSource) {
        $diagramTarget = Join-Path $docsTarget 'diagram'
        New-Item -ItemType Directory -Force $diagramTarget | Out-Null
        Copy-Item (Join-Path $diagramSource '*.png') $diagramTarget -Force
    }

    $demoSource = Join-Path $docsSource 'demo'
    if (Test-Path $demoSource) {
        $demoTarget = Join-Path $docsTarget 'demo'
        New-Item -ItemType Directory -Force $demoTarget | Out-Null
        Copy-Item (Join-Path $demoSource '*') $demoTarget -Force
    }
}

$agent = Join-Path $outPath 'native\Obsidian.Injector.x64.dll'
$payload = Join-Path $outPath 'payload\net48\Obsidian.Dumper.Payload.dll'
$runtimeConfig = Join-Path $outPath 'OBSIDIAN.runtimeconfig.json'
$selfContainedFolder = (Get-ChildItem $outPath -Filter 'coreclr.dll' -ErrorAction SilentlyContinue) -ne $null

Write-Host ''
Write-Host "Publish complete: $outPath" -ForegroundColor Green
Write-Host ("  executable      : " + $(if (Test-Path (Join-Path $outPath 'OBSIDIAN.exe')) { 'OK' } else { 'MISSING' }))
Write-Host ("  flavour         : " + $(if ($selfContainedFolder) { 'self-contained (no runtime needed)' } else { 'framework-dependent (.NET 10 Desktop Runtime needed)' }))
Write-Host ("  native agent    : " + $(if (Test-Path $agent) { 'staged' } else { 'MISSING (deep dump disabled)' }))
Write-Host ("  managed agent   : " + $(if (Test-Path $payload) { 'staged' } else { 'MISSING (deep dump disabled)' }))
if (-not $selfContainedFolder -and -not (Test-Path $runtimeConfig)) {
    Write-Warning 'OBSIDIAN.runtimeconfig.json is missing - the publish may be incomplete.'
}
