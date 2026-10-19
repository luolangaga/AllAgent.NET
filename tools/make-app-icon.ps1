# Builds the multi-size application icon from the master artwork.
# Every frame is stored as an uncompressed BMP frame: the widest compatibility
# across the Windows shell, GDI+ consumers and the installer compiler.
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Output,
    [string]$PreviewDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (!$Source) { $Source = Join-Path $root '..\src\AllAgent.NET.Terminal\Assets\app-icon.png' }
if (!$Output) { $Output = Join-Path $root '..\src\AllAgent.NET.Terminal\Assets\allagent.ico' }
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$padding = 0.03

function New-MasterBitmap([System.Drawing.Image]$image) {
    $minX = $image.Width; $minY = $image.Height; $maxX = -1; $maxY = -1
    $rect = New-Object System.Drawing.Rectangle 0, 0, $image.Width, $image.Height
    $data = ([System.Drawing.Bitmap]$image).LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $pixels = New-Object byte[] ($stride * $image.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
        for ($y = 0; $y -lt $image.Height; $y++) {
            $rowStart = $y * $stride
            for ($x = 0; $x -lt $image.Width; $x++) {
                if ($pixels[$rowStart + $x * 4 + 3] -gt 8) {
                    if ($x -lt $minX) { $minX = $x }
                    if ($x -gt $maxX) { $maxX = $x }
                    if ($y -lt $minY) { $minY = $y }
                    if ($y -gt $maxY) { $maxY = $y }
                }
            }
        }
    }
    finally { ([System.Drawing.Bitmap]$image).UnlockBits($data) }
    if ($maxX -lt 0) { throw "Source artwork is fully transparent: $Source" }

    $contentWidth = $maxX - $minX + 1
    $contentHeight = $maxY - $minY + 1
    $side = [int][Math]::Ceiling(([Math]::Max($contentWidth, $contentHeight)) / (1 - 2 * $padding))
    $master = New-Object System.Drawing.Bitmap $side, $side, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($master)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $x = [int](($side - $contentWidth) / 2)
        $y = [int](($side - $contentHeight) / 2)
        $graphics.DrawImage($image, (New-Object System.Drawing.Rectangle $x, $y, $contentWidth, $contentHeight), (New-Object System.Drawing.Rectangle $minX, $minY, $contentWidth, $contentHeight), [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally { $graphics.Dispose() }
    return $master
}

function New-ScaledFrame([System.Drawing.Image]$master, [int]$size) {
    $frame = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($frame)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.DrawImage($master, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
    }
    finally { $graphics.Dispose() }
    return $frame
}

function ConvertTo-BmpFrame([System.Drawing.Bitmap]$frame) {
    $size = $frame.Width
    $header = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $header
    try {
        $writer.Write([uint32]40)              # BITMAPINFOHEADER
        $writer.Write([int32]$size)
        $writer.Write([int32]($size * 2))      # ICO frames carry the mask below the pixels
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]0)               # BI_RGB
        $writer.Write([uint32]($size * $size * 4))
        $writer.Write([int32]0); $writer.Write([int32]0)
        $writer.Write([uint32]0); $writer.Write([uint32]0)
        $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
        $data = $frame.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $row = New-Object byte[] ($size * 4)
            for ($y = $size - 1; $y -ge 0; $y--) {
                # Locked 32bpp ARGB rows are already BGRA in memory, the order ICO frames want.
                [System.Runtime.InteropServices.Marshal]::Copy([System.IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $row.Length)
                $writer.Write($row)
            }
        }
        finally { $frame.UnlockBits($data) }
        $maskRow = New-Object byte[] ([int]([Math]::Floor(($size + 31) / 32)) * 4)
        for ($y = 0; $y -lt $size; $y++) { $writer.Write($maskRow) }
        $writer.Flush()
        return $header.ToArray()
    }
    finally { $writer.Dispose(); $header.Dispose() }
}

$source = (Resolve-Path $Source).Path
$image = [System.Drawing.Bitmap]::FromFile($source)
try {
    $master = New-MasterBitmap $image
}
finally { $image.Dispose() }

$frames = @()
try {
    foreach ($size in $sizes) {
        $frame = New-ScaledFrame $master $size
        try {
            if ($PreviewDirectory) {
                if (!(Test-Path $PreviewDirectory)) { New-Item -ItemType Directory -Force -Path $PreviewDirectory | Out-Null }
                $frame.Save((Join-Path $PreviewDirectory "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
            }
            $frames += [pscustomobject]@{ Size = $size; Bytes = [byte[]](ConvertTo-BmpFrame $frame) }
        }
        finally { $frame.Dispose() }
    }
}
finally { $master.Dispose() }

$outputPath = [System.IO.Path]::GetFullPath($Output)
$directory = Split-Path -Parent $outputPath
if (!(Test-Path $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$stream = [System.IO.File]::Create($outputPath)
$writer = New-Object System.IO.BinaryWriter $stream
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $writer.Write([byte]($(if ($frame.Size -ge 256) { 0 } else { $frame.Size })))
        $writer.Write([byte]($(if ($frame.Size -ge 256) { 0 } else { $frame.Size })))
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
}
finally { $writer.Dispose(); $stream.Dispose() }

$icon = New-Object System.Drawing.Icon $outputPath
try { "Wrote $outputPath ($([int]($icon.Width))x$([int]($icon.Height)) default, $($frames.Count) frames, $((Get-Item $outputPath).Length) bytes)" }
finally { $icon.Dispose() }
