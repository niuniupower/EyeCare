<#
  把 license.txt(UTF-8)转成 MSI 许可页要的 .rtf。

  为什么要写个转换脚本:RTF 里非 ASCII 字符必须写成 \uNNNN? 转义,直接塞中文
  会随文件编码不同而乱码(Windows Installer 读 RTF 时不认 UTF-8 BOM)。
  这里统一转义,产出的 License.rtf 是纯 ASCII,放哪都不会走样。

  用法:powershell -ExecutionPolicy Bypass -File installer\make-license-rtf.ps1
#>
param(
    [string]$InFile  = (Join-Path $PSScriptRoot 'license.txt'),
    [string]$OutFile = (Join-Path $PSScriptRoot 'License.rtf')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $InFile)) { throw "找不到许可文本:$InFile" }

# 显式按 UTF-8 读,不依赖 Get-Content 的默认编码(PS 5.1 默认是 ANSI,会乱码)
$text = [System.IO.File]::ReadAllText($InFile, (New-Object System.Text.UTF8Encoding($false)))

function ConvertTo-RtfEscaped([string]$s) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $s.ToCharArray()) {
        $code = [int]$ch
        if ($ch -eq '\') { [void]$sb.Append('\\') }
        elseif ($ch -eq '{') { [void]$sb.Append('\{') }
        elseif ($ch -eq '}') { [void]$sb.Append('\}') }
        elseif ($code -lt 128) { [void]$sb.Append($ch) }
        else {
            # RTF 的 \u 参数是有符号 16 位
            $signed = if ($code -gt 32767) { $code - 65536 } else { $code }
            [void]$sb.Append('\u' + $signed + '?')
        }
    }
    return $sb.ToString()
}

$lines = $text -split "\r?\n"
$body = New-Object System.Text.StringBuilder
foreach ($line in $lines) {
    [void]$body.Append('\pard\sl240\slmult1 ')
    [void]$body.Append((ConvertTo-RtfEscaped $line))
    [void]$body.Append("\par`r`n")
}

# 第一行当标题:加粗放大一点
$titleMatch = [regex]::Match($text, '^([^\r\n]+)')
$title = $titleMatch.Value

$rtf = @"
{\rtf1\ansi\ansicpg936\deff0\uc1
{\fonttbl{\f0\fnil\fcharset134 Microsoft YaHei UI;}{\f1\fnil\fcharset134 Microsoft YaHei UI;}}
{\colortbl ;\red40\green40\blue40;\red120\green120\blue120;}
\viewkind4\pard\f0\fs18\cf1
\pard\qc\b\fs28 $(ConvertTo-RtfEscaped $title)\b0\fs18\par
\pard\par
$($body.ToString())}
"@

[System.IO.File]::WriteAllText($OutFile, $rtf, [System.Text.Encoding]::ASCII)

# 回读校验:确认没有非 ASCII 字节残留,且中文能还原
$bytes = [System.IO.File]::ReadAllBytes($OutFile)
$nonAscii = ($bytes | Where-Object { $_ -gt 127 }).Count
$back = [System.IO.File]::ReadAllText($OutFile, [System.Text.Encoding]::ASCII)
$hit = $back -match '\\u26286\?'   # 「暮」

Write-Host ("License.rtf 生成完毕:{0}" -f $OutFile)
Write-Host ("  非 ASCII 字节数 = {0} (应为 0)" -f $nonAscii)
Write-Host ("  含「暮」的 \\u 转义 = {0} (应为 True)" -f $hit)
if ($nonAscii -ne 0 -or -not $hit) { throw 'License.rtf 校验未通过' }
