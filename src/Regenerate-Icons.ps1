# Re-derives every Assets/*.png + AppIcon.ico from the original
# ICON.png at project root. Strips the white background to alpha=0 so
# the V ribbon is the ONLY thing visible against whatever surface
# Windows drops it on. No 3D re-imagining, no shadow tweaking — just the
# source image, downsampled at every scale the MSIX manifest needs.

Add-Type -AssemblyName System.Drawing

$master = "C:\Users\akshi\Documents\Vantage\ICON.png"

# ── 1. Background-stripped master ─────────────────────────────────
# Read once, walk once, write 1024x1024 with transparent background.
# We treat near-white (≥250 across all three channels) as "no icon
# here" and zero the alpha. Cyan / blue ribbons with anti-aliased
# edges round-trip with the edge softness preserved.
$src = [System.Drawing.Bitmap]::FromFile($master)
$w = $src.Width
$h = $src.Height
$stripped = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

for ($y = 0; $y -lt $h; $y++) {
    for ($x = 0; $x -lt $w; $x++) {
        $px = $src.GetPixel($x, $y)
        # Alpha=0 (already transparent): keep zero-alpha.
        # Anti-aliased edge pixel (alpha > 0): honour original alpha,
        # but if the RGB says "near white" snap the alpha down so the
        # fringe doesn't read as a hard white border.
        if ($px.A -le 8) {
            $stripped.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, 0, 0, 0))
            continue
        }
        if ($px.R -ge 250 -and $px.G -ge 250 -and $px.B -ge 250) {
            $stripped.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, 0, 0, 0))
            continue
        }
        $stripped.SetPixel($x, $y, $px)
    }
}

$strippedPath = "C:\Users\akshi\Documents\Vantage\ICON_transparent.png"
$stripped.Save($strippedPath, [System.Drawing.Imaging.ImageFormat]::Png)
$src.Dispose()
$stripped.Dispose()
Write-Output ("Wrote {0}" -f $strippedPath)

# ── 2. Down-sample to every MSIX slot from the stripped master ─────

$masterStripped = "C:\Users\akshi\Documents\Vantage\ICON_transparent.png"
$src2 = [System.Drawing.Bitmap]::FromFile($masterStripped)

function Resize-Save([int]$w, [int]$h, [string]$outPath) {
    $dst = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $dst.SetResolution(72, 72)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
    $g.DrawImage($src2, 0, 0, $w, $h)
    $g.Dispose()
    $dst.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $dst.Dispose()
    Write-Output ("Wrote {0} ({1}x{2}, {3} bytes)" -f $outPath, $w, $h, (Get-Item $outPath).Length)
}

# Square logo family.
Resize-Save 88  88  "Assets\Square44x44Logo.scale-200.png"
Resize-Save 300 300 "Assets\Square150x150Logo.scale-200.png"
Resize-Save 24  24  "Assets\Square44x44Logo.targetsize-24_altform-unplated.png"
Resize-Save 48  48  "Assets\Square44x44Logo.targetsize-48_altform-lightunplated.png"
Resize-Save 50  50  "Assets\StoreLogo.png"

# Wide tile — Start menu + Settings uses this as the wide tile.
Resize-Save 620 300 "Assets\Wide310x150Logo.scale-200.png"

# Splash screen.
Resize-Save 1200 600 "Assets\SplashScreen.scale-200.png"

# AppIcon.ico from a 256x256 PNG. WinUI 3 AppWindow.SetIcon wants an .ico;
# multi-size writing is non-trivial — a single 256 .ico downscales
# cleanly under the modern Windows downscale path.
$tmp = New-Object System.Drawing.Bitmap(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g2 = [System.Drawing.Graphics]::FromImage($tmp)
$g2.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g2.DrawImage($src2, 0, 0, 256, 256)
$g2.Dispose()
$hIcon = $tmp.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hIcon)
$iconStream = New-Object System.IO.MemoryStream
$icon.Save($iconStream)
$iconBytes = $iconStream.ToArray()
$iconStream.Dispose()
$icon.Dispose()
[System.IO.File]::WriteAllBytes('Assets\AppIcon.ico', $iconBytes)
Write-Output ("Wrote Assets\AppIcon.ico ({0} bytes)" -f $iconBytes.Length)

$src2.Dispose()
Write-Output "DONE"
