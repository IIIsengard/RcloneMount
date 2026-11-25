# Renders scripts/generate-app-icon.ps1's design as a single PNG for visual
# review — same drawing code, just saved as .png instead of packed into an ICO.
Add-Type -AssemblyName System.Drawing
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'generate-app-icon.ps1') *> $null

# The dot-source above re-runs the generator (writes app.ico) but also leaves
# New-IconFrame in scope. Render a comparison strip at multiple sizes.
$sizes = 16, 24, 32, 48, 64, 128, 256
$padding = 20
$stripWidth  = ($sizes | Measure-Object -Sum).Sum + ($padding * ($sizes.Count + 1))
$stripHeight = 256 + ($padding * 2)

$canvas = New-Object System.Drawing.Bitmap -ArgumentList @([int]$stripWidth, [int]$stripHeight)
$cg = [System.Drawing.Graphics]::FromImage($canvas)
$cg.Clear([System.Drawing.Color]::FromArgb(255, 40, 40, 45))

$x = $padding
foreach ($s in $sizes) {
    $frame = New-IconFrame $s
    $y = $padding + (256 - $s)
    $cg.DrawImage($frame, $x, $y, $s, $s)
    $frame.Dispose()
    $x += $s + $padding
}
$cg.Dispose()

$outPng = Join-Path $repoRoot 'scripts/app-icon-preview.png'
$canvas.Save($outPng, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Dispose()
Write-Output "Wrote $outPng"
