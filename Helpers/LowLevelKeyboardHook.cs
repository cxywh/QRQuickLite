using System.Runtime.InteropServices;

namespace QRQuick.Helpers;

/// <summary>
/// 低级键盘钩子（WH_KEYBOARD_LL）。截图流程中用它保证 ESC 在任何阶段都能取消，
/// 不依赖遮罩窗口是否拥有键盘焦点（例如焦点被其他程序抢走时）。
/// </summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    public const int VkEscape = 0x1B;
    public const int VkReturn = 0x0D;
    public const int VkSpace = 0x20;
    public const int VkUp = 0x26;
    public const int VkDown = 0x28;

    private readonly Action<int> _onKeyDown;
    private NativeMethods.LowLevelKeyboardProc? _proc;
    private IntPtr _hook;
    private bool _disposed;

    public LowLevelKeyboardHook(Action<int> onKeyDown)
    {
        _onKeyDown = onKeyDown;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>安装钩子；失败返回 false（此时仍有窗口级键盘处理兜底）。</summary>
    public bool Install()
    {
        if (_hook != IntPtr.Zero || _disposed)
        {
            return _hook != IntPtr.Zero;
        }

        try
        {
            _proc = HookProc;

            // 回调在本进程内，hMod 传当前模块句柄
            IntPtr module = NativeMethods.GetModuleHandle(null);
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);

            if (_hook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                LogService.Warn($"键盘钩子安装失败，错误码 {err}（将回落到窗口级按键处理）");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("键盘钩子安装异常", ex);
            return false;
        }
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
        }
        catch (Exception ex)
        {
            LogService.Warn($"卸载键盘钩子异常：{ex.Message}");
        }
        finally
        {
            _hook = IntPtr.Zero;
            _proc = null;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        try
        {
            int message = wParam.ToInt32();
            if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)data.vkCode;

                if (vk is VkEscape or VkUp or VkDown or VkReturn or VkSpace)
                {
                    _onKeyDown(vk);
                    return new IntPtr(1); // 吞掉按键，避免传给下层程序
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("键盘钩子回调异常", ex);
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _disposed = true;
        Uninstall();
    }
}
