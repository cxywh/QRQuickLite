using System.Windows.Interop;

namespace QRQuick.Helpers;

/// <summary>
/// 消息专用窗口（HWND_MESSAGE）。全局热键、托盘图标都挂在这个窗口上，
/// 生命周期与进程一致，主窗口隐藏或关闭都不影响它们。
/// </summary>
public sealed class AppMessageWindow : IDisposable
{
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private bool _disposed;

    public AppMessageWindow()
    {
        var parameters = new HwndSourceParameters("QRQuickMessageSink")
        {
            ParentWindow = HwndMessage,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public IntPtr Hwnd => _source.Handle;

    /// <summary>收到窗口消息时触发（UI 线程）。</summary>
    public event EventHandler<AppWindowMessageEventArgs>? Message;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var args = new AppWindowMessageEventArgs(hwnd, msg, wParam, lParam);
        Message?.Invoke(this, args);
        handled = args.Handled;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

public sealed class AppWindowMessageEventArgs : EventArgs
{
    public AppWindowMessageEventArgs(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        Hwnd = hwnd;
        Message = message;
        WParam = wParam;
        LParam = lParam;
    }

    public IntPtr Hwnd { get; }

    public int Message { get; }

    public IntPtr WParam { get; }

    public IntPtr LParam { get; }

    public bool Handled { get; set; }
}
