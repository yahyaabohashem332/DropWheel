$bytes = [System.IO.File]::ReadAllBytes('D:/Tangerine/dist/Petal/Magick.Native-Q8-x64.dll')
Write-Host "Bytes length: $($bytes.Length)"
$content = [System.Text.Encoding]::Latin1.GetString($bytes)
Write-Host "Content length: $($content.Length)"