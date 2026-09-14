using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace EyeCare.Core;

/// <summary>
/// 休息提醒引擎:每秒统计"活跃秒数",触发休息。
/// 支持智能暂停(检测到用户离开时不累计)。
/// </summary>
public sealed class BreakManager
{
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private int _activeSeconds;

    public event Action? BreakTriggered;
    public event Action? StatusChanged;

    public string NextBreakText { get; private set; } = "";

    /// <summary>距下次休息的倒计时,格式 M:SS(界面 Hero 区大号显示)</summary>
    public string CountdownText { get; private set; } = "--:--";

    /// <summary>状态短语:距下次休息 / 已暂停 / 已关闭 / 正在休息</summary>
    public string StateText { get; private set; } = "距下次休息";

    public BreakManager(AppSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Tick;
        Sync();     // 先按设置铺一次文案,避免界面首帧停在 "--:--"
    }

    public void Start()
    {
        _timer.Start();
        Sync();
    }

    /// <summary>
    /// 按"当前设置 + 已累计活跃秒数"刷新展示文案,不推进计时。
    /// 用在:启动首帧、以及用户在设置页改完间隔/开关后即时反馈。
    /// </summary>
    public void Sync()
    {
        if (!_settings.BreakEnabled)
        {
            NextBreakText = "休息提醒已关闭";
            CountdownText = "--:--";
            StateText = "休息提醒已关闭";
        }
        else
        {
            int remain = Math.Max(0, _settings.BreakIntervalMinutes * 60 - _activeSeconds);
            NextBreakText = $"距下次休息 {remain / 60}:{remain % 60:D2}";
            CountdownText = $"{remain / 60}:{remain % 60:D2}";
            StateText = "距下次休息";
        }
        StatusChanged?.Invoke();
    }

    public void TriggerNow()
    {
        _activeSeconds = 0;
        BreakTriggered?.Invoke();
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (!_settings.BreakEnabled)
        {
            _activeSeconds = 0;
            NextBreakText = "休息提醒已关闭";
            CountdownText = "--:--";
            StateText = "休息提醒已关闭";
            StatusChanged?.Invoke();
            return;
        }

        int idle = IdleSeconds();
        if (_settings.SmartPause && idle > 60)
        {
            NextBreakText = $"已暂停 · 检测到你离开了 {idle / 60} 分 {idle % 60} 秒";
            CountdownText = "--:--";
            StateText = $"已暂停 · 离开 {idle / 60} 分 {idle % 60} 秒";
            StatusChanged?.Invoke();
            return;
        }

        _activeSeconds++;
        int remain = _settings.BreakIntervalMinutes * 60 - _activeSeconds;
        if (remain <= 0)
        {
            _activeSeconds = 0;
            NextBreakText = "正在休息…";
            CountdownText = "--:--";
            StateText = "正在休息…";
            BreakTriggered?.Invoke();
        }
        else
        {
            NextBreakText = $"距下次休息 {remain / 60}:{remain % 60:D2}";
            CountdownText = $"{remain / 60}:{remain % 60:D2}";
            StateText = "距下次休息";
        }
        StatusChanged?.Invoke();
    }

    public static int IdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0;
        return unchecked((int)(Environment.TickCount - (int)lii.dwTime)) / 1000;
    }
}
