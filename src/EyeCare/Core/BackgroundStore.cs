using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace EyeCare.Core;

/// <summary>
/// 设置窗口的背景图。三种背景来源里有两种是「一张图」:
///   · image   = 用户选的图片,复制到 %APPDATA%\EyeCare\backgrounds 下持久化;
///   · desktop = 当前桌面壁纸,每次应用时现读现用(用户换壁纸后重新打开设置即可跟随)。
/// 两者的模糊都在<b>像素层</b>做(降采样 → DrawingVisual + BlurEffect → RenderTargetBitmap → ImageBrush),
/// 不把 BlurEffect 挂在元素上 —— 元素级模糊会把窗口四角的圆角一起糊掉,
/// 而这里产出的画刷交给 Border.Background,圆角由 Border 自己裁,边缘永远是干净的。
/// </summary>
public static class BackgroundStore
{
    /// <summary>背景图目录(与 settings.json 同级)</summary>
    public static readonly string Dir = Path.Combine(SettingsStore.DataDir, "backgrounds");

    /// <summary>模糊前先把图片降到这个宽度再处理:省内存、省 CPU,压暗后肉眼看不出差别</summary>
    private const int WorkWidth = 1280;

    /// <summary>超过这个大小的文件不当背景图处理,免得一个手滑把内存吃光</summary>
    private const long MaxBytes = 64L * 1024 * 1024;

    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif" };

    // ── 桌面壁纸 ──

    /// <summary>
    /// 当前桌面壁纸的文件路径;取不到(纯色桌面 / 读不出来)返回空串,由调用方降级。
    /// <para>
    /// 优先 <c>%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper</c> ——
    /// 这一份才是 Windows 真正拿去渲染的图(已经按用户的"填充/适应"设置裁剪过),
    /// 它**没有扩展名**;读不到时退回注册表里记的原始文件。
    /// </para>
    /// </summary>
    public static string ResolveDesktopWallpaper()
    {
        var candidates = new List<string>
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper"),
        };

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("WallPaper") is string p && !string.IsNullOrWhiteSpace(p))
                candidates.Add(p);
        }
        catch { /* 注册表读不到不影响主路径 */ }

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return "";
    }

    // ── 用户图片 ──

    /// <summary>把用户选中的图片导入到数据目录,返回落盘后的完整路径;失败返回空串</summary>
    public static string Import(string sourcePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return "";

            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (Array.IndexOf(AllowedExtensions, ext) < 0) ext = SniffExtension(sourcePath);

            Directory.CreateDirectory(Dir);
            // 先写临时文件再替换:中途失败不会把原来可用的背景弄丢
            var target = Path.Combine(Dir, "background" + ext);
            var temp = target + ".tmp";
            File.Copy(sourcePath, temp, true);
            File.Delete(target);
            File.Move(temp, target);

            // 换过扩展名(或从别的格式换过来)时,清掉上一次的残留
            foreach (var old in Directory.GetFiles(Dir, "background.*"))
                if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) TryDelete(old);

            Logger.Info($"背景图已导入: {target}");
            return target;
        }
        catch (Exception ex)
        {
            Logger.Error("导入背景图失败: " + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// 源文件没有可用扩展名时按文件头猜一个(桌面的 TranscodedWallpaper 就是没有扩展名的)。
    /// 落盘的文件名要能让人看懂,用户以后想手动替换背景直接覆盖这个文件就行。
    /// </summary>
    private static string SniffExtension(string path)
    {
        try
        {
            var head = new byte[12];
            using (var fs = File.OpenRead(path))
            {
                int n = fs.Read(head, 0, head.Length);
                if (n < 4) return ".img";
            }
            if (head[0] == 0xFF && head[1] == 0xD8) return ".jpg";
            if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return ".png";
            if (head[0] == 0x42 && head[1] == 0x4D) return ".bmp";
            if (head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46) return ".gif";
            if (head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46) return ".webp";
        }
        catch { /* 读不到就退回通用名 */ }
        return ".img";
    }

    /// <summary>清空背景图目录</summary>
    public static void Clear()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir)) TryDelete(f);
        }
        catch (Exception ex) { Logger.Error("清除背景图失败: " + ex.Message); }
    }

    /// <summary>该路径是否是一张能读出来的图(用于启动时校验配置里的路径)</summary>
    public static bool IsUsable(string path)
    {
        var data = ReadImageBytes(path);
        if (data is null) return false;
        var (w, h) = ProbeSize(data);
        return w > 0 && h > 0;
    }

    // ── 画刷 ──

    /// <summary>
    /// 生成背景画刷:等比裁切填满(UniformToFill)+ 居中,blur &gt; 0 时先做像素级高斯模糊。
    /// 读不出来时返回 null,由调用方降级到内置渐变。
    /// <para>
    /// 一律按<b>字节流</b>解码,而不是把路径交给 BitmapImage:桌面壁纸(TranscodedWallpaper)
    /// 没有扩展名,按路径解码挑不到解码器;走流的话 WIC 只认文件头,不看文件名。
    /// </para>
    /// </summary>
    public static ImageBrush? BuildBrush(string path, int blur)
    {
        try
        {
            var data = ReadImageBytes(path);
            if (data is null) return null;

            var (rawW, rawH) = ProbeSize(data);
            if (rawW <= 0 || rawH <= 0) return null;

            bool downscale = rawW > WorkWidth;
            int w = downscale ? WorkWidth : rawW;
            int h = Math.Max(1, (int)Math.Round(rawH * (w / (double)rawW)));

            var src = Decode(data, downscale ? WorkWidth : 0);
            if (src is null) return null;

            ImageSource final = blur <= 0 ? src : Blur(src, w, h, blur);

            var brush = new ImageBrush(final)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };
            brush.Freeze();
            return brush;
        }
        catch (Exception ex)
        {
            Logger.Error("生成背景画刷失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 只读元数据拿原始尺寸(不真解码)。
    /// BitmapImage.PixelWidth 在 EndInit 之前恒为 0,拿它判断是否会漏掉降采样。
    /// </summary>
    private static (int W, int H) ProbeSize(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data, false);
            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch { return (0, 0); }
    }

    private static BitmapSource? Decode(byte[] data, int decodePixelWidth)
    {
        try
        {
            using var ms = new MemoryStream(data, false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad;          // 解码在 EndInit 完成,之后流可以关
            if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>
    /// 读图片字节。用 FileShare.ReadWrite 打开:桌面壁纸正被系统改写(换壁纸的瞬间)时,
    /// 默认的共享模式会直接抛 IOException,而这个失败会表现为"背景突然没了"。
    /// </summary>
    private static byte[]? ReadImageBytes(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0 || fs.Length > MaxBytes) return null;

            var data = new byte[fs.Length];
            int read = 0;
            while (read < data.Length)
            {
                int n = fs.Read(data, read, data.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return read == data.Length ? data : null;
        }
        catch { return null; }
    }

    private static ImageSource Blur(BitmapSource src, int w, int h, int radius)
    {
        var dv = new DrawingVisual { Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian } };
        using (var dc = dv.RenderOpen())
            dc.DrawImage(src, new Rect(0, 0, w, h));

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 被占用就算了,下次再清 */ }
    }
}
