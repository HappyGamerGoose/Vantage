$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repoRoot "src\Assets\Square150x150Logo.scale-200.png"
$outputPath = Join-Path $repoRoot "src\Assets\AppIcon.ico"
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function Write-UInt16([System.IO.BinaryWriter]$Writer, [int]$Value) {
    $Writer.Write([uint16]$Value)
}

function Write-UInt32([System.IO.BinaryWriter]$Writer, [long]$Value) {
    $Writer.Write([uint32]$Value)
}

function New-ClassicIconFrame([System.Drawing.Bitmap]$Source, [int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($Source, 0, 0, $Size, $Size)
        }
        finally {
            $graphics.Dispose()
        }

        $pixelBytes = [System.Collections.Generic.List[byte]]::new()
        for ($y = $Size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $Size; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                $pixelBytes.Add($pixel.B)
                $pixelBytes.Add($pixel.G)
                $pixelBytes.Add($pixel.R)
                $pixelBytes.Add($pixel.A)
            }
        }

        $andRowBytes = [int](([int](($Size + 31) / 32)) * 4)
        $andMaskBytes = [byte[]]::new($andRowBytes * $Size)
        $imageBytes = $pixelBytes.Count + $andMaskBytes.Length
        $frameStream = [System.IO.MemoryStream]::new()
        $frameWriter = [System.IO.BinaryWriter]::new($frameStream)
        try {
            Write-UInt32 $frameWriter 40
            Write-UInt32 $frameWriter $Size
            Write-UInt32 $frameWriter ($Size * 2)
            Write-UInt16 $frameWriter 1
            Write-UInt16 $frameWriter 32
            Write-UInt32 $frameWriter 0
            Write-UInt32 $frameWriter ($Size * $Size * 4)
            Write-UInt32 $frameWriter 0
            Write-UInt32 $frameWriter 0
            Write-UInt32 $frameWriter 0
            Write-UInt32 $frameWriter 0
            $frameWriter.Write([byte[]]$pixelBytes.ToArray())
            $frameWriter.Write($andMaskBytes)
            return $frameStream.ToArray()
        }
        finally {
            $frameWriter.Dispose()
            $frameStream.Dispose()
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

$source = [System.Drawing.Bitmap]::FromFile($sourcePath)
$frames = [System.Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $sizes) {
        $frames.Add((New-ClassicIconFrame $source $size))
    }
}
finally {
    $source.Dispose()
}

$file = [System.IO.File]::Create($outputPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    Write-UInt16 $writer 0
    Write-UInt16 $writer 1
    Write-UInt16 $writer $frames.Count

    $offset = 6 + (16 * $frames.Count)
    for ($index = 0; $index -lt $frames.Count; $index++) {
        $size = $sizes[$index]
        $dimension = if ($size -eq 256) { 0 } else { $size }
        $frame = $frames[$index]
        $pixelBytes = $size * $size * 4
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        Write-UInt16 $writer 1
        Write-UInt16 $writer 32
        Write-UInt32 $writer $frame.Length
        Write-UInt32 $writer $offset
        $offset += $frame.Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host "Native shell icon written to $outputPath"
