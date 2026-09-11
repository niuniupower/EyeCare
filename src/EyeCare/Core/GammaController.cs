using System.Runtime.InteropServices;

namespace EyeCare.Core;

public static class GammaNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red = new ushort[256];
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green = new ushort[256];
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue = new ushort[256];
        public RAMP() { }
    }

    [DllImport("gdi32.dll")]
    public static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

    [DllImport("gdi32.dll")]
    public static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateDCW(string? lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName = "";
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString = "";
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID = "";
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey = "";
        public DISPLAY_DEVICE() { }
    }

    public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string? lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);
}

/// <summary>
/// GPU 级屏幕校色:通过 SetDeviceGammaRamp 直接改写显卡输出查找表(LUT)。
/// 画面在信号输出前已完成调整,因此截图软件截到的像素不受影响,文字保持锐利。
/// </summary>
public sealed class GammaController : IDisposable
{
    public sealed class MonitorHandle
    {
        public required string DeviceName { get; init; }
        public required string Description { get; init; }
        public IntPtr Dc { get; set; }
        public ushort[]? OriginalRed;
        public ushort[]? OriginalGreen;
        public ushort[]? OriginalBlue;
        public bool OriginalSaved;
    }

    public List<MonitorHandle> Monitors { get; } = new();

    /// <summary>最近一次 gamma 写入是否成功(遮罩兜底的判断依据)</summary>
    public bool LastApplySucceeded { get; private set; } = true;

    private bool _loggedGammaState = true;

