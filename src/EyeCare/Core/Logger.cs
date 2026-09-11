using System.IO;

namespace EyeCare.Core;

public static class Logger
{
    private static readonly object Lock = new();
    private static string Dir => SettingsStore.DataDir;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Dir);
                var path = Path.Combine(Dir, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    File.Delete(path);
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* 日志写入失败时静默 */ }
    }
}
