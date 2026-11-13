using QRQuick.Helpers;
using QRQuick.Models;
using QRQuick.Views;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace QRQuick.Services;

/// <summary>截图流程状态。</summary>
public enum CaptureState
{
    /// <summary>空闲。</summary>
    Idle,

    /// <summary>遮罩已显示，等待左键框选（十字辅助线定位）。</summary>
    CaptureReady,

    /// <summary>左键拖动中，选区实时更新。</summary>
    Selecting,

    /// <summary>选区已固定，等待右键菜单。</summary>
    SelectionConfirmed,

    /// <summary>右键菜单已打开。</summary>
    ContextMenu,

    /// <summary>正在隐藏遮罩、截取区域并识别。</summary>
    Processing,

    /// <summary>流程已取消（瞬时状态，随后回到 Idle）。</summary>
    Cancelled
}

public sealed class CaptureCompletedEventArgs : EventArgs
{
    public BitmapSource? Image { get; init; }

    public IReadOnlyList<QRResult> Results { get; init; } = Array.Empty<QRResult>();

    public string? Error { get; init; }

    public bool Cancelled { get; init; }
}

/// <summary>
/// 截图流程控制器：拥有完整状态机、多显示器遮罩窗口、鼠标/键盘输入与截图时机控制。
///
/// 交互约定（严格按需求第五章）：
/// 十字辅助线 → 左键拖动建立选区 → 松开**只固定选区，不识别** → 右键菜单 → 识别/重新框选/取消。
/// ESC 在任何阶段都取消整个流程。
/// </summary>
public sealed class CaptureController : IDisposable
{
    private readonly ScreenCaptureService _captureService;
    private readonly QRCodeService _qrCodeService;

    private readonly List<CaptureOverlayWindow> _overlays = new();
    private readonly List<Window> _hiddenWindows = new();

    private LowLevelKeyboardHook? _keyboardHook;
    private CaptureOverlayWindow? _captureOwner;
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private Int32Rect _selection;
    private Int32Rect _selectionAtMouseDown;
    private Point _anchor;
    private Point _cursor;
    private int _menuIndex;
    private int _menuItemCount = 3;
    private bool _disposed;

    public CaptureController(ScreenCaptureService captureService, QRCodeService qrCodeService)
    {
        _captureService = captureService;
        _qrCodeService = qrCodeService;
    }

    public CaptureState State { get; private set; } = CaptureState.Idle;

    public bool IsActive => State is CaptureState.CaptureReady or CaptureState.Selecting
        or CaptureState.SelectionConfirmed or CaptureState.ContextMenu or CaptureState.Processing;

    /// <summary>当前选区（虚拟桌面物理像素）。无选区时宽高为 0。</summary>
    public Int32Rect CurrentSelection => _selection;

    /// <summary>流程结束（识别完成、失败或取消）时触发，始终在 UI 线程。</summary>
    public event EventHandler<CaptureCompletedEventArgs>? Completed;

    /// <summary>
    /// 进入截图模式。<paramref name="windowsToHide"/> 中的窗口会在截图期间隐藏，
    /// 结束后如果它们原本可见则自动恢复。
    /// </summary>
    public bool Start(IEnumerable<Window>? windowsToHide = null)
    {
        if (_disposed)
        {
            return false;
        }

        if (IsActive)
        {
            // 已在截图流程中：忽略重复触发，避免创建多套遮罩窗口
            LogService.Info("截图流程已在进行中，忽略重复触发");
            return false;
        }

        try
        {
            _monitors = ScreenCoordinateHelper.GetMonitors();
            if (_monitors.Count == 0)
            {
                LogService.Error("未枚举到任何显示器，无法截图");
                return false;
            }

            ResetSessionState();

            HideForeignWindows(windowsToHide);
            CreateOverlays();

            State = CaptureState.CaptureReady;
            _keyboardHook = new LowLevelKeyboardHook(OnGlobalKey);
            _keyboardHook.Install();

            _cursor = ScreenCoordinateHelper.GetCursorPosition();
            UpdateCrosshair();
            ActivateOverlayUnderCursor();

            LogService.Info($"进入截图模式，显示器 {_monitors.Count} 个");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("进入截图模式失败", ex);
            TeardownOverlays();
            RestoreForeignWindows();
            State = CaptureState.Idle;
            return false;
        }
    }

