<#
.SYNOPSIS
    Draws the Call Klingel app icon at every size Windows asks for.

.DESCRIPTION
    The mark is a handset sitting inside a monitor outline: a phone call living on the PC's
    screen, which is the whole product in one shape. Both halves stay readable when the icon
    is 16 pixels wide, because each is a single thick stroke and nothing else.

    Drawn from paths rather than exported from a design tool, so it stays crisp at every
    size and can be regenerated when the brand colour changes.
#>
param([string]$OutDir = "$PSScriptRoot\app\payload\Assets")

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

function New-Icon([int]$size, [bool]$padded = $true) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Squircle tile. Small sizes get less padding so the mark keeps its weight.
    $pad = if ($padded) { [math]::Round($size * 0.055) } else { 0 }
    $box = $size - 2 * $pad
    $r = [math]::Round($box * 0.235)

    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($pad, $pad, 2*$r, 2*$r, 180, 90)
    $tile.AddArc($pad + $box - 2*$r, $pad, 2*$r, 2*$r, 270, 90)
    $tile.AddArc($pad + $box - 2*$r, $pad + $box - 2*$r, 2*$r, 2*$r, 0, 90)
    $tile.AddArc($pad, $pad + $box - 2*$r, 2*$r, 2*$r, 90, 90)
    $tile.CloseFigure()

    # Diagonal gradient, lit from the top left. The dark end is deep enough that white
    # strokes stay legible over the whole tile.
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point($pad, $pad)),
        (New-Object System.Drawing.Point(($pad + $box), ($pad + $box))),
        [System.Drawing.Color]::FromArgb(255, 79, 148, 255),
        [System.Drawing.Color]::FromArgb(255, 17, 38, 105))
    $g.FillPath($grad, $tile)

    # No separate gloss layer. A second gradient stopping short of the tile edge leaves a
    # hard diagonal seam, because GDI+ repeats the brush past its end point instead of
    # holding the last colour. The main gradient already carries the light.

    # --- The mark ---------------------------------------------------------------
    # A screen, and a handset inside it. Below 24px the screen outline is dropped: two
    # nested shapes at that size turn into a smudge, and the handset alone still says
    # "phone", which is the half that matters.

    $white = [System.Drawing.Color]::White
    $stroke = [math]::Max(1.6, $box * 0.075)

    # A rounded rectangle as a path - the only shape this icon needs.
    function New-RoundedRect($x, $y, $w, $h, $radius) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = 2 * $radius
        $p.AddArc([single]$x, [single]$y, [single]$d, [single]$d, 180, 90)
        $p.AddArc([single]($x + $w - $d), [single]$y, [single]$d, [single]$d, 270, 90)
        $p.AddArc([single]($x + $w - $d), [single]($y + $h - $d), [single]$d, [single]$d, 0, 90)
        $p.AddArc([single]$x, [single]($y + $h - $d), [single]$d, [single]$d, 90, 90)
        $p.CloseFigure()
        return $p
    }

    $ink = [System.Drawing.Color]::FromArgb(255, 17, 34, 82)

    if ($size -ge 24) {
        # A monitor, with a phone standing in front of it. That is the product in one
        # picture - the phone's screen living on the PC - and both halves are plain
        # rectangles, which is why it survives being 24 pixels wide.

        $mw = $box * 0.66
        $mh = $mw * 0.72
        $mx = $pad + $box * 0.14
        $my = $pad + $box * 0.19

        $monitor = New-RoundedRect $mx $my $mw $mh ($mh * 0.20)
        $mp = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(240, 255, 255, 255), [single]($stroke * 0.80))
        $mp.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($mp, $monitor)
        $monitor.Dispose()

        # Stand: neck and foot, one stroke each.
        $mp.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $mp.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $footY = $my + $mh + $box * 0.115
        $g.DrawLine($mp, [single]($mx + $mw * 0.42), [single]($my + $mh),
                         [single]($mx + $mw * 0.42), [single]$footY)
        $g.DrawLine($mp, [single]($mx + $mw * 0.20), [single]$footY,
                         [single]($mx + $mw * 0.64), [single]$footY)
        $mp.Dispose()

        # The phone, overlapping the monitor's lower right corner. It is filled, and it
        # carries a ring of tile colour around it, so it reads as standing in front of the
        # screen instead of being drawn on top of it.
        $pw = $box * 0.27
        $ph = $pw * 1.72
        $px = $pad + $box * 0.54
        $py = $pad + $box * 0.33

        $halo = New-RoundedRect ($px - $stroke * 0.75) ($py - $stroke * 0.75) `
                                ($pw + $stroke * 1.5) ($ph + $stroke * 1.5) ($pw * 0.34)
        $haloBrush = New-Object System.Drawing.SolidBrush($ink)
        $g.FillPath($haloBrush, $halo)
        $haloBrush.Dispose(); $halo.Dispose()

        $phone = New-RoundedRect $px $py $pw $ph ($pw * 0.28)
        $phoneBrush = New-Object System.Drawing.SolidBrush($white)
        $g.FillPath($phoneBrush, $phone)
        $phoneBrush.Dispose(); $phone.Dispose()

        # Screen cut-out and speaker slit, so the white block reads as a phone.
        if ($size -ge 48) {
            $inset = $pw * 0.14
            $inner = New-RoundedRect ($px + $inset) ($py + $ph * 0.13) `
                                     ($pw - 2*$inset) ($ph * 0.72) ($pw * 0.13)
            $innerBrush = New-Object System.Drawing.SolidBrush(
                [System.Drawing.Color]::FromArgb(255, 43, 92, 190))
            $g.FillPath($innerBrush, $inner)
            $innerBrush.Dispose(); $inner.Dispose()

            $slit = New-Object System.Drawing.SolidBrush($ink)
            $sw2 = $pw * 0.34
            $g.FillRectangle($slit, [single]($px + ($pw - $sw2)/2), [single]($py + $ph * 0.062),
                             [single]$sw2, [single]([math]::Max(1.0, $ph * 0.028)))
            $slit.Dispose()
        }
    }
    else {
        # 16px: the phone alone, large and centred. Two nested shapes at this size turn
        # into a smudge, and a phone silhouette still says what the app is.
        $pw = $box * 0.42
        $ph = $pw * 1.70
        $px = $pad + ($box - $pw) / 2
        $py = $pad + ($box - $ph) / 2

        $phone = New-RoundedRect $px $py $pw $ph ($pw * 0.26)
        $b = New-Object System.Drawing.SolidBrush($white)
        $g.FillPath($b, $phone)
        $b.Dispose(); $phone.Dispose()
    }

    # No signal arcs. The monitor and the phone already say what this is, and a third
    # element crowded the top right corner - where the phone now stands.

    $grad.Dispose()
    $tile.Dispose(); $g.Dispose()
    return $bmp
}

