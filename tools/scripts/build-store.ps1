<#
.SYNOPSIS
  Builds Petal's Microsoft Store package (.msix) from the release in dist\Petal.

.DESCRIPTION
  Run tools\scripts\build.ps1 first: this packs what it published. The package gets the Store's tiles,
  Petal's file types in File Explorer's menu, the "petal" command for terminals, a startup task, and
  Piper, llama.cpp and DeepFilterNet (the programs behind natural voices, Ask Petal and Remove Noise),
  so the Store's copy never downloads a program: only the models' data.

  The identity values come from Partner Center (Apps and games > Petal > Product management > Product
  identity). Until they are given, the package carries placeholders that Partner Center refuses.

  The Store signs what it publishes, so the upload needs no certificate. -CertificatePfx signs the
  package only to try it on a PC before submitting (that PC must trust the certificate).

.EXAMPLE
  pwsh tools\scripts\build-store.ps1 -Version 1.5.0 -IdentityName 12345PetalDev.Petal -Publisher "CN=ABCD1234-..." -PublisherDisplayName "Petal Dev"
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$IdentityName = 'PetalDev.Petal',
    [string]$Publisher = 'CN=Petal Dev',
    [string]$PublisherDisplayName = 'Petal Dev',
    # The name reserved in Partner Center; the manifest's must match it exactly.
    [string]$DisplayName = 'Petal',
    [string]$CertificatePfx,
    [string]$CertificatePassword,
    # Keeps dist\store\layout, to register it on a PC in developer mode (Add-AppxPackage -Register).
    [switch]$KeepLayout
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$store = Join-Path $root 'tools\store'
$release = Join-Path $root 'dist\Petal'
$out = Join-Path $root 'dist\store'
$layout = Join-Path $out 'layout'
$cache = Join-Path $out 'cache'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version is three numbers, such as 1.5.0 (the Store adds .0)." }
$packageVersion = "$Version.0"
if (-not (Test-Path (Join-Path $release 'Petal.exe'))) { throw "There's no release in $release. Run tools\scripts\build.ps1 -Version $Version first." }
$built = (Get-Item (Join-Path $release 'Petal.exe')).VersionInfo.ProductVersion
if ($built -notlike "$Version*") { throw "dist\Petal holds Petal $built, not $Version. Run tools\scripts\build.ps1 -Version $Version first." }

