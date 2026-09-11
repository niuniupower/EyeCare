# 重置所有显示器的 gamma 到线性(屏幕颜色恢复默认)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GammaReset
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
    public static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);
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

    public static void ResetAll()
    {
        for (int i = 0; i < 64; i++)
        {
            var dev = new DISPLAY_DEVICE();
            dev.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (!EnumDisplayDevices(null, i, ref dev, 0)) break;
            if ((dev.StateFlags & 1) == 0) continue;
            IntPtr dc = CreateDCW(null, dev.DeviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) { Console.WriteLine(dev.DeviceName + ": CreateDC failed"); continue; }
            var ramp = new RAMP();
            ramp.Red = new ushort[256];
            ramp.Green = new ushort[256];
            ramp.Blue = new ushort[256];
            for (int v = 0; v < 256; v++)
            {
                ushort val = (ushort)Math.Round(65535.0 * v / 255.0);
                ramp.Red[v] = val; ramp.Green[v] = val; ramp.Blue[v] = val;
            }
            bool ok = SetDeviceGammaRamp(dc, ref ramp);
            DeleteDC(dc);
            Console.WriteLine(dev.DeviceName + ": gamma reset = " + ok);
        }
    }
}
"@
[GammaReset]::ResetAll()
