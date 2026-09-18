using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EyeCare.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace EyeCare.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly FilterEngine _filter;
    private readonly BreakManager _break;
    private readonly Action _reRegisterHotkeys;
    private readonly DispatcherTimer _debounce;
    /// <summary>「背景模糊」画刷重建的防抖(见构造函数:一次重建要 176~305ms,逐格重算会卡死 UI)</summary>
    private readonly DispatcherTimer _bgBlurDebounce;
    private bool _loading = true;
    private bool _allowClose;

    private static readonly SolidColorBrush AccentDot = new(Color.FromRgb(0xFF, 0xB8, 0x4D));
    private static readonly SolidColorBrush IdleDot = new(Color.FromRgb(0x6B, 0x70, 0x80));

    // 背景:缓存的图片画刷(内含已做好的像素级模糊),切换遮罩浓度时不必重算
    private ImageBrush? _bgBrush;
    /// <summary>上面这把画刷是按哪个文件算出来的 —— 换图(含换桌面壁纸)时靠它判断要不要重算</summary>
    private string _bgPath = "";
    /// <summary>实际生效的背景来源。与设置不一致说明发生了降级(读不到图片 / 读不到桌面壁纸)</summary>
    private string _effectiveBg = "matte";

    // 开机自启的说明文案:原来把完整注册表路径铺在卡片里会折行折得很难看
    private const string AutoStartOnHint = "已注册:当前用户登录后自动启动(带 --silent 参数,不弹窗打扰)。";
    private const string AutoStartOffHint = "未启用。启用后写入注册表 Run 项,登录时静默启动。";

    private const string BgDimHint = "遮罩越浓,背景越暗越护眼;模糊负责把照片细节抹平,免得抢注意力。";
    private const string BgDesktopHint = "背景就是你当前的桌面壁纸,自动读取并模糊;换过壁纸后重新打开这个窗口就会跟上。";
    private const string BgMatteHint = "内置渐变本身就很暗,不需要遮罩和模糊。";

    /// <summary>窗口被收进托盘(此时应用仍在后台运行)</summary>
    public event Action? HiddenToTray;

    public SettingsWindow(AppSettings settings, FilterEngine filter, BreakManager breakMgr, Action reRegisterHotkeys)
    {
        InitializeComponent();
        _settings = settings;
        _filter = filter;
        _break = breakMgr;
        _reRegisterHotkeys = reRegisterHotkeys;

        // 防抖只负责「落盘」,不负责「上屏」。
        // 早先这里连 _filter.Apply() 一起等 300ms,加上 Apply 内部 900ms 的缓动,
        // 拖一下滑杆要 1.2 秒之后画面才开始动 —— 用户看到的就是"拉了没反应"。
        // 现在上屏走 Sld_ValueChanged 里的 ApplyLive(τ=40ms 一阶跟随),落盘仍然防抖。
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            SettingsStore.Save(_settings);
        };

        // 「背景模糊」滑杆每动一格都要重走 BackgroundStore.BuildBrush:重新解码 + 降采样 1280 +
        // 软件高斯模糊 + RenderTargetBitmap。本机实测 blur=18 要 176ms、blur=40 要 305ms ——
        // 逐格重算会把 UI 线程占满,拖起来直接卡死,所以延后到"停下来"再重算。
        // 遮罩浓度只是改 ScrimLayer.Opacity,不需要重算画刷,保持即时反馈。
        _bgBlurDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _bgBlurDebounce.Tick += (_, _) =>
        {
            _bgBlurDebounce.Stop();
            ApplyBackground(rebuildBrush: true);
        };

        LoadValues();
        UpdateBreakStatus(_break.CountdownText, _break.StateText);   // Hero 首帧就显示真实倒计时
        _loading = false;

        Loaded += (_, _) => PlayOpenAnimation();
    }

    private void PlayOpenAnimation()
    {
        Opacity = 0;
        var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } };
        var slide = new ThicknessAnimation(new Thickness(0, 14, 0, -14), new Thickness(0), TimeSpan.FromMilliseconds(240))
        { EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(OpacityProperty, fadeIn);
        BeginAnimation(MarginProperty, slide);
    }

    private void LoadValues()
    {
        ChkFilter.IsChecked = _settings.FilterEnabled;
        SldTemp.Value = _settings.ColorTemperature;
        SldBright.Value = _settings.Brightness;
        SldGreen.Value = _settings.GreenStrength;
        ModeTemp.IsChecked = _settings.FilterMode != "green";
        ModeGreen.IsChecked = _settings.FilterMode == "green";
        UpdateModeSections();
        SyncPresetChips();
        UpdatePreview();

        ChkBreak.IsChecked = _settings.BreakEnabled;
        BoxInterval.Text = _settings.BreakIntervalMinutes.ToString();
        BoxDuration.Text = _settings.BreakDurationSeconds.ToString();
        ChkForce.IsChecked = _settings.ForceBreak;
        ChkAllowSkip.IsChecked = _settings.AllowSkip;
        ChkSmartPause.IsChecked = _settings.SmartPause;

        ChkAutoStart.IsChecked = _settings.AutoStart;
        TxtAutoStartHint.Text = _settings.AutoStart ? AutoStartOnHint : AutoStartOffHint;

        ChkSchedule.IsChecked = _settings.ScheduleEnabled;
        BoxSchedStart.Text = _settings.ScheduleStart;
        BoxSchedEnd.Text = _settings.ScheduleEnd;
        BoxSchedTemp.Text = ((int)_settings.ScheduleTemperature).ToString();

        BoxHotkeyToggle.Text = _settings.HotkeyToggle;
        BoxHotkeyBreak.Text = _settings.HotkeyBreak;

        LoadBackground();
    }

    /// <summary>外部(如托盘快捷切换)修改设置后,重新同步界面</summary>
    public void RefreshFromSettings()
    {
        _loading = true;
        LoadValues();
        _loading = false;
    }

    // ── 滤光 ──

    private void ChkFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.FilterEnabled = ChkFilter.IsChecked == true;
        UpdatePreview();          // 摘要与侧栏状态胶囊要跟着开关变
        PersistAndApply();
    }

    private void Sld_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.ColorTemperature = (int)SldTemp.Value;
        _settings.Brightness = (int)SldBright.Value;
        _settings.GreenStrength = (int)SldGreen.Value;
        _settings.SelectedPreset = MatchPreset();
        SyncPresetChips();
        UpdatePreview();
        _filter.ApplyLive();   // 立刻跟手(τ=40ms 一阶跟随),不等防抖
        Debounce();            // 只把落盘推迟到"拖完停下来"
    }

    // ── 色调模式 ──

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is RadioButton { Tag: string tag })
        {
            _settings.FilterMode = tag;
            UpdateModeSections();
            UpdatePreview();
            PersistAndApply();
        }
    }

    private void UpdateModeSections()
    {
        bool green = _settings.FilterMode == "green";
        GrnSection.Visibility = green ? Visibility.Visible : Visibility.Collapsed;
        TempSection.Visibility = green ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is RadioButton { Tag: string tag })
            SldTemp.Value = double.Parse(tag); // 触发 Sld_ValueChanged 完成保存与应用
    }

    private void Scene_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is RadioButton { Tag: string tag })
        {
            var parts = tag.Split(',');
            // 两行赋值各自触发 Sld_ValueChanged → ApplyLive,值已经实时上屏了,
            // 所以这里只需要落盘,不必再走一次 900ms 的慢过渡把画面拖慢。
            SldTemp.Value = double.Parse(parts[0]);
            SldBright.Value = double.Parse(parts[1]);
            SettingsStore.Save(_settings);
        }
    }

    private string MatchPreset()
    {
        return SldTemp.Value switch
        {
            1900 => "烛光",
            2300 => "暮色",
            2700 => "白炽",
            3400 => "夜间",
            4200 => "暖光",
            4800 => "舒适",
            5500 => "日光",
            5800 => "阅读",
            _ => "自定义"
        };
    }

    private void SyncPresetChips()
    {
        double t = SldTemp.Value;
        PresetCandle.IsChecked = t == 1900;
        PresetDusk.IsChecked = t == 2300;
        PresetIncandescent.IsChecked = t == 2700;
        PresetNight.IsChecked = t == 3400;
        PresetWarm.IsChecked = t == 4200;
        PresetCozy.IsChecked = t == 4800;
        PresetSunlight.IsChecked = t == 5500;
        PresetRead.IsChecked = t == 5800;
    }

    private void UpdatePreview()
    {
        TxtTempValue.Text = $"{SldTemp.Value:0} K";
        TxtBrightValue.Text = $"{SldBright.Value:0} %";
        TxtGreenValue.Text = $"{SldGreen.Value:0} %";

        // 预览色 = 当前模式下的白点等效色,与实际滤光后的白色一致
        (byte r, byte g, byte b) rgb;
        if (_settings.FilterMode == "green")
        {
            var (kr, kg, kb) = GammaController.GreenGainsFor(SldGreen.Value);
            rgb = GammaController.GainsToColor(kr, kg, kb);
        }
        else if (_settings.FilterMode == "darkroom")
        {
            rgb = GammaController.GainsToColor(1.0, 0.08, 0.05);
        }
        else
        {
            rgb = GammaController.WhitePointColor(SldTemp.Value);
        }
        var color = Color.FromRgb(rgb.r, rgb.g, rgb.b);
        PreviewBand.Background = new SolidColorBrush(color);
        double lum = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        var fg = lum > 0.62 ? Color.FromRgb(0x1D, 0x1E, 0x24) : Colors.White;
        PreviewTitle.Foreground = new SolidColorBrush(fg);
        PreviewSub.Foreground = new SolidColorBrush(Color.FromArgb(0xD9, fg.R, fg.G, fg.B));

        UpdateStatusSummary();
    }

    /// <summary>Hero 摘要 + 侧栏状态胶囊:不开到对应页也能一眼看到当前档位</summary>
    private void UpdateStatusSummary()
    {
        string mode = _settings.FilterMode switch
        {
            "green" => "护眼绿",
            "darkroom" => "暗房",
            _ => "暖色色温"
        };

        string summary, pill;
        if (!_settings.FilterEnabled)
        {
            summary = "已关闭 · 屏幕保持原色";
            pill = "滤光已关闭";
        }
        else if (_settings.FilterMode == "green")
        {
            summary = $"护眼绿 · 绿度 {SldGreen.Value:0} % · 亮度 {SldBright.Value:0} %";
            pill = $"护眼绿 · {SldGreen.Value:0} %";
        }
        else if (_settings.FilterMode == "darkroom")
        {
            summary = $"暗房 · 亮度 {SldBright.Value:0} %";
            pill = "暗房";
        }
        else
        {
            summary = $"{mode} · {SldTemp.Value:0} K · 亮度 {SldBright.Value:0} %";
            pill = $"{mode} · {SldTemp.Value:0} K";
        }

        TxtHeroSummary.Text = summary;
        TxtRailStatus.Text = pill;

        var dot = _settings.FilterEnabled ? AccentDot : IdleDot;
        DotHero.Fill = dot;
        DotRailStatus.Fill = dot;
    }

    // ── 休息 ──

    private void ChkBreak_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.BreakEnabled = ChkBreak.IsChecked == true;
        PersistAndApply();
        _break.Sync();      // 开关一改,休息页 Hero 立刻反映"已关闭 / 距下次休息"
    }

    private void ChkForce_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ForceBreak = ChkForce.IsChecked == true;
        PersistAndApply();
    }

    private void ChkAllowSkip_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AllowSkip = ChkAllowSkip.IsChecked == true;
        PersistAndApply();
    }

    private void ChkSmartPause_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.SmartPause = ChkSmartPause.IsChecked == true;
        PersistAndApply();
    }

    private void Num_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool ok = true;
        if (sender == BoxInterval)
            ok = ApplyInt(BoxInterval, 1, 240, v => _settings.BreakIntervalMinutes = v);
        else if (sender == BoxDuration)
            ok = ApplyInt(BoxDuration, 5, 600, v => _settings.BreakDurationSeconds = v);
        if (!ok) return;
        PersistAndApply();
        if (sender == BoxInterval) _break.Sync();   // 间隔变了,倒计时基数要跟着变
    }

    private bool ApplyInt(TextBox box, int min, int max, Action<int> assign)
    {
        if (int.TryParse(box.Text.Trim(), out int v))
        {
            v = Math.Clamp(v, min, max);
            assign(v);
            box.Text = v.ToString();
            return true;
        }
        box.Text = GetCurrentValue(box).ToString();
        return false;
    }

    private double GetCurrentValue(TextBox box) => box.Name switch
    {
        nameof(BoxInterval) => _settings.BreakIntervalMinutes,
        nameof(BoxDuration) => _settings.BreakDurationSeconds,
        _ => 0
    };

    private void BreakNow_Click(object sender, RoutedEventArgs e) => _break.TriggerNow();

    public void UpdateBreakStatus(string countdown, string state)
    {
        TxtNextBreak.Text = countdown;
        TxtBreakState.Text = state;
    }

    // ── 通用 ──

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoStart = ChkAutoStart.IsChecked == true;
        AutoStartManager.Set(_settings.AutoStart);
        TxtAutoStartHint.Text = _settings.AutoStart ? AutoStartOnHint : AutoStartOffHint;
        SettingsStore.Save(_settings);
    }

    private void ChkSchedule_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ScheduleEnabled = ChkSchedule.IsChecked == true;
        PersistAndApply();
    }

    private void Schedule_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool ok = true;
        if (sender == BoxSchedStart)
            ok = ApplyTime(BoxSchedStart, v => _settings.ScheduleStart = v);
        else if (sender == BoxSchedEnd)
            ok = ApplyTime(BoxSchedEnd, v => _settings.ScheduleEnd = v);
        else if (sender == BoxSchedTemp)
            ok = ApplyInt(BoxSchedTemp, 1500, 6500, v => _settings.ScheduleTemperature = v);
        if (ok) PersistAndApply();
    }

    private bool ApplyTime(TextBox box, Action<string> assign)
    {
        if (TimeSpan.TryParseExact(box.Text.Trim(), @"hh\:mm", null, out var t))
        {
            assign(t.ToString(@"hh\:mm"));
            return true;
        }
        box.Text = box.Name == nameof(BoxSchedStart) ? _settings.ScheduleStart : _settings.ScheduleEnd;
        return false;
    }

    private void Hotkey_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender == BoxHotkeyToggle)
        {
            if (HotkeyManager.TryParse(BoxHotkeyToggle.Text, out _, out _))
            {
                _settings.HotkeyToggle = NormalizeHotkey(BoxHotkeyToggle.Text);
                BoxHotkeyToggle.Text = _settings.HotkeyToggle;
                _reRegisterHotkeys();
                SettingsStore.Save(_settings);
            }
            else BoxHotkeyToggle.Text = _settings.HotkeyToggle;
        }
        else if (sender == BoxHotkeyBreak)
        {
            if (HotkeyManager.TryParse(BoxHotkeyBreak.Text, out _, out _))
            {
                _settings.HotkeyBreak = NormalizeHotkey(BoxHotkeyBreak.Text);
                BoxHotkeyBreak.Text = _settings.HotkeyBreak;
                _reRegisterHotkeys();
                SettingsStore.Save(_settings);
            }
            else BoxHotkeyBreak.Text = _settings.HotkeyBreak;
        }
    }

    private static string NormalizeHotkey(string combo) =>
        string.Join("+", combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Length == 1 ? p.ToUpperInvariant() : p));

    private void Num_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            Keyboard.ClearFocus();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    private void PersistAndApply()
    {
        SettingsStore.Save(_settings);
        _filter.Apply();
    }

    private void Debounce()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    // ── 外观 / 背景 ──

    private void LoadBackground()
    {
        // 配置里的图片路径可能已经失效(手工清过目录、换了机器),这里兜一次,
        // 否则会一直渲染成空白背景,而用户根本想不到是路径问题。
        // 退回的是当前的默认背景(跟随桌面壁纸),不是内置渐变 —— 默认值已经在 AppSettings 里。
        if (_settings.BackgroundMode == "image" && !BackgroundStore.IsUsable(_settings.BackgroundImagePath))
        {
            _settings.BackgroundImagePath = "";
            _settings.BackgroundMode = "desktop";
            SettingsStore.Save(_settings);
        }

        SldDim.Value = Math.Clamp(_settings.BackgroundDim, 10, 100);
        SldBlur.Value = Math.Clamp(_settings.BackgroundBlur, 0, 40);
        TxtDimValue.Text = $"{SldDim.Value:0} %";
        TxtBlurValue.Text = $"{SldBlur.Value:0} px";

        BgMatte.IsChecked = _settings.BackgroundMode == "matte";
        BgImageMode.IsChecked = _settings.BackgroundMode == "image";
        BgDesktopMode.IsChecked = _settings.BackgroundMode == "desktop";

        _bgBrush = null;
        _bgPath = "";
        ApplyBackground();
        UpdateBgSummary();
    }

    /// <summary>
    /// 把「外观」页的选择落到窗口背景上。
    /// <para>
    /// desktop 与 image 只差「图从哪来」—— 一个是当前桌面壁纸(每次现读),一个是用户选的图片 ——
    /// 之后完全同路:像素层模糊 → 交给 <c>BgLayer.Background</c>。
    /// 早期版本这里走的是「窗口透明 + 系统亚克力」,实测无效:分层窗口拿不到 DWM 模糊,
    /// 透出来的是<b>清晰明亮</b>的真实桌面,反而最晃眼。所以改成自己读壁纸自己模糊,结果完全可控。
    /// </para>
    /// 任何一步失败都退回内置渐变:背景读不出来时半透明面板糊在空白上是最糟的状态。
    /// </summary>
    private void ApplyBackground(bool rebuildBrush = true)
    {
        string? source = _settings.BackgroundMode switch
        {
            "image" => _settings.BackgroundImagePath,
            "desktop" => BackgroundStore.ResolveDesktopWallpaper(),
            _ => null,
        };

        if (source is null)
        {
            UseMatteBackground();
        }
        else
        {
            // 缓存要连「图从哪来」一起比:两种图片模式切来切去时,路径不同就得重算
            if (rebuildBrush || _bgBrush is null || !string.Equals(_bgPath, source, StringComparison.OrdinalIgnoreCase))
                _bgBrush = BackgroundStore.BuildBrush(source, _settings.BackgroundBlur);

            if (_bgBrush is null) UseMatteBackground();     // 图片 / 壁纸读不出来
            else
            {
                BgLayer.Background = _bgBrush;
                _bgPath = source;
                _effectiveBg = _settings.BackgroundMode;
            }
        }

        // 内置渐变本身就是暗的,再压一层遮罩只会更糊
        ScrimLayer.Opacity = _effectiveBg == "matte"
            ? 0
            : Math.Clamp(_settings.BackgroundDim, 10, 100) / 100.0;
    }

    private void UseMatteBackground()
    {
        BgLayer.Background = (System.Windows.Media.Brush)FindResource("WindowBackground");
        _bgBrush = null;
        _bgPath = "";
        _effectiveBg = "matte";
    }

    private void UpdateBgSummary()
    {
        bool matte = _effectiveBg == "matte";
        bool degraded = _effectiveBg != _settings.BackgroundMode;

        string text = _effectiveBg switch
        {
            "image" => $"自定义图片 · 遮罩 {SldDim.Value:0}% · 模糊 {SldBlur.Value:0}px",
            "desktop" => $"跟随桌面壁纸 · 遮罩 {SldDim.Value:0}% · 模糊 {SldBlur.Value:0}px",
            _ => "内置渐变 · 不跟桌面",
        };
        if (degraded)
            text = _settings.BackgroundMode == "desktop"
                ? "读不到桌面壁纸 · 已退回内置渐变"
                : "图片读取失败 · 已退回内置渐变";

        TxtBgSummary.Text = text;
        DotBg.Fill = degraded ? IdleDot : AccentDot;

        TxtBgFile.Text = _effectiveBg switch
        {
            "image" => string.IsNullOrEmpty(_settings.BackgroundImagePath)
                ? "未选择"
                : System.IO.Path.GetFileName(_settings.BackgroundImagePath),
            "desktop" => "桌面壁纸(自动读取,已模糊)",
            _ => "未选择",
        };

        // 遮罩对内置渐变无意义;模糊对两种「一张图」的背景都生效
        SldDim.IsEnabled = !matte;
        SldBlur.IsEnabled = !matte;
        BtnClearBg.IsEnabled = !string.IsNullOrEmpty(_settings.BackgroundImagePath);

        TxtBgHint.Text = _effectiveBg switch
        {
            "desktop" => BgDesktopHint,
            "image" => BgDimHint,
            _ => BgMatteHint,
        };
    }

    private void BgMode_Checked(object sender, RoutedEventArgs e)
    {
        // 构造期 LoadBackground 设 IsChecked 时会回调进来,此时控件还没全建好
        if (_loading || TxtBgSummary is null) return;

        if (sender is RadioButton rb && rb.Tag is string mode) _settings.BackgroundMode = mode;

        _bgBrush = null;                       // 换回图片模式时按当前模糊值重算
        ApplyBackground();
        UpdateBgSummary();
        SettingsStore.Save(_settings);
    }

    private void Bg_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 解析到 Slider 的 Minimum 时就会触发一次 ValueChanged,
        // 那时本卡片里的 TextBlock 还没创建 → 必须和 ValueChanged 的其它处理一样先挡住。
        if (_loading || TxtDimValue is null || TxtBlurValue is null) return;

        TxtDimValue.Text = $"{SldDim.Value:0} %";
        TxtBlurValue.Text = $"{SldBlur.Value:0} px";

        _settings.BackgroundDim = (int)SldDim.Value;
        _settings.BackgroundBlur = (int)SldBlur.Value;

        if (sender == SldBlur)
        {
            // 模糊:重算画刷很贵(176~305ms/次),防抖到停下来再算。数字先动,背景随后跟上。
            _bgBlurDebounce.Stop();
            _bgBlurDebounce.Start();
        }
        else
        {
            // 遮罩:只需改 ScrimLayer.Opacity,复用缓存画刷,即时反馈
            ApplyBackground(rebuildBrush: false);
        }
        UpdateBgSummary();
        Debounce();     // 落盘防抖
    }

    private void PickBackground_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var stored = BackgroundStore.Import(dlg.FileName);
        if (stored.Length == 0)
        {
            System.Windows.MessageBox.Show(this, "这张图片读不出来,换一张试试。", "EyeCare",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.BackgroundImagePath = stored;
        _settings.BackgroundMode = "image";

        _loading = true;                       // 别让 IsChecked 的回调在这里再应用一次
        BgImageMode.IsChecked = true;
        _loading = false;

        _bgBrush = null;
        ApplyBackground();
        UpdateBgSummary();
        SettingsStore.Save(_settings);
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        BackgroundStore.Clear();
        _settings.BackgroundImagePath = "";
        // 清掉自定义图片后回到默认背景(跟随桌面壁纸),而不是"什么都没有"的渐变
        if (_settings.BackgroundMode == "image") _settings.BackgroundMode = "desktop";

        _loading = true;
        BgDesktopMode.IsChecked = _settings.BackgroundMode == "desktop";
        BgMatte.IsChecked = _settings.BackgroundMode == "matte";
        _loading = false;

        _bgBrush = null;
        _bgPath = "";
        ApplyBackground();
        UpdateBgSummary();
        SettingsStore.Save(_settings);
    }

    private void ResetBackground_Click(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundMode = "desktop";
        _settings.BackgroundDim = 80;
        _settings.BackgroundBlur = 18;

        _loading = true;
        BgDesktopMode.IsChecked = true;
        SldDim.Value = 80;
        SldBlur.Value = 18;
        _loading = false;

        _bgBrush = null;
        _bgPath = "";
        ApplyBackground();
        UpdateBgSummary();
        SettingsStore.Save(_settings);
    }

    // ── 窗口 ──

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 解析中途 NavFilter 的 IsChecked="True" 会触发本事件,此时其他元素尚未创建
        if (FilterPanel is null || PageTitleText is null) return;

        StackPanel panel;
        string title, sub;
        if (sender == NavBreak)
        {
            panel = BreakPanel;
            title = "休息提醒";
            sub = "20-20-20 法则 · 强制休息 · 智能暂停";
        }
        else if (sender == NavLook)
        {
            panel = LookPanel;
            title = "外观";
            sub = "设置窗口的背景来源 · 护眼优先,亮部已收敛";
        }
        else if (sender == NavGeneral)
        {
            panel = GeneralPanel;
            title = "通用";
            sub = "开机启动 · 定时节律 · 全局快捷键";
        }
        else
        {
            panel = FilterPanel;
            title = "护眼滤光";
            sub = "蓝光过滤 · 护眼绿 · 暗房,三种色调一键直达";
        }

        FilterPanel.Visibility = panel == FilterPanel ? Visibility.Visible : Visibility.Collapsed;
        BreakPanel.Visibility = panel == BreakPanel ? Visibility.Visible : Visibility.Collapsed;
        LookPanel.Visibility = panel == LookPanel ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = panel == GeneralPanel ? Visibility.Visible : Visibility.Collapsed;

        PageTitleText.Text = title;
        PageSubText.Text = sub;

        panel.Opacity = 0;
        panel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    // ── 关闭 = 收进托盘 ──
    // 应用是托盘常驻型(ShutdownMode=OnExplicitShutdown),关闭窗口只隐藏、不销毁,
    // 这样再次打开是瞬时的,还能保留当前页签与滚动位置。

    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 / 系统关闭 同样只是收进托盘
        if (!_allowClose)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    /// <summary>真正退出前调用,允许窗口正常关闭(否则托盘「退出」会被取消)</summary>
    public void PrepareForExit() => _allowClose = true;

    private void HideToTray()
    {
        if (!IsVisible) return;

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null); // 先清除动画,再复位基准透明度
            Opacity = 1;
            Hide();
            HiddenToTray?.Invoke();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>从托盘唤回时前置(隐藏后 Show 不会再次触发 Loaded,这里补一次轻微动画)</summary>
    public void PlayResumeAnimation()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
            });
    }
}
