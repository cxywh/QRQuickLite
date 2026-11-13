using QRQuick.Helpers;
using QRQuick.Models;
using QRQuick.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace QRQuick.Views;

/// <summary>识别结果浮动窗口。多结果逐条展示，逐条复制，不自动批量打开链接。</summary>
public partial class ResultWindow : Window
{
    private static App App => (App)Application.Current;

    private QRSource _source = QRSource.ScreenCapture;

    public ResultWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionWindow();
    }

    /// <summary>填充结果并显示。</summary>
    public void ShowResults(IReadOnlyList<QRResult> results, QRSource source)
    {
        var list = results.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Index = i + 1;
        }

        _source = source;

        ResultsList.ItemsSource = null;
        ResultsList.ItemsSource = list;

        CountText.Text = $"{list.Count} 个";
        CopyAllButton.IsEnabled = list.Count > 0;
        Title = $"识别结果（{list.Count}）— 来源：{SourceText(source)}";

        // 已显示时重新定位，避免窗口跑到视口外
        if (IsVisible)
        {
            UpdateLayout();
            PositionWindow();
        }
    }

    private static string SourceText(QRSource source) => source switch
    {
        QRSource.ScreenCapture => "屏幕截图",
        _ => "未知"
    };

    /// <summary>
    /// 定位策略：截图来源贴光标；图片/剪贴板来源且主窗口可见时，贴主窗口右侧（放不下则左侧）。
    /// 全程使用物理像素 + SetWindowPos，避免 DPI 缩放导致偏移。
    /// </summary>
    private void PositionWindow()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
            {
                return;
            }

            int width = rect.Width;
            int height = rect.Height;

            // 图片导入场景：与主窗口并排，避免互相遮挡
            if (_source != QRSource.ScreenCapture &&
                App.MainHost is { IsVisible: true } main &&
                new WindowInteropHelper(main).Handle is var mainHandle &&
                mainHandle != IntPtr.Zero &&
                NativeMethods.GetWindowRect(mainHandle, out NativeMethods.RECT mainRect))
            {
                int gap = 12;
                int x = mainRect.Right + gap;
                int y = mainRect.Top + 24;

                var monitors = ScreenCoordinateHelper.GetMonitors();
                MonitorInfo? monitor = ScreenCoordinateHelper.GetMonitorAt(monitors, mainRect.Right, mainRect.Top + 24)
                                      ?? ScreenCoordinateHelper.GetMonitorAt(monitors, mainRect.Left + 10, mainRect.Top + 10);

                if (monitor is not null && x + width > monitor.Right - 8)
                {
                    // 右侧放不下就放到主窗口左侧
                    x = mainRect.Left - width - gap;
                }

                ClampToMonitor(ref x, ref y, width, height, monitor);
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                return;
            }

            Point cursor = ScreenCoordinateHelper.GetCursorPosition();
            var allMonitors = ScreenCoordinateHelper.GetMonitors();
            MonitorInfo? atCursor = ScreenCoordinateHelper.GetMonitorAt(allMonitors, cursor.X, cursor.Y);

            int cx = (int)cursor.X + 18;
            int cy = (int)cursor.Y + 18;
            ClampToMonitor(ref cx, ref cy, width, height, atCursor);

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, cx, cy, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            LogService.Warn($"结果窗口定位失败（不影响使用）：{ex.Message}");
        }
    }

    private static void ClampToMonitor(ref int x, ref int y, int width, int height, MonitorInfo? monitor)
    {
        if (monitor is null)
        {
            return;
        }

        if (x + width > monitor.Right - 8)
        {
            x = monitor.Right - width - 8;
        }

        if (y + height > monitor.Bottom - 8)
        {
            y = monitor.Bottom - height - 8;
        }

        x = Math.Max(monitor.Left + 8, x);
        y = Math.Max(monitor.Top + 8, y);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void CopyOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not QRResult item)
        {
            return;
        }

        bool ok = App.ClipboardService.TrySetText(item.Text);
        App.MainHost?.SetStatus(ok ? "已复制这条结果。" : "复制失败，剪贴板可能被其他程序占用。",
            ok ? StatusKind.Success : StatusKind.Error);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var items = (ResultsList.ItemsSource as IEnumerable<QRResult>)?.ToList() ?? new List<QRResult>();
        if (items.Count == 0)
        {
            return;
        }

        string text = string.Join(Environment.NewLine, items.Select(i => i.Text));
        bool ok = App.ClipboardService.TrySetText(text);
        App.MainHost?.SetStatus(ok ? $"已复制全部 {items.Count} 条结果。" : "复制失败，剪贴板可能被其他程序占用。",
            ok ? StatusKind.Success : StatusKind.Error);
    }

    private void OpenOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not QRResult item || !item.IsValidUrl)
        {
            return;
        }

        bool ok = App.BrowserService.TryOpen(item.ValidUrl, out string error);
        App.MainHost?.SetStatus(ok ? "已用默认浏览器打开链接。" : error,
            ok ? StatusKind.Success : StatusKind.Warning);
    }

    private void Recapture_Click(object sender, RoutedEventArgs e) => App.BeginCapture();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
