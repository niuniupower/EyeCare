using System.IO;
using System.Text.Json;

namespace EyeCare.Core;

public static class SettingsStore
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCare");

    private static readonly string FilePath = Path.Combine(DataDir, "settings.json");
    private static readonly object Lock = new();

    /// <summary>当前配置结构版本,见 AppSettings.ConfigRevision / Migrate</summary>
    private const int CurrentRevision = 2;

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                s = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            else s = new AppSettings();
        }
        catch (Exception ex)
        {
            Logger.Error("加载设置失败: " + ex.Message);
            s = new AppSettings();
        }

        return Migrate(s);
    }

    /// <summary>
    /// 把老配置拉到当前版本。
    /// <para>
    /// revision 1(v1.6.0):背景默认值从「内置渐变」改成「跟随桌面壁纸」——
    /// 光改字段默认值对已有配置无效:文件里存着旧的 <c>"matte"</c>,会一直被读回来,
    /// 用户升级后观感跟以前一模一样,会以为改动根本没生效。
    /// 只迁「还是旧默认值」的情况:自己选过图片(image)的不动,那是用户的明确选择。
    /// </para>
    /// <para>
    /// revision 2(v1.7.2):背景配方重做 —— 像素层压暗 + 去饱和(见 BackgroundStore),
    /// 默认模糊 18 → 10。老配置里存的是按旧配方调的数值(模糊越大越糊、亮壁纸还耀眼),
    /// 直接沿用还是那副观感;统一拉回新配方默认值,不合口味仍可用滑杆自己调。
    /// </para>
    /// </summary>
    private static AppSettings Migrate(AppSettings s)
    {
        if (s.ConfigRevision >= CurrentRevision) return s;

        if (s.ConfigRevision < 1 && s.BackgroundMode == "matte")
            s.BackgroundMode = "desktop";

        if (s.ConfigRevision < 2)
        {
            s.BackgroundDim = 80;
            s.BackgroundBlur = 10;
        }

        s.ConfigRevision = CurrentRevision;
        Logger.Info($"配置已迁移到 revision {CurrentRevision}(背景来源 = {s.BackgroundMode},模糊 = {s.BackgroundBlur}px)");
        Save(s);
        return s;
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
            lock (Lock)
                File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error("保存设置失败: " + ex.Message);
        }
    }
}
