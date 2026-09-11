using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private bool _loading = true;

    public SettingsWindow(AppSettings settings, FilterEngine filter, BreakManager breakMgr, Action reRegisterHotkeys)
    {
        InitializeComponent();
        _settings = settings;
        _filter = filter;
        _break = breakMgr;
        _reRegisterHotkeys = reRegisterHotkeys;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            SettingsStore.Save(_settings);
            _filter.Apply();
        };

        LoadValues();
        _loading = false;
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
        TxtAutoStartHint.Text = _settings.AutoStart
            ? "已写入注册表 HKCU\\...\\Run,登录后自动启动。"
            : "未启用。启用后写入 HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run。";

        ChkSchedule.IsChecked = _settings.ScheduleEnabled;
        BoxSchedStart.Text = _settings.ScheduleStart;
        BoxSchedEnd.Text = _settings.ScheduleEnd;
        BoxSchedTemp.Text = ((int)_settings.ScheduleTemperature).ToString();

        BoxHotkeyToggle.Text = _settings.HotkeyToggle;
        BoxHotkeyBreak.Text = _settings.HotkeyBreak;
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
        Debounce();
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
            SldTemp.Value = double.Parse(parts[0]);
            SldBright.Value = double.Parse(parts[1]);
            PersistAndApply();
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
    }

    // ── 休息 ──

    private void ChkBreak_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.BreakEnabled = ChkBreak.IsChecked == true;
        PersistAndApply();
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

    public void UpdateBreakStatus(string text) => TxtNextBreak.Text = text;

    // ── 通用 ──

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoStart = ChkAutoStart.IsChecked == true;
        AutoStartManager.Set(_settings.AutoStart);
        TxtAutoStartHint.Text = _settings.AutoStart
            ? "已写入注册表 HKCU\\...\\Run,登录后自动启动。"
            : "未启用。";
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

    // ── 窗口 ──

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        FilterPanel.Visibility = tag == "FilterPanel" ? Visibility.Visible : Visibility.Collapsed;
        BreakPanel.Visibility = tag == "BreakPanel" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = tag == "GeneralPanel" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
