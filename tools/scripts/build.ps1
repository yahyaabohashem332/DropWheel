<#
.SYNOPSIS
  Builds a release of Petal: self-contained, ReadyToRun, with FFmpeg and ExifTool bundled,
  plus the command line (Petal.com), then a portable ZIP, a Setup.exe when Inno Setup is
  installed, and update.json for the update feed.

.PARAMETER Notes
  What's new, shown in Petal's update window.

.PARAMETER Arch
  x64 (the default) or arm64: Petal for ARM64 PCs, its own ZIP, Setup and update feed
  (update-arm64.json). FFmpeg and ExifTool stay x64 programs, which Windows 11 runs on ARM64.

.EXAMPLE
  pwsh tools\scripts\build.ps1
#>
param(
    [string]$Version = '1.0.0',
    [string]$Notes = '',
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dist = Join-Path $root 'dist'
$publish = Join-Path $dist ($(if ($Arch -eq 'arm64') { 'Petal-arm64' } else { 'Petal' }))
$rid = "win-$Arch"

& (Join-Path $PSScriptRoot 'fetch-tools.ps1')

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'tests\Petal.Tests') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\Petal.App\Petal.App.csproj') -c Release -r $rid --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -p:DebugType=none -o $publish --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

# The command line, beside Petal.exe. Renamed Petal.com: typing "petal" in a terminal finds it
# first (.com comes before .exe), while Start, Explorer and shortcuts keep opening Petal.exe.
# Published apart and then merged: the libraries both share are the same builds, and the app's
# fresh copies may still be held open by a virus scan.
$cliStage = Join-Path $dist 'cli-staging'
if (Test-Path $cliStage) { Remove-Item $cliStage -Recurse -Force }
dotnet publish (Join-Path $root 'src\Petal.Cli\Petal.Cli.csproj') -c Release -r $rid --self-contained true `
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

# Whisper.net brings its native libraries for every platform; only this build's processor stays.
# Its graphics-card (Vulkan) build exists for x64 only, so ARM64 transcribes on the processor.
$foreign = if ($Arch -eq 'arm64') { '^(linux|macos|osx|win-x64|win-x86|vulkan)' } else { '^(linux|macos|osx|win-arm64|win-x86)' }
Get-ChildItem (Join-Path $publish 'runtimes') -Directory -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match $foreign } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
# Debug symbols that packages bring along (SkiaSharp's alone is 85 MB) are no use to anyone running Petal.
Get-ChildItem $publish -Recurse -Filter *.pdb | Remove-Item -Force
# So is DirectML's debug layer, which its package copies beside DirectML.dll.
Remove-Item (Join-Path $publish 'DirectML.Debug.dll') -Force -ErrorAction SilentlyContinue
# The command line runs here only when it is built for this PC's processor.
if ($Arch -eq 'x64') {
    & (Join-Path $publish 'Petal.com') version
    if ($LASTEXITCODE -ne 0) { throw 'The command line does not start' }
}

# Licences of everything we ship.
$licences = Join-Path $publish 'licenses'
New-Item -ItemType Directory -Force $licences | Out-Null
Copy-Item (Join-Path $root 'tools\bin\ffmpeg\LICENSE.txt') (Join-Path $licences 'FFmpeg.txt') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'tools\bin\exiftool\README.txt') (Join-Path $licences 'ExifTool.txt') -ErrorAction SilentlyContinue
# DirectML.dll ships beside Petal (Windows 10's own copy is too old for ONNX Runtime): its licence goes too.
$directml = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.ai.directml') -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if ($directml) { Copy-Item (Join-Path $directml.FullName 'LICENSE.txt') (Join-Path $licences 'DirectML.txt') }
# Which FFmpeg build ships, for anyone who wants its source; and the list of everything Petal is built on, as a text
# file that Settings opens in Notepad, with the full texts of the licences it names.
& (Join-Path $publish 'tools\ffmpeg\ffmpeg.exe') -hide_banner -version | Select-Object -First 2 | Set-Content (Join-Path $licences 'FFmpeg-version.txt')
Remove-Item (Join-Path $licences 'THIRD-PARTY-NOTICES.md') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'store\THIRD-PARTY-NOTICES.md') (Join-Path $licences 'THIRD-PARTY-NOTICES.txt')
Copy-Item (Join-Path $root 'store\licenses\*.txt') $licences
# Every package the app is built with, at the version it ships: its own licence and notice files, as its authors wrote them.
$deps = Get-Content (Join-Path $publish 'Petal.deps.json') -Raw | ConvertFrom-Json
$packages = Join-Path $licences 'packages'
New-Item -ItemType Directory -Force $packages | Out-Null
foreach ($library in $deps.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $folder = Join-Path $env:USERPROFILE ('.nuget\packages\' + $library.Value.path)
    $files = Get-ChildItem $folder -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(licen[cs]e|notice|third-?party-?notices)' }
    foreach ($file in $files) {
        Copy-Item $file.FullName (Join-Path $packages (($library.Name -replace '/.*$', '') + ' - ' + $file.Name))
    }
}

# The Stream Deck plugin as Stream Deck installs it: its folder in a ZIP named .streamDeckPlugin, with
# Petal's version. Settings opens it from here.
$deckStage = Join-Path $dist 'streamdeck-staging'
if (Test-Path $deckStage) { Remove-Item $deckStage -Recurse -Force }
New-Item -ItemType Directory -Force $deckStage | Out-Null
Copy-Item (Join-Path $root 'tools\streamdeck\com.petal.app.sdPlugin') $deckStage -Recurse
$deckManifest = Join-Path $deckStage 'com.petal.app.sdPlugin\manifest.json'
(Get-Content $deckManifest -Raw) -replace '"Version": "\d+\.\d+\.\d+\.\d+"', "`"Version`": `"$Version.0`"" | Set-Content $deckManifest -Encoding utf8NoBOM
$integrations = Join-Path $publish 'integrations'
New-Item -ItemType Directory -Force $integrations | Out-Null
$deckZip = Join-Path $dist 'Petal.streamDeckPlugin.zip'
Compress-Archive -Path (Join-Path $deckStage 'com.petal.app.sdPlugin') -DestinationPath $deckZip -Force
Move-Item $deckZip (Join-Path $integrations 'Petal.streamDeckPlugin') -Force
Remove-Item $deckStage -Recurse -Force

