using QRQuick.Models;
using QRQuick.Services;
using System.Windows;
using System.Windows.Input;

namespace QRQuick.Views;

/// <summary>
/// 设置窗口。所有修改立即生效并写入配置，没有“保存”按钮。
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();

        HotkeyControl.RecordingStarted += OnHotkeyRecordingStarted;
        HotkeyControl.RecordingCancelled += OnHotkeyRecordingCancelled;
        HotkeyControl.HotkeyCommitted += OnHotkeyCommitted;

        LoadSettings();
    }

    private static App App => (App)Application.Current;

    private void LoadSettings()
    {
        _loading = true;

        AppSettings settings = App.SettingsService.Current;

        HotkeyControl.SetValue(HotkeyConverter.Format(settings.CaptureHotkey));
        HotkeyStatusText.Text = App.HotkeyStatusText;

        ExplorerMenuCheck.IsChecked = settings.ExplorerContextMenu;
        AutoOpenCheck.IsChecked = settings.AutoOpenBrowser;
        ExitRadio.IsChecked = settings.CloseBehavior == CloseBehavior.Exit;
        TrayRadio.IsChecked = settings.CloseBehavior == CloseBehavior.MinimizeToTray;
        TrayIconCheck.IsChecked = settings.ShowTrayIcon;

        _loading = false;
    }

    // ---------------- 快捷键 ----------------

    private void OnHotkeyRecordingStarted(object? sender, EventArgs e)
    {
        HotkeyStatusText.Text = "请按下新的组合键，Esc 取消。";
    }

    private void OnHotkeyRecordingCancelled(object? sender, EventArgs e)
    {
        HotkeyControl.SetValue(HotkeyConverter.Format(App.SettingsService.Current.CaptureHotkey));
        HotkeyControl.ResetHint();
        HotkeyStatusText.Text = App.HotkeyStatusText;
    }

    private void OnHotkeyCommitted(object? sender, HotkeyDefinition definition)
    {
        var (ok, error) = App.ApplyHotkey(definition);

        if (ok)
        {
            HotkeyControl.SetValue(HotkeyConverter.Format(definition));
            HotkeyControl.ResetHint();
            HotkeyStatusText.Text = App.HotkeyStatusText;
            FooterHint.Text = "快捷键已保存。";
        }
        else
        {
            // 注册失败：退回原组合，并把原因显示在输入框旁
            HotkeyControl.SetValue(HotkeyConverter.Format(App.SettingsService.Current.CaptureHotkey));
            HotkeyControl.ShowError(error);
            HotkeyStatusText.Text = error;
        }
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        var (ok, error) = App.ApplyHotkey(HotkeyDefinition.Default);

        if (ok)
        {
            HotkeyControl.SetValue(HotkeyConverter.Format(HotkeyDefinition.Default));
            HotkeyControl.ResetHint();
            HotkeyStatusText.Text = App.HotkeyStatusText;
            FooterHint.Text = "已恢复默认快捷键。";
        }
        else
        {
            HotkeyStatusText.Text = error;
            FooterHint.Text = error;
        }
    }

    // ---------------- 其它开关 ----------------

    private void ExplorerMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (Persist())
        {
            FooterHint.Text = ExplorerMenuCheck.IsChecked == true
                ? "已加入右键菜单，在文件或桌面空白处右键即可看到“二维码识别”。"
                : "已从右键菜单移除。";
        }
        else
        {
            // 注册表写入失败时不保留开关，避免界面与实际状态不一致
            ExplorerMenuCheck.IsChecked = App.SettingsService.Current.ExplorerContextMenu;
        }
    }

    private void AutoOpen_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            Persist();
        }
    }

    private void CloseBehavior_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // 选择“最小化到托盘”意味着必须有托盘图标
        if (TrayRadio.IsChecked == true)
        {
            TrayIconCheck.IsChecked = true;
        }

        Persist();
    }

    private void TrayIcon_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (TrayIconCheck.IsChecked == false && TrayRadio.IsChecked == true)
        {
            TrayIconCheck.IsChecked = true;
            FooterHint.Text = "关闭窗口的行为是“最小化到托盘”，托盘图标不能关闭。";
            return;
        }

        Persist();
    }

    /// <summary>把当前控件状态写回配置。返回是否成功。</summary>
    private bool Persist()
    {
        if (_loading)
        {
            return true;
        }

        AppSettings next = App.SettingsService.Current.Clone();
        next.AutoOpenBrowser = AutoOpenCheck.IsChecked == true;
        next.ExplorerContextMenu = ExplorerMenuCheck.IsChecked == true;
        next.ShowTrayIcon = TrayIconCheck.IsChecked == true;
        next.CloseBehavior = TrayRadio.IsChecked == true
            ? CloseBehavior.MinimizeToTray
            : CloseBehavior.Exit;

        if (!App.SaveSettings(next, out string error))
        {
            FooterHint.Text = error;
            return false;
        }

        FooterHint.Text = "设置已保存。";
        return true;
    }

    // ---------------- 窗口 ----------------

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
                // 竞态忽略
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !HotkeyControl.IsRecording)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
