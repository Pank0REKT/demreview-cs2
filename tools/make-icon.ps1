# Генерирует иконку приложения: Assets/demreview.png (256px, для Linux) и Assets/demreview.ico (16–256px, для Windows).
# Требуется Windows PowerShell (System.Drawing). Результат коммитится в репозиторий, запуск нужен только при изменении дизайна.
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\src\DemReview.Desktop\Assets'

function New-IconBitmap([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $size / 256.0

    # Фон — скруглённый квадрат
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 56 * $s; $w = 240 * $s; $o = 8 * $s
    $path.AddArc($o, $o, $r, $r, 180, 90); $path.AddArc($o + $w - $r, $o, $r, $r, 270, 90)
    $path.AddArc($o + $w - $r, $o + $w - $r, $r, $r, 0, 90); $path.AddArc($o, $o + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 36, 44))), $path)

    # Кольца радара
    $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 70, 160, 120)), ([Math]::Max(1.5, 10 * $s))
    foreach ($d in 176, 104) { $p = (256 - $d) / 2 * $s; $g.DrawEllipse($ring, $p, $p, $d * $s, $d * $s) }
    $thin = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(160, 70, 160, 120)), ([Math]::Max(1, 6 * $s))
    $g.DrawLine($thin, 128 * $s, 40 * $s, 128 * $s, 216 * $s); $g.DrawLine($thin, 40 * $s, 128 * $s, 216 * $s, 128 * $s)

    # Маршрут игрока и точка
    $route = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 160, 50)), ([Math]::Max(2, 14 * $s))
    $route.StartCap = 'Round'; $route.EndCap = 'Round'; $route.LineJoin = 'Round'
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (70 * $s), (190 * $s)),
        (New-Object System.Drawing.PointF (110 * $s), (150 * $s)),
        (New-Object System.Drawing.PointF (150 * $s), (160 * $s)),
        (New-Object System.Drawing.PointF (180 * $s), (90 * $s)))
    $g.DrawLines($route, $pts)
    $dot = 40 * $s
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 90, 70))), (180 * $s - $dot / 2), (90 * $s - $dot / 2), $dot, $dot)

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
Write-Host "Иконки сохранены в $assets"
