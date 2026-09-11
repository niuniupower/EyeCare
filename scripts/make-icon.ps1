# 生成 EyeCare 多尺寸 .ico 图标(深色圆底 + 琥珀色眼睛)
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $scale = $size / 256.0

    $bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 30, 32, 40))
    $g.FillEllipse($bgBrush, 6*$scale, 6*$scale, 244*$scale, 244*$scale)

    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 245, 178, 78), (20*$scale))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($pen, 34*$scale, 66*$scale, 188*$scale, 128*$scale, 18, 144)
    $g.DrawArc($pen, 34*$scale, 66*$scale, 188*$scale, 128*$scale, 198, 144)

    $iris = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 245, 178, 78))
    $g.FillEllipse($iris, 90*$scale, 92*$scale, 76*$scale, 76*$scale)

    $pupil = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 24, 26, 32))
    $g.FillEllipse($pupil, 111*$scale, 113*$scale, 34*$scale, 34*$scale)

    $g.Dispose()
    return $bmp
}

$sizes = @(256, 64, 48, 32, 24, 16)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@($s, $ms.ToArray())
    $bmp.Dispose()
    $ms.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($entry in $pngs) {
    $s = $entry[0]; $data = $entry[1]
    $dim = $(if ($s -ge 256) { 0 } else { $s })
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($entry in $pngs) { $bw.Write($entry[1]) }
$bw.Flush()

$dir = "D:\PRJ\EyeCare\src\EyeCare\Assets"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
[System.IO.File]::WriteAllBytes("$dir\icon.ico", $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host "OK icon.ico: $((Get-Item "$dir\icon.ico").Length) bytes"
