Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()

foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode       = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality  = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode   = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode     = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint   = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # ---- Rounded-square gradient background ----
    $pad    = [Math]::Max(1, [int]($sz * 0.03))
    $radius = [Math]::Max(2, [int]($sz * 0.22))
    $x = $pad; $y = $pad; $w = $sz - 2*$pad; $h = $sz - 2*$pad

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x,                  $y,                  $radius, $radius, 180, 90)
    $path.AddArc($x + $w - $radius,   $y,                  $radius, $radius, 270, 90)
    $path.AddArc($x + $w - $radius,   $y + $h - $radius,   $radius, $radius,   0, 90)
    $path.AddArc($x,                  $y + $h - $radius,   $radius, $radius,  90, 90)
    $path.CloseFigure()

    $brushRect = New-Object System.Drawing.RectangleF(0, 0, [float]$sz, [float]$sz)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $brushRect,
        [System.Drawing.Color]::FromArgb(255, 30, 58, 138),    # #1E3A8A deep indigo
        [System.Drawing.Color]::FromArgb(255,  8, 145, 178),   # #0891B2 teal
        135.0)
    $g.FillPath($brush, $path)

    # Subtle top highlight
    $hlRect = New-Object System.Drawing.RectangleF(0, 0, [float]$sz, [float]($sz * 0.55))
    $hlBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $hlRect,
        [System.Drawing.Color]::FromArgb(48, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0,  255, 255, 255),
        90.0)
    $clip = $g.Clip
    $g.SetClip($path)
    $g.FillRectangle($hlBrush, $hlRect)
    $g.Clip = $clip

    # ---- Magnifying glass ----
    $white       = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
    $lensStroke  = [Math]::Max(1.0, $sz * 0.085)
    $penLens = New-Object System.Drawing.Pen($white, [float]$lensStroke)
    $penLens.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penLens.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    $lensCX = $sz * 0.44
    $lensCY = $sz * 0.44
    $lensR  = $sz * 0.235
    $g.DrawEllipse($penLens, [float]($lensCX - $lensR), [float]($lensCY - $lensR), [float]($lensR * 2), [float]($lensR * 2))

    # Handle
    $penHandle = New-Object System.Drawing.Pen($white, [float]($lensStroke * 1.15))
    $penHandle.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penHandle.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $hx1 = $lensCX + $lensR * 0.72
    $hy1 = $lensCY + $lensR * 0.72
    $hx2 = $sz * 0.82
    $hy2 = $sz * 0.82
    $g.DrawLine($penHandle, [float]$hx1, [float]$hy1, [float]$hx2, [float]$hy2)

    # ---- Down arrow inside lens (Dive cue) ----
    $arrowStroke = [Math]::Max(1.0, $sz * 0.075)
    $penA = New-Object System.Drawing.Pen($white, [float]$arrowStroke)
    $penA.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penA.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    $ax    = $lensCX
    $ayTop = $lensCY - $lensR * 0.55
    $ayBot = $lensCY + $lensR * 0.50
    $g.DrawLine($penA, [float]$ax, [float]$ayTop, [float]$ax, [float]$ayBot)

    $chev = $lensR * 0.42
    $g.DrawLine($penA, [float]($ax - $chev), [float]($ayBot - $chev), [float]$ax, [float]$ayBot)
    $g.DrawLine($penA, [float]($ax + $chev), [float]($ayBot - $chev), [float]$ax, [float]$ayBot)

    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@($sz, $ms.ToArray())
    $bmp.Dispose()
}

# ---- Pack PNGs into an .ico container ----
$ico = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter($ico)

$bw.Write([UInt16]0)                    # reserved
$bw.Write([UInt16]1)                    # type = icon
$bw.Write([UInt16]$pngs.Count)

$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $size = [int]$p[0]
    $data = [byte[]]$p[1]
    $wh   = if ($size -ge 256) { 0 } else { $size }
    $bw.Write([byte]$wh)                # width
    $bw.Write([byte]$wh)                # height
    $bw.Write([byte]0)                  # color count
    $bw.Write([byte]0)                  # reserved
    $bw.Write([UInt16]1)                # planes
    $bw.Write([UInt16]32)               # bit count
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) {
    $bw.Write([byte[]]$p[1])
}

$outDir = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path $outDir 'app.ico'
[System.IO.File]::WriteAllBytes($outPath, $ico.ToArray())
$bw.Close()

Write-Host "Wrote $outPath ($([Math]::Round((Get-Item $outPath).Length / 1KB, 1)) KB, $($pngs.Count) sizes)"
