# Generates VMTun.ico (a multi-resolution, PNG-compressed icon) without any external tooling.
#
# The mark is the same one the window draws: a rounded orange tile with a white bolt, so the
# taskbar, the title bar, the tray and the header all show one logo in one palette.
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
    $radius = $s * 0.30

    # Rounded square, lit from the top left: the same gradient as the header tile.
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
        [System.Drawing.Color]::FromArgb(255, 255, 138, 61),
        [System.Drawing.Color]::FromArgb(255, 184, 62, 0))
    $g.FillPath($brush, $path)

    # The bolt, in the same six points the window's vector icon uses. Built as a closed path
    # rather than a polygon: FillPolygon cannot be bound from PowerShell here, FillPath can.
    $bolt = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = @(
        (New-Object System.Drawing.PointF ([single](0.60 * $s)), ([single](0.13 * $s))),
        (New-Object System.Drawing.PointF ([single](0.33 * $s)), ([single](0.55 * $s))),
        (New-Object System.Drawing.PointF ([single](0.51 * $s)), ([single](0.55 * $s))),
        (New-Object System.Drawing.PointF ([single](0.44 * $s)), ([single](0.87 * $s))),
        (New-Object System.Drawing.PointF ([single](0.70 * $s)), ([single](0.45 * $s))),
        (New-Object System.Drawing.PointF ([single](0.52 * $s)), ([single](0.45 * $s)))
    )
    for ($i = 0; $i -lt $pts.Count; $i++) {
        $bolt.AddLine($pts[$i], $pts[($i + 1) % $pts.Count])
    }
    $bolt.CloseFigure()

    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 246, 238))
    $g.FillPath($ink, $bolt)

    $g.Dispose()
    $brush.Dispose(); $ink.Dispose(); $path.Dispose(); $bolt.Dispose()
    return $bmp
}

$sizes = @(256, 128, 64, 48, 32, 24, 16)
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
