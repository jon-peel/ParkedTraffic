# Draws icon.ico: a white aircraft (top-down) parked at a cyan stop bar on a dark rounded square.
# Run with Windows PowerShell:  powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'icon.ico'

function New-IconBitmap([int]$size) {
    $s = $size / 256.0
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Dark rounded square.
    $r = 48 * $s; $w = $size - 1
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bg.AddArc(0, 0, $r, $r, 180, 90); $bg.AddArc($w - $r, 0, $r, $r, 270, 90)
    $bg.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $bg.AddArc(0, $w - $r, $r, $r, 90, 90); $bg.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 28, 36))), $bg)

    # Cyan stop bar and lead-in line.
    $cyan = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 200, 230))
    $g.FillRectangle($cyan, [float](92 * $s), [float](16 * $s), [float](72 * $s), [float]([Math]::Max(1, 10 * $s)))
    $g.FillRectangle($cyan, [float](124 * $s), [float](226 * $s), [float]([Math]::Max(1, 8 * $s)), [float](24 * $s))

    # White aircraft seen from above, nose at the stop bar.
    $pts = @(
        @(128, 32), @(141, 50), @(141, 104), @(228, 144), @(228, 162), @(141, 144), @(141, 192),
        @(172, 214), @(172, 226), @(128, 216), @(84, 226), @(84, 214), @(115, 192), @(115, 144),
        @(28, 162), @(28, 144), @(115, 104), @(115, 50)
    ) | ForEach-Object { New-Object System.Drawing.PointF ([float]($_[0] * $s)), ([float]($_[1] * $s)) }
    $g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.PointF[]]$pts)

    $g.Dispose()
    return $bmp
}

# ICO file with PNG-compressed images (supported by Windows Vista and later).
$sizes = 256, 64, 48, 32, 24, 16
$images = foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)  # header: reserved, type=icon, count
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))  # width (0 = 256)
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))  # height
    $bw.Write([byte]0); $bw.Write([byte]0)                       # palette, reserved
    $bw.Write([uint16]1); $bw.Write([uint16]32)                  # planes, bits per pixel
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
"Wrote $out ($((Get-Item $out).Length) bytes, sizes: $($sizes -join ', '))"