    /// <summary>取消整个截图流程（ESC / 取消截图 / 关闭窗口）。</summary>
    public void Cancel()
    {
        if (!IsActive)
        {
            return;
        }

        LogService.Info($"截图流程取消（原状态 {State}）");

        ReleaseMouseCapture();
        _keyboardHook?.Uninstall();
        _keyboardHook = null;
        TeardownOverlays();
        RestoreForeignWindows();

        State = CaptureState.Idle;
        ResetSessionState();

        Completed?.Invoke(this, new CaptureCompletedEventArgs { Cancelled = true });
    }

    // ---------------- 遮罩窗口 ----------------

    private void CreateOverlays()
    {
        foreach (MonitorInfo monitor in _monitors)
        {
            var overlay = new CaptureOverlayWindow(monitor)
            {
                Opacity = 0,
                Left = monitor.Left,
                Top = monitor.Top,
                Width = Math.Max(1, monitor.Width),
                Height = Math.Max(1, monitor.Height)
            };

            overlay.CursorMoved += (_, p) => OnCursorMoved(p);
            overlay.LeftButtonPressed += (_, _) => OnLeftButtonPressed();
            overlay.LeftButtonReleased += (_, _) => OnLeftButtonReleased();
            overlay.RightButtonPressed += (_, _) => OnRightButtonPressed();
            overlay.MenuItemInvoked += ExecuteMenuAction;

            overlay.Show();
            overlay.ApplyGeometry();

            _overlays.Add(overlay);
            _menuItemCount = overlay.MenuItemCount;
        }

        // 等一帧，让 SetWindowPos 生效后再显示，避免错位闪烁
        foreach (CaptureOverlayWindow overlay in _overlays)
        {
            overlay.UpdateLayout();
            overlay.Opacity = 1;
        }
    }

    private void TeardownOverlays()
    {
        foreach (CaptureOverlayWindow overlay in _overlays)
        {
            try
            {
                overlay.HideForCapture();
                overlay.Close();
            }
            catch (Exception ex)
            {
                LogService.Warn($"关闭遮罩窗口异常：{ex.Message}");
            }
        }

        _overlays.Clear();
    }

    private void HideForeignWindows(IEnumerable<Window>? windows)
    {
        _hiddenWindows.Clear();

        if (windows is null)
        {
            return;
        }

        foreach (Window window in windows)
        {
            if (window is null || !window.IsVisible)
            {
                continue;
            }

            window.Hide();
            _hiddenWindows.Add(window);
        }
    }

    private void RestoreForeignWindows()
    {
        foreach (Window window in _hiddenWindows)
        {
            try
            {
                window.Show();
            }
            catch (Exception ex)
            {
                LogService.Warn($"恢复窗口失败：{ex.Message}");
            }
        }

        _hiddenWindows.Clear();
    }

    private void ActivateOverlayUnderCursor()
    {
        CaptureOverlayWindow? target = FindOverlayAt(_cursor) ?? _overlays.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        try
        {
            target.Activate();
            target.Focus();
            Keyboard.Focus(target);
        }
        catch (Exception ex)
        {
            LogService.Warn($"激活遮罩窗口失败：{ex.Message}");
        }
    }

    private CaptureOverlayWindow? FindOverlayAt(Point physical)
    {
        foreach (CaptureOverlayWindow overlay in _overlays)
        {
            if (overlay.Monitor.Contains((int)physical.X, (int)physical.Y))
            {
                return overlay;
            }
        }

        return null;
    }

    // ---------------- 鼠标 ----------------

    private void OnCursorMoved(Point physical)
    {
        _cursor = physical;

        if (State == CaptureState.Selecting)
        {
            UpdateSelectionFromDrag();
        }
        else
        {
            UpdateCrosshair();
        }
    }

    private void OnLeftButtonPressed()
    {
        if (State != CaptureState.CaptureReady)
        {
            return;
        }

        _cursor = ScreenCoordinateHelper.GetCursorPosition();
        _anchor = _cursor;
        _selectionAtMouseDown = default;
        _captureOwner = FindOverlayAt(_cursor);

        State = CaptureState.Selecting;

        // 捕获鼠标：跨显示器拖动时仍能收到移动事件
        CaptureOverlayWindow? owner = _captureOwner;
        if (owner is not null)
        {
            try
            {
                Mouse.Capture(owner, CaptureMode.Element);
            }
            catch (Exception ex)
            {
                LogService.Warn($"鼠标捕获失败：{ex.Message}");
            }
        }

        UpdateSelectionFromDrag();
    }

