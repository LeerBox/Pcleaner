<#
.SYNOPSIS
    Builds src\PCleaner.App\Assets\PCleaner.ico from the vector brand mark in src\PCleaner.App\Assets\Logo.xaml.

.DESCRIPTION
    The mark is defined once, in XAML, on a 256-unit grid (see Logo.xaml). This script renders it with WPF at every
    size Windows asks for (16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 px - the 100/125/150/200 % scales of the
    16, 24 and 32 px slots plus the Explorer and jump-list sizes), each one supersampled 4x and downsampled with a
    Fant filter, so edges are smooth without being blurry.

    Small sizes get their own variant, the way a type designer hints a font: at 16-24 px the shield grows a little,
    the sparkle opens up so its light still shows through, and the shadow, rim light and second sparkle - which would
    only turn into grey pixels - are dropped. From 32 px the shadow returns; from 48 px the mark is complete.

    Frames up to 128 px are stored as 32-bit BMPs with an alpha channel (what every Windows component reads), the
    256 px frame is PNG-compressed, as Windows expects.

.PARAMETER Out
    Path of the .ico to write. Defaults to the application's Assets folder.

.PARAMETER Png
    Optional path for a 256 px PNG of the mark (used for docs).

.PARAMETER Preview
    Optional path for a contact sheet showing every size on light and dark backgrounds.
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\..\src\PCleaner.App\Assets\PCleaner.ico'),
    [string]$Png = '',
    [string]$Preview = ''
)

$ErrorActionPreference = 'Stop'

# WPF needs a single-threaded apartment; re-launch when the host is MTA.
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
    $forward = @('-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Out', $Out)
    if ($Png) { $forward += @('-Png', $Png) }
    if ($Preview) { $forward += @('-Preview', $Preview) }
    & powershell.exe @forward
    exit $LASTEXITCODE
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

$logoPath = Join-Path $PSScriptRoot '..\..\src\PCleaner.App\Assets\Logo.xaml'
$stream = [IO.File]::OpenRead($logoPath)
try { $logo = [Windows.Markup.XamlReader]::Load($stream) } finally { $stream.Dispose() }

$grid = 256.0
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)

# Size classes - the "hinting" described above. Geometry scales are about the shield's optical centre (128,130).
#   Shield   - scale of the shield about its centre
#   Sparkle  - radius of the light cut through it, or 0 to use the master sparkle unchanged
#   Waist    - 0 = the master's concave star, 0.5 = a straight-sided diamond (what survives at 16-24 px)
function Get-Variant([int]$px) {
    if ($px -le 24) { return @{ Shield = 1.2;  Sparkle = 38; Waist = 0.5;  Shadow = $false; Rim = $false; Second = $false; Snap = $true } }
    if ($px -le 40) { return @{ Shield = 1.1;  Sparkle = 50; Waist = 0.22; Shadow = $true;  Rim = $false; Second = $false; Snap = $true } }
    return @{ Shield = 1.0; Sparkle = 0; Waist = 0; Shadow = $true; Rim = $true; Second = $true; Snap = $false }
}

# Four-point sparkle centred on (cx,cy) with tips at radius r. Each side is a quadratic curve whose control point sits
# on the diagonal at (waist * side length) from the centre: 0 pulls the sides all the way in, 0.5 makes them straight.
function New-Sparkle([double]$cx, [double]$cy, [double]$r, [double]$waist) {
    $tips = @(@(0, -1), @(1, 0), @(0, 1), @(-1, 0))
    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendFormat([Globalization.CultureInfo]::InvariantCulture, 'M {0},{1}', $cx, $cy - $r)
    for ($i = 0; $i -lt 4; $i++) {
        $a = $tips[$i]; $b = $tips[($i + 1) % 4]
        $ax = $cx + $a[0] * $r; $ay = $cy + $a[1] * $r
        $bx = $cx + $b[0] * $r; $by = $cy + $b[1] * $r
        $mx = ($ax + $bx) / 2; $my = ($ay + $by) / 2     # midpoint of the straight side
        $qx = $cx + ($mx - $cx) * ($waist / 0.5); $qy = $cy + ($my - $cy) * ($waist / 0.5)
        [void]$sb.AppendFormat([Globalization.CultureInfo]::InvariantCulture, ' Q {0},{1} {2},{3}', $qx, $qy, $bx, $by)
    }
    [void]$sb.Append(' Z')
    return [Windows.Media.Geometry]::Parse($sb.ToString())
}

function New-Geometry([Windows.Media.Geometry]$source, [double]$scale, [double]$about, [double]$aboutY, [double]$dx = 0, [double]$dy = 0) {
    $g = $source.Clone()
    $tg = New-Object Windows.Media.TransformGroup
    if ($about -ne 1.0) { $tg.Children.Add((New-Object Windows.Media.ScaleTransform $about, $about, 128, $aboutY)) }
    if ($dx -ne 0 -or $dy -ne 0) { $tg.Children.Add((New-Object Windows.Media.TranslateTransform $dx, $dy)) }
    $tg.Children.Add((New-Object Windows.Media.ScaleTransform $scale, $scale))
    $g.Transform = $tg
    return $g
}

