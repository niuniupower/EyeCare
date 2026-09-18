using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EyeCare.Core;

/// <summary>
/// 滤光总控:伽马优先,遮罩兜底;含定时模式与显示器/电源/前台窗口变化自动重应用。
/// 色调变化以「一阶迟滞」跟随目标(参考 f.lux / LightBulb 的平滑过渡设计),避免瞬间跳变带来的视觉不适:
/// 开关键与模式切换 τ=250ms(约 0.9 秒内视觉到位),拖滑杆 τ=40ms(跟手)。
/// 选一阶迟滞而不是"起点→终点缓动曲线",是因为后者在目标被连续改写时(拖动滑杆)会一直重排、追不上手指。
/// 前台窗口切换时立即重写 gamma(参考 LightBulb GammaService:全屏应用切换会重置 LUT)。
/// </summary>
public sealed class FilterEngine : IDisposable
{
    // ── 过渡模型:一阶迟滞(指数跟随)──
    // 每帧按"实际经过的时间"把当前值朝目标推进固定比例,由时间常数 τ 决定快慢。
    // 相比「起点→终点 + 缓动曲线」,它有一个决定性的好处:**目标中途改变时不需要重排动画**。
    // 原先每次取值变化都要 _from = 当前值、把计时归零,而缓动曲线起点速度为零 ——
    // 拖滑杆时取值每 16ms 变一次,等于每帧都把速度打回 0,画面就一直挪不动。
    // 一阶迟滞天然处理重定目标:滞后量≈τ×目标速度,与取值频率无关。
    //
    // τ 分两档:开关 / 模式切换要舒缓(约 0.9 秒在视觉上到位,避免瞬间跳变的不适),
    // 拖滑杆要跟手(滞后约 40ms,肉眼视为实时,又不像硬跳变那样刺眼)。
    private const double ComfortTauMs = 250;
    private const double LiveTauMs = 40;
    /// <summary>与目标差距小于此值即视为到位(线性光量级:远小于 ramp 的 1/65535 量化步长)</summary>
    private const double SettleEpsilon = 0.0008;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;

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
    /// <summary>跟随档日志限流用(拖滑杆会 60Hz 触发,不能每次都落盘)</summary>
    private DateTime _lastLiveLog = DateTime.MinValue;
    private string _lastKey = "";

    // 屏幕当前实际状态(线性光空间);过渡即在这组值与目标值之间插值
    private (double kr, double kg, double kb, double bright) _current = (1, 1, 1, 1);
    private (double kr, double kg, double kb, double bright) _target = (1, 1, 1, 1);
    /// <summary>上一帧时刻:按"实际经过的时间"推进,掉帧时不会走得太慢</summary>
    private DateTime _lastTick = DateTime.UtcNow;
    /// <summary>本轮过渡的时间常数(毫秒)。拖滑杆用 LiveTauMs,其余场景用 ComfortTauMs</summary>
    private double _tauMs = ComfortTauMs;
    /// <summary>本轮过渡是否还要做一次"写入是否真生效"的校验(见 ApplyCurrentToHardware)</summary>
    private bool _verifyNextWrite = true;
    private double _dimAlphaTarget;

