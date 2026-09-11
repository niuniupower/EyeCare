using System.Text.Json.Serialization;

namespace EyeCare.Core;

public sealed class AppSettings
{
    // ── 护眼滤光 ──
    public bool FilterEnabled { get; set; } = true;
    public double ColorTemperature { get; set; } = 4800;
    public double Brightness { get; set; } = 100;
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

    public bool FirstRunDone { get; set; } = false;

    /// <summary>运行时状态:当前是否处于定时模式覆盖中(不持久化)</summary>
    [JsonIgnore]
    public bool ScheduleActive { get; set; }
}