function New-Path([Windows.Media.Geometry]$geometry, [Windows.Media.Brush]$fill) {
    $p = New-Object Windows.Shapes.Path
    $p.Data = $geometry
    $p.Fill = $fill
    $p.SnapsToDevicePixels = $false
    return $p
}

# Builds the mark for a final size of $px pixels, drawn $ss times larger (the caller downsamples).
function New-LogoVisual([int]$px, [int]$ss, [hashtable]$variant) {
    $final = $px / $grid            # 256-grid units -> final pixels
    $scale = $final * $ss           # 256-grid units -> render pixels
    $root = New-Object Windows.Controls.Canvas
    $root.Width = $px * $ss; $root.Height = $px * $ss
    [Windows.Media.RenderOptions]::SetEdgeMode($root, [Windows.Media.EdgeMode]::Unspecified)

    $tile = New-Geometry $logo['Logo.Tile'] $scale 1.0 128
    $root.Children.Add((New-Path $tile $logo['Logo.TileBrush'])) | Out-Null
    $root.Children.Add((New-Path $tile $logo['Logo.SheenBrush'])) | Out-Null
    $root.Children.Add((New-Path $tile $logo['Logo.DepthBrush'])) | Out-Null

    if ($variant.Rim) {
        $rim = New-Object Windows.Shapes.Path
        $rim.Data = New-Geometry $logo['Logo.TileRim'] $scale 1.0 128
        $rim.Stroke = $logo['Logo.RimBrush']
        $rim.StrokeThickness = 3 * $scale
        $root.Children.Add($rim) | Out-Null
    }

    # Pixel snapping for the hinted sizes: pick the shield scale that makes its width a whole number of final pixels,
    # then shift the whole mark so the top and the flanks sit exactly on pixel boundaries - crisp edges, no grey seam.
    $shieldScale = $variant.Shield; $dx = 0; $dy = 0
    if ($variant.Snap) {
        $shieldScale = [Math]::Round(128 * $variant.Shield * $final) / (128 * $final)
        $left = (128 - 64 * $shieldScale) * $final
        $top = (130 - 86 * $shieldScale) * $final
        $dx = ([Math]::Round($left) - $left) / $final
        $dy = ([Math]::Round($top) - $top) / $final
    }

    $mark = New-Object Windows.Media.GeometryGroup
    $mark.FillRule = [Windows.Media.FillRule]::EvenOdd
    $parts = $logo['Logo.Mark'].Children   # 0 shield, 1 sparkle, 2 small sparkle
    $mark.Children.Add((New-Geometry $parts[0] $scale $shieldScale 130 $dx $dy))
    $sparkle = if ($variant.Sparkle -gt 0) { New-Sparkle 128 120 $variant.Sparkle $variant.Waist } else { $parts[1] }
    $mark.Children.Add((New-Geometry $sparkle $scale 1.0 120 $dx $dy))
    if ($variant.Second) { $mark.Children.Add((New-Geometry $parts[2] $scale 1.0 80 $dx $dy)) }

    $shield = New-Path $mark $logo['Logo.ShieldBrush']
    if ($variant.Shadow) {
        $shadow = New-Object Windows.Media.Effects.DropShadowEffect
        $shadow.Color = [Windows.Media.Color]::FromRgb(0x0B, 0x2F, 0x66)
        $shadow.Opacity = 0.38
        $shadow.BlurRadius = 20 * $scale
        $shadow.ShadowDepth = 6 * $scale
        $shadow.Direction = 270
        $shadow.RenderingBias = [Windows.Media.Effects.RenderingBias]::Quality
        $shield.Effect = $shadow
    }
    $root.Children.Add($shield) | Out-Null
    return $root
}

function Render-Visual([Windows.UIElement]$visual, [int]$px) {
    $visual.Measure((New-Object Windows.Size $px, $px))
    $visual.Arrange((New-Object Windows.Rect 0, 0, $px, $px))
    $visual.UpdateLayout()
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap $px, $px, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    return $rtb
}

# 4x supersample, then Fant downsample: smooth, symmetric edges at every size.
function Render-Frame([int]$px) {
    $variant = Get-Variant $px
    $big = Render-Visual (New-LogoVisual $px 4 $variant) ($px * 4)
    $img = New-Object Windows.Controls.Image
    $img.Source = $big
    $img.Width = $px; $img.Height = $px
    $img.Stretch = [Windows.Media.Stretch]::Fill
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($img, [Windows.Media.BitmapScalingMode]::HighQuality)
    $small = Render-Visual $img $px
    $straight = New-Object Windows.Media.Imaging.FormatConvertedBitmap $small, ([Windows.Media.PixelFormats]::Bgra32), $null, 0
    $straight.Freeze()
    return $straight
}

