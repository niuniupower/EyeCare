# 生成「暮瞳 DuskEye」应用图标
# 设计:暮色深底圆角方 + 琥珀渐变的「D」花押(负空间为一弯新月)
#      + 左上书法收锋 + 暮星。字母来自英文名 DuskEye,月牙呼应「暮」。
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $s = $size / 512.0

    # ── 圆角方底:暮色对角渐变(靛夜 → 近黑) ──
    $r = 110 * $s
    $m = 14 * $s
    $w = $size - 2*$m
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, 2*$r, 2*$r, 180, 90)
    $path.AddArc($m+$w-2*$r, $m, 2*$r, 2*$r, 270, 90)
    $path.AddArc($m+$w-2*$r, $m+$w-2*$r, 2*$r, 2*$r, 0, 90)
    $path.AddArc($m, $m+$w-2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $bgGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0,0)), (New-Object System.Drawing.Point($size,$size)),
        [System.Drawing.Color]::FromArgb(255, 23, 18, 37),
        [System.Drawing.Color]::FromArgb(255, 11, 12, 16))
    $g.FillPath($bgGrad, $path)

    # ── 字形后方一团暖光 ──
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse((52*$s), (82*$s), (408*$s), (372*$s))
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glow.CenterColor = [System.Drawing.Color]::FromArgb(42, 255, 231, 176)
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 231, 176))
    $g.FillPath($glow, $glowPath)

    # ── 「D」花押:外轮廓 + 负空间(evenodd 挖空)──
    #   整体 -0.05 前倾(斜体势,飘逸);≥48px 负空间 = 新月,≤32px = 圆孔
    $mtx = New-Object System.Drawing.Drawing2D.Matrix
    $mtx.Shear(-0.05, 0)
    $g.Transform = $mtx

    $d = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
    $d.StartFigure()
    $d.AddBezier((134*$s),(122*$s), (170*$s),(106*$s), (226*$s),(104*$s), (276*$s),(126*$s))
    $d.AddArc((276-130)*$s, (256-130)*$s, 260*$s, 260*$s, -90, 180)
    $d.AddBezier((276*$s),(386*$s), (226*$s),(408*$s), (170*$s),(406*$s), (134*$s),(382*$s))
    $d.AddBezier((134*$s),(382*$s), (121*$s),(298*$s), (121*$s),(206*$s), (134*$s),(122*$s))
    $d.CloseFigure()
    $d.StartFigure()
    if ($size -ge 48) {
        # 月牙缝负空间:两圆相减出的细新月,贴着碗内壁 —— 字形保持整块「D」,
        # 缝的 belly 朝右、双角朝左(托盘小尺寸下缝会收没,退化为实心 D,依然清晰)
        $d.AddArc(184*$s, 164*$s, 184*$s, 184*$s, -116.6, 233.2)
        $d.AddArc(168*$s, 172*$s, 168*$s, 168*$s, 101.9, -203.8)
    } else {
        $d.AddEllipse((278-66)*$s, (256-66)*$s, 132*$s, 132*$s)
    }
    $d.CloseFigure()

    $glyphGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, [int](100*$s))), (New-Object System.Drawing.Point(0, [int](420*$s))),
        [System.Drawing.Color]::FromArgb(255, 255, 243, 217),
        [System.Drawing.Color]::FromArgb(255, 255, 138, 61))
    $glyphGrad.InterpolationColors = New-Object System.Drawing.Drawing2D.ColorBlend
    $glyphGrad.InterpolationColors.Colors = @(
        [System.Drawing.Color]::FromArgb(255, 255, 243, 217),
        [System.Drawing.Color]::FromArgb(255, 255, 196, 106),
        [System.Drawing.Color]::FromArgb(255, 255, 138, 61))
    $glyphGrad.InterpolationColors.Positions = @(0.0, 0.45, 1.0)
    $g.FillPath($glyphGrad, $d)

    # ── 暮星:一颗四角星 + 两粒小星(大尺寸) ──
    if ($size -ge 48) {
        $cx = 392*$s; $cy = 84*$s; $a = 28*$s; $b = 7.8*$s
        $star = New-Object System.Drawing.Drawing2D.GraphicsPath
        $star.AddPolygon(@(
            (New-Object System.Drawing.PointF(($cx), ($cy-$a))),
            (New-Object System.Drawing.PointF(($cx+$b), ($cy-$b))),
            (New-Object System.Drawing.PointF(($cx+$a), ($cy))),
            (New-Object System.Drawing.PointF(($cx+$b), ($cy+$b))),
            (New-Object System.Drawing.PointF(($cx), ($cy+$a))),
            (New-Object System.Drawing.PointF(($cx-$b), ($cy+$b))),
            (New-Object System.Drawing.PointF(($cx-$a), ($cy))),
            (New-Object System.Drawing.PointF(($cx-$b), ($cy-$b)))))
        $starBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(242, 255, 231, 176))
        $g.FillPath($starBrush, $star)
        $starBrush.Dispose()

        $dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(153, 255, 231, 176))
        $g.FillEllipse($dot, (347*$s), (133*$s), (10*$s), (10*$s))
        $dot2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(115, 255, 231, 176))
        $g.FillEllipse($dot2, (145*$s), (327*$s), (9*$s), (9*$s))
        $dot.Dispose(); $dot2.Dispose()
    }

    $g.Dispose()
    return $bmp
}

$dir = "D:\PRJ\EyeCare\src\EyeCare\Assets"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$sizes = @(256, 64, 48, 32, 24, 16)
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($sz -eq 256) {
        $bmp.Save("$dir\icon-preview.png", [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $pngs += ,@($sz, $ms.ToArray())
    $bmp.Dispose()
    $ms.Dispose()
}

# ── 打包多尺寸 ICO(PNG-in-ICO,vista+) ──
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($entry in $pngs) {
    $sz = $entry[0]; $data = $entry[1]
    $dim = $(if ($sz -ge 256) { 0 } else { $sz })
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($entry in $pngs) { $bw.Write($entry[1]) }
$bw.Flush()

[System.IO.File]::WriteAllBytes("$dir\icon.ico", $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host "OK icon.ico: $((Get-Item "$dir\icon.ico").Length) bytes"