# ---------- the SDK's packaging tools ----------
function Find-SdkTool([string]$name) {
    $kits = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\$name" -ErrorAction SilentlyContinue
    $nuget = Get-ChildItem (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools\*\bin\10.*\x64\$name") -ErrorAction SilentlyContinue
    @($kits) + @($nuget) | Where-Object { $_ } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not (Find-SdkTool 'makeappx.exe')) {
    # No Windows SDK: the same tools come in a NuGet package.
    $restore = Join-Path $out 'sdk-restore'
    New-Item -ItemType Directory -Force $restore | Out-Null
    Set-Content (Join-Path $restore 'restore.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.4654" /></ItemGroup></Project>'
    dotnet restore (Join-Path $restore 'restore.csproj') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Getting the Windows SDK build tools failed' }
    Remove-Item $restore -Recurse -Force
}
$makeappx = Find-SdkTool 'makeappx.exe'
$makepri = Find-SdkTool 'makepri.exe'
$signtool = Find-SdkTool 'signtool.exe'
if (-not $makeappx -or -not $makepri) { throw "makeappx.exe or makepri.exe wasn't found." }

# ---------- the layout: the release, as the package holds it ----------
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null
robocopy $release $layout /E /NFL /NDL /NJH /NJS /NP /XF '*.pdb' '*.lib' | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying the release failed' }
# The command line: Petal.com in the setup's folder, but an app execution alias must name an .exe.
Move-Item (Join-Path $layout 'Petal.com') (Join-Path $layout 'petal-cli.exe') -Force
Copy-Item (Join-Path $store 'Assets\*') (Join-Path $layout 'Assets') -Force

# The programs among the downloads (Piper, llama.cpp, DeepFilterNet) ship inside the package, checked
# against the same SHA-256 Petal checks: the Store's copy downloads only the models' data.
$catalog = Get-Content (Join-Path $root 'src\Petal.Core\Models\ModelStore.cs') -Raw
New-Item -ItemType Directory -Force $cache | Out-Null
$models = Join-Path $layout 'tools\models'
New-Item -ItemType Directory -Force $models | Out-Null
foreach ($id in 'piper', 'llama-cpp', 'deepfilternet') {
    # The package is x64 (it packs dist\Petal): an entry with an ARM64 build too gives its x64 one.
    $match = [regex]::Matches($catalog, "new\(\s*""$id"",\s*""[^""]+"",\s*""([^""]+)"",\s*""([^""]+)"",\s*""([0-9a-f]{64})"",\s*([0-9_]+)", 'Singleline') |
        Where-Object { $_.Groups[1].Value -notmatch 'arm64' } | Select-Object -First 1
    if (-not $match) { throw "The model catalogue has no x64 entry $id." }
    $file, $url, $sha, $size = $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value, [long]($match.Groups[4].Value -replace '_', '')
    $cached = Join-Path $cache $file
    if (-not (Test-Path $cached) -or (Get-FileHash $cached -Algorithm SHA256).Hash -ne $sha.ToUpperInvariant()) {
        Write-Host "Downloading $file"
        Invoke-WebRequest $url -OutFile "$cached.part" -UseBasicParsing
        if ((Get-FileHash "$cached.part" -Algorithm SHA256).Hash -ne $sha.ToUpperInvariant() -or (Get-Item "$cached.part").Length -ne $size) {
            Remove-Item "$cached.part"
            throw "$file doesn't match the SHA-256 in the model catalogue."
        }
        Move-Item "$cached.part" $cached -Force
    }
    Copy-Item $cached (Join-Path $models $file)
}

# ---------- the manifest and its resources ----------
$escape = { param($text) [System.Security.SecurityElement]::Escape($text) }
$manifest = (Get-Content (Join-Path $store 'AppxManifest.xml') -Raw).
    Replace('$IDENTITY_NAME$', (& $escape $IdentityName)).
    Replace('$PUBLISHER$', (& $escape $Publisher)).
    Replace('$PUBLISHER_DISPLAY_NAME$', (& $escape $PublisherDisplayName)).
    Replace('$VERSION$', $packageVersion)
Set-Content (Join-Path $layout 'AppxManifest.xml') $manifest -Encoding utf8

$priRoot = Join-Path $out 'pri'
if (Test-Path $priRoot) { Remove-Item $priRoot -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $priRoot 'Assets') | Out-Null
Copy-Item (Join-Path $store 'Assets\*') (Join-Path $priRoot 'Assets')
foreach ($language in 'en-us', 'ar-sa') {
    $folder = Join-Path $priRoot "Strings\$language"
    New-Item -ItemType Directory -Force $folder | Out-Null
    $words = (Get-Content (Join-Path $store "Strings\$language\Resources.resw") -Raw).Replace('$DISPLAY_NAME$', (& $escape $DisplayName))
    Set-Content (Join-Path $folder 'Resources.resw') $words -Encoding utf8
}
Copy-Item (Join-Path $layout 'AppxManifest.xml') $priRoot
$config = Join-Path $out 'priconfig.xml'
& $makepri createconfig /cf $config /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri createconfig failed' }
# One package, not a bundle: Arabic and every scale stay in resources.pri instead of resource packs.
$priXml = [xml](Get-Content $config -Raw)
$priXml.SelectNodes('//packaging') | ForEach-Object { $_.ParentNode.RemoveChild($_) | Out-Null }
$priXml.Save($config)
& $makepri new /pr $priRoot /cf $config /mn (Join-Path $priRoot 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o
if ($LASTEXITCODE -ne 0) { throw 'makepri failed' }
if (Get-ChildItem $layout -Filter 'resources.*.pri') { throw 'makepri split the resources into packs.' }
Remove-Item $priRoot -Recurse -Force
Remove-Item $config

# ---------- the package ----------
$package = Join-Path $out "Petal-$Version.msix"
Get-ChildItem $out -Filter 'Petal-*.msix' | Remove-Item
& $makeappx pack /d $layout /p $package /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed' }

if ($CertificatePfx) {
    if (-not $signtool) { throw "signtool.exe wasn't found." }
    $signArgs = @('sign', '/fd', 'SHA256', '/f', $CertificatePfx)
    if ($CertificatePassword) { $signArgs += @('/p', $CertificatePassword) }
    & $signtool @signArgs $package
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed (the certificate''s subject must be the -Publisher value)' }
}
if (-not $KeepLayout) { Remove-Item $layout -Recurse -Force }
$megabytes = [math]::Round((Get-Item $package).Length / 1MB)
Write-Host "Store package: $package ($megabytes MB), $IdentityName $packageVersion"
if ($IdentityName -eq 'PetalDev.Petal') {
    Write-Host 'The identity is still the placeholder: build again with the values from Partner Center before uploading.'
}
