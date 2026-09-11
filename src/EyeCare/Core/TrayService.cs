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
    private readonly AppSettings _settings;
    private readonly ToolStripMenuItem _miFilter;
    private readonly ToolStripMenuItem _miBreak;
    private readonly ToolStripMenuItem _miAutoStart;
    private readonly ToolStripMenuItem _miTempMenu;
    private Icon _currentIcon;

    public event Action? OpenSettings;
    public event Action? BreakNow;
    public event Action? ExitRequested;
    public event Action? FilterToggled;
    public event Action? AutoStartToggled;
    public event Action? TemperatureShortcutSelected;

    public TrayService(AppSettings settings)
    {
        _settings = settings;
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

        // 色温快捷切换(与 f.lux 官方预设一致;点选后自动切回色温模式)
        _miTempMenu = new ToolStripMenuItem("色温切换");
        foreach (var t in new[] { 1900, 2300, 2700, 3400, 4200, 4800, 5500, 6500 })
        {
            var item = new ToolStripMenuItem($"{t} K") { Tag = (double)t };
            item.Click += (_, _) =>
            {
                _settings.FilterMode = "temperature";
                _settings.ColorTemperature = (double)item.Tag!;
                TemperatureShortcutSelected?.Invoke();
            };
            _miTempMenu.DropDownItems.Add(item);
        }

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
            _miTempMenu,
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

        foreach (ToolStripMenuItem it in _miTempMenu.DropDownItems)
            it.Checked = settings.FilterMode == "temperature" && (double)it.Tag! == settings.ColorTemperature;

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

    /// <summary>托盘图标:与主图标同款的「半落日」设计;滤光开启=琥珀色,关闭=灰色</summary>
    private static Icon MakeIcon(bool filterOn)
    {
        var sun = filterOn ? Color.FromArgb(255, 205, 130, 70) : Color.FromArgb(128, 134, 145);
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // 深色圆角方底
            using (var bgPath = new System.Drawing.Drawing2D.GraphicsPath())
            {
                bgPath.AddArc(2, 2, 10, 10, 180, 90);
                bgPath.AddArc(20, 2, 10, 10, 270, 90);
                bgPath.AddArc(20, 20, 10, 10, 0, 90);
                bgPath.AddArc(2, 20, 10, 10, 90, 90);
                bgPath.CloseFigure();
                using var bg = new SolidBrush(Color.FromArgb(32, 34, 42));
                g.FillPath(bg, bgPath);
            }

            // 半落日(地平线 y=21 以下裁掉)
            g.SetClip(new Rectangle(0, 0, 32, 21));
            using var sunBrush = new SolidBrush(sun);
            g.FillEllipse(sunBrush, 8, 6, 16, 16);
            g.ResetClip();

            // 倒影波纹两条
            using (var pen1 = new Pen(Color.FromArgb(150, sun), 2.4f))
            {
                pen1.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen1.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(pen1, 10, 25, 22, 25);
            }
            using (var pen2 = new Pen(Color.FromArgb(80, sun), 2.4f))
            {
                pen2.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen2.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(pen2, 12, 29, 20, 29);
            }
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
