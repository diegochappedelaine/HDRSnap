# Regenere HDRSnap.ico. Usage :
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 HDRSnap.ico [apercu.png]
# Les couleurs du logo sont les deux degradés ci-dessous (plaque, puis disque).

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function New-RoundedPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $p.AddArc([float]$x, [float]$y, [float]$d, [float]$d, 180, 90)
  $p.AddArc([float]($x + $w - $d), [float]$y, [float]$d, [float]$d, 270, 90)
  $p.AddArc([float]($x + $w - $d), [float]($y + $h - $d), [float]$d, [float]$d, 0, 90)
  $p.AddArc([float]$x, [float]($y + $h - $d), [float]$d, [float]$d, 90, 90)
  $p.CloseFigure()
  return $p
}

# Plaque sombre + disque chaud : une seule forme, identique a toutes les tailles,
# donc rien a simplifier en dessous de 32 px.
function New-HdrSnapBitmap {
  param([int]$S)

  $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)

  $pad  = $S * 0.015
  $side = $S - 2 * $pad
  $plate = New-RoundedPath $pad $pad $side $side ($S * 0.22)
  $rect = New-Object System.Drawing.RectangleF([float]$pad, [float]$pad, [float]$side, [float]$side)
  $pb = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, (C '#2B3160'), (C '#0A0C16'), 45.0)
  $g.FillPath($pb, $plate)
  $pb.Dispose()
  $plate.Dispose()

  $r = $S * 0.30
  $cx = $S / 2.0
  $cy = $S / 2.0
  $disc = New-Object System.Drawing.RectangleF([float]($cx - $r), [float]($cy - $r), [float]($r * 2), [float]($r * 2))
  $db = New-Object System.Drawing.Drawing2D.LinearGradientBrush($disc, (C '#FFF6DC'), (C '#E09A4B'), 90.0)
  $g.FillEllipse($db, $disc)
  $db.Dispose()

  $g.Dispose()
  return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
  $w = $bmp.Width; $h = $bmp.Height
  $data = $bmp.LockBits(
    (New-Object System.Drawing.Rectangle(0, 0, $w, $h)),
    [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
    [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $stride = $data.Stride
  $buf = New-Object byte[] ($stride * $h)
  [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
  $bmp.UnlockBits($data)

  $maskStride = [int][Math]::Floor(($w + 31) / 32) * 4
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
  $bw.Write([uint16]1);  $bw.Write([uint16]32); $bw.Write([uint32]0)
  $bw.Write([uint32]($w * $h * 4 + $maskStride * $h))
  $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
  for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($buf, $y * $stride, $w * 4) }   # DIB = bas vers haut
  $bw.Write((New-Object byte[] ($maskStride * $h)))                             # masque AND inutilise (alpha 32bpp)
  $bw.Flush()
  return ,$ms.ToArray()   # virgule : empeche PowerShell de derouler le byte[]
}

# 16=100%, 20=125%, 24=150%, 28=175%, 32=200%, 40=250%, 48/64/256 pour le shell.
# Sans la taille exacte, Windows etire la plus proche et l icone bave.
$sizes = @(16, 20, 24, 28, 32, 40, 48, 64, 256)
$bitmaps = @{}
$blobs = @()
foreach ($s in $sizes) {
  $b = New-HdrSnapBitmap -S $s
  $bitmaps[$s] = $b
  # DIB partout : System.Drawing.Icon ignore les entrees PNG, il ne verrait pas le 256
  $blobs += ,@($s, (Get-DibBytes $b))
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$blobs.Count)
$offset = 6 + 16 * $blobs.Count
foreach ($e in $blobs) {
  $sz = $e[0]; $bytes = $e[1]
  $dim = 0
  if ($sz -lt 256) { $dim = $sz }   # 0 = 256 dans un ICONDIRENTRY
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$bytes.Length); $w.Write([uint32]$offset)
  $offset += $bytes.Length
}
foreach ($e in $blobs) { $w.Write([byte[]]$e[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes($args[0], $out.ToArray())
Write-Output ("ico: {0} octets, {1} images" -f $out.Length, $blobs.Count)

if ($args.Count -lt 2) { return }   # apercu optionnel
$prevW = 560; $prevH = 300
$pv = New-Object System.Drawing.Bitmap($prevW, $prevH)
$pg = [System.Drawing.Graphics]::FromImage($pv)
$pg.FillRectangle((New-Object System.Drawing.SolidBrush((C '#F3F3F3'))), 0, 0, $prevW, ($prevH / 2))
$pg.FillRectangle((New-Object System.Drawing.SolidBrush((C '#202020'))), 0, ($prevH / 2), $prevW, ($prevH / 2))
$x = 280
foreach ($s in @(16, 24, 32, 48)) {
  $pg.DrawImage($bitmaps[$s], $x, (70 - $s / 2), $s, $s)
  $pg.DrawImage($bitmaps[$s], $x, (220 - $s / 2), $s, $s)
  $x += $s + 40
}
$pg.DrawImage($bitmaps[256], 20, 15, 120, 120)
$pg.DrawImage($bitmaps[256], 20, 165, 120, 120)
$pg.Dispose()
$pv.Save($args[1], [System.Drawing.Imaging.ImageFormat]::Png)
