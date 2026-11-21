# make-icon.ps1 - draws the app icon (src\Core\app.ico) at every Windows icon size.
# Run once; the .ico is committed, so builds don't need this.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\Core\app.ico'))

Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function New-Frame([int]$s) {
    $bmp = New-Object Drawing.Bitmap($s, $s, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)

    # Rounded tile with a blue gradient
    $pad = [Math]::Max(0.5, $s * 0.04); $r = $s * 0.22
    $rect = New-Object Drawing.RectangleF($pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad))
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush($rect, [Drawing.Color]::FromArgb(255, 37, 99, 235), [Drawing.Color]::FromArgb(255, 14, 165, 233), 45.0)
    $g.FillPath($brush, $path)

    # Circular "refresh" arrow (fresh install) around a down arrow (restore)
    $w = [Math]::Max(1.2, $s * 0.085)
    $pen = New-Object Drawing.Pen([Drawing.Color]::White, $w)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $m = $s * 0.24
    $arc = New-Object Drawing.RectangleF($m, $m, ($s - 2 * $m), ($s - 2 * $m))
    $g.DrawArc($pen, $arc, -60, 300)
    # arrow head at the end of the arc (angle -60 deg)
    $cx = $s / 2; $cy = $s / 2; $rad = ($s - 2 * $m) / 2
    $a = -60 * [Math]::PI / 180
    $tip = New-Object Drawing.PointF(($cx + $rad * [Math]::Cos($a)), ($cy + $rad * [Math]::Sin($a)))
    $h = $s * 0.13
    $p1 = New-Object Drawing.PointF(($tip.X - $h), ($tip.Y - $h * 0.15))
    $p2 = New-Object Drawing.PointF(($tip.X + $h * 0.15), ($tip.Y + $h))
    $g.FillPolygon([Drawing.Brushes]::White, [Drawing.PointF[]]@($tip, $p1, $p2))
    if ($s -ge 24) {
        # down arrow in the middle
        $pen2 = New-Object Drawing.Pen([Drawing.Color]::White, [Math]::Max(1.2, $s * 0.07))
        $pen2.StartCap = 'Round'; $pen2.EndCap = 'Round'
        $g.DrawLine($pen2, $cx, ($cy - $s * 0.12), $cx, ($cy + $s * 0.10))
        $g.DrawLine($pen2, ($cx - $s * 0.09), ($cy + $s * 0.02), $cx, ($cy + $s * 0.11))
        $g.DrawLine($pen2, ($cx + $s * 0.09), ($cy + $s * 0.02), $cx, ($cy + $s * 0.11))
    }
    $g.Dispose()
    $bmp
}

$pngs = foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$fs = [IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out)).Path + '\' + (Split-Path $Out -Leaf))
$bw = New-Object IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()
Write-Host "Wrote $Out"
