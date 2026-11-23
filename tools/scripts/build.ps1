<#
.SYNOPSIS
  Builds a release of Petal: self-contained, ReadyToRun, with FFmpeg and ExifTool bundled,
  plus the command line (Petal.com), then a portable ZIP, a Setup.exe when Inno Setup is
  installed, and update.json for the update feed.

.PARAMETER Notes
  What's new, shown in Petal's update window.

.PARAMETER Urgency
  How much the update matters, signed into update.json: required (offered every time, can't be
  skipped), recommended (the default: a card, can be skipped) or optional (only the tray's dot).

.PARAMETER Changes
  What changed, one line each, shown as a list in the update window and signed with the feed.

.PARAMETER Arch
  x64 (the default) or arm64: Petal for ARM64 PCs, its own ZIP, Setup and update feed
  (update-arm64.json). FFmpeg and ExifTool stay x64 programs, which Windows 11 runs on ARM64.

.PARAMETER NoFeed
  Leaves out update.json: a build to try out or send by hand, made where the update signing key
  isn't (the build machine on GitHub). Installed copies are never offered such a build as an update.

.EXAMPLE
  pwsh tools\scripts\build.ps1
#>
# PowerShell 7: Windows PowerShell 5.1 lacks ECDsa.ImportFromPem (the update signature) and utf8NoBOM.
#Requires -Version 7.0
param(
    [string]$Version = '1.0.0',
    [string]$Notes = '',
    [ValidateSet('required', 'recommended', 'optional')]
    [string]$Urgency = 'recommended',
    [string[]]$Changes = @(),
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [switch]$SkipTests,
    [switch]$NoFeed
)
$ErrorActionPreference = 'Stop'
# The signed feed reads one change a line: none may be empty or hold a line break.
$Changes = @($Changes | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
if ($Changes | Where-Object { $_ -match "[`r`n]" -or $_.Length -gt 400 }) { throw 'Each change is one line of at most 400 characters.' }
if ($Changes.Count -gt 50) { throw 'At most 50 changes.' }
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
# FFmpeg is LGPL: its licence, the licence files of every library built into its DLLs, how it was built, and the
# exact source of FFmpeg and of its LGPL libraries with the scripts that built them.
$ffLicences = Join-Path $root 'tools\bin\ffmpeg-licenses'
if (-not (Test-Path (Join-Path $ffLicences 'LICENSE.txt'))) { throw "FFmpeg's licences are missing: run fetch-tools.ps1" }
Copy-Item (Join-Path $ffLicences 'LICENSE.txt') (Join-Path $licences 'FFmpeg.txt')
Copy-Item $ffLicences (Join-Path $licences 'ffmpeg') -Recurse
$sources = Join-Path $licences 'sources'
New-Item -ItemType Directory -Force $sources | Out-Null
Copy-Item (Join-Path $root 'tools\bin\ffmpeg-source\*.tar.gz') $sources
if (-not (Get-ChildItem $sources -Filter '*-source.tar.gz')) { throw "FFmpeg's source is missing: run fetch-tools.ps1" }

# Magick.Native: DropWheel's custom Q8-x64 build without libheif/libde265/openh264 (patented codecs).
# Its licences (Apache-2.0 for ImageMagick, LGPL for fribidi/libiconv/zlib/etc.) and exact source.
$magickLicences = Join-Path $root 'tools\bin\magick'
if (-not (Test-Path (Join-Path $magickLicences 'Magick.Native-Q8-x64.dll'))) { throw "Magick.Native is missing: run fetch-tools.ps1 (after the dropwheel-magick release is published)" }
Copy-Item (Join-Path $magickLicences 'Magick.Native-Q8-x64.dll') (Join-Path $publish 'Magick.Native-Q8-x64.dll') -Force
Copy-Item (Join-Path $magickLicences 'Magick.Native-Q8-x64.pdb') (Join-Path $publish 'Magick.Native-Q8-x64.pdb') -Force
$magickLicencesDir = Join-Path $licences 'magick'
New-Item -ItemType Directory -Force $magickLicencesDir | Out-Null
# The licence files are in the magick folder (fetched from the release)
if (Test-Path (Join-Path $magickLicences 'LICENSE.txt')) {
    Copy-Item (Join-Path $magickLicences 'LICENSE.txt') (Join-Path $magickLicencesDir 'ImageMagick.txt')
}
if (Test-Path (Join-Path $magickLicences 'licenses')) {
    Copy-Item (Join-Path $magickLicences 'licenses') $magickLicencesDir -Recurse -Force
}
Copy-Item (Join-Path $root 'tools\bin\magick-source\*.tar.gz') $sources -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'tools\bin\exiftool\README.txt') (Join-Path $licences 'ExifTool.txt') -ErrorAction SilentlyContinue
# Where NuGet keeps packages on this PC.
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
# DirectML.dll ships beside Petal (Windows 10's own copy is too old for ONNX Runtime): its licence goes too.
$directml = Get-ChildItem (Join-Path $nuget 'microsoft.ai.directml') -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if ($directml) { Copy-Item (Join-Path $directml.FullName 'LICENSE.txt') (Join-Path $licences 'DirectML.txt') }
# Which FFmpeg build ships, for anyone who wants its source; and the list of everything Petal is built on, as a text
# file that Settings opens in Notepad, with the full texts of the licences it names.
$ffmpegVersion = & (Join-Path $publish 'tools\ffmpeg\ffmpeg.exe') -hide_banner -version | Select-Object -First 2
# Only DropWheel's own LGPL build may ship: any other (a GPL build, one with patented codecs) stops the release.
if (($ffmpegVersion | Select-Object -First 1) -notmatch 'dropwheel') { throw "The FFmpeg in the build isn't DropWheel's own LGPL build: $ffmpegVersion" }
$ffmpegVersion | Set-Content (Join-Path $licences 'FFmpeg-version.txt')

# Verify Magick.Native is DropWheel's custom build: no HEVC or H.264 codec inside (libde265, openh264, x265,
# kvazaar). libheif itself stays: it only reads and writes the HEIF/AVIF container, and decodes AVIF with
# aom/dav1d. The libraries are linked into the DLL, so their names and API symbols are looked for in its bytes.
$magickDllPath = Join-Path $publish 'Magick.Native-Q8-x64.dll'
if (-not (Test-Path $magickDllPath)) { throw "Magick.Native-Q8-x64.dll not found in build output" }
$magickContent = [System.Text.Encoding]::Latin1.GetString([System.IO.File]::ReadAllBytes($magickDllPath))
$codecMarks = [ordered]@{
    'libde265' = @('libde265', 'de265_')
    'openh264' = @('openh264', 'WelsCreate')
    'x265' = @('x265_')
    'kvazaar' = @('kvazaar', 'kvz_')
}
$found = @($codecMarks.Keys | Where-Object {
    foreach ($mark in $codecMarks[$_]) {
        if ($magickContent.IndexOf($mark, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
})
if ($found.Count -gt 0) {
    throw "FAIL: Magick.Native contains patented codecs: $($found -join ', '). This build cannot ship to the Microsoft Store."
}
Write-Host "PASS: Magick.Native holds no libde265, openh264, x265 or kvazaar"

Remove-Item (Join-Path $licences 'THIRD-PARTY-NOTICES.md') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'store\THIRD-PARTY-NOTICES.md') (Join-Path $licences 'THIRD-PARTY-NOTICES.txt')
Copy-Item (Join-Path $root 'store\licenses\*.txt') $licences
# Every package the app is built with, at the version it ships: its own licence and notice files, as its authors wrote them.
$deps = Get-Content (Join-Path $publish 'Petal.deps.json') -Raw | ConvertFrom-Json
$packages = Join-Path $licences 'packages'
New-Item -ItemType Directory -Force $packages | Out-Null
foreach ($library in $deps.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $folder = Join-Path $nuget $library.Value.path
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

    if ($NoFeed) {
        Write-Host 'No update.json (-NoFeed): this build is for trying out and sending by hand.'
        return
    }
    # The update feed: host update.json next to the installer (the url is relative to it).
    $manifest = [ordered]@{
        version = $Version
        url = $setupName
        sha256 = (Get-FileHash $setup -Algorithm SHA256).Hash
        size = (Get-Item $setup).Length
        notes = $Notes
        urgency = $Urgency
        changes = @($Changes)
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
    # The second signature (DropWheel 1.24 and later) covers the urgency, the changes and the notes as well;
    # older copies check only the first, so they keep updating.
    $details = (@('petal-update-2', $manifest.version, $manifest.url, $manifest.sha256.ToUpperInvariant(), "$($manifest.size)",
        $manifest.urgency, "$($Changes.Count)") + $Changes + @($manifest.notes)) -join "`n"
    $signature2 = $ecdsa.SignData([System.Text.Encoding]::UTF8.GetBytes($details), [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $manifest.signature2 = [Convert]::ToBase64String($signature2)
    $feed = Join-Path $dist ($(if ($Arch -eq 'arm64') { 'update-arm64.json' } else { 'update.json' }))
    $manifest | ConvertTo-Json -Depth 3 | Set-Content $feed -Encoding utf8
    Write-Host "Update feed: $feed"
} else {
    Write-Host 'Inno Setup not found; skipped Setup.exe (winget install JRSoftware.InnoSetup).'
}
