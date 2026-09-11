using System.Windows.Interop;
using System.Windows.Threading;

namespace EyeCare.Core;

/// <summary>
/// 滤光总控:伽马优先,遮罩兜底;含定时模式与显示器/电源变化自动重应用。
/// 亮度 ≥ 50%:纯 gamma 实现;低于 50%:gamma 压到 50% + 黑色遮罩继续加深。
/// </summary>
public sealed class FilterEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly GammaController _gamma = new();
    private readonly OverlayManager _overlays = new();
    private readonly DispatcherTimer _reapply;
    private readonly DispatcherTimer _schedule;
    private readonly HwndSource _msg;
    private string _lastKey = "";

    public FilterEngine(AppSettings settings)
    {
        _settings = settings;
        _gamma.RefreshMonitors();

        // 周期性重应用:锁屏/安全桌面/部分游戏会重置 gamma LUT
        _reapply = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _reapply.Tick += (_, _) => Apply();

        _schedule = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _schedule.Tick += (_, _) => Apply();

        // WS_POPUP 无边框,并移到屏幕外,避免显示为可见悬浮窗
        var msgParams = new HwndSourceParameters("EyeCareMsg")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            PositionX = -32000,
            PositionY = -32000
        };
        _msg = new HwndSource(msgParams);
        _msg.AddHook(MsgHook);
    }

    public void Start()
    {
        _reapply.Start();
        _schedule.Start();
        Apply();
    }

    private IntPtr MsgHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_POWERBROADCAST = 0x0218;
        if (msg is WM_DISPLAYCHANGE or WM_POWERBROADCAST)
        {
            _gamma.RefreshMonitors();
            Apply(force: true);
        }
        return IntPtr.Zero;
    }

    public void Apply(bool force = false)
    {
        EvaluateSchedule();
        string key = $"{_settings.FilterEnabled}|{_settings.FilterMode}|{_settings.ColorTemperature}|{_settings.Brightness}|{_settings.ScheduleActive}|{_settings.GreenStrength}";
        if (!force && key == _lastKey) return;
        _lastKey = key;

        if (!_settings.FilterEnabled)
        {
            _gamma.RestoreAll();
            _overlays.Update(null, 0);
            Logger.Info("滤光: 已关闭,屏幕恢复");
            return;
        }

        double brightness = Math.Clamp(_settings.Brightness, 10, 100) / 100.0;
        double gammaDim = brightness >= 0.5 ? brightness : 0.5;
        double dimAlpha = brightness < 0.5 ? Math.Clamp(1 - brightness / 0.5, 0, 0.92) : 0;

        bool gammaOk;
        (byte pr, byte pg, byte pb) preview;
        string modeDesc;

        if (_settings.FilterMode == "green")
        {
            // 护眼绿(豆沙绿)模式:白点移向 #C7EDCC,定时色温不参与
            var (kr, kg, kb) = GammaController.GreenGainsFor(_settings.GreenStrength);
            gammaOk = _gamma.ApplyGains(kr, kg, kb, gammaDim);
            preview = GammaController.GainsToColor(kr, kg, kb);
            modeDesc = $"护眼绿 {_settings.GreenStrength:0}% → #{preview.pr:X2}{preview.pg:X2}{preview.pb:X2}";
        }
        else
        {
            double temp = _settings.ScheduleActive ? _settings.ScheduleTemperature : _settings.ColorTemperature;
            gammaOk = _gamma.ApplyWhitePoint(temp, gammaDim);
            preview = GammaController.WhitePointColor(temp);
            modeDesc = $"{temp:0}K → #{preview.pr:X2}{preview.pg:X2}{preview.pb:X2}";
        }

        System.Windows.Media.Color? tint = null;
        if (!gammaOk)
        {
            byte a = (byte)Math.Clamp(Math.Round((1 - Math.Min(preview.pr / 255.0, Math.Min(preview.pg / 255.0, preview.pb / 255.0))) * 255), 0, 165);
            tint = System.Windows.Media.Color.FromArgb(a, preview.pr, preview.pg, preview.pb);
            if (brightness < 0.5) dimAlpha = Math.Clamp(1 - brightness, 0, 0.92);
        }

        _overlays.Update(tint, dimAlpha);
        Logger.Info($"滤光: {modeDesc} 亮度{brightness * 100:0}% " +
                    $"gamma={(gammaOk ? "OK" : "拒绝→遮罩兜底")} 遮罩暗度={dimAlpha:0.00}");
    }

    private void EvaluateSchedule()
    {
        if (!_settings.ScheduleEnabled)
        {
            _settings.ScheduleActive = false;
            return;
        }
        if (!TimeSpan.TryParseExact(_settings.ScheduleStart, @"hh\:mm", null, out var start) ||
            !TimeSpan.TryParseExact(_settings.ScheduleEnd, @"hh\:mm", null, out var end))
        {
            _settings.ScheduleActive = false;
            return;
        }
        var now = DateTime.Now.TimeOfDay;
        _settings.ScheduleActive = start <= end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    public void ShutdownRestore()
    {
        _reapply.Stop();
        _schedule.Stop();
        _gamma.RestoreAll();
        _overlays.Update(null, 0);
    }

    public void Dispose()
    {
        ShutdownRestore();
        _gamma.Dispose();
        _msg.Dispose();
    }
}
