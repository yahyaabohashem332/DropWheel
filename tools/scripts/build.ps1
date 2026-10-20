<#
.SYNOPSIS
  Builds a release of Petal: self-contained, ReadyToRun, with FFmpeg and ExifTool bundled,
  plus the command line (Petal.com), then a portable ZIP, a Setup.exe when Inno Setup is
  installed, and update.json for the update feed.

.PARAMETER Notes
  What's new, shown in Petal's update window.

.EXAMPLE
  pwsh tools\scripts\build.ps1
#>
param(
    [string]$Version = '1.0.0',
    [string]$Notes = '',
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
    -p:PublishReadyToRun=true -p:Version=$Version -p:DebugType=none -o $publish --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

# The command line, beside Petal.exe. Renamed Petal.com: typing "petal" in a terminal finds it
# first (.com comes before .exe), while Start, Explorer and shortcuts keep opening Petal.exe.
# Published apart and then merged: the libraries both share are the same builds, and the app's
# fresh copies may still be held open by a virus scan.
$cliStage = Join-Path $dist 'cli-staging'
if (Test-Path $cliStage) { Remove-Item $cliStage -Recurse -Force }
dotnet publish (Join-Path $root 'src\Petal.Cli\Petal.Cli.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -p:DebugType=none -o $cliStage --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Publishing the command line failed' }
Get-ChildItem $cliStage -Recurse -File | ForEach-Object {
    $target = Join-Path $publish $_.FullName.Substring($cliStage.Length + 1)
    if (-not (Test-Path $target)) {
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $_.FullName $target
    }
}
Remove-Item $cliStage -Recurse -Force
Move-Item (Join-Path $publish 'petal-cli.exe') (Join-Path $publish 'Petal.com') -Force

# Whisper.net brings its native libraries for every platform; Petal runs on Windows x64 only.
Get-ChildItem (Join-Path $publish 'runtimes') -Directory -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(linux|macos|osx|win-arm64|win-x86)' } |
    Remove-Item -Recurse -Force
& (Join-Path $publish 'Petal.com') version
if ($LASTEXITCODE -ne 0) { throw 'The command line does not start' }

# Licences of everything we ship.
$licences = Join-Path $publish 'licenses'
New-Item -ItemType Directory -Force $licences | Out-Null
Copy-Item (Join-Path $root 'tools\bin\ffmpeg\LICENSE.txt') (Join-Path $licences 'FFmpeg.txt') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'tools\bin\exiftool\README.txt') (Join-Path $licences 'ExifTool.txt') -ErrorAction SilentlyContinue
# DirectML.dll ships beside Petal (Windows 10's own copy is too old for ONNX Runtime): its licence goes too.
$directml = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.ai.directml') -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if ($directml) { Copy-Item (Join-Path $directml.FullName 'LICENSE.txt') (Join-Path $licences 'DirectML.txt') }

$zip = Join-Path $dist "Petal-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Portable build: $zip"

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$dist" (Join-Path $root 'installer\Petal.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
    $setup = Join-Path $dist "Petal-Setup-$Version.exe"
    Write-Host "Installer: $setup"

    # The update feed: host update.json next to the installer (the url is relative to it).
    $manifest = [ordered]@{
        version = $Version
        url = "Petal-Setup-$Version.exe"
        sha256 = (Get-FileHash $setup -Algorithm SHA256).Hash
        size = (Get-Item $setup).Length
        notes = $Notes
    }
    $manifest | ConvertTo-Json | Set-Content (Join-Path $dist 'update.json') -Encoding utf8
    Write-Host "Update feed: $(Join-Path $dist 'update.json')"
} else {
    Write-Host 'Inno Setup not found; skipped Setup.exe (winget install JRSoftware.InnoSetup).'
}
