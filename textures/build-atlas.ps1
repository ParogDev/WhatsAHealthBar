# Builds textures\status-atlas.png from syrairc's ExileNameplates icon sheets in textures\source (2048x3072, 8 x 12
# cells of 256 px; credit: syrairc, ExileNameplates). Each cell is downscaled on its own (no bleed between cells) to
# $Cell px with high-quality bicubic filtering: the HUD loads textures without mipmaps, so a 256 px cell drawn at 20 px
# would shimmer. One atlas = one texture = one extra draw command for every icon on screen.
# Layout (StatusAtlas.cs must match): gem-badged icons at x 0, bare icons at x 8*Cell, the plain badge at x 16*Cell.
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

function Put($img, $sx, $sy, $sw, $sh, $dx, $dy) {
    $dest = New-Object System.Drawing.Rectangle $dx, $dy, $Cell, $Cell
    $g.DrawImage($img, $dest, $sx, $sy, $sw, $sh, [System.Drawing.GraphicsUnit]::Pixel, $attr)
}

$sheets = @(@{ File = 'Icons_Status_Gem.png'; X = 0 }, @{ File = 'Icons_Status_Gem_Bare.png'; X = $cols * $Cell })
foreach ($s in $sheets) {
    $img = [System.Drawing.Image]::FromFile((Join-Path $dir "source\$($s.File)"))
    for ($i = 0; $i -lt $cols * $rows; $i++) {
        $cx = $i % $cols; $cy = [math]::Floor($i / $cols)
        Put $img ($cx * $srcCell) ($cy * $srcCell) $srcCell $srcCell ($s.X + $cx * $Cell) ($cy * $Cell)
    }
    $img.Dispose()
}
$badge = [System.Drawing.Image]::FromFile((Join-Path $dir 'source\Icons_Status_Badge_Gem.png'))
Put $badge 0 0 $badge.Width $badge.Height (2 * $cols * $Cell) 0
$badge.Dispose()

$g.Dispose()
$out = Join-Path $dir 'status-atlas.png'
$atlas.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$atlas.Dispose()
"wrote $out"
