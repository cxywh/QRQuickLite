using QRQuick.Helpers;
using QRQuick.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace QRQuick.Views;

/// <summary>
/// 覆盖单个显示器的截图遮罩窗口。
/// 窗口自身使用**物理像素坐标系**：Root 的布局尺寸等于显示器物理尺寸，
/// 再用 ScaleTransform(1/scale) 缩放到 DIP，因此这里所有绘制与鼠标坐标
/// 都是物理像素，与 BitBlt 的坐标系完全一致，天然支持混合 DPI 与负坐标。
/// </summary>
public partial class CaptureOverlayWindow : Window
{
    private static readonly string[] MenuLabels = { "识别二维码", "重新框选", "取消截图" };

    private readonly Dictionary<int, Border> _menuItemBorders = new();
    private readonly Dictionary<int, TextBlock> _menuItemTexts = new();

    private Int32Rect? _selectionLocal;
    private Point? _crosshairLocal;

    /// <summary>
    /// WPF 实际使用的 DIP→像素比例。它不一定等于显示器的缩放比
    /// （实测 150% 屏上 GetDpiForWindow 报 144，而 WPF 的合成目标是 1.0），
    /// 所以坐标变换必须取 WPF 自己的矩阵，不能自己用 Dpi/96 推。
    /// </summary>
    private double _pixelPerDip = 1.0;

    /// <summary>实测的 DIP→物理像素比例，供自检与诊断使用。</summary>
    internal double PixelPerDip => _pixelPerDip;

    public CaptureOverlayWindow(MonitorInfo monitor)
    {
        InitializeComponent();

        Monitor = monitor;
        Scale = monitor.Scale;
        BuildMenu();

        Loaded += (_, _) => ApplyGeometry();
        SourceInitialized += (_, _) => ApplyGeometry();
    }

    public MonitorInfo Monitor { get; }

    /// <summary>该显示器的缩放比例（1.0 = 100%）。</summary>
    public double Scale { get; }

    public int MenuItemCount => MenuLabels.Length;

    public int SelectedMenuIndex { get; private set; }

    /// <summary>菜单当前是否可见。</summary>
    public bool MenuPanelVisible => MenuPanel.Visibility == Visibility.Visible;

    /// <summary>鼠标在虚拟桌面中的物理坐标。</summary>
    public event EventHandler<Point>? CursorMoved;

    public event EventHandler? LeftButtonPressed;

    public event EventHandler? LeftButtonReleased;

    public event EventHandler? RightButtonPressed;

    /// <summary>菜单项被鼠标点击（参数为索引）。</summary>
    public event Action<int>? MenuItemInvoked;

