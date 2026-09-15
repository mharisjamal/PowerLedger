#Requires -Version 7

<#
.SYNOPSIS
Draws PowerLedger's logo once and writes every file made from it into this folder.

.DESCRIPTION
The mark is a bold letter P on an amber tile. Its bowl is a meter's dial, with a needle in it; its stem is a ledger's
margin rule, with two rows written beside it. It is drawn on a 48-unit grid whose edges fall on multiples of 3, so it
stays sharp at 16 px (3 units a pixel) and 24 px (2), the sizes the taskbar and the tray use.

Writes:
  PowerLedger.ico       every size Windows asks for, 16 to 256 px (32-bit bitmaps, and PNG for 256)
  mark.svg              the tile alone
  logo.svg              the tile and the name, for light backgrounds; logo-dark.svg for dark ones (the name is outlines,
                        so no font is needed to show it)
  mark-256.png, mark-512.png
  wizard-small-*.png    the installer's top-right image, one per display scale
  wizard-large-*.png    the installer's Welcome and Finished page image, one per display scale

Needs PowerShell 7 on Windows: it draws with WPF. Run it again after changing the drawing; the outputs are committed.

.EXAMPLE
pwsh assets\brand\make-brand.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
[System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture   # path data uses '.'
$here = $PSScriptRoot

# ---- The palette (the App's dark theme) ----
$Amber = '#F2B233'; $AmberTop = '#F6C04F'; $AmberBottom = '#EAA326'
$Graphite = '#1B1D1A'; $Cream = '#FFF6DC'; $Ink = '#ECE9DF'; $Ruling = '#262A26'

# ---- The mark, on its 48-unit grid ----
$TileRadius = 10.5
# The P is one shape, the stem and the dial's outer edge, with the dial's face cut out (even-odd), so no seam shows where
# a stem and a bowl would meet. The dial's outer radius is 12, its face's 6.
$Letter = 'M 12,9 H 27 A 12,12 0 0 1 27,33 H 18 V 45 H 12 Z M 18,15 H 27 A 6,6 0 0 1 27,27 H 18 Z'
$NeedleFrom = '22.5,25.5'; $NeedleTo = '30,18'; $NeedleWidth = 3; $PivotRadius = 2.25
$Rows = '21,36,18,3', '21,42,12,3'

function Brush([string]$Hex) { $b = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString($Hex)); $b.Freeze(); $b }
function Point([string]$Pair) { $x, $y = $Pair -split ','; [System.Windows.Point]::new([double]$x, [double]$y) }
function Rect([string]$Quad) { $x, $y, $w, $h = $Quad -split ','; [System.Windows.Rect]::new([double]$x, [double]$y, [double]$w, [double]$h) }

# Draws the mark in 48-unit space. $Depth gives the tile a gentle top-to-bottom shade, for sizes large enough to show it.
function Add-Mark($Dc, [switch]$Depth) {
    $tileBrush = if ($Depth) {
        $g = [System.Windows.Media.LinearGradientBrush]::new(
            [System.Windows.Media.ColorConverter]::ConvertFromString($AmberTop), [System.Windows.Media.ColorConverter]::ConvertFromString($AmberBottom), 90)
        $g.Freeze(); $g
    }
    else { Brush $Amber }
    $Dc.DrawRoundedRectangle($tileBrush, $null, [System.Windows.Rect]::new(0, 0, 48, 48), $TileRadius, $TileRadius)
    $graphite = Brush $Graphite
    $Dc.DrawGeometry($graphite, $null, [System.Windows.Media.Geometry]::Parse("F0 $Letter"))   # F0: even-odd
    foreach ($row in $Rows) { $Dc.DrawRectangle($graphite, $null, (Rect $row)) }
    $pen = [System.Windows.Media.Pen]::new((Brush $Cream), $NeedleWidth); $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.Freeze()
    $Dc.DrawLine($pen, (Point $NeedleFrom), (Point $NeedleTo))
    $Dc.DrawEllipse((Brush $Cream), $null, (Point $NeedleFrom), $PivotRadius, $PivotRadius)
}

# The mark at $Size pixels, straight (not premultiplied) BGRA, as icons and PNGs want it.
function New-Mark([int]$Size) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($Size / 48, $Size / 48))
    Add-Mark $dc -Depth:($Size -ge 64)
    $dc.Pop(); $dc.Close()
    Get-Bitmap $visual $Size $Size
}

