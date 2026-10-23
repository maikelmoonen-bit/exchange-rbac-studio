#Requires -Version 7.2
<#
.SYNOPSIS
    Builds, tests and packages Exchange RBAC Studio.

.DESCRIPTION
    1. Restores and builds the solution.
    2. Runs the automated tests (core + WPF). Desktop UI automation runs only with -UiTests.
    3. Publishes a self-contained application for the requested runtime.
    4. Produces a portable zip and an MSI installer (WiX v5).

    Artifacts are written to .\artifacts.

.EXAMPLE
    ./build.ps1
.EXAMPLE
    ./build.ps1 -SkipTests -SkipInstaller
.EXAMPLE
    ./build.ps1 -UiTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.0.1',

    [switch] $SkipTests,
    [switch] $SkipInstaller,
    [switch] $UiTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$root = $PSScriptRoot

function Invoke-Step([string] $Title, [scriptblock] $Action) {
    Write-Host "`n==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit code $LASTEXITCODE)." }
}

# Use a user-local .NET SDK when dotnet is not on PATH.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    if (Test-Path (Join-Path $local 'dotnet.exe')) {
        $env:DOTNET_ROOT = $local
        $env:PATH = "$local;$env:PATH"
    }
    else {
        throw 'The .NET 10 SDK was not found. Install it from https://dot.net or with dotnet-install.ps1.'
    }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts "publish\$Runtime"
$versionProps = @("-p:Version=$Version", "-p:InformationalVersion=$Version")

Invoke-Step 'Restore' { dotnet restore (Join-Path $root 'ExchangeRbacStudio.slnx') }
Invoke-Step 'Build' { dotnet build (Join-Path $root 'ExchangeRbacStudio.slnx') -c $Configuration --no-restore @versionProps }

if (-not $SkipTests) {
    Invoke-Step 'Core tests (domain, application, infrastructure, PowerShell host)' {
        dotnet test --project (Join-Path $root 'tests\ExchangeRbacStudio.Tests\ExchangeRbacStudio.Tests.csproj') -c $Configuration --no-build
    }
    Invoke-Step 'WPF tests (views, view models, keyboard, icons)' {
        dotnet test --project (Join-Path $root 'tests\ExchangeRbacStudio.App.Tests\ExchangeRbacStudio.App.Tests.csproj') -c $Configuration --no-build
    }
    if ($UiTests) {
        $env:ERS_UI_TESTS = '1'
        try {
            Invoke-Step 'Desktop UI automation' {
                dotnet test --project (Join-Path $root 'tests\ExchangeRbacStudio.UiTests\ExchangeRbacStudio.UiTests.csproj') -c $Configuration --no-build
            }
        }
        finally {
            Remove-Item Env:\ERS_UI_TESTS -ErrorAction SilentlyContinue
        }
    }
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Invoke-Step "Publish self-contained $Runtime" {
    dotnet publish (Join-Path $root 'src\ExchangeRbacStudio.App\ExchangeRbacStudio.App.csproj') -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -o $publishDir @versionProps
}

foreach ($required in 'ExchangeRbacStudio.exe', 'PowerShell\ExchangeHost.ps1') {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "Publish output is missing $required." }
}

$zip = Join-Path $artifacts "ExchangeRbacStudio-$Version-$Runtime-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Write-Host "`n==> Portable zip" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal

if (-not $SkipInstaller) {
    if ($Runtime -ne 'win-x64') {
        Write-Warning 'The MSI is defined for x64; skipping installer for other runtimes.'
    }
    else {
        Invoke-Step 'MSI installer (WiX v5)' {
            dotnet build (Join-Path $root 'installer\ExchangeRbacStudio.Installer.wixproj') -c Release `
                "-p:AppPublishDir=$publishDir\" "-p:ProductVersion=$Version" "-p:RuntimeIdentifier=$Runtime" -o (Join-Path $artifacts 'installer')
        }
    }
}

Write-Host "`nArtifacts:" -ForegroundColor Green
Get-ChildItem $artifacts -File -Recurse -Include *.zip, *.msi | ForEach-Object { '  {0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