    private void OnLeftButtonReleased()
    {
        if (State != CaptureState.Selecting)
        {
            return;
        }

        ReleaseMouseCapture();

        _cursor = ScreenCoordinateHelper.GetCursorPosition();
        UpdateSelectionFromDrag();

        // 面积过小的选区视为误操作，回到十字辅助线状态
        if (_selection.Width < 3 || _selection.Height < 3)
        {
            LogService.Info("选区过小，已忽略");
            _selection = default;
            ApplySelectionToOverlays();
            State = CaptureState.CaptureReady;
            UpdateCrosshair();
            return;
        }

        // 松开左键只固定选区，绝不在这里开始识别
        _selectionAtMouseDown = _selection;
        State = CaptureState.SelectionConfirmed;

        foreach (CaptureOverlayWindow overlay in _overlays)
        {
            overlay.SetCrosshair(null);
        }

        UpdateCrosshair();
        LogService.Info($"选区已固定 {_selection.Width}x{_selection.Height}（等待右键菜单）");
    }

    private void OnRightButtonPressed()
    {
        if (State != CaptureState.SelectionConfirmed)
        {
            // 拖动过程中、或还没有选区时，右键不抢占操作
            return;
        }

        _cursor = ScreenCoordinateHelper.GetCursorPosition();
        _menuIndex = 0;
        State = CaptureState.ContextMenu;

        CaptureOverlayWindow? target = FindOverlayAt(_cursor);
        if (target is null)
        {
            return;
        }

        try
        {
            target.Activate();
            target.Focus();
        }
        catch (Exception ex)
        {
            LogService.Warn($"激活菜单窗口失败：{ex.Message}");
        }

        Point local = new(_cursor.X - target.Monitor.Left, _cursor.Y - target.Monitor.Top);

        foreach (CaptureOverlayWindow overlay in _overlays)
        {
            if (ReferenceEquals(overlay, target))
            {
                overlay.ShowMenu(local, _menuIndex);
            }
            else
            {
                overlay.HideMenu();
            }
        }
    }

    private void UpdateSelectionFromDrag()
    {
        _selection = ScreenCoordinateHelper.ClampToVirtualScreen(
            ScreenCoordinateHelper.NormalizeRect(_anchor.X, _anchor.Y, _cursor.X, _cursor.Y));

        ApplySelectionToOverlays();
        UpdateCrosshair();
    }

