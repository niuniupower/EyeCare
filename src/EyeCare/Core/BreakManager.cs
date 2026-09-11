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

    public BreakManager(AppSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Tick;
    }

    public void Start() => _timer.Start();

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
            StatusChanged?.Invoke();
            return;
        }

        int idle = IdleSeconds();
        if (_settings.SmartPause && idle > 60)
        {
            NextBreakText = $"已暂停 · 检测到你离开了 {idle / 60} 分 {idle % 60} 秒";
            StatusChanged?.Invoke();
            return;
        }

        _activeSeconds++;
        int remain = _settings.BreakIntervalMinutes * 60 - _activeSeconds;
        if (remain <= 0)
        {
            _activeSeconds = 0;
            NextBreakText = "正在休息…";
            BreakTriggered?.Invoke();
        }
        else
        {
            NextBreakText = $"距下次休息 {remain / 60}:{remain % 60:D2}";
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
