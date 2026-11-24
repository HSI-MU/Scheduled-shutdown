Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$output = Join-Path $assets 'app.ico'
$preview = Join-Path $assets 'app.png'

function New-RoundedPath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
    )
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(0.5, $Size * 0.025)
    $radius = [Math]::Max(2.0, $Size * 0.225)
    $rect = [System.Drawing.RectangleF]::new(
        [float]$inset,
        [float]$inset,
        [float]($Size - $inset * 2),
        [float]($Size - $inset * 2)
    )
    $path = New-RoundedPath -X $rect.X -Y $rect.Y -Width $rect.Width -Height $rect.Height -Radius $radius

    $startColor = [System.Drawing.Color]::FromArgb(255, 251, 172, 116)
    $endColor = [System.Drawing.Color]::FromArgb(255, 226, 93, 42)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect,
        $startColor,
        $endColor,
        50.0
    )
    $graphics.FillPath($brush, $path)

    $lineWidth = [Math]::Max(1.35, $Size * 0.065)
    $powerPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [float]$lineWidth)
    $powerPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $powerPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arcDiameter = $Size * 0.44
    $arcX = ($Size - $arcDiameter) / 2
    $arcY = $Size * 0.29
    $graphics.DrawArc(
        $powerPen,
        [float]$arcX,
        [float]$arcY,
        [float]$arcDiameter,
        [float]$arcDiameter,
        -54.0,
        288.0
    )

    $lineTop = $Size * 0.255
    $lineBottom = $Size * 0.50
    $lineX = $Size / 2.0
    $graphics.DrawLine(
        $powerPen,
        [float]$lineX,
        [float]$lineTop,
        [float]$lineX,
        [float]$lineBottom
    )

    $powerPen.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $graphics.Dispose()

    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    $bitmap.Dispose()
    return $bytes
}

$sizes = @(256, 128, 64, 48, 32, 24, 16)
$images = @()
foreach ($size in $sizes) {
    $images += ,@($size, (New-IconPng -Size $size))
}

$pngBytes = $images[0][1]
[System.IO.File]::WriteAllBytes($preview, $pngBytes)

$file = [System.IO.File]::Create($output)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $size = [int]$image[0]
        $bytes = [byte[]]$image[1]
        $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image[1])
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Output "Created $output"
Write-Output "Created $preview"
