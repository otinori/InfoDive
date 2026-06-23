<#
.SYNOPSIS
  Builds the InfoDive Windows Installer (.msi).

.DESCRIPTION
  1. Runs `dotnet publish` to produce the single-file Release build.
  2. Invokes WiX (v5) to package the published exe into an MSI.

  Requires: .NET 9 SDK and the `wix` global tool (v5.x) with
            WixToolset.UI.wixext and WixToolset.Util.wixext extensions.

.PARAMETER Configuration
  Build configuration (default: Release).

.PARAMETER OutputDir
  Output directory for the produced .msi (default: <repo>/dist).
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$repoRoot     = Split-Path -Parent $PSScriptRoot
$projectPath  = Join-Path $repoRoot 'src\apps\InfoDive\InfoDive.csproj'
$publishDir   = Join-Path $repoRoot 'artifacts\publish\InfoDive'
$iconFile     = Join-Path $repoRoot 'assets\app.ico'
$wxsFile      = Join-Path $repoRoot 'src\installer\wix\InfoDive.wxs'

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'dist' }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# Read product version from .csproj <FileVersion>
[xml]$csproj = Get-Content $projectPath
$fileVersion = ($csproj.Project.PropertyGroup | Where-Object { $_.FileVersion } | Select-Object -First 1).FileVersion
if (-not $fileVersion) { throw "FileVersion not found in $projectPath" }
# MSI ProductVersion only honors major.minor.build — trim to three parts.
$productVersion = ($fileVersion -split '\.')[0..2] -join '.'

Write-Host "==> Publishing InfoDive ($Configuration)" -ForegroundColor Cyan
dotnet publish $projectPath -c $Configuration -o $publishDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exePath = Join-Path $publishDir 'InfoDive.exe'
if (-not (Test-Path $exePath)) { throw "Publish output missing: $exePath" }

$msiPath = Join-Path $OutputDir "InfoDive-$productVersion-win-x64.msi"

Write-Host "==> Building MSI: $msiPath" -ForegroundColor Cyan
wix build $wxsFile `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -ext WixToolset.Firewall.wixext `
    -culture ja-JP `
    -d "ProductVersion=$productVersion" `
    -d "PublishDir=$publishDir" `
    -d "IconFile=$iconFile" `
    -o $msiPath
if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  MSI: $msiPath"
Write-Host "  Size: $([math]::Round((Get-Item $msiPath).Length / 1MB, 1)) MB"