function Get-Bitmap($Visual, [int]$Width, [int]$Height) {
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($Width, $Height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($Visual)
    $straight = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new($bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $straight.Freeze()
    $straight
}

function Get-PngBytes($Bitmap) {
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $stream = [System.IO.MemoryStream]::new(); $encoder.Save($stream)
    , $stream.ToArray()   # the comma keeps PowerShell from unrolling the bytes
}

function Save-Png($Bitmap, [string]$Name) { [System.IO.File]::WriteAllBytes((Join-Path $here $Name), (Get-PngBytes $Bitmap)); $Name }

# A 32-bit icon image: BITMAPINFOHEADER, bottom-up BGRA rows, then an all-clear AND mask (the alpha does the masking).
function Get-IconBitmapBytes($Bitmap) {
    $size = $Bitmap.PixelWidth
    $pixels = [byte[]]::new($size * $size * 4)
    $Bitmap.CopyPixels($pixels, $size * 4, 0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $stream = [System.IO.MemoryStream]::new(); $w = [System.IO.BinaryWriter]::new($stream)
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($size * $size * 4 + $maskStride * $size)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($row = $size - 1; $row -ge 0; $row--) { $w.Write($pixels, $row * $size * 4, $size * 4) }
    $w.Write([byte[]]::new($maskStride * $size))
    $w.Flush()
    , $stream.ToArray()
}

function Save-Icon([int[]]$Sizes, [string]$Name) {
    $images = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $Sizes) {
        $bitmap = New-Mark $size
        $images.Add($(if ($size -ge 256) { Get-PngBytes $bitmap } else { Get-IconBitmapBytes $bitmap }))
    }
    $stream = [System.IO.MemoryStream]::new(); $w = [System.IO.BinaryWriter]::new($stream)
    $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$Sizes.Count)
    $offset = 6 + 16 * $Sizes.Count
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $side = if ($Sizes[$i] -ge 256) { 0 } else { $Sizes[$i] }   # 0 means 256
        $w.Write([byte]$side); $w.Write([byte]$side); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $w.Write($image) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $here $Name), $stream.ToArray())
    $Name
}

# ---- The name, as outlines ----
$Typeface = [System.Windows.Media.Typeface]::new([System.Windows.Media.FontFamily]::new('Bahnschrift'), 'Normal', [System.Windows.FontWeights]::SemiBold, 'Normal')

function Get-NameGeometry([double]$EmSize, [double]$X, [double]$Baseline) {
    $text = [System.Windows.Media.FormattedText]::new('PowerLedger', [System.Globalization.CultureInfo]::InvariantCulture, 'LeftToRight', $Typeface, $EmSize, (Brush $Ink), 1.0)
    $geometry = $text.BuildGeometry([System.Windows.Point]::new($X, $Baseline - $text.Baseline))
    [pscustomobject]@{ Geometry = $geometry; Width = $text.WidthIncludingTrailingWhitespace; Height = $text.Height }
}

function Get-SvgPath($Geometry) {
    $flat = $Geometry.GetFlattenedPathGeometry(0.02, [System.Windows.Media.ToleranceType]::Absolute)
    ($flat.ToString() -replace '^F1\s*', '')
}

function Get-MarkSvg([double]$Scale, [double]$X = 0, [double]$Y = 0) {
    $rows = foreach ($row in $Rows) { $rx, $ry, $rw, $rh = $row -split ','; "<rect x=`"$rx`" y=`"$ry`" width=`"$rw`" height=`"$rh`" fill=`"$Graphite`"/>" }
    $fx, $fy = $NeedleFrom -split ','; $tx, $ty = $NeedleTo -split ','
    @"
<g transform="translate($X $Y) scale($Scale)">
  <rect width="48" height="48" rx="$TileRadius" fill="$Amber"/>
  <path d="$Letter" fill="$Graphite" fill-rule="evenodd"/>
  $($rows -join "`n  ")
  <line x1="$fx" y1="$fy" x2="$tx" y2="$ty" stroke="$Cream" stroke-width="$NeedleWidth" stroke-linecap="round"/>
  <circle cx="$fx" cy="$fy" r="$PivotRadius" fill="$Cream"/>
</g>
"@
}

function Save-Svg([string]$Name, [double]$Width, [double]$Height, [string]$Body) {
    $svg = "<svg xmlns=`"http://www.w3.org/2000/svg`" viewBox=`"0 0 $Width $Height`" width=`"$Width`" height=`"$Height`" role=`"img`" aria-label=`"PowerLedger`">`n$Body`n</svg>`n"
    [System.IO.File]::WriteAllText((Join-Path $here $Name), $svg, [System.Text.UTF8Encoding]::new($false))
    $Name
}

# ---- The installer's images ----
function New-WizardSmall([int]$Size) {
    $visual = [System.Windows.Media.DrawingVisual]::new(); $dc = $visual.RenderOpen()
    $inset = $Size * 0.06; $side = $Size - 2 * $inset
    $dc.PushTransform([System.Windows.Media.TranslateTransform]::new($inset, $inset))
    $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($side / 48, $side / 48))
    Add-Mark $dc -Depth:($Size -ge 64)
    $dc.Pop(); $dc.Pop(); $dc.Close()
    Get-Bitmap $visual $Size $Size
}

