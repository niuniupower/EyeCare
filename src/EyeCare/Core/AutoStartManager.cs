using Microsoft.Win32;

namespace EyeCare.Core;

public static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "EyeCare";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is string;
    }

    public static void Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                // 带 --silent:开机时静默驻留托盘,不弹出主界面打扰
                key.SetValue(AppName, $"\"{exe}\" --silent");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
            Logger.Info($"开机自启: {(enable ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            Logger.Error("设置开机自启失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 旧版本写入的启动项没有 --silent,开机时会弹出主界面。
    /// 启动时调用本方法把命令行升级到当前格式(顺带修正 exe 路径变动)。
    /// </summary>
    public static void EnsureUpToDate()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(AppName) is not string current) return;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var wanted = $"\"{exe}\" --silent";
            if (current.Trim() == wanted) return;

            key.SetValue(AppName, wanted);
            Logger.Info("开机自启命令已升级为静默启动");
        }
        catch (Exception ex)
        {
            Logger.Error("升级开机自启失败: " + ex.Message);
        }
    }
}
