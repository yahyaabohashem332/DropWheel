<#
.SYNOPSIS
  Builds a release of Petal: self-contained, ReadyToRun, with FFmpeg and ExifTool bundled,
  then a portable ZIP and (when Inno Setup is installed) a Setup.exe.

.EXAMPLE
  pwsh tools\scripts\build.ps1
#>
param(
    [string]$Version = '1.0.0',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dist = Join-Path $root 'dist'
$publish = Join-Path $dist 'Petal'

& (Join-Path $PSScriptRoot 'fetch-tools.ps1')

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'tests\Petal.Tests') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\Petal.App\Petal.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -p:DebugType=none -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

# Licences of everything we ship.
$licences = Join-Path $publish 'licenses'
New-Item -ItemType Directory -Force $licences | Out-Null
Copy-Item (Join-Path $root 'tools\bin\ffmpeg\LICENSE.txt') (Join-Path $licences 'FFmpeg.txt') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'tools\bin\exiftool\README.txt') (Join-Path $licences 'ExifTool.txt') -ErrorAction SilentlyContinue

$zip = Join-Path $dist "Petal-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Portable build: $zip"

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$dist" (Join-Path $root 'installer\Petal.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
    Write-Host "Installer: $(Join-Path $dist "Petal-Setup-$Version.exe")"
} else {
    Write-Host 'Inno Setup not found; skipped Setup.exe (winget install JRSoftware.InnoSetup).'
}
