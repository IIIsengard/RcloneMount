# Regenerates src/RcloneMount/app.ico with a rounded-square design carrying
# "rcM" white text on a blue gradient. Run manually whenever the icon design
# changes; the app's <ApplicationIcon> pulls the resulting file at build time.
#
# ICO format: 6-byte header + 16-byte directory entry per frame + PNG payloads.
# We emit multi-resolution frames so Explorer / taskbar / tray each pick a
# native size and the icon stays crisp instead of nearest-neighbor scaled.

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$outPath  = Join-Path $repoRoot 'src/RcloneMount/app.ico'
$sizes    = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-IconFrame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # Corner radius scales with size — ~18% keeps the same visual roundness at
    # every resolution instead of looking chunkier at large sizes.
    $radius = [int][math]::Max(2, [math]::Round($size * 0.18))
    $rect   = New-Object System.Drawing.RectangleF 0, 0, $size, $size

    # Rounded-square path (four arcs + close). Manual construction because
    # System.Drawing.Drawing2D.GraphicsPath has no built-in rounded rect.
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    # Blue gradient (top → bottom) picked to match the brand accent used
    # elsewhere in the app (Theme.Accent territory).
    $top    = [System.Drawing.Color]::FromArgb(255, 74, 144, 226)
    $bottom = [System.Drawing.Color]::FromArgb(255, 30,  80, 165)
    $brush  = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $top, $bottom, 90
    $g.FillPath($brush, $path)
    $brush.Dispose()

    # Faint inner highlight along the top edge — cheap depth cue so the tile
    # doesn't look like a flat sticker at large sizes.
    if ($size -ge 32) {
        $highlightRect = New-Object System.Drawing.RectangleF 0, 0, $size, ($size * 0.45)
        $highlightTop    = [System.Drawing.Color]::FromArgb(70, 255, 255, 255)
        $highlightBottom = [System.Drawing.Color]::FromArgb(0,  255, 255, 255)
        $hBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $highlightRect, $highlightTop, $highlightBottom, 90
        $g.SetClip($path)
        $g.FillRectangle($hBrush, $highlightRect)
        $g.ResetClip()
        $hBrush.Dispose()
    }

    # Outline — thin at every size, subtle enough not to compete with the text.
    $penWidth = [math]::Max(1, $size / 48.0)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 20, 55, 120)), $penWidth
    $g.DrawPath($pen, $path)
    $pen.Dispose()

    # Below ~24 px three glyphs turn to mush, so the tiny frames fall back to a
    # single bold "M" that still reads at a taskbar glance. Larger frames draw
    # the "rc/M" monogram — "rc" small on top, "M" large below — which reads
    # as a proper mark rather than three cramped letters on one line.
    $textBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment     = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $format.FormatFlags   = [System.Drawing.StringFormatFlags]::NoWrap

    if ($size -lt 24) {
        $fontSize = [math]::Max(4, $size * 0.68)
        $font = New-Object System.Drawing.Font 'Segoe UI', $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $textRect = New-Object System.Drawing.RectangleF 0, ($size * 0.03), $size, $size
        $g.DrawString('M', $font, $textBrush, $textRect, $format)
        $font.Dispose()
    } else {
        # "rc" on the top third, "M" on the bottom two-thirds. Sizes chosen so
        # the two rows share the same visual weight — the M is bigger because
        # a single glyph needs to "answer" a two-letter row above it.
        $rcSize = [math]::Max(6, $size * 0.30)
        $mSize  = [math]::Max(8, $size * 0.58)
        $rcFont = New-Object System.Drawing.Font 'Segoe UI', $rcSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $mFont  = New-Object System.Drawing.Font 'Segoe UI', $mSize,  ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)

        $rcRect = New-Object System.Drawing.RectangleF 0, ($size * 0.06), $size, ($size * 0.38)
        $mRect  = New-Object System.Drawing.RectangleF 0, ($size * 0.36), $size, ($size * 0.62)
        $g.DrawString('rc', $rcFont, $textBrush, $rcRect, $format)
        $g.DrawString('M',  $mFont,  $textBrush, $mRect,  $format)
        $rcFont.Dispose()
        $mFont.Dispose()
    }
    $textBrush.Dispose()
    $format.Dispose()

    $g.Dispose()
    $path.Dispose()
    return $bmp
}

function Convert-BitmapToPng([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

# Build the frames + PNG payloads first so we know payload sizes before we
# write the directory (each entry needs its payload's byte size + offset).
$frames = @()
foreach ($size in $sizes) {
    $bmp = New-IconFrame $size
    $png = Convert-BitmapToPng $bmp
    $frames += [pscustomobject]@{ Size = $size; Png = $png }
    $bmp.Dispose()
}

# Assemble the ICO.
$out = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter $out

# ICONDIR (6 bytes): reserved=0, type=1 (ICO), count.
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$frames.Count)

# Directory entries start at offset 6; payloads start after all entries.
$dirEntrySize = 16
$dataOffset   = 6 + ($dirEntrySize * $frames.Count)

foreach ($f in $frames) {
    # width/height: 0 signals 256 in the ICO spec.
    $dim = if ($f.Size -ge 256) { [byte]0 } else { [byte]$f.Size }
    $bw.Write([byte]$dim)          # width
    $bw.Write([byte]$dim)          # height
    $bw.Write([byte]0)             # color palette (0 for 32bpp)
    $bw.Write([byte]0)             # reserved
    $bw.Write([UInt16]1)           # color planes
    $bw.Write([UInt16]32)          # bpp
    $bw.Write([UInt32]$f.Png.Length)
    $bw.Write([UInt32]$dataOffset)
    $dataOffset += $f.Png.Length
}

# Write payloads directly to the underlying stream — BinaryWriter.Write on a
# byte[] resolves ambiguously under PowerShell and can end up calling the
# single-byte overload, truncating each PNG to one byte.
foreach ($f in $frames) { $out.Write($f.Png, 0, $f.Png.Length) }

$bw.Flush()
$finalBytes = $out.ToArray()
[System.IO.File]::WriteAllBytes($outPath, $finalBytes)
$bw.Dispose()
$out.Dispose()

Write-Output "Wrote $outPath ($($finalBytes.Length) bytes, $($frames.Count) frames)"
