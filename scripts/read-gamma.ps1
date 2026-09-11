# 读取当前显示器 gamma ramp 并导出为 CSV(用于分析其他滤镜软件的曲线配方)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GammaRead
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;
    }
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateDCW(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);
    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    public static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    public static RAMP ReadPrimary()
    {
        for (int i = 0; i < 64; i++)
        {
            var dev = new DISPLAY_DEVICE();
            dev.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (!EnumDisplayDevices(null, i, ref dev, 0)) break;
            if ((dev.StateFlags & 1) == 0) continue;
            IntPtr dc = CreateDCW(null, dev.DeviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            var ramp = new RAMP();
            ramp.Red = new ushort[256]; ramp.Green = new ushort[256]; ramp.Blue = new ushort[256];
            bool ok = GetDeviceGammaRamp(dc, ref ramp);
            DeleteDC(dc);
            if (ok) return ramp;
            throw new Exception("GetDeviceGammaRamp failed on " + dev.DeviceName);
        }
        throw new Exception("No display found");
    }
}
"@
$ramp = [GammaRead]::ReadPrimary()
$samples = @(0, 16, 32, 64, 96, 128, 160, 192, 224, 240, 255)
"index,red,green,blue"
foreach ($i in $samples) {
    "{0},{1:F4},{2:F4},{3:F4}" -f $i, ($ramp.Red[$i]/65535.0), ($ramp.Green[$i]/65535.0), ($ramp.Blue[$i]/65535.0)
}
