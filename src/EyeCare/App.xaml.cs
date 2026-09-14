using System.Threading;
using System.Windows;
using Application = System.Windows.Application;
using EyeCare.Core;
using EyeCare.UI;

namespace EyeCare;

public partial class App : Application
{
    /// <summary>第二实例通过它通知第一实例「把主界面弹出来」</summary>
    private const string ShowWindowEventName = "EyeCare_ShowWindow_Event";

    private static Mutex? _mutex;

    private AppSettings _settings = new();
    private FilterEngine _filter = null!;
    private BreakManager _break = null!;
    private TrayService _tray = null!;
    private HotkeyManager _hotkeys = null!;
    private BreakSession? _session;
    private SettingsWindow? _settingsWindow;
    private EventWaitHandle? _showWindowEvent;
    private CancellationTokenSource? _listenCts;
    private bool _trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "EyeCare_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 已有实例在跑(通常正驻留托盘):不弹提示框,直接把它的主界面叫出来
            try { EventWaitHandle.OpenExisting(ShowWindowEventName).Set(); }
            catch { /* 对方尚未就绪或权限异常时静默忽略 */ }
            Shutdown();
            return;
        }

        // 启动参数:
        //   (无参数)          打开主界面并驻留托盘
        //   --silent / --tray 静默驻留托盘(开机自启用,不弹窗)
        //   --settings / -s   打开主界面(等价于无参数)
        //   --break / -b      立即休息一次
        bool silent = e.Args.Any(a => a is "--silent" or "--tray" or "-q");
        bool openWindow = e.Args.Any(a => a is "--settings" or "-s") || !silent;
        bool breakNow = e.Args.Any(a => a is "--break" or "-b");

        Logger.Info("==== 暮瞳 DuskEye 启动 ====");

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

        _tray.OpenSettings += ShowSettingsWindow;
        _tray.BreakNow += StartBreak;
        _tray.ExitRequested += ExitApp;
        _tray.FilterToggled += OnSettingsChangedExternally;
        _tray.FilterShortcutSelected += OnSettingsChangedExternally;
        _tray.AutoStartToggled += () => AutoStartManager.Set(_settings.AutoStart);

        _break.BreakTriggered += StartBreak;
        _break.StatusChanged += () =>
        {
            _tray.SyncState(_settings, _break.NextBreakText);
            _settingsWindow?.UpdateBreakStatus(_break.CountdownText, _break.StateText);
        };

        // 注销/关机:窗口的「关闭=隐藏」不能挡住系统,必须放行并恢复屏幕
        SessionEnding += (_, _) =>
        {
            Logger.Info("系统注销/关机:恢复屏幕后退出");
            _listenCts?.Cancel();
            _settingsWindow?.PrepareForExit();
            _filter?.ShutdownRestore();
        };

        _break.Start();
        _filter.Start();
        RegisterHotkeys();
        StartShowWindowListener();

        // 开机自启的注册表项是旧版本写入的(缺 --silent)时顺手升级,避免开机弹窗打扰
        if (_settings.AutoStart) AutoStartManager.EnsureUpToDate();

        if (!_settings.FirstRunDone)
        {
            _settings.FirstRunDone = true;
            SettingsStore.Save(_settings);
            if (!openWindow)
            {
                _tray.ShowBalloon("暮瞳 已在后台运行 🌙",
                    "双击托盘图标打开设置。默认每 20 分钟提醒你休息 20 秒。");
            }
        }

        _tray.SyncState(_settings, _break.NextBreakText);

        if (openWindow)
        {
            ShowSettingsWindow();
        }
        else
        {
            Logger.Info("静默启动:仅驻留托盘");
        }

        if (breakNow) StartBreak();
    }

    /// <summary>第二实例唤起主界面(后台线程等待 → 回 UI 线程打开窗口)</summary>
    private void StartShowWindowListener()
    {
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _listenCts = new CancellationTokenSource();
        var token = _listenCts.Token;

        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (_showWindowEvent.WaitOne(500) is false) continue;
                if (token.IsCancellationRequested) break;
                try { Dispatcher.Invoke(ShowSettingsWindow); }
                catch (TaskCanceledException) { break; }
                catch (Exception ex) { Logger.Error("唤起主界面失败: " + ex.Message); }
            }
        });
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

    /// <summary>打开并前置主界面。窗口实例复用(关闭=隐藏到托盘),不会重建丢状态。</summary>
    private void ShowSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, _filter, _break, RegisterHotkeys);
            _settingsWindow.HiddenToTray += OnWindowHiddenToTray;
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        bool wasHidden = !_settingsWindow.IsVisible;
        if (wasHidden) _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;

        _settingsWindow.Activate();
        // 置顶一瞬再取消:确保从其他窗口后面弹到最前(Activate 单独用并不可靠)
        _settingsWindow.Topmost = true;
        _settingsWindow.Topmost = false;
        _settingsWindow.Focus();
        if (wasHidden) _settingsWindow.PlayResumeAnimation();
    }

    private void OnWindowHiddenToTray()
    {
        if (_trayHintShown) return;
        _trayHintShown = true;
        _tray.ShowBalloon("暮瞳 仍在后台运行 🌙",
            "窗口已收进托盘(护眼滤光继续生效)。单击托盘图标即可重新打开,退出请用托盘右键菜单。");
    }

    private void ExitApp()
    {
        Logger.Info("==== 退出,恢复屏幕 ====");
        _listenCts?.Cancel();
        _settingsWindow?.PrepareForExit();
        _filter.ShutdownRestore();
        _tray.Dispose();
        _hotkeys.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _listenCts?.Cancel();
            _settingsWindow?.PrepareForExit();
            _filter?.ShutdownRestore();
            _tray?.Dispose();
            _hotkeys?.Dispose();
            _showWindowEvent?.Dispose();
            _listenCts?.Dispose();
            _mutex?.Dispose();
        }
        catch { /* 退出兜底 */ }
        base.OnExit(e);
    }
}
