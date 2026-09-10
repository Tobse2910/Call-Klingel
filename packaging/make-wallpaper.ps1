<#
.SYNOPSIS
    Draws the wallpaper shown on the handset illustration.

.DESCRIPTION
    A night sky over layered ridges. Rendered to a bitmap rather than drawn as XAML paths,
    because depth is what makes it read as a photograph: haze between the layers, stars
    that fade towards the horizon, a lit snow face. Vector shapes in the view gave two flat
    triangles.

    Generated rather than shipped as a stock photo - nothing to license, nothing to credit,
    and it can be regenerated at any resolution.
#>
param([string]$OutDir = "$PSScriptRoot\..\src\CallKlingel.App\Assets")

Add-Type -AssemblyName System.Drawing

$W = 400
$H = 856

$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

# --- Sky ------------------------------------------------------------------------
# Deep blue overhead easing into a warmer, lighter band at the horizon, the way a
# clear night actually looks above a mountain line.
$sky = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)),
    (New-Object System.Drawing.Point(0, [int]($H * 0.72))),
    [System.Drawing.Color]::FromArgb(255, 8, 14, 30),
    [System.Drawing.Color]::FromArgb(255, 38, 74, 122))
$g.FillRectangle($sky, 0, 0, $W, [int]($H * 0.72))
$sky.Dispose()

# Below the gradient the horizon colour simply continues. Filling this with a darker
# tone left black wedges wherever a ridge sat higher than the fill started - the ridges
# cover this area anyway, so it only has to match where they do not.
$g.FillRectangle(
    (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 38, 74, 122))),
    0, [int]($H * 0.71), $W, $H)

# --- Stars ----------------------------------------------------------------------
# Fewer and fainter towards the horizon, where the atmosphere washes them out.
$rand = New-Object System.Random(20260911)
for ($i = 0; $i -lt 190; $i++) {
    $x = $rand.Next(0, $W)
    $y = $rand.Next(0, [int]($H * 0.62))

    $hoehe = 1.0 - ($y / ($H * 0.62))
    $alpha = [int](($rand.Next(40, 210)) * (0.25 + 0.75 * $hoehe))
    $r = $rand.NextDouble() * 1.1 + 0.4

    $b = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb($alpha, 226, 238, 255))
    $g.FillEllipse($b, [single]$x, [single]$y, [single]($r * 2), [single]($r * 2))
    $b.Dispose()
}

# A few brighter ones with a soft halo, so the sky has focal points
foreach ($s in @(@(74, 120, 2.1), @(300, 86, 1.8), @(196, 210, 1.6), @(348, 266, 1.5))) {
    $halo = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb(30, 200, 224, 255))
    $g.FillEllipse($halo, [single]($s[0] - $s[2] * 4), [single]($s[1] - $s[2] * 4),
                   [single]($s[2] * 8), [single]($s[2] * 8))
    $halo.Dispose()

    $core = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb(235, 255, 255, 255))
    $g.FillEllipse($core, [single]($s[0] - $s[2]), [single]($s[1] - $s[2]),
                   [single]($s[2] * 2), [single]($s[2] * 2))
    $core.Dispose()
}

# --- Ridges ---------------------------------------------------------------------
# Three layers, each darker and lower than the one behind it. That ordering is the
# whole trick: nearer things are darker at night, and the eye reads it as distance.
function Add-Ridge($punkte, $farbe, $nebelHoehe) {
    $pfad = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pfad.AddPolygon([System.Drawing.PointF[]]$punkte)

    $b = New-Object System.Drawing.SolidBrush($farbe)
    $g.FillPath($b, $pfad)
    $b.Dispose()

    # Haze sitting on the ridge line, fading upward
    if ($nebelHoehe -gt 0) {
        $obersteY = ($punkte | Measure-Object -Property Y -Minimum).Minimum
        $nebel = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.Point(0, [int]$obersteY)),
            (New-Object System.Drawing.Point(0, [int]($obersteY + $nebelHoehe))),
            [System.Drawing.Color]::FromArgb(58, 120, 168, 220),
            [System.Drawing.Color]::FromArgb(0, 120, 168, 220))
        $g.SetClip($pfad)
        $g.FillRectangle($nebel, 0, [int]$obersteY, $W, [int]$nebelHoehe)
        $g.ResetClip()
        $nebel.Dispose()
    }

    $pfad.Dispose()
}

function P($x, $y) { New-Object System.Drawing.PointF([single]$x, [single]$y) }

# Farthest ridge: pale, high, low contrast
Add-Ridge @(
    (P 0 640), (P 52 566), (P 96 604), (P 150 522), (P 208 596),
    (P 262 548), (P 322 606), (P 400 560), (P 400 $H), (P 0 $H)
) ([System.Drawing.Color]::FromArgb(255, 34, 58, 94)) 90

# Middle ridge
Add-Ridge @(
    (P 0 700), (P 44 648), (P 88 682), (P 146 596), (P 196 660),
    (P 248 622), (P 312 676), (P 400 638), (P 400 $H), (P 0 $H)
) ([System.Drawing.Color]::FromArgb(255, 22, 40, 68)) 70

# Nearest ridge: darkest, and the one carrying the snow face
Add-Ridge @(
    (P 0 764), (P 60 712), (P 112 748), (P 172 664), (P 232 726),
    (P 296 690), (P 348 738), (P 400 706), (P 400 $H), (P 0 $H)
) ([System.Drawing.Color]::FromArgb(255, 13, 24, 42)) 46

# Lit snow on the tallest near peak - one highlight, otherwise it reads as noise
$schnee = New-Object System.Drawing.Drawing2D.GraphicsPath
$schnee.AddPolygon([System.Drawing.PointF[]]@(
    (P 172 664), (P 196 690), (P 186 700), (P 172 686), (P 160 698), (P 150 688)
))
$schneeBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(150, 664)),
    (New-Object System.Drawing.Point(196, 700)),
    [System.Drawing.Color]::FromArgb(190, 156, 194, 240),
    [System.Drawing.Color]::FromArgb(30, 120, 160, 210))
$g.FillPath($schneeBrush, $schnee)
$schneeBrush.Dispose(); $schnee.Dispose()

$g.Dispose()

$ziel = Join-Path $OutDir 'wallpaper.png'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$bmp.Save($ziel, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "Wallpaper erzeugt: $ziel" -ForegroundColor Green
