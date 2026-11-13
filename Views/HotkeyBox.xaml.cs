using QRQuick.Models;
using QRQuick.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QRQuick.Views;

/// <summary>
/// 快捷键录入控件：点击进入录入状态，按下“修饰键 + 主键”后提交。
/// 录入期间由宿主窗口负责注销全局热键，否则同一组合会被系统拦截、永远录不进去。
/// </summary>
public partial class HotkeyBox : UserControl
{
    private bool _recording;

    public HotkeyBox()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        LostKeyboardFocus += (_, _) =>
        {
            if (_recording)
            {
                CancelRecording();
            }
        };
    }

    /// <summary>开始录入（宿主据此注销全局热键）。</summary>
    public event EventHandler? RecordingStarted;

    /// <summary>录入完成。</summary>
    public event EventHandler<HotkeyDefinition>? HotkeyCommitted;

    /// <summary>录入被取消（ESC 或失焦）。</summary>
    public event EventHandler? RecordingCancelled;

    public bool IsRecording => _recording;

    /// <summary>设置显示文本。</summary>
    public void SetValue(string text)
    {
        ValueText.Text = text;
        ValueText.Foreground = (Brush)FindResource("TextPrimaryBrush");
    }

    /// <summary>显示错误提示（红色）。</summary>
    public void ShowError(string message)
    {
        HintText.Text = message;
        HintText.Foreground = (Brush)FindResource("DangerBrush");
    }

    /// <summary>恢复默认提示样式。</summary>
    public void ResetHint()
    {
        HintText.Text = "点击后按下新快捷键";
        HintText.Foreground = (Brush)FindResource("TextMutedBrush");
    }

    private void Box_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        StartRecording();
    }

    private void StartRecording()
    {
        if (_recording)
        {
            return;
        }

        _recording = true;
        ValueText.Text = "请按下新的快捷键…";
        ValueText.Foreground = (Brush)FindResource("AccentBrush");
        HintText.Text = "Esc 取消";
        HintText.Foreground = (Brush)FindResource("TextMutedBrush");

        RecordingStarted?.Invoke(this, EventArgs.Empty);

        Focusable = true;
        Focus();
        Keyboard.Focus(this);
    }

    private void CancelRecording()
    {
        if (!_recording)
        {
            return;
        }

        _recording = false;
        ResetHint();
        RecordingCancelled?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            CancelRecording();
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 只按修饰键不算设定
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            HintText.Text = "需要包含 Ctrl / Alt / Shift / Win";
            HintText.Foreground = (Brush)FindResource("WarningBrush");
            return;
        }

        HotkeyDefinition? definition = HotkeyConverter.FromKeyEvent(key, modifiers);
        if (definition is null)
        {
            HintText.Text = "这个按键不能作为快捷键";
            HintText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        _recording = false;
        ResetHint();
        HotkeyCommitted?.Invoke(this, definition);
    }
}
