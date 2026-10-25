# Renders src\app.ico: a green tile with a white battery filled to 80%.
# Each size is drawn separately and snapped to whole pixels so the small sizes stay sharp.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 [-Preview dir]
param([string]$Preview)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Render([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'

    $m = [Math]::Round($s * 0.04)
    $tile = RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ([Math]::Max(2.5, $s * 0.22))
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $s, $s), ([Drawing.Color]::FromArgb(0x34, 0xD3, 0x7A)), ([Drawing.Color]::FromArgb(0x0B, 0x8F, 0x86))
    $g.FillPath($grad, $tile)

    $white = [Drawing.Color]::White
    $stroke = [Math]::Max(1, [Math]::Round($s * 0.065))
    $nubW = [Math]::Max(1, [Math]::Round($s * 0.06))
    $bw = [Math]::Round($s * 0.58)
    $bh = [Math]::Round($s * 0.36)
    if (($bh - $s) % 2) { $bh++ }
    $bx = [Math]::Round(($s - $bw - $nubW) / 2)
    $by = [Math]::Round(($s - $bh) / 2)

    $pen = New-Object Drawing.Pen $white, $stroke
    $half = $stroke / 2
    $g.DrawPath($pen, (RoundRect ($bx + $half) ($by + $half) ($bw - $stroke) ($bh - $stroke) ([Math]::Max(0.5, $s * 0.07))))

    $nubH = [Math]::Max(2, [Math]::Round($bh * 0.4))
    if (($bh - $nubH) % 2) { $nubH++ }
    $g.FillRectangle((New-Object Drawing.SolidBrush $white), $bx + $bw, $by + ($bh - $nubH) / 2, $nubW, $nubH)

    $gap = if ($s -le 20) { 1 } else { [Math]::Round($s * 0.045) }
    $ix = $bx + $stroke + $gap
    $iy = $by + $stroke + $gap
    $iw = $bw - 2 * ($stroke + $gap)
    $ih = $bh - 2 * ($stroke + $gap)
    $fw = [Math]::Round($iw * 0.8)
    $g.FillPath((New-Object Drawing.SolidBrush $white), (RoundRect $ix $iy $fw $ih ([Math]::Max(0.5, $s * 0.025))))
    $g.Dispose()
    $bmp
}

# PNG-compressed entries (supported by Windows for every size) keep the file small.
function Entry([Drawing.Bitmap]$bmp) {
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}

# 40 and 64 are left out: Windows scales them from 48 and 256 with no visible loss.
$sizes = 16, 20, 24, 32, 48, 256
$images = foreach ($s in $sizes) { $b = Render $s; if ($Preview) { $b.Save((Join-Path $Preview "icon$s.png")) }; , (Entry $b) }

$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i] % 256
    $w.Write([byte]$s); $w.Write([byte]$s); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot '..\src\app.ico'), $out.ToArray())
'wrote src\app.ico ({0} bytes)' -f $out.Length
