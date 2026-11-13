using QRQuick.Helpers;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace QRQuick.Services;

/// <summary>
/// 屏幕捕获。使用 BitBlt + 屏幕 DC，坐标一律为虚拟桌面物理像素（可含负值）。
/// </summary>
public sealed class ScreenCaptureService
{
    /// <summary>截取指定区域。区域会被裁剪到虚拟桌面范围内。</summary>
    public BitmapSource CaptureRegion(Int32Rect physicalRect)
    {
        if (physicalRect.Width <= 0 || physicalRect.Height <= 0)
        {
            throw new ScreenCaptureException("选区为空，请重新框选。");
        }

        var rect = ScreenCoordinateHelper.ClampToVirtualScreen(physicalRect);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new ScreenCaptureException("选区超出屏幕范围，请重新框选。");
        }

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldObject = IntPtr.Zero;

        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                throw new ScreenCaptureException("无法获取屏幕设备上下文。");
            }

            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, rect.Width, rect.Height);

            if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new ScreenCaptureException("无法创建截图缓冲区。");
            }

            oldObject = NativeMethods.SelectObject(memDc, bitmap);

            bool ok = NativeMethods.BitBlt(memDc, 0, 0, rect.Width, rect.Height,
                screenDc, rect.X, rect.Y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            if (!ok)
            {
                throw new ScreenCaptureException("截取屏幕失败（BitBlt 返回失败），请重试。");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch (ScreenCaptureException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("截图异常", ex);
            throw new ScreenCaptureException($"截取屏幕时发生错误：{ex.Message}", ex);
        }
        finally
        {
            if (oldObject != IntPtr.Zero && memDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memDc, oldObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    /// <summary>
    /// 等待 DWM 把窗口隐藏真正合成到屏幕。
    /// 截图前必须调用，否则可能截到底下的遮罩/选区边框。
    /// </summary>
    public async Task WaitForUiHiddenAsync(int extraDelayMs = 80)
    {
        // 让 WPF 完成一次布局与渲染
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await Task.Delay(extraDelayMs).ConfigureAwait(true);

        try
        {
            NativeMethods.DwmFlush();
        }
        catch (DllNotFoundException)
        {
            // 极旧系统没有 dwmapi，忽略
        }
    }
}

/// <summary>截图失败，消息可直接展示给用户。</summary>
public sealed class ScreenCaptureException : Exception
{
    public ScreenCaptureException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
