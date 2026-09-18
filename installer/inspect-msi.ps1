<#
  回读已生成的 MSI 数据库,核对关键字段。

  "wix build 成功" 不等于 "包是对的" —— 版本号、安装目录名、快捷方式目标、载荷大小、
  向导文案有没有乱码,都只有从 MSI 数据库里读回来才算数。中文尤其要查:MSI 是按
  code page 存字符串的,写错了在向导里显示成方块,而构建过程一声不吭。

  注意:这里用 PowerShell 对 COM 的晚期绑定直接调方法($db.OpenView(...)),
  不要用 $obj.GetType().InvokeMember(...) 那套 —— New-Object -ComObject 出来的对象
  反射拿到的是 System.__ComObject,InvokeMember 调不到 FieldCount 之类,
  报错还是一句没有行号的 "无法对 Null 数组进行索引"。

  用法:powershell -ExecutionPolicy Bypass -File installer\inspect-msi.ps1 -Msi <路径>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Msi,
    # 期望值,给了就做断言,不符直接失败(退出码 2)
    [string]$ExpectVersion,
    [switch]$ExpectChinese
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Msi)) { throw "找不到 MSI:$Msi" }

# Columns 必须显式给:COM 记录对象上的 FieldCount 取不到值(拿回来是 null),
# 而 New-Object 'object[]' $null 会抛 "无法对 Null 数组进行索引" —— 报错还指不到出错的这一行。
function Invoke-MsiQuery {
    param($Database, [string]$Sql, [int]$Columns)
    $view = $Database.OpenView($Sql)
    # 必须 $null = 接住:COM 里返回 void 的方法经 IDispatch 适配后会把一个 null
    # 写进 PowerShell 管道,函数返回值前面就会多冒几个 null —— 调用方一取 $r[0]
    # 就是 "无法对 Null 数组进行索引",而且报错指向调用方那一行,极难定位。
    $null = $view.Execute()
    $rows = New-Object System.Collections.ArrayList
    while ($true) {
        $rec = $view.Fetch()
        if ($null -eq $rec) { break }
        $row = New-Object 'object[]' $Columns
        for ($i = 1; $i -le $Columns; $i++) { $row[$i - 1] = [string]$rec.StringData($i) }
        [void]$rows.Add($row)
    }
    $null = $view.Close()
    # 前置逗号:不然 PowerShell 会把集合摊平,单行结果退化成"一堆字符串"
    return , $rows
}

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($Msi, 0)
if ($null -eq $db) { throw '打开 MSI 数据库失败' }

$fail = @()

# ── Property 表 ─────────────────────────────────────────────
$prop = @{}
foreach ($r in (Invoke-MsiQuery $db 'SELECT `Property`,`Value` FROM Property' 2)) {
    $prop[[string]$r[0]] = [string]$r[1]
}

$len = (Get-Item -LiteralPath $Msi).Length
Write-Host ("MSI                : {0}" -f (Split-Path -Leaf $Msi))
Write-Host ("大小               : {0:N1} MB" -f ($len / 1MB))
Write-Host ("ProductName        : {0}" -f $prop['ProductName'])
Write-Host ("ProductVersion     : {0}" -f $prop['ProductVersion'])
Write-Host ("Manufacturer       : {0}" -f $prop['Manufacturer'])
Write-Host ("UpgradeCode        : {0}" -f $prop['UpgradeCode'])
Write-Host ("ProductLanguage    : {0}   (2052 = 简体中文)" -f $prop['ProductLanguage'])
Write-Host ("ALLUSERS           : {0}   (1 = 为所有用户安装)" -f $prop['ALLUSERS'])
Write-Host ("ARPPRODUCTICON     : {0}" -f $prop['ARPPRODUCTICON'])

if ($ExpectVersion -and $prop['ProductVersion'] -ne $ExpectVersion) {
    $fail += "版本号不符:MSI 里是 '$($prop['ProductVersion'])',期望 '$ExpectVersion'"
}
if (-not $prop['UpgradeCode']) { $fail += 'UpgradeCode 为空 —— 以后无法升级覆盖' }
if ($prop['ProductName'] -notmatch '暮瞳') { $fail += 'ProductName 里没有「暮瞳」,中文可能丢失' }

# ── Directory 表:安装位置 ──────────────────────────────────
Write-Host ''
Write-Host '安装位置(Directory 表):'
$dirs = @{}
foreach ($r in (Invoke-MsiQuery $db 'SELECT `Directory`,`DefaultDir` FROM Directory' 2)) {
    $dirs[[string]$r[0]] = [string]$r[1]
}
foreach ($k in @('INSTALLFOLDER', 'AppMenuFolder', 'DesktopFolder')) {
    if ($dirs.ContainsKey($k)) { Write-Host ("  {0,-16} {1}" -f $k, $dirs[$k]) }
    else { $fail += "Directory 表缺少 $k" }
}
if ($dirs['INSTALLFOLDER'] -notmatch '暮瞳') { $fail += 'INSTALLFOLDER 默认目录名里没有「暮瞳」,可能编码丢失' }

# ── Shortcut 表 ─────────────────────────────────────────────
Write-Host ''
Write-Host '快捷方式(Shortcut 表):'
$scCount = 0
foreach ($r in (Invoke-MsiQuery $db 'SELECT `Name`,`Target` FROM Shortcut' 2)) {
    Write-Host ("  {0,-16} -> {1}" -f $r[0], $r[1])
    $scCount++
}
if ($scCount -lt 2) { $fail += "快捷方式只有 $scCount 个,期望 2 个(开始菜单 + 桌面)" }

# ── File 表:载荷 ────────────────────────────────────────────
Write-Host ''
Write-Host '载荷(File 表):'
$fileCount = 0
foreach ($r in (Invoke-MsiQuery $db 'SELECT `FileName`,`FileSize` FROM File' 2)) {
    $mb = [double]$r[1] / 1MB
    Write-Host ("  {0}   {1:N1} MB" -f $r[0], $mb)
    $fileCount++
    if ($mb -lt 50) { $fail += "载荷 $($r[0]) 只有 $mb MB,不像自包含单文件" }
}
if ($fileCount -ne 1) { $fail += "载荷文件有 $fileCount 个,期望 1 个" }

# ── 中文文案(MSI code page 没设对的话这里就是乱码)──────────
Write-Host ''
Write-Host '向导文案抽样(Control 表):'
$texts = @()
foreach ($r in (Invoke-MsiQuery $db 'SELECT `Text` FROM Control' 1)) {
    $t = [string]$r[0]
    if ($t) { $texts += $t }
}
foreach ($t in ($texts | Where-Object { $_.Length -lt 50 } | Select-Object -First 6)) {
    Write-Host ("  {0}" -f $t)
}
$cnCount = @($texts | Where-Object { $_ -match '[\u4e00-\u9fa5]' }).Count
Write-Host ("  含中文的控件文案:{0} 条" -f $cnCount)
if ($ExpectChinese -and $cnCount -lt 3) {
    $fail += "向导界面里中文文案只有 $cnCount 条,疑似语言包没生效"
}

Write-Host ''
if ($fail.Count -gt 0) {
    Write-Host '校验未通过:' -ForegroundColor Red
    $fail | ForEach-Object { Write-Host "  x $_" -ForegroundColor Red }
    exit 2
}
Write-Host '校验通过:版本 / 安装目录 / 快捷方式 / 载荷 / 中文文案 全部符合预期。' -ForegroundColor Green
exit 0
