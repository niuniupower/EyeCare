using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EyeCare.Core;

/// <summary>
/// 滤光总控:伽马优先,遮罩兜底;含定时模式与显示器/电源/前台窗口变化自动重应用。
/// 所有色调变化均以 ~0.9 秒缓动过渡(参考 f.lux / LightBulb 的平滑过渡设计),
/// 避免瞬间跳变带来的视觉不适。
/// 前台窗口切换时立即重写 gamma(参考 LightBulb GammaService:全屏应用切换会重置 LUT)。
/// </summary>
public sealed class FilterEngine : IDisposable
{
    // 过渡时长(毫秒)。滑块拖动与开关、模式切换统一使用。
    private const int TransitionMs = 900;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private delegate void WinEventDelegate(IntPtr hHook, uint event_, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private readonly AppSettings _settings;
    private readonly GammaController _gamma = new();
    private readonly OverlayManager _overlays = new();
    private readonly DispatcherTimer _reapply;
    private readonly DispatcherTimer _schedule;
    private readonly HwndSource _msg;
    private readonly DispatcherTimer _transition;
    private readonly IntPtr _foregroundHook;
    private readonly WinEventDelegate _foregroundHookProc; // 防 GC 回收
    private DateTime _lastForegroundApply = DateTime.MinValue;
    private string _lastKey = "";

    // 屏幕当前实际状态(线性光空间);过渡即在这组值与目标值之间插值
    private (double kr, double kg, double kb, double bright) _current = (1, 1, 1, 1);
    private (double kr, double kg, double kb, double bright) _target = (1, 1, 1, 1);
    private (double kr, double kg, double kb, double bright) _from = (1, 1, 1, 1);
    private DateTime _transitionStart = DateTime.UtcNow;
    private double _dimAlphaTarget;

    public FilterEngine(AppSettings settings)
    {
        _settings = settings;
        _gamma.RefreshMonitors();

        // 周期性重应用:锁屏/安全桌面/部分游戏会重置 gamma LUT(直接写当前值,不做过渡)
        _reapply = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _reapply.Tick += (_, _) => ApplyCurrentToHardware();

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

        _transition = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _transition.Tick += TransitionTick;

        // 前台窗口切换时重写 gamma(防抖 200ms,参考 LightBulb)
        _foregroundHookProc = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundHookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void OnForegroundChanged(IntPtr hHook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastForegroundApply).TotalMilliseconds < 200) return;
        _lastForegroundApply = now;
        ApplyCurrentToHardware();
    }

    public void Start()
    {
        _reapply.Start();
        _schedule.Start();
        Apply(force: true);
    }

