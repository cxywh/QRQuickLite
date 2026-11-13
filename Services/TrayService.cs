using QRQuick.Helpers;
using System.Runtime.InteropServices;

namespace QRQuick.Services;

/// <summary>
/// 托盘图标（Shell_NotifyIcon 原生实现，不引入 WinForms）。
/// 右键菜单用 TrackPopupMenu + TPM_RETURNCMD，菜单选择直接返回命令号。
/// </summary>
public sealed class TrayService : IDisposable
{
    private const uint TrayId = 0x51;

    private const uint MenuShow = 1;
    private const uint MenuCapture = 2;
    private const uint MenuExit = 3;

    private readonly AppMessageWindow _sink;
    private IntPtr _icon;
    private bool _added;
    private bool _disposed;

    public TrayService(AppMessageWindow sink)
    {
        _sink = sink;
        _sink.Message += OnMessage;
    }

    /// <summary>请求显示主窗口。</summary>
    public event EventHandler? ShowRequested;

    /// <summary>请求进入截图识别。</summary>
    public event EventHandler? CaptureRequested;

    /// <summary>请求退出程序。</summary>
    public event EventHandler? ExitRequested;

    public bool IsVisible => _added;

    /// <summary>显示托盘图标。</summary>
    public void Show(string tooltip)
    {
        if (_disposed)
        {
            return;
        }

        if (_icon == IntPtr.Zero)
        {
            _icon = LoadApplicationIcon();
        }

        var data = CreateData(tooltip);
        data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;

        bool ok;
        if (_added)
        {
            ok = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
        }
        else
        {
            ok = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
            _added = ok;
        }

        if (!ok)
        {
            LogService.Warn("托盘图标添加失败");
        }
    }

    /// <summary>移除托盘图标。</summary>
    public void Hide()
    {
        if (!_added || _disposed)
        {
            return;
        }

        var data = CreateData(string.Empty);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        _added = false;
    }

    private NativeMethods.NOTIFYICONDATA CreateData(string tooltip)
    {
        return new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _sink.Hwnd,
            uID = TrayId,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = _icon,
            szTip = tooltip ?? string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private void OnMessage(object? sender, AppWindowMessageEventArgs e)
    {
        if (e.Message != NativeMethods.WM_TRAYICON)
        {
            return;
        }

        // 低字为鼠标消息
        int mouseMessage = e.LParam.ToInt32() & 0xFFFF;

        if (mouseMessage == NativeMethods.WM_LBUTTONUP)
        {
            e.Handled = true;
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (mouseMessage is NativeMethods.WM_RBUTTONUP or NativeMethods.WM_CONTEXTMENU)
        {
            e.Handled = true;
            ShowContextMenu();
        }
    }

    private void ShowContextMenu()
    {
        IntPtr menu = IntPtr.Zero;
        try
        {
            NativeMethods.GetCursorPos(out NativeMethods.POINT pt);

            menu = NativeMethods.CreatePopupMenu();
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, new UIntPtr(MenuShow), "打开主窗口");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, new UIntPtr(MenuCapture), "截图识别");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, new UIntPtr(MenuExit), "退出");

            // 让菜单能正常获得焦点并响应点击外部关闭
            NativeMethods.SetForegroundWindow(_sink.Hwnd);

            int command = NativeMethods.TrackPopupMenu(
                menu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_NONOTIFY,
                pt.X, pt.Y, 0, _sink.Hwnd, IntPtr.Zero);

            switch ((uint)command)
            {
                case MenuShow:
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case MenuCapture:
                    CaptureRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case MenuExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("托盘菜单异常", ex);
        }
        finally
        {
            if (menu != IntPtr.Zero)
            {
                NativeMethods.DestroyMenu(menu);
            }
        }
    }

    /// <summary>从可执行文件资源中取图标；失败则用系统默认图标。</summary>
    private static IntPtr LoadApplicationIcon()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var small = new IntPtr[1];
                if (NativeMethods.ExtractIconEx(exePath, 0, Array.Empty<IntPtr>(), small, 1) > 0 && small[0] != IntPtr.Zero)
                {
                    return small[0];
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取程序图标失败：{ex.Message}");
        }

        // IDI_APPLICATION = 32512
        return NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _sink.Message -= OnMessage;

        if (_icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        _disposed = true;
    }
}
