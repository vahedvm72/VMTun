# Generates VMTun.ico (a multi-resolution, PNG-compressed icon) without any external tooling.
param([Parameter(Mandatory = $true)][string]$OutPath)

Add-Type -AssemblyName System.Drawing

function New-Frame {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$Size
    $radius = $s * 0.22

    # Rounded-square background with a blue gradient.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point([int]$s, [int]$s)),
        [System.Drawing.Color]::FromArgb(255, 64, 132, 255),
        [System.Drawing.Color]::FromArgb(255, 32, 196, 178))
    $g.FillPath($brush, $path)

    # Shield outline.
    $w = $s * 0.52
    $h = $s * 0.60
    $cx = $s / 2.0
    $top = $s * 0.20

    $shield = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shield.AddLine([single]($cx - $w / 2), [single]($top + $h * 0.06), [single]$cx, [single]$top)
    $shield.AddLine([single]$cx, [single]$top, [single]($cx + $w / 2), [single]($top + $h * 0.06))
    $shield.AddBezier(
        [single]($cx + $w / 2), [single]($top + $h * 0.06),
        [single]($cx + $w / 2), [single]($top + $h * 0.62),
        [single]($cx + $w * 0.30), [single]($top + $h * 0.92),
        [single]$cx, [single]($top + $h))
    $shield.AddBezier(
        [single]$cx, [single]($top + $h),
        [single]($cx - $w * 0.30), [single]($top + $h * 0.92),
        [single]($cx - $w / 2), [single]($top + $h * 0.62),
        [single]($cx - $w / 2), [single]($top + $h * 0.06))
    $shield.CloseFigure()

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(245, 255, 255, 255))
    $g.FillPath($white, $shield)

    # A horizontal arrow through the shield: traffic passing through the tunnel.
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 36, 92, 190))
    $barH = [Math]::Max(1.0, $s * 0.075)
    $barY = $top + $h * 0.44
    $g.FillRectangle($blue, [single]($cx - $w * 0.26), [single]$barY, [single]($w * 0.40), [single]$barH)
    $tip = New-Object 'System.Drawing.PointF[]' 3
    $tip[0] = New-Object System.Drawing.PointF([single]($cx + $w * 0.30), [single]($barY + $barH / 2 - $s * 0.085))
    $tip[1] = New-Object System.Drawing.PointF([single]($cx + $w * 0.30), [single]($barY + $barH / 2 + $s * 0.085))
    $tip[2] = New-Object System.Drawing.PointF([single]($cx + $w * 0.46), [single]($barY + $barH / 2))
    $g.FillPolygon($blue, $tip)

    $g.Dispose()
    $brush.Dispose(); $white.Dispose(); $blue.Dispose(); $path.Dispose(); $shield.Dispose()
    return $bmp
}

$sizes = @(256, 128, 64, 48, 32, 16)
$frames = @()
foreach ($size in $sizes) {
    $bmp = New-Frame -Size $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $size; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type: icon
$bw.Write([UInt16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $dim = $f.Size
    if ($dim -ge 256) { $dim = 0 }    # 0 means 256 in the ICO directory
    $bw.Write([Byte]$dim)             # width
    $bw.Write([Byte]$dim)             # height
    $bw.Write([Byte]0)                # palette size
    $bw.Write([Byte]0)                # reserved
    $bw.Write([UInt16]1)              # colour planes
    $bw.Write([UInt16]32)             # bits per pixel
    $bw.Write([UInt32]$f.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush()

[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host ("Icon written: {0} ({1} bytes)" -f $OutPath, (Get-Item $OutPath).Length)
