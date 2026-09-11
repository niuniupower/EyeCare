using System.Windows;
using System.Windows.Threading;

namespace EyeCare.Core;

/// <summary>一次休息会话:在每台显示器上弹出一个全屏倒计时窗口</summary>
public sealed class BreakSession
{
    private readonly List<BreakWindow> _windows = new();
    private readonly DispatcherTimer _timer;
    private int _remaining;

    public int Duration { get; }
    public bool Force { get; }
    public bool AllowSkip { get; }

    public event Action? Finished;

    public BreakSession(int durationSeconds, bool force, bool allowSkip)
    {
        Duration = durationSeconds;
        Force = force;
        AllowSkip = allowSkip;
        _remaining = durationSeconds;

        foreach (var scr in System.Windows.Forms.Screen.AllScreens)
        {
            var w = new BreakWindow(this);
            w.Place(scr.Bounds);
            _windows.Add(w);
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Step();

        foreach (var w in _windows) w.Show();
        Step();
        _timer.Start();
    }

    private void Step()
    {
        _remaining--;
        foreach (var w in _windows.Where(w => w.IsLoaded))
            w.UpdateRemaining(_remaining);

        if (_remaining <= 0)
            Finish();
    }

    /// <summary>请求跳过:强制模式下需满 3 秒才允许</summary>
    public void SkipRequested()
    {
        if (!AllowSkip) return;
        if (Force && Duration - _remaining < 3) return;
        Finish();
    }

    private void Finish()
    {
        _timer.Stop();
        foreach (var w in _windows.Where(w => w.IsLoaded))
            w.Close();
        Finished?.Invoke();
    }
}