    public void RefreshMonitors()
    {
        foreach (var m in Monitors)
            if (m.Dc != IntPtr.Zero)
                GammaNative.DeleteDC(m.Dc);
        Monitors.Clear();

        var dev = new GammaNative.DISPLAY_DEVICE();
        for (int i = 0; i < 64; i++)
        {
            dev = new GammaNative.DISPLAY_DEVICE();
            dev.cb = Marshal.SizeOf<GammaNative.DISPLAY_DEVICE>();
            if (!GammaNative.EnumDisplayDevices(null, i, ref dev, 0))
                break;
            if ((dev.StateFlags & GammaNative.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                continue;

            var dc = GammaNative.CreateDCW(null, dev.DeviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;

            var mh = new MonitorHandle { DeviceName = dev.DeviceName, Description = dev.DeviceString, Dc = dc };
            SaveOriginal(mh);
            Monitors.Add(mh);
        }
        Logger.Info($"显示器枚举: {Monitors.Count} 个 [{string.Join(", ", Monitors.Select(m => m.DeviceName))}]");
    }

    private static void SaveOriginal(MonitorHandle m)
    {
        var r = new GammaNative.RAMP();
        if (GammaNative.GetDeviceGammaRamp(m.Dc, ref r))
        {
            m.OriginalRed = r.Red;
            m.OriginalGreen = r.Green;
            m.OriginalBlue = r.Blue;
            m.OriginalSaved = true;
        }
        else
        {
            Logger.Error($"读取原始 gamma 失败: {m.DeviceName}");
        }
    }

    /// <summary>
    /// 应用色温(K)与亮度(0..1)。返回是否全部显示器都成功(GPU 级生效)。
    /// Windows 会校验 LUT 偏离程度,过暗或过偏会被拒绝 —— 失败时调用方应回退为遮罩层。
    /// </summary>
    public bool Apply(double kelvin, double brightness)
    {
        var (r, g, b) = KelvinToChannels(kelvin);
        bool allOk = Monitors.Count > 0;
        foreach (var m in Monitors)
        {
            var ramp = new GammaNative.RAMP();
            for (int i = 0; i < 256; i++)
            {
                ushort baseV = (ushort)Math.Clamp(Math.Round(65535.0 * (i / 255.0) * brightness), 0, 65535);
                ramp.Red[i] = (ushort)Math.Clamp((long)baseV * r, 0, 65535);
                ramp.Green[i] = (ushort)Math.Clamp((long)baseV * g, 0, 65535);
                ramp.Blue[i] = (ushort)Math.Clamp((long)baseV * b, 0, 65535);
            }
            bool ok = GammaNative.SetDeviceGammaRamp(m.Dc, ref ramp);
            if (ok) ok = Verify(m, ramp);
            if (!ok)
            {
                allOk = false;
                Logger.Info($"gamma 写入被系统拒绝: {m.DeviceName}");
            }
        }
        return allOk;
    }

    private static bool Verify(MonitorHandle m, GammaNative.RAMP ramp)
    {
        var rb = new GammaNative.RAMP();
        if (!GammaNative.GetDeviceGammaRamp(m.Dc, ref rb)) return false;
        int[] pts = { 0, 64, 128, 255 };
        foreach (var i in pts)
        {
            if (Math.Abs((int)rb.Red[i] - (int)ramp.Red[i]) > 2048) return false;
            if (Math.Abs((int)rb.Green[i] - (int)ramp.Green[i]) > 2048) return false;
            if (Math.Abs((int)rb.Blue[i] - (int)ramp.Blue[i]) > 2048) return false;
        }
        return true;
    }

    /// <summary>恢复所有显示器的原始 gamma</summary>
    public void RestoreAll()
    {
        foreach (var m in Monitors)
        {
            GammaNative.RAMP r;
            if (m.OriginalSaved)
            {
                r = new GammaNative.RAMP { Red = m.OriginalRed!, Green = m.OriginalGreen!, Blue = m.OriginalBlue! };
            }
            else
            {
                r = new GammaNative.RAMP();
                for (int i = 0; i < 256; i++)
                {
                    ushort v = (ushort)(65535.0 * i / 255.0);
                    r.Red[i] = v; r.Green[i] = v; r.Blue[i] = v;
                }
            }
            GammaNative.SetDeviceGammaRamp(m.Dc, ref r);
        }
    }

    /// <summary>
    /// 感知柔和版:Bradford 色适应变换 + sRGB 感知编码。
    /// 在线性光空间把 D65 白点整体适配到目标色温的黑体轨迹白点,
    /// 灰阶保持"干净的暖灰"(不发黄发脏),明暗分布与对比不受影响。
    /// 返回是否全部显示器成功。
    /// </summary>
    public bool ApplyWhitePoint(double kelvin, double brightness)
    {
        var (kr, kg, kb) = BradfordGains(kelvin);
        return ApplyGains(kr, kg, kb, brightness);
    }

    /// <summary>按任意通道增益应用滤光(绿模式与色温模式共用的底层)</summary>
    public bool ApplyGains(double kr, double kg, double kb, double brightness)
    {
        bool allOk = Monitors.Count > 0;
        foreach (var mon in Monitors)
        {
            var ramp = new GammaNative.RAMP();
            for (int i = 0; i < 256; i++)
            {
                double lin = SrgbToLinear(i / 255.0) * brightness;
                ramp.Red[i] = ToRamp(LinearToSrgb(Clamp01(lin * kr)));
                ramp.Green[i] = ToRamp(LinearToSrgb(Clamp01(lin * kg)));
                ramp.Blue[i] = ToRamp(LinearToSrgb(Clamp01(lin * kb)));
            }
            bool ok = GammaNative.SetDeviceGammaRamp(mon.Dc, ref ramp);
            if (ok) ok = Verify(mon, ramp);
            if (!ok) allOk = false;
        }

        LastApplySucceeded = allOk;
        if (allOk != _loggedGammaState)
        {
            _loggedGammaState = allOk;
            Logger.Info(allOk ? "gamma 写入恢复正常" : "gamma 写入被系统拒绝,启用遮罩兜底");
        }
        return allOk;
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    private static ushort ToRamp(double v) => (ushort)Math.Clamp(Math.Round(v * 65535.0), 0, 65535);

    /// <summary>sRGB 传递函数:感知值 → 线性光</summary>
    private static double SrgbToLinear(double v) =>
        v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    /// <summary>sRGB 传递函数:线性光 → 感知值</summary>
    private static double LinearToSrgb(double v) =>
        v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;

    /// <summary>黑体轨迹 CIE xy 色坐标(Krystek 多项式近似,适用 1000K–15000K)</summary>
    public static (double x, double y) KelvinToXY(double kelvin)
    {
        double t = Math.Clamp(kelvin, 1000, 15000);
        double u = (0.860117757 + 1.54118254e-4 * t + 1.28641212e-7 * t * t)
                 / (1 + 8.42420235e-4 * t + 7.08145163e-7 * t * t);
        double v = (0.317398726 + 4.22806245e-5 * t + 4.20481691e-8 * t * t)
                 / (1 - 2.89741816e-5 * t + 1.61456053e-7 * t * t);
        double d = 2 * u - 8 * v + 4;
        double x = 3 * u / d;
        double y = 2 * v / d;
        return (x, y);
    }

    private static readonly double[] D65 = { 0.95047, 1.00000, 1.08883 };

    private static readonly double[,] Bradford =
    {
        {  0.8951000,  0.2664000, -0.1614000 },
        { -0.7502000,  1.7135000,  0.0367000 },
        {  0.0389000, -0.0685000,  1.0296000 }
    };

    /// <summary>
    /// Bradford 白点适配的各通道线性光增益(kr, kg, kb)。
    /// 取适配矩阵作用于灰阶向量 (1,1,1) 的结果(即矩阵行和),
    /// 因此 ramp[255] 精确等于目标色温白点;通道增益亮度加权和 ≈ 1,不改变整体明暗。
    /// gamma ramp 是单通道曲线,非对角耦合(饱和色的微小色相偏移)在此不可表达,属业界标准近似。
    /// </summary>
    public static (double kr, double kg, double kb) BradfordGains(double kelvin)
    {
        var (x, y) = KelvinToXY(kelvin);
        double[] wd = { x / y, 1.0, (1 - x - y) / y };

        // d_i = (M·Wd)_i / (M·Ws)_i
        double[] d = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double src = 0, dst = 0;
            for (int j = 0; j < 3; j++)
            {
                src += Bradford[i, j] * D65[j];
                dst += Bradford[i, j] * wd[j];
            }
            d[i] = dst / src;
        }

        // M' = M⁻¹ · diag(d) · M,再取行和得到灰阶增益
        double[,] inv = Inverse3(Bradford);
        double kr = 0, kg = 0, kb = 0;
        double[] rowSum = new double[3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    sum += inv[i, k] * d[k] * Bradford[k, j];
                rowSum[i] += sum;
            }
        kr = rowSum[0];
        kg = rowSum[1];
        kb = rowSum[2];
        return (kr, kg, kb);
    }

    private static double[,] Inverse3(double[,] m)
    {
        double a = m[0, 0], b = m[0, 1], c = m[0, 2];
        double d = m[1, 0], e = m[1, 1], f = m[1, 2];
        double g = m[2, 0], h = m[2, 1], i = m[2, 2];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return new double[,]
        {
            {  (e * i - f * h) / det, -(b * i - c * h) / det,  (b * f - c * e) / det },
            { -(d * i - f * g) / det,  (a * i - c * g) / det, -(a * f - c * d) / det },
            {  (d * h - e * g) / det, -(a * h - b * g) / det,  (a * e - b * d) / det }
        };
    }

    /// <summary>目标色温下"白色"的等效显示颜色(0-255 RGB),用于 UI 预览与遮罩兜底,与实际白点一致</summary>
    public static (byte r, byte g, byte b) WhitePointColor(double kelvin)
    {
        var (kr, kg, kb) = BradfordGains(kelvin);
        return GainsToColor(kr, kg, kb);
    }

    /// <summary>经典豆沙绿 #C7EDCC 的线性光白点增益:整个画面泛暖绿,白色背景呈现护眼绿</summary>
    public static readonly (double kr, double kg, double kb) GreenGains = (0.571, 0.845, 0.604);

    /// <summary>按浓度(0-100%)在原色与豆沙绿之间插值的增益</summary>
    public static (double kr, double kg, double kb) GreenGainsFor(double strengthPercent)
    {
        double s = Math.Clamp(strengthPercent, 0, 100) / 100.0;
        return (1 + (GreenGains.kr - 1) * s,
                1 + (GreenGains.kg - 1) * s,
                1 + (GreenGains.kb - 1) * s);
    }

    /// <summary>通道增益 → 等效白点颜色(0-255 RGB)</summary>
    public static (byte r, byte g, byte b) GainsToColor(double kr, double kg, double kb) =>
        ((byte)Math.Round(LinearToSrgb(Clamp01(kr)) * 255),
         (byte)Math.Round(LinearToSrgb(Clamp01(kg)) * 255),
         (byte)Math.Round(LinearToSrgb(Clamp01(kb)) * 255));

    /// <summary>色温 → RGB 通道系数(Tanner Helland 近似,旧版线性方案,保留用于兼容)</summary>
    [Obsolete("改用 ApplyWhitePoint/WhitePointColor(感知柔和方案)")]
    public static (double r, double g, double b) KelvinToChannels(double kelvin)
    {
        double t = Math.Clamp(kelvin, 1000, 40000) / 100.0;
        double r, g, b;
        if (t <= 66) r = 255;
        else r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);

        if (t <= 66) g = 99.4708025861 * Math.Log(t) - 161.1195681661;
        else g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);

        if (t >= 66) b = 255;
        else if (t <= 19) b = 0;
        else b = 138.5177312231 * Math.Log(t - 10) - 305.0447927307;

        r = Math.Clamp(r, 0, 255) / 255.0;
        g = Math.Clamp(g, 0, 255) / 255.0;
        b = Math.Clamp(b, 0, 255) / 255.0;
        return (r, g, b);
    }

    public void Dispose()
    {
        foreach (var m in Monitors)
            if (m.Dc != IntPtr.Zero)
                GammaNative.DeleteDC(m.Dc);
        Monitors.Clear();
    }
}
