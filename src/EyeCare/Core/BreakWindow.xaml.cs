using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace EyeCare.Core;

public partial class BreakWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly BreakSession _session;
    private System.Drawing.Rectangle _bounds;

    public BreakWindow(BreakSession session)
    {
        InitializeComponent();
        _session = session;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x12, 0x13, 0x18));

        SkipBtn.Visibility = session.AllowSkip && !session.Force ? Visibility.Visible : Visibility.Collapsed;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _session.SkipRequested();
        };
    }

    public void Place(System.Drawing.Rectangle bounds) => _bounds = bounds;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);

        var src = PresentationSource.FromVisual(this) as HwndSource;
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        Left = _bounds.Left / sx;
        Top = _bounds.Top / sy;
        Width = Math.Max(1, _bounds.Width / sx);
        Height = Math.Max(1, _bounds.Height / sy);
    }

    public void UpdateRemaining(int sec)
    {
        TimeText.Text = $"{sec / 60}:{Math.Max(0, sec % 60):D2}";
        double progress = Math.Clamp((double)(_session.Duration - sec) / _session.Duration, 0, 1);
        Ring.Width = 340 * progress;

        if (_session.Force && _session.AllowSkip && _session.Duration - sec >= 3)
            SkipBtn.Visibility = Visibility.Visible;
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => _session.SkipRequested();
}
