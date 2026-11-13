using QRQuick.Models;
using System.Windows;

namespace QRQuick.Helpers;

/// <summary>
/// 屏幕坐标系与 DPI 的统一入口。
/// 约定：本类返回的所有屏幕坐标都是**物理像素**（虚拟桌面坐标，允许负值），
/// 与 BitBlt / RegisterHotKey / GetCursorPos 使用的坐标系一致。
/// WPF 的逻辑坐标（DIP）只在控件内部使用，跨界转换必须走这里。
/// </summary>
internal static class ScreenCoordinateHelper
{
    /// <summary>枚举全部显示器；失败时返回空列表。</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
        {
            var mi = new NativeMethods.MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                szDevice = string.Empty
            };

            if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            {
                // GetMonitorInfo 失败时仍用枚举回调给的矩形，避免漏掉显示器
                list.Add(new MonitorInfo
                {
                    Handle = hMonitor,
                    Left = rect.Left,
                    Top = rect.Top,
                    Width = Math.Max(1, rect.Width),
                    Height = Math.Max(1, rect.Height),
                    Dpi = 96,
                    IsPrimary = false,
                    DeviceName = "MONITOR?"
                });
                return true;
            }

            list.Add(new MonitorInfo
            {
                Handle = hMonitor,
                Left = mi.rcMonitor.Left,
                Top = mi.rcMonitor.Top,
                Width = Math.Max(1, mi.rcMonitor.Width),
                Height = Math.Max(1, mi.rcMonitor.Height),
                Dpi = GetMonitorDpi(hMonitor),
                IsPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                DeviceName = mi.szDevice
            });
            return true;
        }

        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            LogService.Warn($"枚举显示器失败：{ex.Message}");
        }

        return list;
    }

    /// <summary>取显示器有效 DPI；Shcore 不可用时回落 96，再回落窗口 DPI。</summary>
    public static uint GetMonitorDpi(IntPtr hMonitor)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX;
            }
        }
        catch (DllNotFoundException)
        {
            // Windows 8.1 以下没有 Shcore.dll
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 96;
    }

    /// <summary>整个虚拟桌面的物理矩形（覆盖所有显示器，左上角可能为负）。</summary>
    public static Int32Rect GetVirtualScreenRect()
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        if (w <= 0 || h <= 0)
        {
            // 度量失败时退化为主显示器尺寸，避免抛出异常
            w = Math.Max(1, (int)SystemParameters.PrimaryScreenWidth);
            h = Math.Max(1, (int)SystemParameters.PrimaryScreenHeight);
            x = 0;
            y = 0;
        }

        return new Int32Rect(x, y, w, h);
    }

    /// <summary>鼠标当前位置，物理像素。</summary>
    public static Point GetCursorPosition()
    {
        return NativeMethods.GetCursorPos(out NativeMethods.POINT p)
            ? new Point(p.X, p.Y)
            : new Point(0, 0);
    }

    /// <summary>取包含指定物理点的显示器。</summary>
    public static MonitorInfo? GetMonitorAt(IReadOnlyList<MonitorInfo> monitors, double x, double y)
    {
        foreach (var m in monitors)
        {
            if (m.Contains((int)Math.Floor(x), (int)Math.Floor(y)))
            {
                return m;
            }
        }

        return null;
    }

    /// <summary>把两点构成的矩形规范化（支持任意方向拖动）。</summary>
    public static Int32Rect NormalizeRect(double x1, double y1, double x2, double y2)
    {
        int left = (int)Math.Round(Math.Min(x1, x2));
        int top = (int)Math.Round(Math.Min(y1, y2));
        int right = (int)Math.Round(Math.Max(x1, x2));
        int bottom = (int)Math.Round(Math.Max(y1, y2));

        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>把矩形裁剪到指定显示器范围内（物理像素）。</summary>
    public static Int32Rect Intersect(Int32Rect rect, MonitorInfo monitor)
    {
        int left = Math.Max(rect.X, monitor.Left);
        int top = Math.Max(rect.Y, monitor.Top);
        int right = Math.Min(rect.X + rect.Width, monitor.Right);
        int bottom = Math.Min(rect.Y + rect.Height, monitor.Bottom);

        if (right <= left || bottom <= top)
        {
            return new Int32Rect(0, 0, 0, 0);
        }

        return new Int32Rect(left, top, right - left, bottom - top);
    }

    /// <summary>把矩形裁剪到虚拟桌面范围内。</summary>
    public static Int32Rect ClampToVirtualScreen(Int32Rect rect)
    {
        var vs = GetVirtualScreenRect();
        int left = Math.Max(rect.X, vs.X);
        int top = Math.Max(rect.Y, vs.Y);
        int right = Math.Min(rect.X + rect.Width, vs.X + vs.Width);
        int bottom = Math.Min(rect.Y + rect.Height, vs.Y + vs.Height);

        if (right <= left || bottom <= top)
        {
            return new Int32Rect(0, 0, 0, 0);
        }

        return new Int32Rect(left, top, right - left, bottom - top);
    }
}