    private void ApplySelectionToOverlays()
    {
        foreach (CaptureOverlayWindow overlay in _overlays)
        {
            Int32Rect clipped = ScreenCoordinateHelper.Intersect(_selection, overlay.Monitor);

            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                overlay.SetSelection(null);
                continue;
            }

            overlay.SetSelection(new Int32Rect(
                clipped.X - overlay.Monitor.Left,
                clipped.Y - overlay.Monitor.Top,
                clipped.Width,
                clipped.Height));
        }
    }

    private void UpdateCrosshair()
    {
        foreach (CaptureOverlayWindow overlay in _overlays)
        {
            bool underCursor = overlay.Monitor.Contains((int)_cursor.X, (int)_cursor.Y);
            if (underCursor)
            {
                overlay.SetCrosshair(new Point(
                    Math.Clamp(_cursor.X - overlay.Monitor.Left, 0, overlay.Monitor.Width),
                    Math.Clamp(_cursor.Y - overlay.Monitor.Top, 0, overlay.Monitor.Height)));
            }
            else
            {
                overlay.SetCrosshair(null);
            }
        }
    }

    private void ReleaseMouseCapture()
    {
        try
        {
            if (Mouse.Captured is not null)
            {
                Mouse.Capture(null);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"释放鼠标捕获失败：{ex.Message}");
        }

        _captureOwner = null;
    }

    // ---------------- 键盘 ----------------

    /// <summary>低级键盘钩子的回调（已在 UI 线程派发）。</summary>
    private void OnGlobalKey(int virtualKey)
    {
        // 钩子回调内不做重操作，转投到消息队列
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsActive)
            {
                return;
            }

            switch (virtualKey)
            {
                case LowLevelKeyboardHook.VkEscape:
                    // ESC 在任何阶段都取消整个流程
                    Cancel();
                    break;

                case LowLevelKeyboardHook.VkUp when State == CaptureState.ContextMenu:
                    _menuIndex = (_menuIndex - 1 + _menuItemCount) % _menuItemCount;
                    ApplyMenuIndex();
                    break;

                case LowLevelKeyboardHook.VkDown when State == CaptureState.ContextMenu:
                    _menuIndex = (_menuIndex + 1) % _menuItemCount;
                    ApplyMenuIndex();
                    break;

                case LowLevelKeyboardHook.VkReturn when State == CaptureState.ContextMenu:
                case LowLevelKeyboardHook.VkSpace when State == CaptureState.ContextMenu:
                    ExecuteMenuAction(_menuIndex);
                    break;
            }
        }));
    }

    private void ApplyMenuIndex()
    {
        foreach (CaptureOverlayWindow overlay in _overlays)
        {
            if (overlay.MenuPanelVisible)
            {
                overlay.SetMenuIndex(_menuIndex);
            }
        }
    }

    // ---------------- 菜单动作 ----------------

    private void ExecuteMenuAction(int index)
    {
        if (State != CaptureState.ContextMenu && State != CaptureState.SelectionConfirmed)
        {
            return;
        }

        switch (index)
        {
            case 0:
                _ = ProcessSelectionAsync();
                break;

            case 1:
                RestartSelection();
                break;

            case 2:
                Cancel();
                break;
        }
    }

    /// <summary>重新框选：清除选区，回到十字辅助线定位状态。</summary>
    private void RestartSelection()
    {
        foreach (CaptureOverlayWindow overlay in _overlays)
        {
            overlay.HideMenu();
            overlay.SetSelection(null);
        }

        _selection = default;
        _selectionAtMouseDown = default;
        _menuIndex = 0;
        State = CaptureState.CaptureReady;
        UpdateCrosshair();
        ActivateOverlayUnderCursor();
        LogService.Info("已回到十字辅助线状态");
    }

    /// <summary>识别：先彻底隐藏遮罩 UI，再截图。</summary>
    private async Task ProcessSelectionAsync()
    {
        Int32Rect rect = _selection;
        if (rect.Width < 1 || rect.Height < 1)
        {
            return;
        }

        State = CaptureState.Processing;
        ReleaseMouseCapture();
        _keyboardHook?.Uninstall();
        _keyboardHook = null;

        try
        {
            // 1) 隐藏所有遮罩（含十字线、选区边框、菜单）
            foreach (CaptureOverlayWindow overlay in _overlays)
            {
                overlay.HideForCapture();
            }

            // 2) 等 DWM 真正把桌面合成出来，避免截到自己的 UI
            await _captureService.WaitForUiHiddenAsync();

            // 3) 关闭遮罩窗口，释放资源
            TeardownOverlays();

            // 4) 截取用户选定的区域（保持原分辨率）
            BitmapSource image = _captureService.CaptureRegion(rect);

            State = CaptureState.Idle;
            _selection = default;
            _selectionAtMouseDown = default;
            RestoreForeignWindows();

            // 5) 后台识别
            IReadOnlyList<QRResult> results = await _qrCodeService.DecodeAsync(image, QRSource.ScreenCapture);
            Completed?.Invoke(this, new CaptureCompletedEventArgs { Image = image, Results = results });
        }
        catch (ScreenCaptureException ex)
        {
            State = CaptureState.Idle;
            TeardownOverlays();
            RestoreForeignWindows();
            Completed?.Invoke(this, new CaptureCompletedEventArgs { Error = ex.Message });
        }
        catch (Exception ex)
        {
            LogService.Error("截图识别流程异常", ex);
            State = CaptureState.Idle;
            TeardownOverlays();
            RestoreForeignWindows();
            Completed?.Invoke(this, new CaptureCompletedEventArgs { Error = $"截图识别失败：{ex.Message}" });
        }
    }

    private void ResetSessionState()
    {
        _selection = default;
        _selectionAtMouseDown = default;
        _menuIndex = 0;
        _captureOwner = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (IsActive)
        {
            Cancel();
        }

        _keyboardHook?.Uninstall();
        _keyboardHook = null;
        TeardownOverlays();
    }
}
