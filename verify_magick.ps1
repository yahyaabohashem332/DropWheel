$bytes = [System.IO.File]::ReadAllBytes('D:/Tangerine/dist/Petal/Magick.Native-Q8-x64.dll')
$content = [System.Text.Encoding]::Latin1.GetString($bytes)
$found = @()
foreach ($lib in 'libheif', 'libde265', 'openh264') {
    if ($content -match $lib) { $found += $lib }
}
if ($found.Count -gt 0) {
    Write-Host "FAIL: $($found -join ', ')"
    exit 1
} else {
    Write-Host "PASS: Magick.Native verified - no libheif/libde265/openh264 found"
}