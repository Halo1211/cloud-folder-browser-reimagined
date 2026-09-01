[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetDirectory = Join-Path $repositoryRoot 'CloudFolderBrowser\Assets'
$logoPath = Join-Path $assetDirectory 'cloud-browser-icon.png'
$applicationIconPath = Join-Path $assetDirectory 'cloud-browser-modern.ico'
$legacyIconPath = Join-Path $repositoryRoot 'CloudFolderBrowser\icon.ico'
$loadingIconPath = Join-Path $repositoryRoot 'Aga.Controls\Resources\loading_icon'

[System.IO.Directory]::CreateDirectory($assetDirectory) | Out-Null

function New-FolderPath {
    param([float]$Scale)

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.StartFigure()
    $path.AddBezier(176 * $Scale, 300 * $Scale, 176 * $Scale, 248 * $Scale, 212 * $Scale, 212 * $Scale, 264 * $Scale, 212 * $Scale)
    $path.AddLine(264 * $Scale, 212 * $Scale, 454 * $Scale, 212 * $Scale)
    $path.AddBezier(454 * $Scale, 212 * $Scale, 478 * $Scale, 212 * $Scale, 494 * $Scale, 222 * $Scale, 512 * $Scale, 242 * $Scale)
    $path.AddLine(512 * $Scale, 242 * $Scale, 608 * $Scale, 344 * $Scale)
    $path.AddLine(608 * $Scale, 344 * $Scale, 816 * $Scale, 344 * $Scale)
    $path.AddBezier(816 * $Scale, 344 * $Scale, 870 * $Scale, 344 * $Scale, 904 * $Scale, 382 * $Scale, 904 * $Scale, 434 * $Scale)
    $path.AddLine(904 * $Scale, 434 * $Scale, 904 * $Scale, 762 * $Scale)
    $path.AddBezier(904 * $Scale, 762 * $Scale, 904 * $Scale, 816 * $Scale, 868 * $Scale, 852 * $Scale, 814 * $Scale, 852 * $Scale)
    $path.AddLine(814 * $Scale, 852 * $Scale, 266 * $Scale, 852 * $Scale)
    $path.AddBezier(266 * $Scale, 852 * $Scale, 212 * $Scale, 852 * $Scale, 176 * $Scale, 816 * $Scale, 176 * $Scale, 762 * $Scale)
    $path.CloseFigure()
    return $path
}

function New-CloudPath {
    param([float]$Scale)

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.StartFigure()
    $path.AddBezier(420 * $Scale, 858 * $Scale, 352 * $Scale, 858 * $Scale, 298 * $Scale, 804 * $Scale, 298 * $Scale, 736 * $Scale)
    $path.AddBezier(298 * $Scale, 736 * $Scale, 298 * $Scale, 676 * $Scale, 342 * $Scale, 624 * $Scale, 402 * $Scale, 614 * $Scale)
    $path.AddBezier(402 * $Scale, 614 * $Scale, 422 * $Scale, 518 * $Scale, 506 * $Scale, 448 * $Scale, 606 * $Scale, 448 * $Scale)
    $path.AddBezier(606 * $Scale, 448 * $Scale, 718 * $Scale, 448 * $Scale, 808 * $Scale, 530 * $Scale, 822 * $Scale, 640 * $Scale)
    $path.AddBezier(822 * $Scale, 640 * $Scale, 892 * $Scale, 646 * $Scale, 946 * $Scale, 704 * $Scale, 946 * $Scale, 774 * $Scale)
    $path.AddBezier(946 * $Scale, 774 * $Scale, 946 * $Scale, 820 * $Scale, 910 * $Scale, 858 * $Scale, 864 * $Scale, 858 * $Scale)
    $path.CloseFigure()
    return $path
}

function New-BrandBitmap {
    param([int]$Size)

    $scale = $Size / 1024.0
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $folderPath = New-FolderPath -Scale $scale
        $cloudPath = New-CloudPath -Scale $scale
        $folderBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(32, 32, 29))
        $cloudBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(214, 161, 91))
        $outlinePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(243, 241, 235), [Math]::Max(2, 26 * $scale))
        $outlinePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        try {
            $graphics.FillPath($folderBrush, $folderPath)
            $graphics.DrawPath($outlinePen, $cloudPath)
            $graphics.FillPath($cloudBrush, $cloudPath)

            $arrowPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(32, 32, 29), [Math]::Max(2, 42 * $scale))
            $arrowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $arrowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            try {
                $graphics.DrawLine($arrowPen, 650 * $scale, 594 * $scale, 650 * $scale, 762 * $scale)
                $graphics.DrawLine($arrowPen, 570 * $scale, 690 * $scale, 650 * $scale, 770 * $scale)
                $graphics.DrawLine($arrowPen, 730 * $scale, 690 * $scale, 650 * $scale, 770 * $scale)
            }
            finally {
                $arrowPen.Dispose()
            }
        }
        finally {
            $outlinePen.Dispose()
            $cloudBrush.Dispose()
            $folderBrush.Dispose()
            $cloudPath.Dispose()
            $folderPath.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Write-MultiSizeIcon {
    param([System.Drawing.Bitmap]$Source, [string]$Path)

    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $scaled = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($scaled)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($Source, 0, 0, $size, $size)
        }
        finally {
            $graphics.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $scaled.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $images.Add($stream.ToArray())
        }
        finally {
            $stream.Dispose()
            $scaled.Dispose()
        }
    }

    $fileStream = [System.IO.File]::Create($Path)
    $writer = [System.IO.BinaryWriter]::new($fileStream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + (16 * $sizes.Count)
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $size = $sizes[$index]
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$index].Length
        }
        foreach ($imageBytes in $images) {
            $writer.Write($imageBytes)
        }
    }
    finally {
        $writer.Dispose()
    }
}

