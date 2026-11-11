<#
.SYNOPSIS
  Fills tools\bin with the helper programs Petal ships with: FFmpeg (shared build) and ExifTool.
  Every download is checked against a pinned SHA-256 before it is unpacked.

.DESCRIPTION
  FFmpeg is DropWheel's own LGPL build (https://github.com/yahyaabohashem332/dropwheel-ffmpeg): no decoder or
  encoder of any format still under patent; Windows decodes and encodes those. Its release holds the build and
  the exact sources the LGPL asks for, which go to tools\bin\ffmpeg-licenses and tools\bin\ffmpeg-source.

.EXAMPLE
  pwsh tools\scripts\fetch-tools.ps1
#>
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\bin'),
    [string]$ExifToolVersion = '13.59',
    [string]$ExifToolSha256 = '44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec',
    [string]$FfmpegRepository = 'yahyaabohashem332/dropwheel-ffmpeg',
    [string]$FfmpegRelease = 'ffmpeg-n9.0.2-dropwheel-win64',
    [string]$FfmpegZipSha256 = 'd85c5cb454572eacae0c9e878e0b2ce2761272defa29146634900efa865171ae',
    [string]$FfmpegSourceSha256 = '7a28f54f50df2a33f1fd79f78d213279e56794a99644bb66c4383bd3a18a60bf'
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

# FFmpeg: DropWheel's own LGPL build, its programs and DLLs (shared by ffmpeg.exe and ffprobe.exe, and replaceable
# by the user, as the LGPL wants). Any other FFmpeg found here (an earlier GPL build) is replaced.
$ffDir = Join-Path $Destination 'ffmpeg'
$ffLicences = Join-Path $Destination 'ffmpeg-licenses'
$ffSource = Join-Path $Destination 'ffmpeg-source'
$ffExe = Join-Path $ffDir 'ffmpeg.exe'
$current = if (Test-Path $ffExe) { (& $ffExe -hide_banner -version | Select-Object -First 1) } else { '' }
if ($current -notmatch 'dropwheel' -or -not (Test-Path (Join-Path $ffSource "$FfmpegRelease-source.tar.gz"))) {
    $zipName = "$FfmpegRelease.zip"
    $sourceName = "$FfmpegRelease-source.tar.gz"
    $zip = Join-Path $downloads $zipName
    $source = Join-Path $downloads $sourceName
    foreach ($file in @(@($zip, $zipName, $FfmpegZipSha256), @($source, $sourceName, $FfmpegSourceSha256))) {
        $path, $name, $sha = $file
        if ((Test-Path $path) -and (Get-FileHash $path -Algorithm SHA256).Hash -eq $sha.ToUpper()) { continue }
        # The build repository may be private: GitHub's command line, signed in, fetches from it; a public
        # repository needs no sign-in.
        if (Get-Command gh -ErrorAction SilentlyContinue) {
            & gh release download $FfmpegRelease -R $FfmpegRepository -p $name -D $downloads --clobber
        }
        if (-not (Test-Path $path)) {
            & curl.exe -sSL --ssl-no-revoke -o $path "https://github.com/$FfmpegRepository/releases/download/$FfmpegRelease/$name"
        }
        $actual = (Get-FileHash $path -Algorithm SHA256).Hash
        if ($actual -ne $sha.ToUpper()) { throw "Checksum mismatch for $path (got $actual)" }
    }
    $tmp = Join-Path $downloads 'ffmpeg-x'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive $zip -DestinationPath $tmp -Force
    $root = Get-ChildItem $tmp -Directory | Select-Object -First 1
    foreach ($dir in @($ffDir, $ffLicences, $ffSource)) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        New-Item -ItemType Directory -Force $dir | Out-Null
    }
    Copy-Item (Join-Path $root.FullName '*.exe') $ffDir -Force
    Copy-Item (Join-Path $root.FullName '*.dll') $ffDir -Force
    Copy-Item (Join-Path $root.FullName '*.txt') $ffLicences -Force
    Copy-Item (Join-Path $root.FullName 'licenses') $ffLicences -Recurse -Force
    Copy-Item $source (Join-Path $ffSource $sourceName) -Force
    Remove-Item $tmp -Recurse -Force
    # The files keep their archive's times, older than a copy an earlier build left beside the app or the tests,
    # which a build then leaves in place (it copies only what is newer): made new, they replace it.
    Get-ChildItem $ffDir, $ffLicences, $ffSource -Recurse -File | ForEach-Object { $_.LastWriteTime = Get-Date }
}
$version = & $ffExe -hide_banner -version | Select-Object -First 1
if ($version -notmatch 'dropwheel') { throw "tools\bin\ffmpeg isn't DropWheel's FFmpeg build: $version" }
Write-Host "FFmpeg: $version"
# Real-ESRGAN general x4v3 (BSD-3-Clause), Qualcomm AI Hub's ONNX export: the AI enlargement model.
$esrganDir = Join-Path $Destination 'models\realesrgan-x4v3'
if (-not (Test-Path (Join-Path $esrganDir 'real_esrgan_general_x4v3.onnx'))) {
    $zip = Join-Path $downloads 'real_esrgan_general_x4v3-onnx-float.zip'
    Get-Verified 'https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/real_esrgan_general_x4v3/releases/v0.64.0/real_esrgan_general_x4v3-onnx-float.zip' $zip 'fc094329b24a00c936ebc1cc4c51bc59633e7dc36f4a553344342171cbd3c7cf'
    $tmp = Join-Path $downloads 'esrgan-x'
    Expand-Archive $zip -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Force $esrganDir | Out-Null
    # The graph and its external weights must keep their names; the graph refers to the .data file.
    Copy-Item (Join-Path $tmp 'real_esrgan_general_x4v3-onnx-float\*') $esrganDir -Force
    Remove-Item $tmp -Recurse -Force
}
# Silero VAD 6.2 (MIT), whisper.cpp's ggml file: tells speech from other sound for live captions.
Get-Verified 'https://huggingface.co/ggml-org/whisper-vad/resolve/9ffd54a1e1ee413ddf265af9913beaf518d1639b/ggml-silero-v6.2.0.bin' (Join-Path $Destination 'models\ggml-silero-v6.2.0.bin') '2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987'
Write-Host "Models: $((Get-ChildItem (Join-Path $Destination 'models') -Recurse -File).Name -join ', ')"
Write-Host "Helper programs are in $Destination"