# Sizes the MSIX manifest and the Windows shell ask for.
$targets = @(
    @{ N = 'StoreLogo.png';               S = 50  },
    @{ N = 'Square44x44Logo.png';         S = 44  },
    @{ N = 'Square71x71Logo.png';         S = 71  },
    @{ N = 'Square150x150Logo.png';       S = 150 },
    @{ N = 'Square310x310Logo.png';       S = 310 },
    @{ N = 'Square44x44Logo.targetsize-16.png'; S = 16 },
    @{ N = 'Square44x44Logo.targetsize-24.png'; S = 24 },
    @{ N = 'Square44x44Logo.targetsize-32.png'; S = 32 },
    @{ N = 'Square44x44Logo.targetsize-48.png'; S = 48 },
    @{ N = 'Square44x44Logo.targetsize-256.png'; S = 256 }
)

foreach ($t in $targets) {
    $bmp = New-Icon -size $t.S
    $bmp.Save((Join-Path $OutDir $t.N), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Wide tile: mark on the left, breathing room on the right.
$wide = New-Object System.Drawing.Bitmap(310, 150)
$wg = [System.Drawing.Graphics]::FromImage($wide)
$wg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$wg.Clear([System.Drawing.Color]::Transparent)
$mark = New-Icon -size 120
$wg.DrawImage($mark, 22, 15, 120, 120)
$mark.Dispose(); $wg.Dispose()
$wide.Save((Join-Path $OutDir 'Wide310x150Logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$wide.Dispose()

# Multi-resolution .ico for the window, the taskbar and the installer.
$icoPath = Join-Path $OutDir 'app.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($s in $sizes) {
    $b = New-Icon -size $s -padded ($s -ge 32)
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , $ms.ToArray()
    $ms.Dispose(); $b.Dispose()
}

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
for ($i = 0; $i -lt $images.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$images[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

# The app project keeps its own copy, so a plain build has the icon too.
$appAssets = Join-Path (Split-Path $PSScriptRoot) 'src\CallKlingel.App\Assets'
if (Test-Path $appAssets) {
    Get-ChildItem $OutDir -Filter *.png | Copy-Item -Destination $appAssets -Force
    Copy-Item $icoPath (Join-Path $appAssets 'app.ico') -Force
    Write-Host "Icons auch nach src\CallKlingel.App\Assets kopiert" -ForegroundColor DarkGray
}

Write-Host "Icons erzeugt in $OutDir" -ForegroundColor Green
