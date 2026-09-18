using System.Text.Json.Serialization;

namespace EyeCare.Core;

public sealed class AppSettings
{
    // ── 护眼滤光 ──
    /// <summary>滤光模式:temperature = 色温(黑体轨迹) | green = 护眼绿(豆沙绿白点)</summary>
    public string FilterMode { get; set; } = "temperature";
    public bool FilterEnabled { get; set; } = true;
    public double ColorTemperature { get; set; } = 4800;
    public double Brightness { get; set; } = 100;
    public double GreenStrength { get; set; } = 100;
    public string SelectedPreset { get; set; } = "暖光";

    // ── 休息提醒 ──
    public bool BreakEnabled { get; set; } = true;
    public int BreakIntervalMinutes { get; set; } = 20;
    public int BreakDurationSeconds { get; set; } = 20;
    public bool ForceBreak { get; set; } = false;
    public bool AllowSkip { get; set; } = true;
    public bool SmartPause { get; set; } = true;

    // ── 通用 ──
    public bool AutoStart { get; set; } = false;
    public bool ScheduleEnabled { get; set; } = false;
    public string ScheduleStart { get; set; } = "22:00";
    public string ScheduleEnd { get; set; } = "07:30";
    public double ScheduleTemperature { get; set; } = 3400;
    public string HotkeyToggle { get; set; } = "Ctrl+Alt+E";
    public string HotkeyBreak { get; set; } = "Ctrl+Alt+B";

    // ── 外观 / 背景 ──
    /// <summary>
    /// 设置窗口的背景来源。
    /// desktop = 跟随桌面壁纸(默认):自动读取当前壁纸,像素层模糊 + 压暗去饱和后当背景。
    ///           注意<b>不是</b>让窗口半透明去透出桌面 —— 分层窗口拿不到 DWM 模糊,透出来的是清晰桌面;
    /// image   = 自定义图片:用户选的照片,复制到 %APPDATA%\EyeCare 下持久化;
    /// matte   = 内置哑光渐变:不跟桌面,最不刺眼。
    /// </summary>
    public string BackgroundMode { get; set; } = "desktop";
    /// <summary>自定义背景图片的落盘路径(BackgroundStore 管理,源文件删除也不受影响)</summary>
    public string BackgroundImagePath { get; set; } = "";
    /// <summary>背景遮罩浓度 %(10-100):越高背景越沉;仅 image / desktop 有意义。
    /// 注意它压不住亮度上限 —— 封顶靠 BackgroundStore 的像素层压缩(见「暗玻璃」注释)</summary>
    public int BackgroundDim { get; set; } = 80;
    /// <summary>背景模糊半径(0-40):把照片细节抹掉。10 在"看得清轮廓"与"不抢注意力"之间;
    /// 再大就重新糊成乳白光雾(旧版默认 18 的教训)</summary>
    public int BackgroundBlur { get; set; } = 10;

    /// <summary>
    /// 配置迁移版本号。老配置里没有这个字段 → 反序列化得 0 → 触发一次迁移(见 SettingsStore.Migrate)。
    /// 加这个字段是因为「改了字段的默认值」对已有配置无效:文件里存着旧默认值,会一直被读回来。
    /// </summary>
    public int ConfigRevision { get; set; } = 0;

    public bool FirstRunDone { get; set; } = false;

    /// <summary>运行时状态:当前是否处于定时模式覆盖中(不持久化)</summary>
    [JsonIgnore]
    public bool ScheduleActive { get; set; }
}
