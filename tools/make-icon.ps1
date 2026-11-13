# Generates windows/app.ico (gradient rounded square + play symbol) — same design as AppIcon in Ui.cs.
param([string]$Out = "$PSScriptRoot\..\windows\app.ico")
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $r = $size * 0.24
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc(0, 0, $r*2, $r*2, 180, 90)
    $p.AddArc($size-$r*2-1, 0, $r*2, $r*2, 270, 90)
    $p.AddArc($size-$r*2-1, $size-$r*2-1, $r*2, $r*2, 0, 90)
    $p.AddArc(0, $size-$r*2-1, $r*2, $r*2, 90, 90)
    $p.CloseFigure()
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0,0), (New-Object System.Drawing.Point $size,$size), ([System.Drawing.Color]::FromArgb(139,92,246)), ([System.Drawing.Color]::FromArgb(236,72,153))
    $g.FillPath($br, $p)
    $cx = $size*0.54; $cy = $size*0.5; $t = $size*0.22
    $pts = [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ($cx-$t*0.8),($cy-$t)), (New-Object System.Drawing.PointF ($cx-$t*0.8),($cy+$t)), (New-Object System.Drawing.PointF ($cx+$t),$cy))
    $g.FillPolygon([System.Drawing.Brushes]::White, $pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = $sizes | ForEach-Object { ,(New-IconPng $_) }
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $w.Write($d) }
$w.Close()
"Wrote $Out"
