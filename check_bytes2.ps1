# Read file in chunks and search for patterns
$filePath = 'D:/Tangerine/dist/Petal/Magick.Native-Q8-x64.dll'
$patterns = @('libheif', 'libde265', 'openh264')
$found = @()

$bufferSize = 1024 * 1024  # 1MB chunks
$buffer = New-Object byte[] $bufferSize

$stream = [System.IO.File]::OpenRead($filePath)
try {
    while ($true) {
        $read = $stream.Read($buffer, 0, $bufferSize)
        if ($read -eq 0) { break }

        $chunk = [System.Text.Encoding]::Latin1.GetString($buffer, 0, $read)
        foreach ($pattern in $patterns) {
            if ($chunk -match $pattern) {
                $found += $pattern
            }
        }
    }
} finally {
    $stream.Close()
}

if ($found.Count -gt 0) {
    Write-Host "FAIL: $($found | Select-Object -Unique -join ', ')"
    exit 1
} else {
    Write-Host "PASS: Magick.Native verified - no libheif/libde265/openh264 found"
}