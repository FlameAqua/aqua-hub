# Renders the Aqua Hub logo with WPF and writes a multi-resolution .ico (PNG frames) + a 256px .png.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\make-icon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\AquaHub\Assets'))

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-LogoPng([int]$size) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $s = [double]$size
    $grad = New-Object System.Windows.Media.LinearGradientBrush
    $grad.StartPoint = [System.Windows.Point]::new(0, 0)
    $grad.EndPoint = [System.Windows.Point]::new(1, 1)
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x2B, 0xE0, 0xD2), 0)))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x14, 0xA9, 0xE8), 0.55)))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x25, 0x5C, 0xF0), 1)))
    $inset = [Math]::Max(0.5, $s * 0.03)
    $side = $s - 2 * $inset
    $rect = [System.Windows.Rect]::new($inset, $inset, $side, $side)
    $r = $s * 0.24
    $dc.DrawRoundedRectangle($grad, $null, $rect, $r, $r)

    # Soft top highlight for depth
    $hl = New-Object System.Windows.Media.LinearGradientBrush
    $hl.StartPoint = [System.Windows.Point]::new(0.5, 0)
    $hl.EndPoint = [System.Windows.Point]::new(0.5, 0.6)
    $hl.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(70, 255, 255, 255), 0)))
    $hl.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(0, 255, 255, 255), 1)))
    $dc.DrawRoundedRectangle($hl, $null, $rect, $r, $r)

    $white = [System.Windows.Media.Brushes]::White
    $half = $s / 2
    $c = [System.Windows.Point]::new($half, $half)
    $ringR = $s * 0.265
    $penW = [Math]::Max(1.15, $s * 0.075)
    $pen = [System.Windows.Media.Pen]::new($white, $penW)
    $dc.DrawEllipse($null, $pen, $c, $ringR, $ringR)
    $dotR = $s * 0.105
    $dc.DrawEllipse($white, $null, $c, $dotR, $dotR)
    # Satellite node on the orbit (top-right), ringed with the background colour to "cut" the orbit
    $a = -[Math]::PI / 4
    $sx = $half + $ringR * [Math]::Cos($a)
    $sy = $half + $ringR * [Math]::Sin($a)
    $sat = [System.Windows.Point]::new($sx, $sy)
    $cut = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x18, 0x9C, 0xEA))
    $dc.DrawEllipse($cut, $null, $sat, $dotR, $dotR)
    $satR = $s * 0.068
    $dc.DrawEllipse($white, $null, $sat, $satR, $satR)
    $dc.Close()

    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$frames = @{}
foreach ($sz in $sizes) { $frames[$sz] = New-LogoPng $sz }

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($sz in $sizes) {
    $data = $frames[$sz]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([Byte]$dim); $w.Write([Byte]$dim); $w.Write([Byte]0); $w.Write([Byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($sz in $sizes) { $w.Write($frames[$sz]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'AquaHub.ico'), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'AquaHub.png'), $frames[256])
"Wrote $(Join-Path $OutDir 'AquaHub.ico') ($($ico.Length) bytes)"
