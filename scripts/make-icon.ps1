# 生成 EyeCare「半落日」应用图标
# 设计:深色圆角方底 + 琥珀渐变的半落日(地平线裁剪)+ 三条渐弱的倒影波纹
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $s = $size / 512.0

    # ── 圆角方形底:深色对角渐变 ──
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
        [System.Drawing.Color]::FromArgb(255, 27, 22, 17),
        [System.Drawing.Color]::FromArgb(255, 13, 14, 18))
    $g.FillPath($bgGrad, $path)

    # ── 太阳外发光(径向渐隐)──
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse(30*$s, 10*$s, 452*$s, 452*$s)
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glow.CenterColor = [System.Drawing.Color]::FromArgb(70, 255, 184, 77)
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 184, 77))
    $g.FillPath($glow, $glowPath)

    # ── 半落日:渐变圆,地平线(66%)以下裁掉 ──
    $oldClip = $g.Clip
    $horizon = [int](338 * $s)
    $g.SetClip((New-Object System.Drawing.Rectangle(0, 0, $size, $horizon)))
    $sunGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, [int](82*$s))), (New-Object System.Drawing.Point(0, $horizon)),
        [System.Drawing.Color]::FromArgb(255, 255, 227, 168),
        [System.Drawing.Color]::FromArgb(255, 255, 128, 56))
    $g.FillEllipse($sunGrad, 112*$s, 82*$s, 288*$s, 288*$s)
    $g.Clip = $oldClip

    # ── 倒影波纹:三条渐弱的圆角横线 ──
    $penColors = @(@(130, 255, 184, 77), @(84, 255, 184, 77), @(46, 255, 184, 77))
    $lineSpecs = @(@(144, 379, 224, 18), @(176, 421, 160, 18), @(208, 462, 96, 16))  # x,y,w,h
    for ($i = 0; $i -lt 3; $i++) {
        $c = $penColors[$i]; $sp = $lineSpecs[$i]
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($c[0], $c[1], $c[2], $c[3]), [single]($sp[3]*$s))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $y = ($sp[1] + $sp[3]/2) * $s
        $g.DrawLine($pen, $sp[0]*$s, $y, ($sp[0]+$sp[2])*$s, $y)
        $pen.Dispose()
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
