using QRQuick.Helpers;
using QRQuick.Models;
using QRQuick.Services;
using QRQuick.Views;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QRQuick.Diagnostics;

/// <summary>
/// 遮罩渲染演示：把截图遮罩、十字辅助线、选区、尺寸标签、右键菜单全部显示出来，
/// 然后截取整块桌面保存为 PNG，用于人工/视觉核对渲染与定位是否正确。
/// </summary>
internal static class OverlayDemo
{
    public static async Task<int> RunAsync()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QRQuick", "selftest", "overlay-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        Directory.CreateDirectory(directory);

        var monitors = ScreenCoordinateHelper.GetMonitors();
        if (monitors.Count == 0)
        {
            return 1;
        }

        MonitorInfo primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        var overlays = new List<CaptureOverlayWindow>();
        var service = new ScreenCaptureService();

        try
        {
            // 背景：放一个白底窗口，便于观察选区“挖孔”是否真的透出桌面
            var background = new BackgroundWindow();
            background.ShowAt(new Int32Rect(
                primary.Left + primary.Width / 2 - 260,
                primary.Top + primary.Height / 2 - 160, 520, 320));
            await Task.Delay(250);

            foreach (MonitorInfo monitor in monitors)
            {
                var overlay = new CaptureOverlayWindow(monitor)
                {
                    Left = monitor.Left,
                    Top = monitor.Top,
                    Width = monitor.Width,
                    Height = monitor.Height,
                    Opacity = 1
                };

                overlay.Show();
                overlay.ApplyGeometry();
                overlays.Add(overlay);
            }

            await Task.Delay(220);

            // 主显示器：十字线 + 选区 + 尺寸标签 + 菜单
            Point cursorVirtual = new(primary.Left + primary.Width / 2.0 + 40, primary.Top + primary.Height / 2.0 + 30);
            var localCross = new Point(cursorVirtual.X - primary.Left, cursorVirtual.Y - primary.Top);

            var localSelection = new Int32Rect(
                (int)(primary.Width / 2.0 - 180),
                (int)(primary.Height / 2.0 - 120),
                360, 240);

            CaptureOverlayWindow main = overlays.First(o => ReferenceEquals(o.Monitor, primary));
            main.SetSelection(localSelection);
            main.SetCrosshair(localCross);
            main.ShowMenu(new Point(localSelection.X + localSelection.Width + 6, localSelection.Y + 40), 0);

            await Task.Delay(450);

            // 整块虚拟桌面截图（含遮罩本身，正是要检查的合成结果）
            Int32Rect vs = ScreenCoordinateHelper.GetVirtualScreenRect();
            BitmapSource screenshot = service.CaptureRegion(vs);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(screenshot));
            string path = Path.Combine(directory, "overlay-demo.png");

            using (var fs = File.Create(path))
            {
                encoder.Save(fs);
            }

            File.WriteAllText(Path.Combine(directory, "url.txt"), path);
            LogService.Info($"遮罩演示图片：{path}");

            background.Close();
            return 0;
        }
        catch (Exception ex)
        {
            LogService.Error("遮罩演示失败", ex);
            return 1;
        }
        finally
        {
            foreach (CaptureOverlayWindow overlay in overlays)
            {
                overlay.HideForCapture();
                overlay.Close();
            }
        }
    }

    private sealed class BackgroundWindow : Window
    {
        public BackgroundWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = System.Windows.Media.Brushes.White;
        }

        public void ShowAt(Int32Rect rect)
        {
            Show();
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }
}