function Write-LoadingAnimation {
    param([string]$Path)

    $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($null -eq $ffmpeg) {
        throw 'ffmpeg is required to regenerate the animated loading icon.'
    }

    $frameDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('cfb-brand-' + [Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($frameDirectory) | Out-Null
    try {
        $dotColors = @(
            [System.Drawing.Color]::FromArgb(214, 161, 91),
            [System.Drawing.Color]::FromArgb(194, 145, 82),
            [System.Drawing.Color]::FromArgb(170, 126, 72),
            [System.Drawing.Color]::FromArgb(145, 108, 64),
            [System.Drawing.Color]::FromArgb(119, 91, 57),
            [System.Drawing.Color]::FromArgb(96, 76, 51),
            [System.Drawing.Color]::FromArgb(76, 64, 47),
            [System.Drawing.Color]::FromArgb(62, 55, 44)
        )

        for ($frame = 0; $frame -lt 8; $frame++) {
            $bitmap = [System.Drawing.Bitmap]::new(16, 16, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
                for ($dot = 0; $dot -lt 8; $dot++) {
                    $age = ($dot - $frame + 8) % 8
                    $angle = (($dot * 45) - 90) * [Math]::PI / 180
                    $x = 8 + ([Math]::Cos($angle) * 5.25)
                    $y = 8 + ([Math]::Sin($angle) * 5.25)
                    $diameter = if ($age -eq 0) { 3.4 } else { 2.6 }
                    $brush = [System.Drawing.SolidBrush]::new($dotColors[$age])
                    try {
                        $graphics.FillEllipse($brush, [float]($x - ($diameter / 2)), [float]($y - ($diameter / 2)), [float]$diameter, [float]$diameter)
                    }
                    finally {
                        $brush.Dispose()
                    }
                }

                $bitmap.Save((Join-Path $frameDirectory ('frame-{0:D2}.png' -f $frame)), [System.Drawing.Imaging.ImageFormat]::Png)
            }
            finally {
                $graphics.Dispose()
                $bitmap.Dispose()
            }
        }

        $palettePath = Join-Path $frameDirectory 'palette.png'
        & $ffmpeg.Source -hide_banner -loglevel error -y -framerate 10 -i (Join-Path $frameDirectory 'frame-%02d.png') -vf 'palettegen=reserve_transparent=1:transparency_color=ffffff' $palettePath
        if ($LASTEXITCODE -ne 0) { throw 'ffmpeg could not create the loading-animation palette.' }

        & $ffmpeg.Source -hide_banner -loglevel error -y -framerate 10 -i (Join-Path $frameDirectory 'frame-%02d.png') -i $palettePath -lavfi 'paletteuse=alpha_threshold=128' -loop 0 -f gif $Path
        if ($LASTEXITCODE -ne 0) { throw 'ffmpeg could not create the animated loading icon.' }
    }
    finally {
        if (Test-Path -LiteralPath $frameDirectory) {
            Remove-Item -LiteralPath $frameDirectory -Recurse -Force
        }
    }
}

$logo = New-BrandBitmap -Size 1024
try {
    $logo.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-MultiSizeIcon -Source $logo -Path $applicationIconPath
    [System.IO.File]::Copy($applicationIconPath, $legacyIconPath, $true)
}
finally {
    $logo.Dispose()
}

Write-LoadingAnimation -Path $loadingIconPath

Write-Host "Generated $logoPath"
Write-Host "Generated $applicationIconPath"
Write-Host "Generated $loadingIconPath"
