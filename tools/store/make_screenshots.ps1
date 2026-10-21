<#
.SYNOPSIS
  Draws the Store's screenshots (1920 x 1080, English and Arabic) into store\screenshots.

.DESCRIPTION
  Petal renders its own wheels and windows (--snapshots, --snapshot-tools) over a soft backdrop; this
  script then sets each one beside a headline in its language. WPF draws the text, so Arabic is shaped
  and laid out right to left. Settings, history and statistics go to a scratch folder, never the user's.

.EXAMPLE
  pwsh tools\store\make_screenshots.ps1
#>
param(
    [string]$Petal
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $Petal) { $Petal = Join-Path $root 'src\Petal.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Petal.exe' }
if (-not (Test-Path $Petal)) { throw "Petal.exe isn't at ${Petal}: build the app first (dotnet build src\Petal.App)." }
$out = Join-Path $root 'store\screenshots'
$work = Join-Path ([IO.Path]::GetTempPath()) ('petal-shots-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$W = 1920; $H = 1080; $Wheel = 840; $Margin = 120

function Get-Colour([string]$hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }

function Save-Png($bitmap, [string]$path) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create($path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

function Open-Image([string]$path) {
    $image = New-Object System.Windows.Media.Imaging.BitmapImage
    $image.BeginInit()
    $image.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
    $image.UriSource = New-Object Uri($path)
    $image.EndInit()
    $image.Freeze()
    $image
}

function Render-Element($element, [int]$width, [int]$height) {
    $element.Measure((New-Object System.Windows.Size($width, $height)))
    $element.Arrange((New-Object System.Windows.Rect(0, 0, $width, $height)))
    $element.UpdateLayout()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($width, $height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($element)
    $bitmap
}

# ---------- the backdrop: soft light behind the wheel ----------
$visual = New-Object System.Windows.Media.DrawingVisual
$dc = $visual.RenderOpen()
$base = New-Object System.Windows.Media.LinearGradientBrush((Get-Colour '#1A1230'), (Get-Colour '#0C2230'), 35)
$dc.DrawRectangle($base, $null, (New-Object System.Windows.Rect(0, 0, $W, $H)))
foreach ($glow in @(
    @('#806E56CF', 320, 260, 760), @('#5512A594', 1520, 900, 700), @('#4DD6409F', 1680, 140, 560),
    @('#33FFB224', 760, 1040, 520), @('#406E56CF', 1100, 520, 420))) {
    $centre = Get-Colour $glow[0]
    $edge = [System.Windows.Media.Color]::FromArgb(0, $centre.R, $centre.G, $centre.B)
    $brush = New-Object System.Windows.Media.RadialGradientBrush($centre, $edge)
    $dc.DrawEllipse($brush, $null, (New-Object System.Windows.Point($glow[1], $glow[2])), $glow[3], $glow[3])
}
$dc.Close()
$backdrop = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($W, $H, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$backdrop.Render($visual)
$backdrop.Freeze()

# The wheel's own background is the part of the backdrop it sits on, at half size (it renders at 2x).
function Save-WheelBackground([int]$left, [string]$path) {
    $crop = New-Object System.Windows.Media.Imaging.CroppedBitmap($backdrop, (New-Object System.Windows.Int32Rect($left, (($H - $Wheel) / 2), $Wheel, $Wheel)))
    $half = New-Object System.Windows.Media.Imaging.TransformedBitmap($crop, (New-Object System.Windows.Media.ScaleTransform(0.5, 0.5)))
    Save-Png $half $path
}

# A sample photo to drag: a sunset, drawn here.
function Save-Sample([string]$path) {
    $v = New-Object System.Windows.Media.DrawingVisual
    $c = $v.RenderOpen()
    $sky = New-Object System.Windows.Media.LinearGradientBrush((Get-Colour '#2B1A6B'), (Get-Colour '#FFA05A'), 90)
    $c.DrawRectangle($sky, $null, (New-Object System.Windows.Rect(0, 0, 1600, 1066)))
    $c.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush((Get-Colour '#FFE28A'))), $null, (New-Object System.Windows.Point(990, 700)), 120, 120)
    $c.DrawRectangle((New-Object System.Windows.Media.SolidColorBrush((Get-Colour '#3A1F5C'))), $null, (New-Object System.Windows.Rect(0, 760, 1600, 306)))
    $c.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(1600, 1066, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($v)
    $encoder = New-Object System.Windows.Media.Imaging.JpegBitmapEncoder
    $encoder.QualityLevel = 90
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $stream = [IO.File]::Create($path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

function Invoke-Petal([string[]]$arguments, [string]$expect) {
    for ($try = 1; $try -le 2 -and -not (Test-Path $expect); $try++) {
        $quoted = $arguments | ForEach-Object { '"' + $_ + '"' }
        Start-Process $Petal -ArgumentList $quoted -Wait -WindowStyle Hidden | Out-Null
    }
    if (-not (Test-Path $expect)) { throw "Petal didn't render $expect" }
}

$env:PETAL_SETTINGS_DIR = Join-Path $work 'settings'
New-Item -ItemType Directory -Force $env:PETAL_SETTINGS_DIR | Out-Null

$shots = @{
    'en' = @(
        @{ Image = 'dark-conversions-hover'; Title = 'Drag. Tap Shift. Drop.'; Text = 'Every format a file can become, right under your cursor. The result is saved beside the original.' },
        @{ Image = 'dark-tools-hover'; Title = '53 tools on the Ctrl wheel'; Text = 'Compress, resize, watermark, remove backgrounds, transcribe and more, all on your PC.' },
        @{ Image = 'dark-actions-hover'; Title = 'Actions on the Alt wheel'; Text = 'Share, send to your phone, clean for sharing, rename, zip, or copy to folders of your own.' },
        @{ Image = 'dark-ring-quick'; Title = 'Wheels without files'; Text = 'Dark mode, mute, the microphone, keep awake and screenshots. Press Ctrl + Win + Z with nothing selected.' },
        @{ Image = 'dark-ring-browser'; Title = 'A wheel for the app in front'; Text = 'Ready-made shortcuts for browsers, Office, VS Code, Photoshop, Teams and more.' },
        @{ Image = 'window:dark-stats'; Title = 'Everything stays on your PC'; Text = 'No account, no uploads, no tracking. Even your statistics are counted on this PC only.' }
    )
    'ar' = @(
        @{ Image = 'dark-conversions-hover'; Title = 'اسحب. اضغط Shift. أفلت.'; Text = 'كل صيغة يمكن أن يصير إليها الملف، تحت المؤشر مباشرة. ويُحفظ الناتج بجانب الأصل.' },
        @{ Image = 'dark-tools-hover'; Title = '53 أداة في عجلة Ctrl'; Text = 'الضغط، وتغيير الحجم، والعلامة المائية، وإزالة الخلفية، وتفريغ الكلام وغيرها، كلها على جهازك.' },
        @{ Image = 'dark-actions-hover'; Title = 'الإجراءات في عجلة Alt'; Text = 'شارك، وأرسل إلى هاتفك، ونظّف قبل المشاركة، وأعد التسمية، واضغط في ZIP، أو انسخ إلى مجلداتك.' },
        @{ Image = 'dark-ring-quick'; Title = 'عجلات لا تحتاج ملفات'; Text = 'الوضع الداكن، وكتم الصوت، والميكروفون، وإبقاء الجهاز متيقظًا، ولقطات الشاشة. اضغط Ctrl + Win + Z دون تحديد شيء.' },
        @{ Image = 'dark-ring-browser'; Title = 'عجلة للتطبيق الذي في الواجهة'; Text = 'اختصارات جاهزة للمتصفحات وOffice وVS Code وPhotoshop وTeams وغيرها.' },
        @{ Image = 'window:dark-stats'; Title = 'كل شيء يبقى على جهازك'; Text = 'لا حساب، ولا رفع، ولا تتبّع. حتى إحصاءاتك تُعدّ على هذا الجهاز وحده.' }
    )
}
$logo = Open-Image (Join-Path $root 'src\Petal.App\Assets\Petal-1024.png')

foreach ($lang in 'en', 'ar') {
    $rtl = $lang -eq 'ar'
    # English reads from the left, so the wheel sits on the right; Arabic the other way round.
    $wheelLeft = if ($rtl) { $Margin } else { $W - $Margin - $Wheel }
    $textLeft = if ($rtl) { $W - $Margin - 720 } else { $Margin }
    $folder = Join-Path $work $lang
    New-Item -ItemType Directory -Force $folder | Out-Null
    $background = Join-Path $work "background-$lang.png"
    Save-WheelBackground $wheelLeft $background
    $sample = Join-Path $work ($(if ($rtl) { 'غروب.jpg' } else { 'Sunset.jpg' }))
    Save-Sample $sample
    Invoke-Petal @('--snapshots', $folder, $background, $sample, '--lang', $lang) (Join-Path $folder 'dark-ring-browser.png')
    Invoke-Petal @('--snapshot-tools', $folder, $work, 'stats', '--lang', $lang) (Join-Path $folder 'dark-stats.png')

    $target = Join-Path $out $(if ($rtl) { 'ar-SA' } else { 'en-US' })
    New-Item -ItemType Directory -Force $target | Out-Null
    Get-ChildItem $target -Filter '*.png' | Remove-Item
    $n = 0
    foreach ($shot in $shots[$lang]) {
        $n++
        $canvas = New-Object System.Windows.Controls.Canvas -Property @{ Width = $W; Height = $H; FlowDirection = 'LeftToRight' }
        $canvas.Children.Add((New-Object System.Windows.Controls.Image -Property @{ Source = $backdrop; Width = $W; Height = $H })) | Out-Null
        if ($shot.Image -like 'window:*') {
            $picture = Open-Image (Join-Path $folder ($shot.Image.Substring(7) + '.png'))
            $height = [Math]::Min(860, $picture.PixelHeight)
            $width = $picture.PixelWidth / $picture.PixelHeight * $height
            $image = New-Object System.Windows.Controls.Image -Property @{ Source = $picture; Width = $width; Height = $height }
            $image.Effect = New-Object System.Windows.Media.Effects.DropShadowEffect -Property @{ BlurRadius = 48; ShadowDepth = 12; Opacity = 0.45; Direction = 270 }
            [System.Windows.Controls.Canvas]::SetLeft($image, $wheelLeft + ($Wheel - $width) / 2)
            [System.Windows.Controls.Canvas]::SetTop($image, ($H - $height) / 2)
        } else {
            $image = New-Object System.Windows.Controls.Image -Property @{ Source = (Open-Image (Join-Path $folder ($shot.Image + '.png'))); Width = $Wheel; Height = $Wheel }
            [System.Windows.Controls.Canvas]::SetLeft($image, $wheelLeft)
            [System.Windows.Controls.Canvas]::SetTop($image, ($H - $Wheel) / 2)
        }
        $canvas.Children.Add($image) | Out-Null

        $words = New-Object System.Windows.Controls.StackPanel -Property @{ Width = 720; FlowDirection = $(if ($rtl) { 'RightToLeft' } else { 'LeftToRight' }) }
        $brand = New-Object System.Windows.Controls.StackPanel -Property @{ Orientation = 'Horizontal'; Margin = '0,0,0,40' }
        $brand.Children.Add((New-Object System.Windows.Controls.Image -Property @{ Source = $logo; Width = 52; Height = 52; FlowDirection = 'LeftToRight' })) | Out-Null
        $brand.Children.Add((New-Object System.Windows.Controls.TextBlock -Property @{ Text = 'Petal'; FontFamily = 'Segoe UI'; FontWeight = 'SemiBold'; FontSize = 30; Foreground = 'White'; VerticalAlignment = 'Center'; Margin = '14,0,14,0' })) | Out-Null
        $words.Children.Add($brand) | Out-Null
        $words.Children.Add((New-Object System.Windows.Controls.TextBlock -Property @{ Text = $shot.Title; FontFamily = 'Segoe UI'; FontWeight = $(if ($rtl) { 'Bold' } else { 'SemiBold' }); FontSize = 64; LineHeight = 80; Foreground = 'White'; TextWrapping = 'Wrap' })) | Out-Null
        $words.Children.Add((New-Object System.Windows.Controls.TextBlock -Property @{ Text = $shot.Text; FontFamily = 'Segoe UI'; FontSize = 30; LineHeight = 44; Foreground = (New-Object System.Windows.Media.SolidColorBrush((Get-Colour '#D9D4F2'))); TextWrapping = 'Wrap'; Margin = '0,24,0,0' })) | Out-Null
        $words.Measure((New-Object System.Windows.Size(720, [double]::PositiveInfinity)))
        [System.Windows.Controls.Canvas]::SetLeft($words, $textLeft)
        [System.Windows.Controls.Canvas]::SetTop($words, ($H - $words.DesiredSize.Height) / 2)
        $canvas.Children.Add($words) | Out-Null

        $file = Join-Path $target ('{0:00}-{1}.png' -f $n, ($shot.Image -replace '^(window:)?(dark|light)-', ''))
        Save-Png (Render-Element $canvas $W $H) $file
        Write-Host $file
    }
}
# ---------- the listing's art: box art, poster and tile icon ----------
$logos = Join-Path $root 'store\logos'
New-Item -ItemType Directory -Force $logos | Out-Null
foreach ($art in @(@{ Name = 'box-art-2160x2160.png'; W = 2160; H = 2160 }, @{ Name = 'poster-art-1440x2160.png'; W = 1440; H = 2160 })) {
    $canvas = New-Object System.Windows.Controls.Grid -Property @{ Width = $art.W; Height = $art.H }
    $canvas.Children.Add((New-Object System.Windows.Controls.Image -Property @{ Source = $backdrop; Stretch = 'UniformToFill' })) | Out-Null
    $stack = New-Object System.Windows.Controls.StackPanel -Property @{ VerticalAlignment = 'Center'; HorizontalAlignment = 'Center' }
    $stack.Children.Add((New-Object System.Windows.Controls.Image -Property @{ Source = $logo; Width = $art.W * 0.42; Height = $art.W * 0.42 })) | Out-Null
    $stack.Children.Add((New-Object System.Windows.Controls.TextBlock -Property @{ Text = 'Petal'; FontFamily = 'Segoe UI'; FontWeight = 'SemiBold'; FontSize = $art.W * 0.085; Foreground = 'White'; HorizontalAlignment = 'Center'; Margin = "0,$($art.W * 0.04),0,0" })) | Out-Null
    $canvas.Children.Add($stack) | Out-Null
    Save-Png (Render-Element $canvas $art.W $art.H) (Join-Path $logos $art.Name)
}
$tile = New-Object System.Windows.Controls.Grid -Property @{ Width = 300; Height = 300 }
$tile.Children.Add((New-Object System.Windows.Controls.Image -Property @{ Source = $logo; Width = 270; Height = 270 })) | Out-Null
Save-Png (Render-Element $tile 300 300) (Join-Path $logos 'tile-icon-300x300.png')
Write-Host "Listing art in $logos"

Remove-Item $work -Recurse -Force
