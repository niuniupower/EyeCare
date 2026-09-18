<#
  构建「暮瞳 DuskEye」安装包(MSI)。

  前置:先发布一次自包含单文件(约 2 分钟)
    cd src\EyeCare
    dotnet publish -c Release -r win-x64 --self-contained true `
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
      -o ..\..\dist\EyeCare_v<版本>_win-x64

  然后:
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1

  产物:dist\EyeCare_Setup_v<版本>_win-x64.msi
#>
[CmdletBinding()]
param(
    # 缺省从 EyeCare.csproj 读,保证版本号只有一个来源
    [string]$Version,
    [string]$SourceDir,
    [string]$OutMsi
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ── 1. 版本号 ────────────────────────────────────────────────
$csproj = Join-Path $repoRoot 'src\EyeCare\EyeCare.csproj'
if (-not $Version) {
    $xml = [xml](Get-Content -LiteralPath $csproj -Encoding UTF8)
    $Version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "无法从 $csproj 读出 <Version>" }
Write-Step "版本号 $Version(来源：$(if ($PSBoundParameters.ContainsKey('Version')) { '命令行' } else { 'EyeCare.csproj' }))"

# ── 2. 发布产物 ──────────────────────────────────────────────
if (-not $SourceDir) { $SourceDir = Join-Path $repoRoot "dist\EyeCare_v${Version}_win-x64" }
$exe = Join-Path $SourceDir 'EyeCare.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "找不到 $exe`n请先按脚本头部注释里的 dotnet publish 命令发布一次。"
}
$exeLen = (Get-Item -LiteralPath $exe).Length
Write-Step ("源文件 EyeCare.exe {0:N1} MB" -f ($exeLen / 1MB))

# 单文件发布只应有一个 exe;多出来的文件说明参数忘了带 PublishSingleFile
$extra = @(Get-ChildItem -LiteralPath $SourceDir -File | Where-Object { $_.Name -ne 'EyeCare.exe' })
if ($extra.Count -gt 0) {
    Write-Warning ("源目录里还有 {0} 个额外文件,安装包会忽略它们：{1}" -f $extra.Count, (($extra | Select-Object -ExpandProperty Name) -join ', '))
}

# ── 3. 许可页 RTF ────────────────────────────────────────────
Write-Step '生成许可页 License.rtf'
& (Join-Path $PSScriptRoot 'make-license-rtf.ps1') | Out-Host

# ── 4. 定位 wix.exe ──────────────────────────────────────────
$wix = (Get-Command wix.exe -ErrorAction SilentlyContinue)
if (-not $wix) {
    $cand = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
    if (Test-Path -LiteralPath $cand) { $wix = Get-Item -LiteralPath $cand } else { $wix = $null }
}
if (-not $wix) {
    throw "找不到 wix.exe。安装：dotnet tool install --global wix --version 5.*"
}
$wixPath = $wix.Source
Write-Step ("WiX {0}" -f (& $wixPath --version))

# ── 5. 构建 ──────────────────────────────────────────────────
if (-not $OutMsi) { $OutMsi = Join-Path $repoRoot "dist\EyeCare_Setup_v${Version}_win-x64.msi" }
if (Test-Path -LiteralPath $OutMsi) { [System.IO.File]::Delete($OutMsi) }

$icon = Join-Path $repoRoot 'src\EyeCare\Assets\icon.ico'
$rtf  = Join-Path $PSScriptRoot 'License.rtf'
$wxs  = Join-Path $PSScriptRoot 'Package.wxs'

Write-Step '编译 MSI(146MB 载荷要压一会儿)'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $wixPath build $wxs `
    -arch x64 `
    -culture zh-CN `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -d "AppVersion=$Version" `
    -d "AppSourceDir=$SourceDir" `
    -d "IconPath=$icon" `
    -d "LicenseRtf=$rtf" `
    -o $OutMsi
if ($LASTEXITCODE -ne 0) { throw "wix build 失败(退出码 $LASTEXITCODE)" }
$sw.Stop()
Write-Host ("    用时 {0:0.0} 秒" -f $sw.Elapsed.TotalSeconds)

# ── 6. ICE 静态校验 ──────────────────────────────────────────
# 这一步真的有用:它抓出过"per-machine 包里拿 HKLM 当快捷方式组件键路径"
# (ICE38/ICE43/ICE57)——构建成功、装也能装,但属于结构性错误。
Write-Step 'ICE 静态校验'
$ice = & $wixPath msi validate $OutMsi 2>&1
$ice | Out-Host
if ($LASTEXITCODE -ne 0) { throw "ICE 校验未通过(退出码 $LASTEXITCODE)" }

# ── 7. 回读 MSI 校验("wix build 成功"不等于"包是对的")──────
Write-Step '回读 MSI 数据库校验'
$msiLen = (Get-Item -LiteralPath $OutMsi).Length
Write-Host ("    压缩率 {0:P0}({1:N1} MB -> {2:N1} MB)" -f (1 - $msiLen / $exeLen), ($exeLen / 1MB), ($msiLen / 1MB))
Write-Host ''

& (Join-Path $PSScriptRoot 'inspect-msi.ps1') -Msi $OutMsi -ExpectVersion $Version -ExpectChinese
if ($LASTEXITCODE -ne 0) { throw "MSI 校验未通过(退出码 $LASTEXITCODE)" }

Write-Host ''
Write-Host ("完成:{0}" -f $OutMsi) -ForegroundColor Green