    private IntPtr MsgHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_POWERBROADCAST = 0x0218;
        if (msg is WM_DISPLAYCHANGE or WM_POWERBROADCAST)
        {
            _gamma.RefreshMonitors();
            ApplyCurrentToHardware();
        }
        return IntPtr.Zero;
    }

    /// <summary>计算目标状态并发起平滑过渡。force=true 时即使设置未变也重新过渡。</summary>
    public void Apply(bool force = false)
    {
        EvaluateSchedule();
        string key = $"{_settings.FilterEnabled}|{_settings.FilterMode}|{_settings.ColorTemperature}|{_settings.Brightness}|{_settings.ScheduleActive}|{_settings.GreenStrength}";
        if (!force && key == _lastKey) return;
        _lastKey = key;

        double brightness = Math.Clamp(_settings.Brightness, 10, 100) / 100.0;
        double gammaDim = brightness >= 0.5 ? brightness : 0.5;
        double dimAlpha = brightness < 0.5 ? Math.Clamp(1 - brightness / 0.5, 0, 0.92) : 0;
        _dimAlphaTarget = 0;

        (double kr, double kg, double kb) gains;
        (byte pr, byte pg, byte pb) preview;
        string modeDesc;

        if (!_settings.FilterEnabled)
        {
            gains = (1, 1, 1); // 目标:原始状态(过渡结束后 RestoreAll 校准过的显示器)
            preview = (255, 255, 255);
            modeDesc = "已关闭";
            _overlays.Update(null, 0);
        }
        else if (_settings.FilterMode == "green")
        {
            // 护眼绿(豆沙绿)模式:白点移向 #C7EDCC,定时色温不参与
            gains = GammaController.GreenGainsFor(_settings.GreenStrength);
            preview = GammaController.GainsToColor(gains.kr, gains.kg, gains.kb);
            modeDesc = $"护眼绿 {_settings.GreenStrength:0}% → #{preview.pr:X2}{preview.pg:X2}{preview.pb:X2}";
            _dimAlphaTarget = dimAlpha;
            _overlays.Update(null, dimAlpha);
        }
        else if (_settings.FilterMode == "darkroom")
        {
            // 暗房模式(f.lux 同名):仅保留红色成分,深夜最低亮度刺激
            gains = (1.0, 0.08, 0.05);
            preview = GammaController.GainsToColor(gains.kr, gains.kg, gains.kb);
            modeDesc = $"暗房 → #{preview.pr:X2}{preview.pg:X2}{preview.pb:X2}";
            _dimAlphaTarget = dimAlpha;
            _overlays.Update(null, dimAlpha);
        }
        else
        {
            double temp = _settings.ScheduleActive ? _settings.ScheduleTemperature : _settings.ColorTemperature;
            gains = GammaController.BradfordGains(temp);
            preview = GammaController.GainsToColor(gains.kr, gains.kg, gains.kb);
            modeDesc = $"{temp:0}K → #{preview.pr:X2}{preview.pg:X2}{preview.pb:X2}";
            _dimAlphaTarget = dimAlpha;
            _overlays.Update(null, dimAlpha);
        }

        if (!GammaRampIsReliable())
        {
            // gamma 被系统拒绝的环境(如远程桌面):遮罩兜底(视觉近似,立即生效)
            byte a = (byte)Math.Clamp(Math.Round((1 - Math.Min(preview.pr / 255.0, Math.Min(preview.pg / 255.0, preview.pb / 255.0))) * 255), 0, 165);
            _overlays.Update(System.Windows.Media.Color.FromArgb(a, preview.pr, preview.pg, preview.pb), _dimAlphaTarget);
        }

        _target = (gains.kr, gains.kg, gains.kb, gammaDim);
        BeginTransition();
        Logger.Info($"滤光: {modeDesc} 亮度{brightness * 100:0}% (平滑过渡 {TransitionMs}ms)");
    }

    /// <summary>判断 gamma 通道是否可用:校验显示器句柄存在且此前写入成功过</summary>
    private bool GammaRampIsReliable()
    {
        if (_gamma.Monitors.Count == 0) return false;
        return _gamma.LastApplySucceeded;
    }

    private void BeginTransition()
    {
        _from = _current;
        _transitionStart = DateTime.UtcNow;
        _transition.Start();
    }

    private void TransitionTick(object? sender, EventArgs e)
    {
        double t = (DateTime.UtcNow - _transitionStart).TotalMilliseconds / TransitionMs;
        if (t >= 1)
        {
            _current = _target;
            _transition.Stop();
        }
        else
        {
            double eased = SmoothStep(t);
            _current = (Lerp(_from.kr, _target.kr, eased),
                        Lerp(_from.kg, _target.kg, eased),
                        Lerp(_from.kb, _target.kb, eased),
                        Lerp(_from.bright, _target.bright, eased));
        }
        ApplyCurrentToHardware();

        if (!_transition.IsEnabled && !_settings.FilterEnabled)
            _gamma.RestoreAll(); // 过渡到恒等后,一次性还原校准过的原始 LUT
    }

    /// <summary>把当前插值状态写入显卡(无过渡,过渡循环与周期重应用共用)</summary>
    private void ApplyCurrentToHardware()
    {
        if (_settings.FilterEnabled || _current != (1, 1, 1, 1))
            _gamma.ApplyGains(_current.kr, _current.kg, _current.kb, _current.bright);
        else
            _gamma.RestoreAll();
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
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
        _transition.Stop();
        _gamma.RestoreAll();
        _overlays.Update(null, 0);
        _current = (1, 1, 1, 1);
    }

    public void Dispose()
    {
        ShutdownRestore();
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        _gamma.Dispose();
        _msg.Dispose();
    }
}
