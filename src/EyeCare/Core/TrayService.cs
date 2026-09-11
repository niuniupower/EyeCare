using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EyeCare.Core;

/// <summary>系统托盘:图标(状态变色)、菜单、气泡提示</summary>
public sealed class TrayService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _miFilter;
    private readonly ToolStripMenuItem _miBreak;
    private readonly ToolStripMenuItem _miAutoStart;
    private Icon _currentIcon;

    public event Action? OpenSettings;
    public event Action? BreakNow;
    public event Action? ExitRequested;
    public event Action? FilterToggled;
    public event Action? AutoStartToggled;

    public TrayService(AppSettings settings)
    {
        _currentIcon = MakeIcon(settings.FilterEnabled);

        _icon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = "EyeCare 护眼卫士",
            Visible = true
        };

        _miFilter = new ToolStripMenuItem("护眼滤光") { CheckOnClick = true, Checked = settings.FilterEnabled };
        _miFilter.CheckedChanged += (_, _) =>
        {
            if (settings.FilterEnabled != _miFilter.Checked)
            {
                settings.FilterEnabled = _miFilter.Checked;
                FilterToggled?.Invoke();
            }
        };

        _miBreak = new ToolStripMenuItem("立即休息") { Enabled = settings.BreakEnabled };
        _miBreak.Click += (_, _) => BreakNow?.Invoke();

        _miAutoStart = new ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = settings.AutoStart };
        _miAutoStart.CheckedChanged += (_, _) =>
        {
            if (settings.AutoStart != _miAutoStart.Checked)
            {
                settings.AutoStart = _miAutoStart.Checked;
                AutoStartToggled?.Invoke();
            }
        };

        var miSettings = new ToolStripMenuItem("设置…");
        miSettings.Click += (_, _) => OpenSettings?.Invoke();

        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += (_, _) => ExitRequested?.Invoke();

        _icon.ContextMenuStrip = new ContextMenuStrip();
        _icon.ContextMenuStrip.Items.AddRange(
        [
            _miFilter,
            new ToolStripSeparator(),
            _miBreak,
            new ToolStripSeparator(),
            miSettings,
            _miAutoStart,
            new ToolStripSeparator(),
            miExit
        ]);

        _icon.DoubleClick += (_, _) => OpenSettings?.Invoke();
    }

    /// <summary>同步状态到菜单、气泡文字与图标颜色(每秒调用)</summary>
    public void SyncState(AppSettings settings, string status)
    {
        if (_miFilter.Checked != settings.FilterEnabled)
            _miFilter.Checked = settings.FilterEnabled;
        if (_miBreak.Enabled != settings.BreakEnabled)
            _miBreak.Enabled = settings.BreakEnabled;
        if (_miAutoStart.Checked != settings.AutoStart)
            _miAutoStart.Checked = settings.AutoStart;

        string tip = $"EyeCare 护眼卫士 · 滤光{(settings.FilterEnabled ? "开" : "关")}\n{status}";
        if (_icon.Text != tip && tip.Length <= 63)
            _icon.Text = tip;
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(4000);
    }

    private static Icon MakeIcon(bool filterOn)
    {
        var color = filterOn ? Color.FromArgb(245, 178, 78) : Color.FromArgb(128, 132, 142);
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(30, 32, 40));
            g.FillEllipse(bg, 0, 0, 31, 31);
            using var pen = new Pen(color, 2.6f);
            pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            g.DrawArc(pen, 3, 6, 26, 20, 15, 150);
            g.DrawArc(pen, 3, 6, 26, 20, 195, 150);
            using var iris = new SolidBrush(color);
            g.FillEllipse(iris, 12, 11, 8, 8);
            using var pupil = new SolidBrush(Color.FromArgb(30, 32, 40));
            g.FillEllipse(pupil, 14.5f, 13.5f, 3, 3);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        DestroyIcon(_currentIcon.Handle);
    }
}