function Get-Pixels([Windows.Media.Imaging.BitmapSource]$bmp) {
    $stride = $bmp.PixelWidth * 4
    $bytes = New-Object byte[] ($stride * $bmp.PixelHeight)
    $bmp.CopyPixels($bytes, $stride, 0)
    return $bytes
}

function ConvertTo-DibEntry([Windows.Media.Imaging.BitmapSource]$bmp) {
    # BITMAPINFOHEADER (height doubled for the mask) + bottom-up 32bpp BGRA + all-zero 1bpp AND mask (alpha rules).
    $w = $bmp.PixelWidth; $h = $bmp.PixelHeight
    $pixels = Get-Pixels $bmp
    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter $ms
    $bw.Write([UInt32]40); $bw.Write([Int32]$w); $bw.Write([Int32]($h * 2)); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]0); $bw.Write([UInt32]($w * $h * 4)); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $w * 4, $w * 4) }
    $maskRowBytes = [int]([Math]::Ceiling($w / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRowBytes * $h)))
    $bw.Flush()
    return ,$ms.ToArray()
}

function ConvertTo-Png([Windows.Media.Imaging.BitmapSource]$bmp) {
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$frames = @{}
foreach ($px in $sizes) { $frames[$px] = Render-Frame $px }

$entries = @()
foreach ($px in $sizes) {
    if ($px -ge 256) { $entries += ,([byte[]](ConvertTo-Png $frames[$px])) } else { $entries += ,([byte[]](ConvertTo-DibEntry $frames[$px])) }
}

$icoStream = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $icoStream
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $px = $sizes[$i]
    $dim = if ($px -ge 256) { 0 } else { $px }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$entries[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $entries[$i].Length
}
foreach ($e in $entries) { $bw.Write([byte[]]$e) }
$bw.Flush()

$Out = [IO.Path]::GetFullPath($Out)
[IO.File]::WriteAllBytes($Out, $icoStream.ToArray())
Write-Host ("Icon written: {0} ({1:N0} bytes, {2} frames: {3})" -f $Out, $icoStream.Length, $sizes.Count, ($sizes -join ', '))

if ($Png) {
    $Png = [IO.Path]::GetFullPath($Png)
    [IO.File]::WriteAllBytes($Png, (ConvertTo-Png $frames[256]))
    Write-Host "PNG written:  $Png"
}

if ($Preview) {
    # Contact sheet: every frame at 1:1 on a light and a dark strip, plus the 256 px frame on both.
    $pad = 24
    $stripW = ($sizes | Where-Object { $_ -lt 256 } | Measure-Object -Sum).Sum + $pad * ($sizes.Count) + 40
    $sheetW = [Math]::Max($stripW, 256 * 2 + $pad * 3)
    $stripH = 128 + $pad * 2
    $sheetH = $stripH * 2 + 256 + $pad * 2
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $light = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb(0xF3, 0xF3, 0xF3))
    $dark = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb(0x20, 0x20, 0x20))
    $dc.DrawRectangle($light, $null, (New-Object Windows.Rect 0, 0, $sheetW, $stripH))
    $dc.DrawRectangle($dark, $null, (New-Object Windows.Rect 0, $stripH, $sheetW, $stripH))
    $dc.DrawRectangle($light, $null, (New-Object Windows.Rect 0, ($stripH * 2), ($sheetW / 2), (256 + $pad * 2)))
    $dc.DrawRectangle($dark, $null, (New-Object Windows.Rect ($sheetW / 2), ($stripH * 2), ($sheetW / 2), (256 + $pad * 2)))
    foreach ($row in 0, 1) {
        $x = $pad
        foreach ($px in ($sizes | Where-Object { $_ -lt 256 })) {
            $y = $row * $stripH + $stripH - $pad - $px
            $dc.DrawImage($frames[$px], (New-Object Windows.Rect $x, $y, $px, $px))
            $x += $px + $pad
        }
    }
    $dc.DrawImage($frames[256], (New-Object Windows.Rect (($sheetW / 2 - 256) / 2), ($stripH * 2 + $pad), 256, 256))
    $dc.DrawImage($frames[256], (New-Object Windows.Rect ($sheetW / 2 + ($sheetW / 2 - 256) / 2), ($stripH * 2 + $pad), 256, 256))
    $dc.Close()
    $sheet = New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]$sheetW), ([int]$sheetH), 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $sheet.Render($dv)
    $Preview = [IO.Path]::GetFullPath($Preview)
    [IO.File]::WriteAllBytes($Preview, (ConvertTo-Png $sheet))
    Write-Host "Preview written: $Preview"
}