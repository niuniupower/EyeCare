using System.Threading;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using EyeCare.Core;
using EyeCare.UI;

namespace EyeCare;

public partial class App : Application
{
    private static Mutex? _mutex;

    private AppSettings _settings = new();
    private FilterEngine _filter = null!;
    private BreakManager _break = null!;
    private TrayService _tray = null!;
    private HotkeyManager _hotkeys = null!;
    private BreakSession? _session;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "EyeCare_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("EyeCare 已在运行中(请查看系统托盘)。", "EyeCare",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Logger.Info("==== EyeCare 启动 ====");

        // 全局异常日志:崩溃可诊断
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("UI 异常: " + args.Exception);
            args.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("未处理异常: " + args.ExceptionObject);

        _settings = SettingsStore.Load();
        _filter = new FilterEngine(_settings);
        _break = new BreakManager(_settings);
        _tray = new TrayService(_settings);
        _hotkeys = new HotkeyManager();

        _tray.OpenSettings += OpenSettingsWindow;
        _tray.BreakNow += StartBreak;
        _tray.ExitRequested += ExitApp;
        _tray.FilterToggled += OnSettingsChangedExternally;
        _tray.TemperatureShortcutSelected += OnSettingsChangedExternally;
        _tray.AutoStartToggled += () => AutoStartManager.Set(_settings.AutoStart);

        _break.BreakTriggered += StartBreak;
        _break.StatusChanged += () =>
        {
            _tray.SyncState(_settings, _break.NextBreakText);
            _settingsWindow?.UpdateBreakStatus(_break.NextBreakText);
        };

        _break.Start();
        _filter.Start();
        RegisterHotkeys();

        if (!_settings.FirstRunDone)
        {
            _settings.FirstRunDone = true;
            SettingsStore.Save(_settings);
            _tray.ShowBalloon("EyeCare 已在后台运行 👁",
                "双击托盘图标打开设置。默认每 20 分钟提醒你休息 20 秒。");
        }

        _tray.SyncState(_settings, _break.NextBreakText);

        // 命令行:--settings 打开设置窗口;--break 立即休息
        foreach (var arg in e.Args)
        {
            if (arg is "--settings" or "-s") OpenSettingsWindow();
            else if (arg is "--break" or "-b") StartBreak();
        }
    }

    private void OnSettingsChangedExternally()
    {
        PersistAndApply();
        _settingsWindow?.RefreshFromSettings();
    }

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        _hotkeys.Register(_settings.HotkeyToggle, () =>
        {
            _settings.FilterEnabled = !_settings.FilterEnabled;
            PersistAndApply();
        });
        _hotkeys.Register(_settings.HotkeyBreak, StartBreak);
    }

    private void PersistAndApply()
    {
        SettingsStore.Save(_settings);
        _filter.Apply();
        _tray.SyncState(_settings, _break.NextBreakText);
    }

    private void StartBreak()
    {
        if (_session != null) return;
        Logger.Info($"开始休息 ({_settings.BreakDurationSeconds}s, 强制={_settings.ForceBreak})");
        _session = new BreakSession(_settings.BreakDurationSeconds, _settings.ForceBreak, _settings.AllowSkip);
        _session.Finished += () =>
        {
            _session = null;
            Logger.Info("休息结束");
        };
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _filter, _break, RegisterHotkeys);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ExitApp()
    {
        Logger.Info("==== 退出,恢复屏幕 ====");
        _filter.ShutdownRestore();
        _tray.Dispose();
        _hotkeys.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _filter?.ShutdownRestore();
            _tray?.Dispose();
            _hotkeys?.Dispose();
            _mutex?.Dispose();
        }
        catch { /* 退出兜底 */ }
        base.OnExit(e);
    }
}
