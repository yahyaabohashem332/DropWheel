<#
.SYNOPSIS
  Fills tools\bin with the helper programs Petal ships with: FFmpeg (shared build) and ExifTool.
  Every download is checked against a published SHA-256 before it is unpacked.

.EXAMPLE
  pwsh tools\scripts\fetch-tools.ps1
#>
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\bin'),
    [string]$ExifToolVersion = '13.59',
    [string]$ExifToolSha256 = '44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec'
)
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
$downloads = Join-Path $Destination 'download'
New-Item -ItemType Directory -Force $downloads | Out-Null

function Get-Verified([string]$Url, [string]$File, [string]$Sha256) {
    if (-not (Test-Path $File) -or (Get-FileHash $File -Algorithm SHA256).Hash -ne $Sha256.ToUpper()) {
        Write-Host "Downloading $Url"
        & curl.exe -sSL --ssl-no-revoke -A 'Wget/1.21' -o $File $Url
    }
    $actual = (Get-FileHash $File -Algorithm SHA256).Hash
    if ($actual -ne $Sha256.ToUpper()) { throw "Checksum mismatch for $File (got $actual)" }
}

# ExifTool: official Windows build from exiftool.org (hosted on SourceForge).
$exifZip = Join-Path $downloads "exiftool-$($ExifToolVersion)_64.zip"
Get-Verified "https://downloads.sourceforge.net/project/exiftool/exiftool-$($ExifToolVersion)_64.zip" $exifZip $ExifToolSha256
$exifDir = Join-Path $Destination 'exiftool'
if (-not (Test-Path (Join-Path $exifDir 'exiftool.exe'))) {
    $tmp = Join-Path $downloads 'exiftool-x'
    Expand-Archive $exifZip -DestinationPath $tmp -Force
    $root = Get-ChildItem $tmp -Directory | Select-Object -First 1
    if (Test-Path $exifDir) { Remove-Item $exifDir -Recurse -Force }
    Move-Item $root.FullName $exifDir
    Rename-Item (Join-Path $exifDir 'exiftool(-k).exe') 'exiftool.exe'
    Remove-Item $tmp -Recurse -Force
}
Write-Host "ExifTool: $(& (Join-Path $exifDir 'exiftool.exe') -ver)"

# FFmpeg: the GPL "shared" build from BtbN (DLLs shared by ffmpeg.exe and ffprobe.exe keeps it small).
$ffDir = Join-Path $Destination 'ffmpeg'
if (-not (Test-Path (Join-Path $ffDir 'ffmpeg.exe'))) {
    $release = Invoke-RestMethod 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest' -Headers @{ 'User-Agent' = 'petal-build' }
    $asset = $release.assets | Where-Object { $_.name -match '^ffmpeg-n\d+(\.\d+)*-latest-win64-gpl-shared-\d+(\.\d+)*\.zip$' } | Select-Object -First 1
    if (-not $asset) { $asset = $release.assets | Where-Object { $_.name -eq 'ffmpeg-master-latest-win64-gpl-shared.zip' } | Select-Object -First 1 }
    $sums = Invoke-RestMethod ($release.assets | Where-Object name -eq 'checksums.sha256').browser_download_url
    $line = ($sums -split "`n") | Where-Object { $_ -match [regex]::Escape($asset.name) } | Select-Object -First 1
    $sha = ($line -split '\s+')[0]
    $zip = Join-Path $downloads $asset.name
    Get-Verified $asset.browser_download_url $zip $sha
    $tmp = Join-Path $downloads 'ffmpeg-x'
    Expand-Archive $zip -DestinationPath $tmp -Force
    $root = Get-ChildItem $tmp -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Force $ffDir | Out-Null
    Copy-Item (Join-Path $root.FullName 'bin\*') $ffDir -Force
    Copy-Item (Join-Path $root.FullName 'LICENSE.txt') (Join-Path $ffDir 'LICENSE.txt') -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $ffDir 'ffplay.exe') -ErrorAction SilentlyContinue
    Remove-Item $tmp -Recurse -Force
}
Write-Host "FFmpeg: $((& (Join-Path $ffDir 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1))"
Write-Host "Helper programs are in $Destination"
