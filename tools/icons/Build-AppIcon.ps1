<#
.SYNOPSIS
    Builds BattTray/Assets/batttray-app.ico, the icon Explorer shows for the exe.

.DESCRIPTION
    The tray icon and the file icon have opposite problems, which is why they are two
    different files rather than one.

    In the tray, the icon sits on a taskbar whose colour the app already knows, so the right
    answer is a bare glyph in black or white and a swap when the theme changes. That is
    batttray-black.ico and batttray-white.ico, and TrayIcons picks between them.

    In Explorer there is no such swap. A downloaded file is drawn once, on a background the
    exe never learns, and the shipped icon was the black glyph on transparency — which very
    nearly disappears against dark mode's near-black list background. So the file icon
    carries its own background: a mid-blue tile that holds its own against white and against
    #202020, with the glyph knocked out of it in white.

    The glyph is not redrawn here. It is lifted from batttray-black.ico by using that file's
    own alpha channel as a stencil, so the mark on the exe is the same mark as in the tray,
    to the pixel, and stays that way if the artwork is ever revised. Only the colour and the
    tile behind it are this script's invention.

    Run it from the repository root after changing the source artwork:
        pwsh -File tools/icons/Build-AppIcon.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$source = Join-Path $repoRoot 'BattTray\Assets\batttray-black.ico'
$target = Join-Path $repoRoot 'BattTray\Assets\batttray-app.ico'

# The frame sizes the shipped icons carry. Explorer, the taskbar, Alt+Tab and the file
# properties dialog each ask for a different one, and a missing size is scaled from the
# nearest — which is what makes a 16px icon look smeared.
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

# Mid-blue: light enough to read on dark mode's near-black, dark enough to read on white.
# Deliberately not green — green on a battery reads as "charged", and this icon says which
# app the file is, not what any device's level is.
$tile = [System.Drawing.Color]::FromArgb(255, 45, 108, 223)

# How much of the tile the glyph spans, by frame size. The source mark is nearly full-bleed
# horizontally, so at a comfortable size it needs real inset to sit on a tile without
# crowding the corners. Small frames cannot afford that: the mark is a battery outline with
# three bars inside it, and at 16 px an inset of a quarter leaves the bars sharing pixels.
# The margin is spent where there are pixels to spare and reclaimed where there are not,
# which costs a little tidiness at 16 px and buys the difference between three bars and a
# smudge.
$glyphScaleFor = {
    param([int]$size)
    if ($size -le 20) { 0.92 }
    elseif ($size -le 32) { 0.84 }
    else { 0.76 }
}

# Corner radius as a fraction of the tile, matching the squircle-ish radius Windows 11 uses
# for app tiles closely enough not to look foreign beside them.
$cornerFraction = 0.22

<#
    The largest frame of an .ico, as a Bitmap. Everything is rendered from this one rather
    than from each matching frame, because the source's small frames are already
    hinted for a bare glyph and rescaling them onto a tile reintroduces the smearing the
    separate frames existed to avoid.