$zip = Join-Path $dist "DropWheel-$Version-win-$Arch.zip"
if (Test-Path $zip) { Remove-Item $zip }
# .NET's own zip rather than Compress-Archive, which is slow and twice stopped dead half way through the ARM64 build.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "Portable build: $zip"

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$dist" "/DArch=$Arch" (Join-Path $root 'installer\Petal.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
    $setupName = if ($Arch -eq 'arm64') { "DropWheel-Setup-$Version-arm64.exe" } else { "DropWheel-Setup-$Version.exe" }
    $setup = Join-Path $dist $setupName
    Write-Host "Installer: $setup"

    # The update feed: host update.json next to the installer (the url is relative to it).
    $manifest = [ordered]@{
        version = $Version
        url = $setupName
        sha256 = (Get-FileHash $setup -Algorithm SHA256).Hash
        size = (Get-Item $setup).Length
        notes = $Notes
    }
    # Signed with the maker's key, kept outside the repository: installed copies only offer signed updates.
    $keyFile = Join-Path $env:USERPROFILE '.petal\update-signing-key.pem'
    if (-not (Test-Path $keyFile)) {
        throw "The update signing key isn't at $keyFile. Restore it from your backup: a new key would make installed copies refuse every update."
    }
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    $ecdsa.ImportFromPem((Get-Content $keyFile -Raw))
    $signed = "petal-update`n$($manifest.version)`n$($manifest.url)`n$($manifest.sha256.ToUpperInvariant())`n$($manifest.size)"
    $signature = $ecdsa.SignData([System.Text.Encoding]::UTF8.GetBytes($signed), [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $manifest.signature = [Convert]::ToBase64String($signature)
    $feed = Join-Path $dist ($(if ($Arch -eq 'arm64') { 'update-arm64.json' } else { 'update.json' }))
    $manifest | ConvertTo-Json | Set-Content $feed -Encoding utf8
    Write-Host "Update feed: $feed"
} else {
    Write-Host 'Inno Setup not found; skipped Setup.exe (winget install JRSoftware.InnoSetup).'
}
