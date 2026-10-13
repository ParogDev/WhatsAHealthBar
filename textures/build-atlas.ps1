# Builds textures\status-atlas.png from syrairc's ExileNameplates icon sheets in textures\source (2048x3072, 8 x 12
# cells of 256 px; credit: syrairc, ExileNameplates). Each cell is downscaled on its own (no bleed between cells) to
# $Cell px with high-quality bicubic filtering: the HUD loads textures without mipmaps, so a 256 px cell drawn at 20 px
# would shimmer. One atlas = one texture = one extra draw command for every icon on screen.
# Layout (StatusAtlas.cs must match): gem-badged icons at x 0, bare icons at x 8*Cell, the plain badge at x 16*Cell y 0,
# and two tiling hatch patterns (white diagonal stripes, period 8 then 4 px) at x 16*Cell, y 2*Cell and 3*Cell. The bare
# glyphs are brightened (their body is a 56-100% grey gradient in the source, which made every tint look dim): body to
# white, the dark edge kept, so a tint draws at its own color.
# Windows PowerShell 5.1: powershell -NoProfile -ExecutionPolicy Bypass -File textures\build-atlas.ps1
param([int]$Cell = 64)

Add-Type -AssemblyName System.Drawing
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$cols = 8; $rows = 12; $srcCell = 256
$atlas = New-Object System.Drawing.Bitmap ((2 * $cols + 1) * $Cell), ($rows * $Cell), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($atlas)
$g.Clear([System.Drawing.Color]::Transparent)
$g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$attr = New-Object System.Drawing.Imaging.ImageAttributes
$attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

# Brightening for the bare sheet: rgb * 1.5 - 0.08 (clamped), alpha untouched. 56% grey -> 76%, 70% -> 97%, 76%+ -> white;
# the ~10% edge stays dark (7%).
$bright = New-Object System.Drawing.Imaging.ImageAttributes
$bright.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
$m = New-Object System.Drawing.Imaging.ColorMatrix
$m.Matrix00 = 1.5; $m.Matrix11 = 1.5; $m.Matrix22 = 1.5; $m.Matrix33 = 1; $m.Matrix44 = 1
$m.Matrix40 = -0.08; $m.Matrix41 = -0.08; $m.Matrix42 = -0.08
$bright.SetColorMatrix($m)

function Put($img, $sx, $sy, $sw, $sh, $dx, $dy, $a) {
    $dest = New-Object System.Drawing.Rectangle $dx, $dy, $Cell, $Cell
    $g.DrawImage($img, $dest, $sx, $sy, $sw, $sh, [System.Drawing.GraphicsUnit]::Pixel, $a)
}

$sheets = @(@{ File = 'Icons_Status_Gem.png'; X = 0; Attr = $attr }, @{ File = 'Icons_Status_Gem_Bare.png'; X = $cols * $Cell; Attr = $bright })
foreach ($s in $sheets) {
    $img = [System.Drawing.Image]::FromFile((Join-Path $dir "source\$($s.File)"))
    for ($i = 0; $i -lt $cols * $rows; $i++) {
        $cx = $i % $cols; $cy = [math]::Floor($i / $cols)
        Put $img ($cx * $srcCell) ($cy * $srcCell) $srcCell $srcCell ($s.X + $cx * $Cell) ($cy * $Cell) $s.Attr
    }
    $img.Dispose()
}
$badge = [System.Drawing.Image]::FromFile((Join-Path $dir 'source\Icons_Status_Badge_Gem.png'))
Put $badge 0 0 $badge.Width $badge.Height (2 * $cols * $Cell) 0 $attr
$badge.Dispose()

# Hatch cells: 45-degree white stripes that tile seamlessly (the cell is a multiple of the period). Drawn on their own bitmap
# so the clipping is exact, then copied in. Row 1 under the badge stays empty as filter padding.
function Hatch($period, $width, $dy) {
    # ($bmp, not $cell: PowerShell variable names are case-insensitive and $Cell is the size)
    $bmp = New-Object System.Drawing.Bitmap $Cell, $Cell, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $cg = [System.Drawing.Graphics]::FromImage($bmp)
    $cg.Clear([System.Drawing.Color]::Transparent)
    $cg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $width
    for ($k = -$Cell; $k -le 2 * $Cell; $k += $period) {
        # the line x - y = k, from the top edge to the bottom edge, extended past the cell so the ends are clipped
        $cg.DrawLine($pen, [single]($k - $Cell), [single](-$Cell), [single]($k + 2 * $Cell), [single](2 * $Cell))
    }
    $pen.Dispose(); $cg.Dispose()
    $g.DrawImage($bmp, (New-Object System.Drawing.Rectangle (2 * $cols * $Cell), $dy, $Cell, $Cell), 0, 0, $Cell, $Cell, [System.Drawing.GraphicsUnit]::Pixel)
    $bmp.Dispose()
}
Hatch 8 2.2 (2 * $Cell)
Hatch 4 1.3 (3 * $Cell)

$g.Dispose()
$out = Join-Path $dir 'status-atlas.png'
$atlas.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$atlas.Dispose()
"wrote $out"