#>
function Get-LargestFrame([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $count = [BitConverter]::ToUInt16($bytes, 4)

    $best = -1
    $bestSize = -1
    for ($i = 0; $i -lt $count; $i++) {
        $entry = 6 + ($i * 16)
        $width = $bytes[$entry]
        if ($width -eq 0) { $width = 256 }   # 0 means 256; the field is one byte.
        if ($width -gt $bestSize) { $bestSize = $width; $best = $entry }
    }

    $length = [BitConverter]::ToUInt32($bytes, $best + 8)
    $offset = [BitConverter]::ToUInt32($bytes, $best + 12)
    $stream = New-Object System.IO.MemoryStream (, $bytes[$offset..($offset + $length - 1)])
    return [System.Drawing.Image]::FromStream($stream)
}

<# A rounded rectangle covering the whole canvas. #>
function New-TilePath([int]$size, [int]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

<#
    Recolours every pixel to white while leaving its alpha alone, turning the dark glyph
    into a stencil. The translation row supplies the colour and the RGB rows contribute
    nothing, so the source's own colour is discarded rather than lightened.
#>
function New-WhiteAttributes {
    $matrix = New-Object System.Drawing.Imaging.ColorMatrix
    $matrix.Matrix00 = 0; $matrix.Matrix11 = 0; $matrix.Matrix22 = 0
    $matrix.Matrix33 = 1                       # alpha passes through untouched
    $matrix.Matrix40 = 1; $matrix.Matrix41 = 1; $matrix.Matrix42 = 1
    $attributes = New-Object System.Drawing.Imaging.ImageAttributes
    $attributes.SetColorMatrix($matrix)
    return $attributes
}

function New-Frame($glyph, [int]$size, $attributes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        # At 16 and 20 px a rounded corner costs more in mush than it buys in shape, and the
        # tile is only a few pixels of margin anyway, so square it off.
        $radius = [Math]::Max(1, [int][Math]::Round($size * $cornerFraction))
        if ($size -le 20) {
            $g.FillRectangle((New-Object System.Drawing.SolidBrush $tile), 0, 0, $size, $size)
        }
        else {
            $path = New-TilePath $size $radius
            $g.FillPath((New-Object System.Drawing.SolidBrush $tile), $path)
            $path.Dispose()
        }

        # Centred, preserving the source's aspect ratio rather than stretching to the box.
        $span = $size * (& $glyphScaleFor $size)
        $scale = [Math]::Min($span / $glyph.Width, $span / $glyph.Height)
        $w = $glyph.Width * $scale
        $h = $glyph.Height * $scale
        $rect = New-Object System.Drawing.Rectangle (
            [int][Math]::Round(($size - $w) / 2),
            [int][Math]::Round(($size - $h) / 2),
            [int][Math]::Round($w),
            [int][Math]::Round($h))

        $g.DrawImage($glyph, $rect, 0, 0, $glyph.Width, $glyph.Height,
            [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    }
    finally {
        $g.Dispose()
    }

    return $bitmap
}

<#
    Assembles an .ico from PNG-encoded frames. PNG in every frame, including the small ones,
    matching what the existing icons already do — Windows has read PNG frames at any size
    since Vista, and the alternative (a BMP with a separate AND mask) exists only for XP.
#>
function Write-Icon([string]$path, $frames) {
    $stream = [System.IO.File]::Create($path)
    $writer = New-Object System.IO.BinaryWriter $stream
    try {
        $writer.Write([UInt16]0)                 # reserved
        $writer.Write([UInt16]1)                 # type: icon
        $writer.Write([UInt16]$frames.Count)

        # Every frame's payload follows the whole directory, so the first offset is past it.
        $offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            $size = $frame.Size
            $writer.Write([Byte]$(if ($size -ge 256) { 0 } else { $size }))   # 0 means 256
            $writer.Write([Byte]$(if ($size -ge 256) { 0 } else { $size }))
            $writer.Write([Byte]0)               # palette entries: none, it is 32bpp
            $writer.Write([Byte]0)               # reserved
            $writer.Write([UInt16]1)             # colour planes
            $writer.Write([UInt16]32)            # bits per pixel
            $writer.Write([UInt32]$frame.Bytes.Length)
            $writer.Write([UInt32]$offset)
            $offset += $frame.Bytes.Length
        }

        foreach ($frame in $frames) { $writer.Write($frame.Bytes) }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$glyph = Get-LargestFrame $source
$attributes = New-WhiteAttributes
$frames = @()

try {
    foreach ($size in $sizes) {
        $bitmap = New-Frame $glyph $size $attributes
        try {
            $memory = New-Object System.IO.MemoryStream
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += [pscustomobject]@{ Size = $size; Bytes = $memory.ToArray() }
            $memory.Dispose()
        }
        finally {
            $bitmap.Dispose()
        }
    }

    Write-Icon $target $frames
}
finally {
    $attributes.Dispose()
    $glyph.Dispose()
}

Write-Output ("Wrote {0} ({1} frames, {2:N0} bytes)" -f $target, $frames.Count, (Get-Item $target).Length)