    /// <summary>把窗口精确对齐到显示器物理矩形。</summary>
    public void ApplyGeometry()
    {
        // 布局坐标系 = 物理像素：几何全部用显示器物理像素坐标，
        // 关键：窗口裁剪发生在渲染变换之前（按设备像素裁），
        // 所以 1/scale 的缩放不能挂在 Root 上——那样内容会先被裁成交付区的
        // 1/scale，再缩小一倍。正确做法是 Root 按客户端 DIP 尺寸布局，
        // 缩放在子层 Layer 上完成，几何仍用物理像素坐标。
        _pixelPerDip = ResolvePixelPerDip();
        double rootW = Monitor.Width / _pixelPerDip;
        double rootH = Monitor.Height / _pixelPerDip;

        Root.Width = rootW;
        Root.Height = rootH;
        Root.RenderTransform = Transform.Identity;

        Layer.Width = Monitor.Width;
        Layer.Height = Monitor.Height;
        Layer.RenderTransform = new ScaleTransform(1.0 / _pixelPerDip, 1.0 / _pixelPerDip);
        Layer.RenderTransformOrigin = new Point(0, 0);

        double renderedPx = rootW * _pixelPerDip;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT before);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT after);

            LogService.Info($"[overlay] monitor={Monitor.Width}x{Monitor.Height}@{Monitor.Left},{Monitor.Top} " +
                $"winDpi={NativeMethods.GetDpiForWindow(hwnd)} dp={Monitor.Dpi} scale={Scale:0.###} pxPerDip={_pixelPerDip:0.###} " +
                $"winBefore={before.Width}x{before.Height} winAfter={after.Width}x{after.Height} " +
                $"winPos={after.Left},{after.Top} rootW={Root.Width} rootActual={Root.ActualWidth:0.#} " +
                $"expectPx={renderedPx:0.#}");
        }

        RebuildMask();
    }

    /// <summary>
    /// 实测 DIP→物理像素比例。
    /// 分层透明窗口上 TransformToDevice 可能报 1.5 而实际按 1:1 合成，
    /// 所以直接量：把两个本地坐标点换算到屏幕坐标再相减，得到真实比例。
    /// </summary>
    private double ResolvePixelPerDip()
    {
        try
        {
            Point origin = PointToScreen(new Point(0, 0));
            Point probe = PointToScreen(new Point(200, 0));
            double measured = (probe.X - origin.X) / 200.0;
            if (measured > 0.2 && measured < 5)
            {
                return measured;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"实测 DIP 比例失败，回落到显示器 DPI：{ex.Message}");
        }

        try
        {
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                double m11 = target.TransformToDevice.M11;
                if (m11 > 0.1 && m11 < 10)
                {
                    return m11;
                }
            }
        }
        catch (Exception)
        {
            // 忽略，走显示器缩放比兜底
        }

        return Scale > 0.1 ? Scale : 1.0;
    }

    /// <summary>设置十字辅助线的本地物理坐标；null 表示隐藏。</summary>
    public void SetCrosshair(Point? localPoint)
    {
        _crosshairLocal = localPoint;

        if (localPoint is null)
        {
            CrossH.Visibility = Visibility.Collapsed;
            CrossV.Visibility = Visibility.Collapsed;
            CrossShadowH.Visibility = Visibility.Collapsed;
            CrossShadowV.Visibility = Visibility.Collapsed;
            return;
        }

        double x = Math.Clamp(localPoint.Value.X, 0, Monitor.Width);
        double y = Math.Clamp(localPoint.Value.Y, 0, Monitor.Height);

        CrossH.X1 = 0;
        CrossH.X2 = Monitor.Width;
        CrossH.Y1 = y;
        CrossH.Y2 = y;

        CrossV.Y1 = 0;
        CrossV.Y2 = Monitor.Height;
        CrossV.X1 = x;
        CrossV.X2 = x;

        // 阴影线偏移 1 物理像素，提升在浅色背景上的可见度
        CrossShadowH.X1 = 0;
        CrossShadowH.X2 = Monitor.Width;
        CrossShadowH.Y1 = y + 1;
        CrossShadowH.Y2 = y + 1;

        CrossShadowV.Y1 = 0;
        CrossShadowV.Y2 = Monitor.Height;
        CrossShadowV.X1 = x + 1;
        CrossShadowV.X2 = x + 1;

        CrossH.Visibility = Visibility.Visible;
        CrossV.Visibility = Visibility.Visible;
        CrossShadowH.Visibility = Visibility.Visible;
        CrossShadowV.Visibility = Visibility.Visible;
    }

    /// <summary>设置选区（本地物理坐标）；null 表示清除。</summary>
    public void SetSelection(Int32Rect? localRect)
    {
        _selectionLocal = localRect is { Width: > 0, Height: > 0 } ? localRect : null;

        if (_selectionLocal is Int32Rect sel)
        {
            Canvas.SetLeft(SelBorder, sel.X);
            Canvas.SetTop(SelBorder, sel.Y);
            SelBorder.Width = sel.Width;
            SelBorder.Height = sel.Height;
            SelBorder.StrokeThickness = Math.Max(1.0, 2 * Scale);
            SelBorder.Visibility = Visibility.Visible;

            SizeText.Text = $"{sel.Width} × {sel.Height}";
            SizeText.FontSize = 13 * Scale;
            SizeBadge.Padding = new Thickness(8 * Scale, 3 * Scale, 8 * Scale, 3 * Scale);

            SizeBadge.Visibility = Visibility.Visible;
            SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double badgeW = SizeBadge.DesiredSize.Width;
            double badgeH = SizeBadge.DesiredSize.Height;

            // 尺寸标签放在选区右上角外侧，超出屏幕则移入选区内部
            double bx = sel.X + sel.Width - badgeW;
            double by = sel.Y - badgeH - 6 * Scale;
            if (by < 4)
            {
                by = sel.Y + 6 * Scale;
            }

            bx = Math.Clamp(bx, 4, Math.Max(4, Monitor.Width - badgeW - 4));
            Canvas.SetLeft(SizeBadge, bx);
            Canvas.SetTop(SizeBadge, by);
        }
        else
        {
            SelBorder.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
        }

        RebuildMask();
    }

    /// <summary>显示右键菜单，位置为本地物理坐标（自动避让屏幕边缘）。</summary>
    public void ShowMenu(Point localAnchor, int selectedIndex)
    {
        MenuPanel.Visibility = Visibility.Visible;

        double itemHeight = 34 * Scale;
        double menuWidth = 176 * Scale;
        double menuHeight = MenuLabels.Length * itemHeight + 8 * Scale;

        double x = localAnchor.X + 2;
        double y = localAnchor.Y + 2;

        if (x + menuWidth > Monitor.Width - 4)
        {
            x = localAnchor.X - menuWidth - 2;
        }

        if (y + menuHeight > Monitor.Height - 4)
        {
            y = localAnchor.Y - menuHeight - 2;
        }

        x = Math.Clamp(x, 4, Math.Max(4, Monitor.Width - menuWidth - 4));
        y = Math.Clamp(y, 4, Math.Max(4, Monitor.Height - menuHeight - 4));

        MenuPanel.Width = menuWidth;
        Canvas.SetLeft(MenuPanel, x);
        Canvas.SetTop(MenuPanel, y);

        SetMenuIndex(selectedIndex);
    }

    public void HideMenu()
    {
        MenuPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>更新菜单选中项（键盘高亮）。</summary>
    public void SetMenuIndex(int index)
    {
        if (MenuItemCount == 0)
        {
            return;
        }

        SelectedMenuIndex = ((index % MenuItemCount) + MenuItemCount) % MenuItemCount;

        foreach (var (i, border) in _menuItemBorders)
        {
            bool selected = i == SelectedMenuIndex;
            border.Background = selected
                ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))
                : Brushes.Transparent;

            TextBlock text = _menuItemTexts[i];
            text.Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
            text.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>在遮罩上隐藏本窗口（截图前必须调用，确保不被截进结果）。</summary>
    public void HideForCapture()
    {
        HideMenu();
        SetCrosshair(null);
        Hide();
    }

    private void BuildMenu()
    {
        MenuItems.Children.Clear();
        _menuItemBorders.Clear();
        _menuItemTexts.Clear();

        for (int i = 0; i < MenuLabels.Length; i++)
        {
            int index = i;

            var text = new TextBlock
            {
                Text = MenuLabels[i],
                FontSize = 13 * Scale,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
                VerticalAlignment = VerticalAlignment.Center
            };

            var border = new Border
            {
                Height = 34 * Scale,
                CornerRadius = new CornerRadius(4 * Scale),
                Padding = new Thickness(10 * Scale, 0, 10 * Scale, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = text
            };

            border.MouseEnter += (_, _) => SetMenuIndex(index);
            border.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                MenuItemInvoked?.Invoke(index);
            };

            _menuItemBorders[index] = border;
            _menuItemTexts[index] = text;
            MenuItems.Children.Add(border);
        }
    }

    private void RebuildMask()
    {
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };

        using (StreamGeometryContext ctx = geometry.Open())
        {
            // 外框：整个显示器
            ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(Monitor.Width, 0), false, false);
            ctx.LineTo(new Point(Monitor.Width, Monitor.Height), false, false);
            ctx.LineTo(new Point(0, Monitor.Height), false, false);

            // 内孔：选区（避免遮罩盖住选区内容）
            if (_selectionLocal is Int32Rect sel && sel.Width > 0 && sel.Height > 0)
            {
                ctx.BeginFigure(new Point(sel.X, sel.Y), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(sel.X + sel.Width, sel.Y), false, false);
                ctx.LineTo(new Point(sel.X + sel.Width, sel.Y + sel.Height), false, false);
                ctx.LineTo(new Point(sel.X, sel.Y + sel.Height), false, false);
            }
        }

        geometry.Freeze();
        MaskPath.Data = geometry;
    }

    // ---------------- 输入转发（统一用 GetCursorPos 取物理坐标） ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        CursorMoved?.Invoke(this, ScreenCoordinateHelper.GetCursorPosition());
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        LeftButtonPressed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        LeftButtonReleased?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        RightButtonPressed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        // 右键按下阶段不抢占拖选，忽略
        e.Handled = false;
        base.OnMouseRightButtonDown(e);
    }

    /// <summary>隐藏本窗口后用于调试/测试的物理矩形。</summary>
    public Int32Rect PhysicalBounds => new(Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height);
}
