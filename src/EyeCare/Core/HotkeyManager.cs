using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace EyeCare.Core;

/// <summary>全局热键:注册在独立消息窗口上</summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x1;
    private const uint MOD_CONTROL = 0x2;
    private const uint MOD_SHIFT = 0x4;
    private const uint MOD_WIN = 0x8;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;

    private readonly HwndSource _src;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        _src = new HwndSource(new HwndSourceParameters("EyeCareHotkeys") { Width = 0, Height = 0 });
        _src.AddHook(WndProc);
    }

    public bool Register(string combo, Action action)
    {
        if (!TryParse(combo, out uint mods, out uint vk))
        {
            Logger.Error($"热键格式无效: {combo}");
            return false;
        }
        int id = _nextId++;
        if (!RegisterHotKey(_src.Handle, id, mods | MOD_NOREPEAT, vk))
        {
            Logger.Info($"热键注册失败(可能被占用): {combo}");
            return false;
        }
        _actions[id] = action;
        Logger.Info($"热键注册成功: {combo}");
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
            UnregisterHotKey(_src.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        foreach (var part in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
                        vk = (uint)char.ToUpperInvariant(part[0]);
                    else if (part.Length is 2 or 3 && part[0] == 'F' && int.TryParse(part[1..], out int f) && f is >= 1 and <= 12)
                        vk = (uint)(0x70 + f - 1); // F1 = 0x70
                    else
                        return false;
                    break;
            }
        }
        return vk != 0 && mods != 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _src.Dispose();
    }
}
