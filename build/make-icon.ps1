<#
.SYNOPSIS
    Generates the application icon (multi-resolution .ico with PNG frames) and a 256px PNG.
.EXAMPLE
    pwsh build/make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\src\ValheimServerManager.App\Assets'
}
Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconFrame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0

    # Night-sky tile
    $tile = New-RoundedRectPath (8 * $s) (8 * $s) (240 * $s) (240 * $s) (52 * $s)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, 0),
        (New-Object System.Drawing.PointF $size, $size),
        [System.Drawing.Color]::FromArgb(255, 30, 58, 74),
        [System.Drawing.Color]::FromArgb(255, 11, 22, 32))
    $g.FillPath($bg, $tile)

    # Shield
    $shield = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shield.AddBezier((128 * $s), (46 * $s), (150 * $s), (58 * $s), (178 * $s), (62 * $s), (196 * $s), (62 * $s))
    $shield.AddBezier((196 * $s), (62 * $s), (198 * $s), (140 * $s), (176 * $s), (186 * $s), (128 * $s), (212 * $s))
    $shield.AddBezier((128 * $s), (212 * $s), (80 * $s), (186 * $s), (58 * $s), (140 * $s), (60 * $s), (62 * $s))
    $shield.AddBezier((60 * $s), (62 * $s), (78 * $s), (62 * $s), (106 * $s), (58 * $s), (128 * $s), (46 * $s))
    $shield.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, (46 * $s)),
        (New-Object System.Drawing.PointF 0, (212 * $s)),
        [System.Drawing.Color]::FromArgb(255, 245, 184, 78),
        [System.Drawing.Color]::FromArgb(255, 196, 118, 30))
    $g.FillPath($fill, $shield)

    # A bold "V" for Valheim
    $penWidth = [Math]::Max(1.8, 22 * $s)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 18, 32, 44)), $penWidth
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $pen.LineJoin = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (94 * $s), (94 * $s)),
        (New-Object System.Drawing.PointF (128 * $s), (170 * $s)),
        (New-Object System.Drawing.PointF (162 * $s), (94 * $s))))

    $g.Dispose()
    return $bmp
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($size in $sizes) {
    $bmp = New-IconFrame $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $OutputDirectory 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
}

$icoPath = Join-Path $OutputDirectory 'AppIcon.ico'
$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$f.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Dispose()
Write-Host "Ícone gerado em $icoPath"
