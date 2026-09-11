using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace EyeCare.Core;

/// <summary>
/// 全屏遮罩窗口:置顶、鼠标穿透、不进任务栏、不抢焦点。
/// 仅当 GPU gamma 被系统拒绝时作为兜底方案(视觉近似),或用于低于 50% 的深度调光。
/// </summary>
public sealed class OverlayWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly System.Drawing.Rectangle _bounds;

    public OverlayWindow(System.Drawing.Rectangle bounds, Brush brush)
    {
        _bounds = bounds;
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = brush;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        var src = PresentationSource.FromVisual(this) as HwndSource;
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        Left = _bounds.Left / sx;
        Top = _bounds.Top / sy;
        Width = Math.Max(1, _bounds.Width / sx);
        Height = Math.Max(1, _bounds.Height / sy);
    }
}

/// <summary>管理每台显示器的两层遮罩:暖色滤层 + 黑色调光层</summary>
public sealed class OverlayManager
{
    private readonly List<OverlayWindow> _tint = new();
    private readonly List<OverlayWindow> _dim = new();

    public void Update(Color? tint, double dimAlpha)
    {
        RebuildIfNeeded();

        bool showTint = tint.HasValue && tint.Value.A > 0;
        foreach (var w in _tint)
        {
            if (showTint)
            {
                w.Background = new SolidColorBrush(tint!.Value);
                EnsureVisible(w);
            }
            else if (w.IsVisible) w.Hide();
        }

        bool showDim = dimAlpha > 0.005;
        foreach (var w in _dim)
        {
            if (showDim)
            {
                byte a = (byte)Math.Round(Math.Clamp(dimAlpha, 0, 1) * 255);
                w.Background = new SolidColorBrush(Color.FromArgb(a, 0, 0, 0));
                EnsureVisible(w);
            }
            else if (w.IsVisible) w.Hide();
        }
    }

    public void HideAll()
    {
        foreach (var w in _tint.Concat(_dim).Where(w => w.IsVisible))
            w.Hide();
    }

    private static void EnsureVisible(Window w)
    {
        if (!w.IsVisible) w.Show();
    }

    private void RebuildIfNeeded()
    {
        int screens = System.Windows.Forms.Screen.AllScreens.Length;
        if (_tint.Count == screens && _dim.Count == screens) return;

        foreach (var w in _tint.Concat(_dim)) w.Close();
        _tint.Clear();
        _dim.Clear();

        foreach (var s in System.Windows.Forms.Screen.AllScreens)
        {
            _tint.Add(new OverlayWindow(s.Bounds, Brushes.Transparent));
            _dim.Add(new OverlayWindow(s.Bounds, Brushes.Transparent));
        }
        Logger.Info($"遮罩层重建: {screens} 个显示器");
    }
}
