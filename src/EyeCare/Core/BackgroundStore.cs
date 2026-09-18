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
/// 两者都在<b>像素层</b>做完整处理(降采样 → DrawingVisual + BlurEffect → RenderTargetBitmap
/// → 压暗去饱和 → ImageBrush),不把 BlurEffect 挂在元素上 —— 元素级模糊会把窗口四角的
/// 圆角一起糊掉,而这里产出的画刷交给 Border.Background,圆角由 Border 自己裁,边缘永远是干净的。
/// <para>
/// 「模糊 + 黑遮罩」的老配方有两个治不好的毛病,是背景"又糊又耀眼"的根源:
/// ① 高斯模糊把亮部搅开,一张亮壁纸会变成整片乳白光雾 —— 越模糊越亮;
/// ② 深色遮罩是 lerp(c, 深色, α),只压亮度、<b>饱和度原样保留</b>,彩色壁纸照样花花绿绿地透上来。
/// 所以在像素层加一道「暗玻璃」处理(见 <see cref="Render"/>):先把饱和度收掉,再把亮度整体
/// 压进一个很低的区间 —— 之后无论遮罩滑杆拉到多低,背景最多也只是"灰调暗图",不可能再刺眼。
/// </para>
/// </summary>
public static class BackgroundStore
{
    /// <summary>背景图目录(与 settings.json 同级)</summary>
    public static readonly string Dir = Path.Combine(SettingsStore.DataDir, "backgrounds");

    /// <summary>模糊前先把图片降到这个宽度再处理:省内存、省 CPU,压暗后肉眼看不出差别</summary>
    private const int WorkWidth = 1280;

    // ── 暗玻璃配方 ──
    /// <summary>去饱和比例(0-255):颜色通道向亮度收 96/256 ≈ 38%,壁纸退成灰调、不再抢注意力</summary>
    private const int DesatMix = 96;
    /// <summary>亮度压缩 out = Floor + in × Gain:纯白(255)封顶约 112,深夜也不刺眼;
    /// 低值抬底色,模糊的暗角不至于死黑。遮罩滑杆在这之上再压,所以「浓度」低到 10% 也安全</summary>
    private const double ToneFloor = 10;
    private const double ToneGain = 0.40;

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
    /// 生成背景画刷:等比裁切填满(UniformToFill)+ 居中,先像素级模糊、再做「暗玻璃」压暗去饱和。
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

            ImageSource? final = Render(src, w, h, blur);
            if (final is null) return null;

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

    /// <summary>
    /// 背景图的完整像素处理:blur &gt; 0 先高斯模糊(消除细节),然后<b>一律</b>做「暗玻璃」——
    /// 去饱和 + 亮度压缩。blur = 0 也要做:耀眼是亮度问题,跟模不模糊无关。
    /// <para>
    /// 为什么压缩必须做在<b>位图像素</b>里、而不是靠窗口上的遮罩层:遮罩是 lerp(c, 深色, α),
    /// 无论怎么调 α 都治不了两件事 —— 亮部封不了顶(α 低时白壁纸照透),饱和度原样保留
    /// (彩色照样刺眼)。像素层的 out = Floor + in×Gain 才是真正的亮度上限。
    /// </para>
    /// <para>
    /// 模糊会把四边拉进半透明(Pbgra 预乘),先转成非预乘的 Bgra32 再逐像素处理,
    /// 否则边缘会被二次压暗出一圈暗边。逐像素只有乘加 + 256 项查表,1280 宽的图约 10ms。
    /// </para>
    /// </summary>
    private static ImageSource? Render(BitmapSource src, int w, int h, int blur)
    {
        try
        {
            var dv = new DrawingVisual();
            if (blur > 0)
                dv.Effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian };
            using (var dc = dv.RenderOpen())
                dc.DrawImage(src, new Rect(0, 0, w, h));

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            var bgra = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
            int stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);

            // 亮度压缩查表:循环里只剩一次查表,不再做浮点
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
                lut[i] = (byte)Math.Round(ToneFloor + ToneGain * i);

            for (int p = 0; p < pixels.Length; p += 4)
            {
                int b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
                // ITU-R 601 亮度,定点化:(0.299, 0.587, 0.114) × 65536,正好凑满 2^16
                int lum = (r * 19595 + g * 38470 + b * 7471) >> 16;
                pixels[p]     = lut[b + ((lum - b) * DesatMix >> 8)];
                pixels[p + 1] = lut[g + ((lum - g) * DesatMix >> 8)];
                pixels[p + 2] = lut[r + ((lum - r) * DesatMix >> 8)];
                // alpha 不动
            }

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error("背景暗玻璃处理失败: " + ex.Message);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 被占用就算了,下次再清 */ }
    }
}
