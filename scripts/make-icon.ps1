# Regenerates src/GlassRea.App/Assets/app.ico from the Twin flow mark (design/logo/twin-flow.svg, by Tem Revil):
# the blue gradient tile with its sheen, a long down arrow and a shorter, softer up arrow.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\GlassRea.App\Assets\app.ico'))
Add-Type -AssemblyName System.Drawing
$out = $Out
$sizes = 16, 24, 32, 48, 64, 256
# Draws the mark, $s pixels wide, at ($left, $top) of $g.
function Draw-Mark($g, [single]$s, [single]$left = 0, [single]$top = 0) {
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $state = $g.Save()
    $g.TranslateTransform($left, $top)
    $k = [single]($s / 64)          # the SVG is drawn on a 64-unit grid
    $g.ScaleTransform($k, $k)
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = [single]5; $w = [single]54; $r = [single]15
    $tile.AddArc($x, $x, 2 * $r, 2 * $r, 180, 90); $tile.AddArc($x + $w - 2 * $r, $x, 2 * $r, 2 * $r, 270, 90)
    $tile.AddArc($x + $w - 2 * $r, $x + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90); $tile.AddArc($x, $x + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $tile.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF $x, $x, $w, $w
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0x5B, 0x9B, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0x0A, 0x3F, 0x9E)), ([single]45)
    $g.FillPath($fill, $tile)
    $sheenRect = New-Object System.Drawing.RectangleF $x, $x, $w, ($w / 2)
    $sheen = New-Object System.Drawing.Drawing2D.LinearGradientBrush $sheenRect, ([System.Drawing.Color]::FromArgb(140, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), ([single]90)
    $g.SetClip($tile); $g.FillRectangle($sheen, $sheenRect); $g.ResetClip()
    foreach ($arrow in @(@{ A = 255; P = @(24, 15, 24, 45, 15.5, 37, 24, 45.5, 32.5, 37) }, @{ A = 184; P = @(41, 49, 41, 27, 33, 34.5, 41, 26.5, 49, 34.5) })) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($arrow.A, 255, 255, 255)), ([single]5.5)
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
        $q = [single[]]$arrow.P
        $g.DrawLine($pen, $q[0], $q[1], $q[2], $q[3])
        $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF $q[4], $q[5]), (New-Object System.Drawing.PointF $q[6], $q[7]), (New-Object System.Drawing.PointF $q[8], $q[9])))
    }
    $g.Restore($state)
}

$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Mark $g $s
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Small sizes as classic 32-bit DIB frames, which every Windows icon loader reads.
        $w2 = New-Object System.IO.BinaryWriter $ms
        $w2.Write([uint32]40); $w2.Write([int32]$s); $w2.Write([int32]($s * 2)); $w2.Write([uint16]1); $w2.Write([uint16]32)
        $w2.Write([uint32]0); $w2.Write([uint32]($s * $s * 4)); $w2.Write([int32]0); $w2.Write([int32]0); $w2.Write([uint32]0); $w2.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $w2.Write([byte]$c.B); $w2.Write([byte]$c.G); $w2.Write([byte]$c.R); $w2.Write([byte]$c.A) } }
        $maskRow = [Math]::Ceiling($s / 32) * 4
        $w2.Write((New-Object byte[] ($maskRow * $s)))
        $w2.Flush()
    }
    $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"Wrote $out"

# The Microsoft Store package's logos (src/GlassRea.App/Store/Package.appxmanifest): Start, the taskbar, Settings > Apps
# and the Store listing. A tile keeps a margin around the mark, as Windows' own tiles do.
$storeDir = Join-Path (Split-Path $out -Parent) 'Store'
New-Item -ItemType Directory -Force $storeDir | Out-Null
function Save-Png([string]$name, [int]$w, [int]$h, [single]$mark) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Mark $g $mark (($w - $mark) / 2) (($h - $mark) / 2)
    $g.Dispose()
    $bmp.Save((Join-Path $storeDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Save-Png 'StoreLogo.png' 50 50 50
Save-Png 'Square44x44Logo.png' 44 44 44
foreach ($t in 16, 24, 32, 48, 256) {
    Save-Png "Square44x44Logo.targetsize-$t.png" $t $t $t
    Save-Png "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t $t $t
}
Save-Png 'Square150x150Logo.png' 150 150 100
Save-Png 'Wide310x150Logo.png' 310 150 100
"Wrote the Store logos to $storeDir"
