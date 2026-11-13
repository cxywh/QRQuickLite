using QRQuick.Helpers;
using System.Runtime.InteropServices;
using System.Windows;

namespace QRQuick.Diagnostics;

/// <summary>
/// 合成鼠标/键盘输入（SendInput）。仅用于自检与端到端验证，
/// 运行时会真实移动系统光标。
/// </summary>
internal static class SyntheticInput
{
    public static void MoveTo(int x, int y)
    {
        Int32Rect vs = ScreenCoordinateHelper.GetVirtualScreenRect();

        int nx = (int)Math.Round((x - vs.X) * 65535.0 / Math.Max(1, vs.Width - 1));
        int ny = (int)Math.Round((y - vs.Y) * 65535.0 / Math.Max(1, vs.Height - 1));

        Send(MouseInput(nx, ny, 0,
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK));
    }

    public static void LeftDown() =>
        Send(MouseInput(0, 0, 0, NativeMethods.MOUSEEVENTF_LEFTDOWN));

    public static void LeftUp() =>
        Send(MouseInput(0, 0, 0, NativeMethods.MOUSEEVENTF_LEFTUP));

    public static void RightDown() =>
        Send(MouseInput(0, 0, 0, NativeMethods.MOUSEEVENTF_RIGHTDOWN));

    public static void RightUp() =>
        Send(MouseInput(0, 0, 0, NativeMethods.MOUSEEVENTF_RIGHTUP));

    public static void RightClick()
    {
        RightDown();
        Thread.Sleep(30);
        RightUp();
    }

    /// <summary>按下并释放一个按键（虚拟键码）。</summary>
    public static void PressKey(ushort virtualKey)
    {
        Send(KeyInput(virtualKey, false));
        Thread.Sleep(25);
        Send(KeyInput(virtualKey, true));
    }

    public static void KeyDown(ushort virtualKey) => Send(KeyInput(virtualKey, false));

    public static void KeyUp(ushort virtualKey) => Send(KeyInput(virtualKey, true));

    private static NativeMethods.INPUT MouseInput(int dx, int dy, uint data, uint flags) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        u = new NativeMethods.INPUTUNION
        {
            mi = new NativeMethods.MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = data,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static NativeMethods.INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static void Send(NativeMethods.INPUT input)
    {
        var inputs = new[] { input };
        uint sent = NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.INPUT>());

        if (sent == 0)
        {
            LogService.Warn($"SendInput 失败，错误码 {Marshal.GetLastWin32Error()}");
        }
    }
}
