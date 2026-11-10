param([string]$OutDir = "$PSScriptRoot\..\EeroDesktop\Assets")
# Generates the eero Desktop app icon (rounded tile + Wi-Fi arcs over a mesh dot) as a multi-size .ico and a 256px .png.
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile with a diagonal blue-to-teal gradient
    $pad = [Math]::Max(0.5, $s * 0.03); $r = $s * 0.22; $w = $s - 2 * $pad
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, 2*$r, 2*$r, 180, 90); $path.AddArc($pad+$w-2*$r, $pad, 2*$r, 2*$r, 270, 90)
    $path.AddArc($pad+$w-2*$r, $pad+$w-2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc($pad, $pad+$w-2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0, 103, 192)), ([System.Drawing.Color]::FromArgb(255, 0, 178, 169)), 45.0
    $g.FillPath($grad, $path)

    # Wi-Fi arcs centered on a dot near the bottom
    $cx = $s / 2; $cy = $s * 0.71
    $stroke = [Math]::Max(1.6, $s * 0.085)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $radii = if ($s -le 20) { @(0.22, 0.40) } else { @(0.17, 0.295, 0.42) }
    foreach ($k in $radii) {
        $rr = $s * $k
        $g.DrawArc($pen, [single]($cx - $rr), [single]($cy - $rr), [single](2*$rr), [single](2*$rr), 225, 90)
    }
    $dot = [Math]::Max(2.5, $s * 0.13)
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]($cx - $dot/2), [single]($cy - $dot/2), [single]$dot, [single]$dot)

    $pen.Dispose(); $grad.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = New-IconBitmap $s; $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $b.Save("$OutDir\AppIcon.png", [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose(); , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista)
$fs = [System.IO.File]::Create("$OutDir\AppIcon.ico"); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose(); $fs.Dispose()
"Wrote $OutDir\AppIcon.ico ($($sizes -join ', ') px) and AppIcon.png"
