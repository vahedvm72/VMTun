# Generates VMTun.ico without any external tooling.
#
# The mark is the same one the window draws: a rounded orange tile with an off-white bolt, so
# the taskbar, the title bar, the tray, the notification banner and the header all show one logo
# in one palette.
#
# The file layout matters as much as the drawing. Windows and System.Drawing both look an icon
# up by size, and an .ico whose small frames are PNG-compressed cannot be read that way: the
# sized lookup fails, the shell falls back to whatever icon it had cached for the exe, and the
# taskbar and notification banners keep showing the logo from a previous version. So every frame
# below 256 is written as the classic 32-bit DIB (a BITMAPINFOHEADER, the pixels bottom-up, and
# an AND mask), which every icon path on Windows reads, and only the 256 frame uses PNG.
param([Parameter(Mandatory = $true)][string]$OutPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ------------------------------------------------------------------ drawing

function New-Frame {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$Size
    # The tile keeps its corner radius, but at sixteen pixels a 30% radius eats the silhouette,
    # so the small frames are drawn a little squarer.
    $radius = if ($Size -le 24) { $s * 0.26 } else { $s * 0.30 }

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

# ------------------------------------------------------------------ encoding

function Get-DibFrame {
    param([int]$Size)

    $bmp = New-Frame -Size $Size
    $rect = New-Object System.Drawing.Rectangle 0, 0, $Size, $Size
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $Size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $bmp.UnlockBits($data)
    $bmp.Dispose()

    # The AND mask is kept, all zeroes, because a 32-bit frame without one is read as truncated
    # by some of the older icon paths.
    $maskStride = [int]([math]::Floor(($Size + 31) / 32) * 4)
    $mask = New-Object byte[] ($maskStride * $Size)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt32]40)                 # BITMAPINFOHEADER
    $bw.Write([Int32]$Size)               # width
    $bw.Write([Int32]($Size * 2))         # height: colour rows + mask rows
    $bw.Write([UInt16]1)                  # planes
    $bw.Write([UInt16]32)                 # bits per pixel
    $bw.Write([UInt32]0)                  # BI_RGB
    $bw.Write([UInt32]($Size * $Size * 4))
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    for ($y = $Size - 1; $y -ge 0; $y--) { $bw.Write($raw, $y * $stride, $Size * 4) }
    $bw.Write($mask, 0, $mask.Length)

    $bw.Flush()
    $bytes = [byte[]]$ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    # The comma stops PowerShell from unrolling the array on the way out, which would hand the
    # caller a list of numbers instead of a frame.
    return , $bytes
}

function Get-PngFrame {
    param([int]$Size)

    $bmp = New-Frame -Size $Size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = [byte[]]$ms.ToArray()
    $ms.Dispose(); $bmp.Dispose()
    return , $bytes
}

# ------------------------------------------------------------------ write

# 256 is stored as PNG; everything below it as DIB. 20, 40 and 96 are the odd sizes Windows asks
# for at 125%, 250% and in the notification banner on some builds.
$sizes = @(256, 128, 96, 64, 48, 40, 32, 24, 20, 16)
$frames = @()
foreach ($size in $sizes) {
    if ($size -ge 256) { $bytes = Get-PngFrame -Size $size } else { $bytes = Get-DibFrame -Size $size }
    $frames += , @{ Size = $size; Bytes = $bytes }
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type: icon
$bw.Write([UInt16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $dim = $f.Size
    if ($dim -ge 256) { $dim = 0 }   # 0 means 256 in the ICO directory
    $bw.Write([Byte]$dim)            # width
    $bw.Write([Byte]$dim)            # height
    $bw.Write([Byte]0)               # palette size
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # colour planes
    $bw.Write([UInt16]32)            # bits per pixel
    $bw.Write([UInt32]$f.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) {
    $frame = [byte[]]$f.Bytes
    $bw.Write($frame, 0, $frame.Length)
}
$bw.Flush()

[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()

# ------------------------------------------------------------------ self-check
# Read every frame back the way Windows and the app do — by size — and refuse to ship a file
# that cannot answer that lookup. This is the check that was missing when the small frames were
# PNG and the shell quietly fell back to a cached icon.
$failures = @()
foreach ($size in @(256, 96, 64, 48, 40, 32, 24, 20, 16)) {
    try {
        $ico = New-Object System.Drawing.Icon($OutPath, $size, $size)
        $bmp = $ico.ToBitmap()
        if ($bmp.Width -lt 1 -or $bmp.Height -lt 1) { $failures += "$size (empty)" }
        $bmp.Dispose(); $ico.Dispose()
    }
    catch {
        $failures += ("{0} ({1})" -f $size, $_.Exception.Message)
    }
}
if ($failures.Count -gt 0) {
    throw ("Generated icon cannot be read at these sizes: " + ($failures -join '; '))
}

Write-Host ("Icon written: {0} ({1} bytes, {2} frames, verified at 16-256 px)" -f `
    $OutPath, (Get-Item $OutPath).Length, $frames.Count)
