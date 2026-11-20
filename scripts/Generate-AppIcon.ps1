$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$projectRoot = Split-Path -Parent $PSScriptRoot
$iconPath = Join-Path $projectRoot 'Hero.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngFrames = [System.Collections.Generic.List[byte[]]]::new()

function New-Brush([string]$Start, [string]$End, [double]$Angle = 135) {
    $brush = [System.Windows.Media.LinearGradientBrush]::new(
        [System.Windows.Media.ColorConverter]::ConvertFromString($Start),
        [System.Windows.Media.ColorConverter]::ConvertFromString($End),
        $Angle)
    $brush.Freeze()
    return $brush
}

foreach ($size in $sizes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new($size / 256.0, $size / 256.0))

    $baseBrush = New-Brush '#101E2A' '#071018' 130
    $basePen = [System.Windows.Media.Pen]::new((New-Brush '#44E1C1' '#426FE8' 45), 5)
    $basePen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $drawing.DrawRoundedRectangle($baseBrush, $basePen, [System.Windows.Rect]::new(8, 8, 240, 240), 58, 58)

    $glowBrush = [System.Windows.Media.RadialGradientBrush]::new()
    $glowBrush.Center = [System.Windows.Point]::new(.25, .2)
    $glowBrush.GradientOrigin = [System.Windows.Point]::new(.25, .2)
    $glowBrush.RadiusX = .85
    $glowBrush.RadiusY = .85
    $glowBrush.MappingMode = [System.Windows.Media.BrushMappingMode]::RelativeToBoundingBox
    $glowBrush.GradientStops.Add([System.Windows.Media.GradientStop]::new(
        [System.Windows.Media.ColorConverter]::ConvertFromString('#5529C7C1'), 0))
    $glowBrush.GradientStops.Add([System.Windows.Media.GradientStop]::new(
        [System.Windows.Media.ColorConverter]::ConvertFromString('#0029C7C1'), 1))
    $glowBrush.Freeze()
    $drawing.DrawRoundedRectangle($glowBrush, $null, [System.Windows.Rect]::new(12, 12, 232, 232), 52, 52)

    $shield = [System.Windows.Media.StreamGeometry]::new()
    $context = $shield.Open()
    $context.BeginFigure([System.Windows.Point]::new(128, 35), $true, $true)
    $context.LineTo([System.Windows.Point]::new(199, 62), $true, $false)
    $context.LineTo([System.Windows.Point]::new(195, 127), $true, $false)
    $context.BezierTo([System.Windows.Point]::new(192, 163), [System.Windows.Point]::new(162, 192), [System.Windows.Point]::new(128, 215), $true, $false)
    $context.BezierTo([System.Windows.Point]::new(94, 192), [System.Windows.Point]::new(64, 163), [System.Windows.Point]::new(61, 127), $true, $false)
    $context.LineTo([System.Windows.Point]::new(57, 62), $true, $false)
    $context.LineTo([System.Windows.Point]::new(128, 35), $true, $false)
    $context.Close()
    $shield.Freeze()

    $shieldPen = [System.Windows.Media.Pen]::new((New-Brush '#7BFFE0' '#7A95FF' 45), 6)
    $shieldPen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $drawing.DrawGeometry((New-Brush '#172C39' '#0C1823' 135), $shieldPen, $shield)

    $typeface = [System.Windows.Media.Typeface]::new(
        [System.Windows.Media.FontFamily]::new('Segoe UI Variable Display, Segoe UI'),
        [System.Windows.FontStyles]::Normal,
        [System.Windows.FontWeights]::Bold,
        [System.Windows.FontStretches]::Normal)
    $text = [System.Windows.Media.FormattedText]::new(
        'H',
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Windows.FlowDirection]::LeftToRight,
        $typeface,
        103,
        (New-Brush '#FFFFFF' '#B5FFF0' 90),
        1.0)
    $drawing.DrawText($text, [System.Windows.Point]::new((256 - $text.Width) / 2, 72))

    $drawing.DrawEllipse((New-Brush '#6CFFF0' '#39A9FF' 45), $null, [System.Windows.Point]::new(199, 48), 5.5, 5.5)
    $drawing.Pop()
    $drawing.Close()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $pngFrames.Add($stream.ToArray())
    $stream.Dispose()
    $drawing.Dispose()
}

$directoryBytes = 6 + (16 * $sizes.Count)
$offset = $directoryBytes
$file = [System.IO.File]::Open($iconPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$sizes.Count)

    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { [byte]0 } else { [byte]$sizes[$index] }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$pngFrames[$index].Length)
        $writer.Write([UInt32]$offset)
        $offset += $pngFrames[$index].Length
    }

    foreach ($frame in $pngFrames) { $writer.Write($frame) }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host "Generated $iconPath ($((Get-Item -LiteralPath $iconPath).Length) bytes, $($sizes -join ', ') px)."
