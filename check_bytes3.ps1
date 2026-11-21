# Search raw bytes for ASCII patterns
$filePath = 'D:/Tangerine/dist/Petal/Magick.Native-Q8-x64.dll'
$patterns = @(
    [System.Text.Encoding]::ASCII.GetBytes('libheif'),
    [System.Text.Encoding]::ASCII.GetBytes('libde265'),
    [System.Text.Encoding]::ASCII.GetBytes('openh264')
)
$patternNames = @('libheif', 'libde265', 'openh264')
$found = @()

$bufferSize = 1024 * 1024  # 1MB chunks
$buffer = New-Object byte[] $bufferSize

$stream = [System.IO.File]::OpenRead($filePath)
try {
    while ($true) {
        $read = $stream.Read($buffer, 0, $bufferSize)
        if ($read -eq 0) { break }

        for ($i = 0; $i -lt $patterns.Count; $i++) {
            $pattern = $patterns[$i]
            $patternName = $patternNames[$i]
            # Simple byte array search
            for ($j = 0; $j -le $read - $pattern.Length; $j++) {
                $match = $true
                for ($k = 0; $k -lt $pattern.Length; $k++) {
                    if ($buffer[$j + $k] -ne $pattern[$k]) {
                        $match = $false
                        break
                    }
                }
                if ($match) {
                    $found += $patternName
                    break
                }
            }
        }
    }
} finally {
    $stream.Close()
}

$uniqueFound = $found | Select-Object -Unique
if ($uniqueFound.Count -gt 0) {
    Write-Host "FAIL: $($uniqueFound -join ', ')"
    exit 1
} else {
    Write-Host "PASS: Magick.Native verified - no libheif/libde265/openh264 found"
}