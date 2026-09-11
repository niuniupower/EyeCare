using System.IO;
using System.Text.Json;

namespace EyeCare.Core;

public static class SettingsStore
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCare");

    private static readonly string FilePath = Path.Combine(DataDir, "settings.json");
    private static readonly object Lock = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("加载设置失败: " + ex.Message);
        }
        return new AppSettings();
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
