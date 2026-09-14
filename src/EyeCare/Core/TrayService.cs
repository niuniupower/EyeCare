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
    private readonly ToolStripMenuItem _miMode;
    private readonly ToolStripMenuItem _miBreak;
    private readonly ToolStripMenuItem _miAutoStart;
    private readonly ToolStripMenuItem _miTempMenu;
    private Icon _currentIcon;

    public event Action? OpenSettings;
    public event Action? BreakNow;
    public event Action? ExitRequested;
    public event Action? FilterToggled;
    public event Action? AutoStartToggled;
    public event Action? FilterShortcutSelected;

    public TrayService(AppSettings settings)
    {
        _settings = settings;
        _currentIcon = MakeIcon(settings.FilterEnabled);

        _icon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = "暮瞳 DuskEye",
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

        // 色调模式:色温 / 护眼绿 / 暗房 —— 不必打开主界面即可一键切换
        _miMode = new ToolStripMenuItem("色调模式");
        foreach (var (mode, label) in new[]
                 {
                     ("temperature", "🌅  色温(暖色)"),
                     ("green", "🌿  护眼绿"),
                     ("darkroom", "🟥  暗房")
                 })
        {
            var item = new ToolStripMenuItem(label) { Tag = mode };
            item.Click += (_, _) =>
            {
                if (settings.FilterMode == mode) return;
                settings.FilterMode = mode;
                FilterShortcutSelected?.Invoke();
            };
            _miMode.DropDownItems.Add(item);
        }

        _miBreak = new ToolStripMenuItem("立即休息") { Enabled = settings.BreakEnabled };
        _miBreak.Click += (_, _) => BreakNow?.Invoke();

        // 色温快捷切换(与 f.lux 官方预设一致;点选后自动切回色温模式)
        _miTempMenu = new ToolStripMenuItem("色温预设");
        foreach (var t in new[] { 1900, 2300, 2700, 3400, 4200, 4800, 5500, 6500 })
        {
            var item = new ToolStripMenuItem($"{t} K") { Tag = (double)t };
            item.Click += (_, _) =>
            {
                _settings.FilterMode = "temperature";
                _settings.ColorTemperature = (double)item.Tag!;
                FilterShortcutSelected?.Invoke();
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

        // 主界面入口:置顶到菜单最上方并加粗,单击托盘图标同样直达
        var miSettings = new ToolStripMenuItem("打开主界面");
        miSettings.Font = new Font(miSettings.Font, System.Drawing.FontStyle.Bold);
        miSettings.Click += (_, _) => OpenSettings?.Invoke();

        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += (_, _) => ExitRequested?.Invoke();

        _icon.ContextMenuStrip = new ContextMenuStrip();
        _icon.ContextMenuStrip.Items.AddRange(
        [
            miSettings,
            new ToolStripSeparator(),
            _miFilter,
            _miMode,
            _miTempMenu,
            new ToolStripSeparator(),
            _miBreak,
            new ToolStripSeparator(),
            _miAutoStart,
            new ToolStripSeparator(),
            miExit
        ]);

        // 单击 = 打开(并前置)主界面;双击兼容保留
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenSettings?.Invoke();
        };
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

        foreach (ToolStripMenuItem it in _miMode.DropDownItems)
            it.Checked = settings.FilterMode == (string)it.Tag!;

        foreach (ToolStripMenuItem it in _miTempMenu.DropDownItems)
            it.Checked = settings.FilterMode == "temperature" && (double)it.Tag! == settings.ColorTemperature;

        string tip = $"暮瞳 DuskEye · 滤光{(settings.FilterEnabled ? "开" : "关")}\n{status}";
        if (_icon.Text != tip && tip.Length <= 63)
            _icon.Text = tip;
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(4000);
    }

    /// <summary>托盘图标:与主图标同源的「D」花押;滤光开启=琥珀渐变,关闭=灰色</summary>
    private static Icon MakeIcon(bool filterOn)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float s = 32f / 512f;

            // 暮色圆角方底
            using (var bgPath = new System.Drawing.Drawing2D.GraphicsPath())
            {
                float m = 14 * s, r = 110 * s, w = 32 - 2 * m;
                bgPath.AddArc(m, m, 2 * r, 2 * r, 180, 90);
                bgPath.AddArc(m + w - 2 * r, m, 2 * r, 2 * r, 270, 90);
                bgPath.AddArc(m + w - 2 * r, m + w - 2 * r, 2 * r, 2 * r, 0, 90);
                bgPath.AddArc(m, m + w - 2 * r, 2 * r, 2 * r, 90, 90);
                bgPath.CloseFigure();
                Brush bg = filterOn
                    ? new System.Drawing.Drawing2D.LinearGradientBrush(
                        new Rectangle(0, 0, 32, 32),
                        Color.FromArgb(255, 23, 18, 37), Color.FromArgb(255, 11, 12, 16), 45f)
                    : new SolidBrush(Color.FromArgb(255, 34, 37, 46));
                g.FillPath(bg, bgPath);
                bg.Dispose();
            }

            // 「D」花押:外轮廓 + 圆孔负空间(托盘小尺寸下月牙缝看不清,用圆孔)
            using (var d = new System.Drawing.Drawing2D.GraphicsPath())
            {
                d.FillMode = System.Drawing.Drawing2D.FillMode.Alternate;
                d.StartFigure();
                d.AddBezier(134 * s, 122 * s, 170 * s, 106 * s, 226 * s, 104 * s, 276 * s, 126 * s);
                d.AddArc((276 - 130) * s, (256 - 130) * s, 260 * s, 260 * s, -90, 180);
                d.AddBezier(276 * s, 386 * s, 226 * s, 408 * s, 170 * s, 406 * s, 134 * s, 382 * s);
                d.AddBezier(134 * s, 382 * s, 121 * s, 298 * s, 121 * s, 206 * s, 134 * s, 122 * s);
                d.CloseFigure();
                d.AddEllipse((278 - 66) * s, (256 - 66) * s, 132 * s, 132 * s);

                Brush glyph = filterOn
                    ? new System.Drawing.Drawing2D.LinearGradientBrush(
                        new Rectangle(0, 0, 32, 32),
                        Color.FromArgb(255, 255, 243, 217), Color.FromArgb(255, 255, 138, 61), 90f)
                    : new SolidBrush(Color.FromArgb(235, 154, 160, 172));
                g.FillPath(glyph, d);
                glyph.Dispose();
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
