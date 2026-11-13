using QRQuick.Helpers;
using QRQuick.Models;
using QRQuick.Services;
using QRQuick.Views;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace QRQuick;

/// <summary>状态提示的语义类型，决定文字颜色。</summary>
public enum StatusKind
{
    Ready,
    Busy,
    Success,
    Warning,
    Error
}

/// <summary>
/// Lite 版主窗口：只有一个截图入口、一行状态、一块最近结果。
/// 不提供图片导入、拖放、剪贴板粘贴。
/// </summary>
public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();

        StateChanged += OnStateChanged;
        Loaded += (_, _) => RefreshHotkeyDisplay();
        Closing += OnClosing;
    }

    private static App App => (App)Application.Current;

    // ---------------- 状态与结果 ----------------

    public void SetStatus(string message, StatusKind kind = StatusKind.Ready)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetStatus(message, kind)));
            return;
        }

        StatusText.Text = message;
        StatusText.Foreground = kind switch
        {
            StatusKind.Success => (Brush)FindResource("SuccessBrush"),
            StatusKind.Warning => (Brush)FindResource("WarningBrush"),
            StatusKind.Error => (Brush)FindResource("DangerBrush"),
            _ => (Brush)FindResource("TextSecondaryBrush")
        };
    }

    /// <summary>把最近一次识别结果显示在主界面。</summary>
    public void ApplyResults(IReadOnlyList<QRResult> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            results[i].Index = i + 1;
        }

        bool hasResults = results.Count > 0;

        EmptyHint.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
        ResultScroll.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        ResultActions.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        CopyAllButton.Visibility = results.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        OpenLinkButton.Visibility = results.Any(r => r.IsValidUrl) ? Visibility.Visible : Visibility.Collapsed;

        if (!hasResults)
        {
            ResultText.Text = string.Empty;
            return;
        }

        ResultText.Text = results.Count == 1
            ? results[0].Text
            : string.Join(Environment.NewLine + Environment.NewLine,
                results.Select(r => $"{r.Index}. {r.Text}"));

        ResultScroll.ScrollToTop();
    }

    public void RefreshHotkeyDisplay()
    {
        HotkeyDefinition definition = App.SettingsService.Current.CaptureHotkey;
        string text = HotkeyConverter.Format(definition);

        CaptureHotkeyText.Text = App.HotkeyRegistered
            ? $"{text}　呼出截图"
            : $"{text}（未生效，点“设置”更换）";
    }

    // ---------------- 顶栏 ----------------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 快速连点时的竞态，忽略
            }
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

        if (WindowState == WindowState.Minimized)
        {
            App.TrimMemoryForBackground();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow { Owner = IsVisible ? this : null };
        window.ShowDialog();

        RefreshHotkeyDisplay();
        App.UpdateTrayVisibility();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose || App.IsExiting)
        {
            return;
        }

        e.Cancel = true;

        if (App.SettingsService.Current.CloseBehavior == CloseBehavior.MinimizeToTray)
        {
            Hide();
            App.TrimMemoryForBackground();
            return;
        }

        _allowClose = true;
        App.ExitApplication();
    }

    // ---------------- 截图 ----------------

    private void Capture_Click(object sender, RoutedEventArgs e) => App.BeginCapture();

    // ---------------- 结果操作 ----------------

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<QRResult> results = App.LastResults;
        if (results.Count == 0)
        {
            return;
        }

        string text = results.Count == 1
            ? results[0].Text
            : string.Join(Environment.NewLine, results.Select(r => r.Text));

        SetStatus(App.ClipboardService.TrySetText(text) ? "已复制到剪贴板。" : "复制失败，剪贴板可能被占用。",
            StatusKind.Success);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<QRResult> results = App.LastResults;
        if (results.Count == 0)
        {
            return;
        }

        string text = string.Join(Environment.NewLine, results.Select(r => r.Text));
        SetStatus(App.ClipboardService.TrySetText(text) ? "已复制全部结果。" : "复制失败，剪贴板可能被占用。");
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        QRResult? first = App.LastResults.FirstOrDefault(r => r.IsValidUrl);
        if (first?.ValidUrl is null)
        {
            SetStatus("当前结果里没有可打开的网页链接。", StatusKind.Warning);
            return;
        }

        SetStatus(App.BrowserService.TryOpen(first.ValidUrl, out string error) ? "已在浏览器中打开。" : error,
            StatusKind.Success);
    }
}
