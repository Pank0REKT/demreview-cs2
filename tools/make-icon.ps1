# Генерирует иконки из логотипа tools/logo-source.png (прозрачный фон):
#   src/DemReview.Desktop/Assets/demreview.png (256px, окно на Linux и боковая панель) и demreview.ico (16–256px, Windows),
#   site/assets/icon.png (256px, favicon и шапка сайта), docs/images/logo.png (512px, шапка README).
# Требуется Windows PowerShell (System.Drawing). Результат коммитится в репозиторий, запуск нужен только при смене логотипа.
Add-Type -AssemblyName System.Drawing
$root = Join-Path $PSScriptRoot '..'
$assets = Join-Path $root 'src\DemReview.Desktop\Assets'
$source = [System.Drawing.Bitmap]::FromFile((Join-Path $PSScriptRoot 'logo-source.png'))

# Поля вокруг радара обрезаются: квадрат по непрозрачной части с небольшим отступом.
function Get-ContentSquare($bmp) {
    $minX = $bmp.Width; $minY = $bmp.Height; $maxX = 0; $maxY = 0
    for ($y = 0; $y -lt $bmp.Height; $y += 2) {
        for ($x = 0; $x -lt $bmp.Width; $x += 2) {
            if ($bmp.GetPixel($x, $y).A -gt 40) {
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    $side = [Math]::Max($maxX - $minX, $maxY - $minY) * 1.04
    $cx = ($minX + $maxX) / 2; $cy = ($minY + $maxY) / 2
    return New-Object System.Drawing.RectangleF ($cx - $side / 2), ($cy - $side / 2), $side, $side
}

$square = Get-ContentSquare $source

function New-IconBitmap([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.SmoothingMode = 'AntiAlias'
    $g.DrawImage($source, (New-Object System.Drawing.RectangleF 0, 0, $size, $size), $square, 'Pixel')
    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) { , (Get-PngBytes (New-IconBitmap $size)) }

[System.IO.File]::WriteAllBytes((Join-Path $assets 'demreview.png'), $images[-1])
[System.IO.File]::WriteAllBytes((Join-Path $root 'site\assets\icon.png'), $images[-1])
[System.IO.File]::WriteAllBytes((Join-Path $root 'docs\images\logo.png'), (Get-PngBytes (New-IconBitmap 512)))

# ICO с PNG-кадрами (поддерживается Windows Vista+)
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'demreview.ico'), $ico.ToArray())
$source.Dispose()
Write-Host "Иконки сохранены: $assets, site\assets\icon.png, docs\images\logo.png"
