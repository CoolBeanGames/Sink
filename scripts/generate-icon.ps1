# Generates Assets/sink.ico — a kitchen sink with an iPod in the basin, in the
# Sink / Zen dark-violet design language. Pure GDI+ so it runs on a stock
# Windows box with no extra tooling.
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot '..\Assets'
$null = New-Item -ItemType Directory -Force -Path $outDir
$icoPath = Join-Path $outDir 'sink.ico'

function New-Frame([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'

    $u = $S / 256.0

    function RoundRect($x, $y, $w, $h, $r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $r * 2
        $p.AddArc($x, $y, $d, $d, 180, 90)
        $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
        $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure()
        return $p
    }

    # Background: dark panel with a soft violet glow.
    $bg = RoundRect 0 0 ($S - 1) ($S - 1) (56 * $u)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 18, 21, 28))), $bg)
    $glow = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($S, $S)),
        [System.Drawing.Color]::FromArgb(90, 139, 124, 255),
        [System.Drawing.Color]::FromArgb(0, 139, 124, 255))
    $g.FillPath($glow, $bg)

    # Faucet: neck rising then arcing over the basin.
    $steel = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 173, 179, 191)), (16 * $u)
    $steel.StartCap = 'Round'; $steel.EndCap = 'Round'
    $g.DrawArc($steel, (118 * $u), (52 * $u), (96 * $u), (96 * $u), 200, 160)
    $g.DrawLine($steel, (120 * $u), (150 * $u), (120 * $u), (196 * $u))
    # Tap handle
    $g.DrawLine($steel, (96 * $u), (150 * $u), (144 * $u), (150 * $u))

    # Basin: trapezoid bowl.
    $basin = New-Object System.Drawing.Drawing2D.GraphicsPath
    $basin.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(([single](36 * $u)), ([single](150 * $u)))),
        (New-Object System.Drawing.PointF(([single](220 * $u)), ([single](150 * $u)))),
        (New-Object System.Drawing.PointF(([single](200 * $u)), ([single](214 * $u)))),
        (New-Object System.Drawing.PointF(([single](56 * $u)), ([single](214 * $u))))
    ))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 37, 46))), $basin)
    $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 120, 128, 143)), (7 * $u)
    $g.DrawPath($rim, $basin)

    # iPod sitting in the basin: rounded rect + click wheel + screen.
    $ipod = RoundRect (98 * $u) (120 * $u) (60 * $u) (92 * $u) (14 * $u)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 244, 246, 250))), $ipod)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 20, 23, 30))),
        (RoundRect (106 * $u) (128 * $u) (44 * $u) (34 * $u) (5 * $u)))
    $wheel = New-Object System.Drawing.Drawing2D.GraphicsPath
    $wheel.AddEllipse((108 * $u), (166 * $u), (40 * $u), (40 * $u))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 139, 124, 255))), $wheel)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 244, 246, 250))),
        (122 * $u), (180 * $u), (12 * $u), (12 * $u))

    # A couple of water drops from the faucet.
    $water = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(210, 155, 200, 255))
    $g.FillEllipse($water, (150 * $u), (150 * $u), (7 * $u), (10 * $u))
    $g.FillEllipse($water, (156 * $u), (176 * $u), (5 * $u), (8 * $u))

    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256

# Each frame as an uncompressed 32bpp BGRA DIB (BITMAPINFOHEADER + pixels +
# AND mask). Universally decodable by the shell and WPF, unlike PNG frames.
function Get-Dib([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt32]40); $bw.Write([Int32]$w); $bw.Write([Int32]($h * 2))
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
    $bw.Write([UInt32]($w * $h * 4)); $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([Byte]$c.B); $bw.Write([Byte]$c.G); $bw.Write([Byte]$c.R); $bw.Write([Byte]$c.A)
        }
    }
    $maskRow = [Math]::Floor((($w + 31) / 32)) * 4
    for ($i = 0; $i -lt $maskRow * $h; $i++) { $bw.Write([Byte]0) }
    $bw.Flush()
    return $ms.ToArray()
}

$dibs = @()
foreach ($s in $sizes) {
    $f = New-Frame $s
    [byte[]]$d = Get-Dib $f
    $dibs += , $d
    $f.Dispose()
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$dibs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $dibs[$i].Length
}
foreach ($d in $dibs) { $bw.Write([byte[]]$d, 0, $d.Length) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
Write-Host "wrote $icoPath ($($ms.Length) bytes)"
