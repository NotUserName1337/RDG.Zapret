# Generates the RDG.Zapret app icon (rounded gradient square + lightning bolt).
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\RDG.Zapret\Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null

function New-BrandBitmap([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    [single]$pad = [Math]::Max(1.0, $size * 0.035)
    $rect = [System.Drawing.RectangleF]::new($pad, $pad, [single]($size - 2 * $pad), [single]($size - 2 * $pad))
    [single]$radius = $size * 0.235

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    [single]$d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $from = [System.Drawing.ColorTranslator]::FromHtml('#4338CA')
    $to = [System.Drawing.ColorTranslator]::FromHtml('#06B6D4')
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $from, $to, [single]38.0)
    $g.FillPath($brush, $path)

    # soft light from the top-left
    $glow = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect,
        [System.Drawing.Color]::FromArgb(75, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
        [single]125.0)
    $g.FillPath($glow, $path)

    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(55, 255, 255, 255), [single][Math]::Max(1.0, $size * 0.008))
    $g.DrawPath($pen, $path)

    # shield
    $cx = $size * 0.5
    $w = $size * 0.285
    $top = $size * 0.145
    $shoulder = $size * 0.26
    $mid = $size * 0.55
    $tip = $size * 0.86

    $shield = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $shield.AddLine([single]$cx, [single]$top, [single]($cx + $w), [single]$shoulder)
    $shield.AddLine([single]($cx + $w), [single]$shoulder, [single]($cx + $w * 0.90), [single]$mid)
    $shield.AddBezier(
        [single]($cx + $w * 0.90), [single]$mid,
        [single]($cx + $w * 0.78), [single]($size * 0.72),
        [single]($cx + $w * 0.40), [single]($size * 0.83),
        [single]$cx, [single]$tip)
    $shield.AddBezier(
        [single]$cx, [single]$tip,
        [single]($cx - $w * 0.40), [single]($size * 0.83),
        [single]($cx - $w * 0.78), [single]($size * 0.72),
        [single]($cx - $w * 0.90), [single]$mid)
    $shield.AddLine([single]($cx - $w * 0.90), [single]$mid, [single]($cx - $w), [single]$shoulder)
    $shield.CloseFigure()

    $shieldFill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(38, 255, 255, 255))
    $g.FillPath($shieldFill, $shield)

    $shieldPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(235, 255, 255, 255), [single][Math]::Max(1.5, $size * 0.023))
    $shieldPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($shieldPen, $shield)

    # lightning bolt inside the shield
    $points = @(
        [System.Drawing.PointF]::new([single]($size * 0.558), [single]($size * 0.320)),
        [System.Drawing.PointF]::new([single]($size * 0.408), [single]($size * 0.558)),
        [System.Drawing.PointF]::new([single]($size * 0.488), [single]($size * 0.558)),
        [System.Drawing.PointF]::new([single]($size * 0.452), [single]($size * 0.715)),
        [System.Drawing.PointF]::new([single]($size * 0.598), [single]($size * 0.497)),
        [System.Drawing.PointF]::new([single]($size * 0.520), [single]($size * 0.497))
    )

    $bolt = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $bolt.AddPolygon($points)
    $bolt.CloseFigure()

    $shadowMatrix = [System.Drawing.Drawing2D.Matrix]::new()
    $shadowMatrix.Translate([single]($size * 0.010), [single]($size * 0.016))
    $shadow = $bolt.Clone()
    $shadow.Transform($shadowMatrix)
    $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(55, 15, 8, 40))
    $g.FillPath($shadowBrush, $shadow)

    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $g.FillPath($white, $bolt)

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($s in $sizes) {
    $images += ,(New-BrandBitmap $s)
}

# preview
$preview = New-BrandBitmap 512
$preview.Save((Join-Path $assets 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

$pngStreams = @()
foreach ($img in $images) {
    $ms = [System.IO.MemoryStream]::new()
    $img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngStreams += ,$ms.ToArray()
    $ms.Dispose()
    $img.Dispose()
}

$icoPath = Join-Path $assets 'app.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = [System.IO.BinaryWriter]::new($fs)

$bw.Write([UInt16]0)          # reserved
$bw.Write([UInt16]1)          # type: icon
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $data = $pngStreams[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)        # palette
    $bw.Write([byte]0)        # reserved
    $bw.Write([UInt16]1)      # planes
    $bw.Write([UInt16]32)     # bpp
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}

foreach ($data in $pngStreams) {
    $bw.Write($data)
}

$bw.Flush()
$bw.Dispose()
$fs.Dispose()

Write-Host "Icon written to $icoPath ($([Math]::Round((Get-Item $icoPath).Length / 1KB, 1)) KB)"
