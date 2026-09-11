# EyeCare 护眼卫士

一款免费、完全本地运行的 Windows 护眼工具,对标 [CareUEyes](https://care-eyes.com/) 的核心功能。
C# / .NET 8 WPF 原生实现,单文件 exe,无联网、无账户、无广告,代码完全属于你。

![图标](src/EyeCare/Assets/icon.ico)

## 功能

### 🔍 护眼滤光(GPU 级)
- **蓝光过滤**:色温 1500K–6500K 无级调节,通过显卡 gamma 查找表(LUT)在 GPU 层面完成,
  **截图与录屏不受影响**,文字保持锐利(与 CareUEyes 同样的技术路线,优于普通覆盖式滤光)。
- **屏幕亮度**:10%–100%。≥50% 纯 gamma 实现;低于 50% 自动叠加鼠标穿透遮罩,实现深度调暗。
- **四档预设**:🌙 夜间 3400K / 🕯 暖光 4200K / 💼 办公 5000K / 📖 阅读 5800K,点击即用。
- **多显示器**:枚举所有活动显示器逐一应用。
- **自适应恢复**:锁屏、唤醒、显示器热插拔后自动重新应用(每 5 秒兜底校验)。
- **遮罩兜底**:个别环境(如远程桌面)系统拒绝 gamma 写入时,自动降级为半透明暖色遮罩。

### ⏰ 休息提醒
- 默认 **20-20-20 法则**:每 20 分钟,远眺 6 米外 20 秒,间隔与时长均可自定义。
- **强制休息**:全屏倒计时锁定(可选允许 3 秒后跳过)。
- **智能暂停**:检测到用户离开(无键鼠输入)自动暂停计时,回来后继续。
- 手动"立即休息"按钮。

### ⚙ 通用
- **开机自启**(写入 HKCU 注册表 Run 键,仅当前用户)。
- **定时护眼模式**:指定时间段(支持跨零点)自动切换夜间色温。
- **全局快捷键**:切换滤光 `Ctrl+Alt+E`,立即休息 `Ctrl+Alt+B`(可自定义)。
- 托盘常驻:左键双击打开设置,右键菜单快速开关滤光/立即休息/退出;图标颜色指示滤光状态。

## 使用

直接运行 `EyeCare.exe`(单文件,无需安装任何运行时)。启动后驻留托盘,双击托盘图标打开设置。

| 命令行参数 | 作用 |
|---|---|
| (无参数) | 后台启动,驻留托盘 |
| `--settings` | 启动并打开设置窗口(可做桌面快捷方式) |
| `--break` | 启动并立即休息一次 |

配置与日志:`%APPDATA%\EyeCare\settings.json`、`log.txt`。

**退出与屏幕恢复**:通过托盘菜单"退出",程序会把所有显示器 gamma 恢复原始状态再退出。
(异常被强杀导致屏幕偏色时,可运行 `scripts/reset-gamma.ps1` 一键恢复。)

## 从源码构建

```powershell
# 需要 .NET 8 SDK (winget install Microsoft.DotNet.SDK.8)
cd src\EyeCare
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ..\..\dist
```

图标重新生成:`powershell -File scripts\make-icon.ps1`。

## 项目结构

```
src/EyeCare/
├── App.xaml(.cs)          应用入口:单实例、命令行、服务装配
├── Core/
│   ├── GammaController    GPU gamma LUT、多显示器枚举、色温→通道换算
│   ├── OverlayManager     遮罩兜底层(鼠标穿透、置顶)
│   ├── FilterEngine       滤光总控:gamma 优先/遮罩兜底、定时模式、自动重应用
│   ├── BreakManager       休息计时引擎、空闲检测(智能暂停)
│   ├── BreakSession(.xaml) 全屏休息窗口(倒计时/进度条/跳过)
│   ├── TrayService        托盘图标(状态变色)、菜单、气泡
│   ├── HotkeyManager      全局热键注册
│   ├── AutoStartManager   开机自启(注册表)
│   └── SettingsStore/Logger  配置持久化与日志
└── UI/
    ├── SettingsWindow(.xaml)  设置界面(滤光/休息/通用 三页)
    └── Theme.xaml             深色主题样式
```

## 与 CareUEyes 的对比

| 功能 | CareUEyes(收费) | EyeCare(本项目) |
|---|---|---|
| 蓝光过滤(GPU 级,截图不受影响) | ✅ | ✅ |
| 亮度调节 + 深度调暗 | ✅ | ✅ |
| 休息提醒 / 强制休息 / 智能暂停 | ✅ | ✅ |
| 20-20-20 法则 | ✅ | ✅ |
| 多显示器 | ✅ | ✅ |
| 开机自启 / 全局热键 / 定时模式 | ✅ | ✅ |
| 价格 | 终身许可收费 | **免费,代码自有** |
| Focus Read / Magic Window / Auto Dark | ✅ | 暂未实现(路线图) |

## 设计参考

实现过程中参考了以下公开方案的设计与算法:

- **[redshift](https://github.com/jonls/redshift)**(开源,Linux/Windows):gamma ramp 色温调整的经典实现,
  其 `colorramp.c` 使用 Ingo Thies 黑体色表 + `(输入×亮度×白点)^(1/γ)` 变换。
  本项目在其基础上改用 Bradford 色适应 + 线性光空间计算,白点更准、灰阶更干净。
- **[LightBulb](https://github.com/Tyrrrz/LightBulb)**(开源 MIT,.NET/WPF,与本项目同技术栈):
  参考其平滑 gamma 过渡、日出日落时间表、应用白名单等设计。
- **[f.lux](https://justgetflux.com/)**(闭源):参考其"色调变化永远平缓过渡"的理念
  (默认 30 分钟过渡窗口)与本项目的 0.9 秒缓动过渡同源。

## 路线图(可按需扩展)

- 短休息/长休息交替、休息提示音
- Focus Read(高亮阅读区)/ Focus Blur(背景模糊)
- 日出日落自动色温(地理位置)
- 颜色敏感应用白名单(游戏/修图时自动暂停)
- 多语言