# Graphite, ruled like a ledger page, with a meter's tick scale along the foot, the mark above and the name under it.
function New-WizardLarge([int]$Width, [int]$Height) {
    $u = $Width / 202.0
    $visual = [System.Windows.Media.DrawingVisual]::new(); $dc = $visual.RenderOpen()
    $dc.DrawRectangle((Brush $Graphite), $null, [System.Windows.Rect]::new(0, 0, $Width, $Height))
    $rule = [System.Windows.Media.Pen]::new((Brush $Ruling), [Math]::Max(1, $u)); $rule.Freeze()
    for ($y = 24 * $u; $y -lt $Height - 70 * $u; $y += 18 * $u) { $dc.DrawLine($rule, [System.Windows.Point]::new(0, $y), [System.Windows.Point]::new($Width, $y)) }
    $margin = [System.Windows.Media.Pen]::new((Brush '#3A2E1A'), [Math]::Max(1, $u)); $margin.Freeze()
    $dc.DrawLine($margin, [System.Windows.Point]::new(28 * $u, 0), [System.Windows.Point]::new(28 * $u, $Height - 70 * $u))
    $markSide = 96 * $u; $markX = ($Width - $markSide) / 2; $markY = 92 * $u
    $dc.PushTransform([System.Windows.Media.TranslateTransform]::new($markX, $markY))
    $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($markSide / 48, $markSide / 48))
    Add-Mark $dc -Depth
    $dc.Pop(); $dc.Pop()
    $name = Get-NameGeometry (26 * $u) 0 0
    $nameX = ($Width - $name.Width) / 2
    $placed = Get-NameGeometry (26 * $u) $nameX ($markY + $markSide + 44 * $u)
    $dc.DrawGeometry((Brush $Ink), $null, $placed.Geometry)
    $tick = [System.Windows.Media.Pen]::new((Brush '#4B514B'), [Math]::Max(1, 1.2 * $u)); $tick.Freeze()
    $amberTick = [System.Windows.Media.Pen]::new((Brush $Amber), [Math]::Max(1, 1.6 * $u)); $amberTick.Freeze()
    $base = $Height - 34 * $u
    for ($i = 0; $i -le 20; $i++) {
        $x = 21 * $u + $i * 8 * $u
        $long = ($i % 5 -eq 0)
        $dc.DrawLine($(if ($i -eq 13) { $amberTick } else { $tick }), [System.Windows.Point]::new($x, $base), [System.Windows.Point]::new($x, $base - $(if ($long -or $i -eq 13) { 12 } else { 6 }) * $u))
    }
    $dc.Close()
    Get-Bitmap $visual $Width $Height
}

# ---- Write everything ----
Save-Icon @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256) 'PowerLedger.ico'
Save-Png (New-Mark 256) 'mark-256.png'
Save-Png (New-Mark 512) 'mark-512.png'
Save-Svg 'mark.svg' 48 48 (Get-MarkSvg 1)

$name = Get-NameGeometry 30 60 34.5
foreach ($variant in @(@{ File = 'logo.svg'; Fill = $Graphite }, @{ File = 'logo-dark.svg'; Fill = $Ink })) {
    $body = (Get-MarkSvg 1 0 0) + "`n<path d=`"$(Get-SvgPath $name.Geometry)`" fill=`"$($variant.Fill)`"/>"
    Save-Svg $variant.File ([Math]::Ceiling(60 + $name.Width + 2)) 48 $body
}

foreach ($size in 58, 72, 87, 116, 159) { Save-Png (New-WizardSmall $size) "wizard-small-$size.png" }
foreach ($scale in 1.0, 1.25, 1.5, 2.0, 2.5) {
    $w = [int][Math]::Round(202 * $scale); $h = [int][Math]::Round(386 * $scale)
    Save-Png (New-WizardLarge $w $h) "wizard-large-$w.png"
}