    public FilterEngine(AppSettings settings)
    {
        _settings = settings;
        _gamma.RefreshMonitors();

        // 周期性重应用:锁屏/安全桌面/部分游戏会重置 gamma LUT(直接写当前值,不做过渡)
        _reapply = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _reapply.Tick += (_, _) => ApplyCurrentToHardware(verify: true, forceWrite: true);

        _schedule = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _schedule.Tick += (_, _) => Apply();

        // 纯消息辅助窗口:WS_EX_TOOLWINDOW 排除出任务栏/Alt-Tab,创建后立即隐藏;
        // 隐藏的顶层窗口仍能收到 WM_DISPLAYCHANGE / WM_POWERBROADCAST 系统广播
        var msgParams = new HwndSourceParameters("EyeCareMsg")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x08000080,         // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            PositionX = -32000,
            PositionY = -32000
        };
        _msg = new HwndSource(msgParams);
        ShowWindow(_msg.Handle, SW_HIDE);
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
        ApplyCurrentToHardware(verify: true, forceWrite: true);
    }

    public void Start()
    {
        _reapply.Start();
        _schedule.Start();
        // 先探一次「gamma 写入到底被不被系统接受」:远程桌面一类环境是静默拒绝的,
        // 而遮罩兜底必须在第一次 Apply() 里就决定开不开,不能等过渡跑完才发现(那要 900ms)。
        _gamma.ProbeWritable();
        Apply(force: true);
    }

    private IntPtr MsgHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_POWERBROADCAST = 0x0218;
        if (msg is WM_DISPLAYCHANGE or WM_POWERBROADCAST)
        {
            _gamma.RefreshMonitors();
            ApplyCurrentToHardware(verify: true, forceWrite: true);
        }
        return IntPtr.Zero;
    }

    /// <summary>计算目标状态并发起平滑过渡。force=true 时即使设置未变也重新过渡。</summary>
    public void Apply(bool force = false) => ApplyCore(force, ComfortTauMs);

    /// <summary>
    /// 拖动滑杆时的实时应用:一阶迟滞的时间常数取 40ms(≈ 2~3 帧内跟上)。
    /// 之所以不干脆"立即写死"——那样每一格都是硬跳变,拖快了会一顿一顿的;
    /// 40ms 的跟随在感官上等同于实时,又能把跳变磨平。
    /// </summary>
    public void ApplyLive() => ApplyCore(true, LiveTauMs);

    private void ApplyCore(bool force, double tauMs)
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
        BeginTransition(tauMs);
        LogApply(modeDesc, brightness, tauMs);
    }

    /// <summary>
    /// 拖滑杆时本方法会以 ~60Hz 被调用,每次都写日志等于每秒 60 次磁盘 I/O,还会把 log.txt 冲爆;
    /// 跟随档限流到每秒一条(舒缓档照常记录)。Logger 是 File.AppendAllText,不是免费的。
    /// </summary>
    private void LogApply(string modeDesc, double brightness, double tauMs)
    {
        var now = DateTime.UtcNow;
        if (tauMs <= LiveTauMs && (now - _lastLiveLog).TotalSeconds < 1) return;
        _lastLiveLog = now;
        Logger.Info($"滤光: {modeDesc} 亮度{brightness * 100:0}% (一阶跟随 τ={tauMs:0}ms)");
    }

    /// <summary>判断 gamma 通道是否可用:校验显示器句柄存在且此前写入成功过</summary>
    private bool GammaRampIsReliable()
    {
        if (_gamma.Monitors.Count == 0) return false;
        return _gamma.LastApplySucceeded;
    }

    private void BeginTransition(double tauMs)
    {
        _tauMs = tauMs;
        _lastTick = DateTime.UtcNow;
        // 本轮过渡的第一帧要校验一次「写进去的值是否真的生效」:
        // 远程桌面等环境会静默拒绝 gamma 写入,只有读回来比对才知道。
        // 逐帧校验太贵(每帧一次驱动往返),而首帧校验一次就足以判定环境是否可用。
        _verifyNextWrite = true;
        _transition.Start();
    }

    private void TransitionTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dtMs = Math.Max(1.0, (now - _lastTick).TotalMilliseconds);
        _lastTick = now;

        // 一阶迟滞:本帧朝目标推进的固定比例。用"实际经过的时间"而不是定时器的标称间隔,
        // 掉帧(拖动时很常见)时不会走得比预期慢。
        double k = 1 - Math.Exp(-dtMs / _tauMs);
        _current = (Lerp(_current.kr, _target.kr, k),
                    Lerp(_current.kg, _target.kg, k),
                    Lerp(_current.kb, _target.kb, k),
                    Lerp(_current.bright, _target.bright, k));

        bool final = MaxGap(_current, _target) <= SettleEpsilon;
        if (final)
        {
            _current = _target;      // 指数衰减永远到不了目标,最后一步直接吸附
            _transition.Stop();
        }
        // 落定的那一帧再校验一次:此时写的正好是最终值,能确认"最终状态"确实生效了
        ApplyCurrentToHardware(verify: _verifyNextWrite || final);
        _verifyNextWrite = false;

        if (final && !_settings.FilterEnabled)
            _gamma.RestoreAll(); // 过渡到恒等后,一次性还原校准过的原始 LUT
    }

    private static double MaxGap((double kr, double kg, double kb, double bright) a,
                                 (double kr, double kg, double kb, double bright) b) =>
        Math.Max(Math.Max(Math.Abs(a.kr - b.kr), Math.Abs(a.kg - b.kg)),
                 Math.Max(Math.Abs(a.kb - b.kb), Math.Abs(a.bright - b.bright)));

    /// <summary>
    /// 把当前插值状态写入显卡(无过渡,过渡循环与周期重应用共用)。
    /// <para>
    /// <paramref name="verify"/> = 写完后读回来比对(每帧都做等于每帧多一次驱动往返,拖滑杆时很贵);
    /// <paramref name="forceWrite"/> = 即使与上次写入的值完全相同也重写 —— 周期重应用必须为 true,
    /// 因为它的意义正是"锁屏/全屏游戏把 LUT 冲掉了,再写回去",被去重挡掉就失效了。
    /// </para>
    /// </summary>
    private void ApplyCurrentToHardware(bool verify = false, bool forceWrite = false)
    {
        if (_settings.FilterEnabled || _current != (1, 1, 1, 1))
            _gamma.ApplyGains(_current.kr, _current.kg, _current.kb, _current.bright, verify, forceWrite);
        else
            _gamma.RestoreAll();
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

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
