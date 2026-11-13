using QRQuick.Diagnostics;
using QRQuick.Helpers;
using QRQuick.Models;
using QRQuick.Services;
using QRQuick.Views;
using System.Windows;
using System.Windows.Threading;

namespace QRQuick;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private AppMessageWindow? _messageWindow;
    private const string ActivateEventName = @"Local\QRQuickLite.Activate";
    private const string CaptureEventName = @"Local\QRQuickLite.Capture";

    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _captureEvent;
    private Thread? _signalThread;
    private volatile bool _signalStop;
    private bool _exiting;
    private bool _startedHidden;
    private DispatcherTimer? _idleTrimTimer;
    private long _lastActivityTicks;
    private long _lastIdleTrimTicks;

    public SettingsService SettingsService { get; private set; } = null!;

    public HotkeyService HotkeyService { get; private set; } = null!;

    public TrayService? TrayService { get; private set; }

    public ClipboardService ClipboardService { get; private set; } = null!;

    public ExplorerContextMenuService ExplorerContextMenu { get; private set; } = null!;

    public QRCodeService QRCodeService { get; private set; } = null!;

    public BrowserService BrowserService { get; private set; } = null!;

    public ScreenCaptureService ScreenCaptureService { get; private set; } = null!;

    public CaptureController? CaptureController { get; private set; }

    public MainWindow? MainHost { get; private set; }

    public ResultWindow? Result { get; private set; }

    /// <summary>快捷键注册状态文本，供界面显示。</summary>
    public string HotkeyStatusText { get; private set; } = string.Empty;

    public bool HotkeyRegistered => HotkeyService?.IsRegistered == true;

    /// <summary>程序是否正在退出（用于放过关闭事件的拦截）。</summary>
    public bool IsExiting => _exiting;

    /// <summary>最近一次识别结果（只保留一次任务的结果，不做历史库）。</summary>
    public IReadOnlyList<QRResult> LastResults { get; private set; } = Array.Empty<QRResult>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 自检 / 端到端验证模式：不显示主界面
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest(e.Args);
            return;
        }

        // 安装/卸载程序使用：只改右键菜单状态并退出，不显示任何界面。
        // 这里只解析参数，真正执行要等服务初始化之后（SettingsService 尚未加载）。
        bool enableMenuArg = e.Args.Any(a => a.Equals("--enable-context-menu", StringComparison.OrdinalIgnoreCase));
        bool disableMenuArg = e.Args.Any(a => a.Equals("--disable-context-menu", StringComparison.OrdinalIgnoreCase));
        bool menuSwitch = enableMenuArg || disableMenuArg;

        bool captureOnStart = e.Args.Any(a => a.Equals("--capture", StringComparison.OrdinalIgnoreCase));
        _startedHidden = captureOnStart;

        // 菜单开关完全跳过单实例判定：安装/卸载时主程序可能正在运行也不应被它挡掉
        if (!menuSwitch && !EnsureSingleInstance(captureOnStart))
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogService.Error("未处理的域异常", args.ExceptionObject as Exception);

        LogService.Info($"QR Quick Lite 启动，版本 {typeof(App).Assembly.GetName().Version}");

        try
        {
            InitializeServices();
        }
        catch (Exception ex)
        {
            LogService.Error("初始化失败", ex);
            MessageBox.Show($"QR Quick Lite 启动失败：{ex.Message}", "QR Quick Lite", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        if (menuSwitch)
        {
            RunContextMenuSwitch(enableMenuArg);
            Shutdown();
            return;
        }

        if (e.Args.Any(a => a.Equals("--overlay-demo", StringComparison.OrdinalIgnoreCase)))
        {
            RunOverlayDemo();
            return;
        }

        // 由资源管理器右键菜单启动：直接进截图模式，不闪主窗口
        if (captureOnStart)
        {
            LogService.Info("从右键菜单启动，直接进入截图模式");
            BeginCapture();
            return;
        }

        ShowMainWindow();
    }

    private bool EnsureSingleInstance(bool captureOnStart)
    {
        try
        {
            _instanceMutex = new Mutex(true, @"Local\QRQuickLite.SingleInstance", out bool createdNew);
            if (createdNew)
            {
                StartSignalListener();
                return true;
            }

            // 已有实例：通知它显示主窗口或直接进截图模式，然后本进程退出。
            // 消息窗口是 HWND_MESSAGE，HWND_BROADCAST 到不了，因此用命名事件。
            SignalRunningInstance(captureOnStart);

            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        catch (Exception ex)
        {
            // 互斥体不可用时仍允许运行，不阻塞使用
            LogService.Warn($"单实例检测失败：{ex.Message}");
            return true;
        }
    }

    /// <summary>创建命名事件并启动等待线程，接收后续进程发来的请求。</summary>
    private void StartSignalListener()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _captureEvent = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureEventName);

            _signalThread = new Thread(SignalLoop)
            {
                IsBackground = true,
                Name = "QRQuickLite.SignalListener"
            };
            _signalThread.Start();
        }
        catch (Exception ex)
        {
            LogService.Warn($"单实例通知通道不可用：{ex.Message}");
        }
    }

    private void SignalLoop()
    {
        try
        {
            WaitHandle[] handles = { _activateEvent!, _captureEvent! };

            while (!_signalStop)
            {
                int index = WaitHandle.WaitAny(handles, 400);
                if (index == 0)
                {
                    Dispatcher.BeginInvoke(new Action(ShowMainWindow));
                }
                else if (index == 1)
                {
                    Dispatcher.BeginInvoke(new Action(BeginCapture));
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"单实例通知线程结束：{ex.Message}");
        }
    }

    private static void SignalRunningInstance(bool capture)
    {
        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(capture ? CaptureEventName : ActivateEventName);
            handle.Set();
        }
        catch (Exception ex)
        {
            LogService.Warn($"通知已运行的实例失败：{ex.Message}");
        }
    }

    private void InitializeServices()
    {
        SettingsService = new SettingsService();
        SettingsService.Load();

        ClipboardService = new ClipboardService();
        ExplorerContextMenu = new ExplorerContextMenuService();
        QRCodeService = new QRCodeService();
        BrowserService = new BrowserService();
        ScreenCaptureService = new ScreenCaptureService();

        _messageWindow = new AppMessageWindow();

        HotkeyService = new HotkeyService(_messageWindow);
        HotkeyService.Pressed += (_, _) => Dispatcher.BeginInvoke(new Action(BeginCapture));

        CaptureController = new CaptureController(ScreenCaptureService, QRCodeService);
        CaptureController.Completed += OnCaptureCompleted;

        RegisterConfiguredHotkey();

        TrayService = new TrayService(_messageWindow);
        TrayService.ShowRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ShowMainWindow));
        TrayService.CaptureRequested += (_, _) => Dispatcher.BeginInvoke(new Action(BeginCapture));
        TrayService.ExitRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication));

        UpdateTrayVisibility();
        ApplyContextMenuSetting();
        StartIdleTrimWatchdog();
    }

    /// <summary>把资源管理器右键菜单同步到设置要求的状态。</summary>
    public (bool Ok, string Error) ApplyContextMenuSetting()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return (false, "无法获取程序路径，右键菜单未处理。");
        }

        var (ok, error) = ExplorerContextMenu.Sync(SettingsService.Current.ExplorerContextMenu, exePath);
        if (!ok)
        {
            LogService.Warn(error);
        }

        return (ok, error);
    }

    /// <summary>
    /// 安装/卸载程序调用的右键菜单开关。
    /// 必须同时写配置：只写注册表的话，下次启动会按配置回滚把菜单删掉。
    /// </summary>
    private void RunContextMenuSwitch(bool enable)
    {
        try
        {
            // 直接同步注册表，不看配置里原来的值：
            // 配置可能停留在“已启用”但用户手动删过注册表，只按配置比较会漏掉这一步。
            var (ok, error) = ExplorerContextMenu.Sync(enable, Environment.ProcessPath ?? string.Empty);
            if (!ok)
            {
                LogService.Warn($"安装程序设置右键菜单失败：{error}");
            }

            AppSettings next = SettingsService.Current.Clone();
            next.ExplorerContextMenu = enable;
            SettingsService.Save(next);
            UpdateTrayVisibility();

            LogService.Info(enable ? "安装程序已加入右键菜单" : "安装程序已移除右键菜单");
        }
        catch (Exception ex)
        {
            LogService.Error("安装程序设置右键菜单异常", ex);
        }
    }

    /// <summary>没有可见窗口、也不在截图流程中时，定期把占用的内存还给系统。</summary>
    public void StartIdleTrimWatchdog()
    {
        _lastActivityTicks = Environment.TickCount64;

        _idleTrimTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        _idleTrimTimer.Tick += (_, _) =>
        {
            if (!ShouldTrimWhenIdle())
            {
                return;
            }

            _lastIdleTrimTicks = Environment.TickCount64;
            _ = Task.Run(MemoryTrimmer.TrimToBackground);
        };

        _idleTrimTimer.Start();
    }

    /// <summary>记录一次用户可见的交互，重置空闲计时。</summary>
    public void NoteActivity() => _lastActivityTicks = Environment.TickCount64;

    private bool HasVisibleWindow()
    {
        if (MainHost?.IsVisible == true || Result?.IsVisible == true)
        {
            return true;
        }

        return CaptureController?.State != CaptureState.Idle;
    }

    private bool ShouldTrimWhenIdle()
    {
        if (_exiting || HasVisibleWindow())
        {
            return false;
        }

        long now = Environment.TickCount64;

        // 刚交互过先不裁，避免马上又要用的时候重新换页
        if (now - _lastActivityTicks < 12000)
        {
            return false;
        }

        return now - _lastIdleTrimTicks >= 20000;
    }

    /// <summary>
    /// 退到后台（托盘、最小化）或一次截图识别结束后回收内存。
    /// 放到后台线程执行，并留一点余量让界面先落定，避免阻塞交互。
    /// </summary>
    public void TrimMemoryForBackground()
    {
        _ = Task.Run(() =>
        {
            Thread.Sleep(150);
            MemoryTrimmer.TrimToBackground();
        });
    }

    /// <summary>
    /// 一次识别任务结束后释放解码用的位图缓冲。
    /// 只做托管回收、不动工作集，避免结果窗口刚显示就被换出。
    /// </summary>
    private void ReleaseRecognitionBuffers()
    {
        _ = Task.Run(() =>
        {
            Thread.Sleep(200);
            MemoryTrimmer.ReleaseBuffers();
        });
    }

    // ---------------- 主窗口 ----------------

    public void ShowMainWindow()
    {
        if (_exiting)
        {
            return;
        }

        NoteActivity();

        if (MainHost is null)
        {
            MainHost = new MainWindow();
            MainHost.Closed += (_, _) =>
            {
                if (!_exiting)
                {
                    MainHost = null;
                }
            };
        }

        if (!MainHost.IsVisible)
        {
            MainHost.Show();
        }

        if (MainHost.WindowState == WindowState.Minimized)
        {
            MainHost.WindowState = WindowState.Normal;
        }

        MainHost.Activate();
        MainHost.Focus();
    }

    // ---------------- 截图流程 ----------------

    public void BeginCapture()
    {
        if (_exiting || CaptureController is null)
        {
            return;
        }

        if (CaptureController.IsActive)
        {
            // 截图进行中重复触发：忽略，避免创建多套遮罩
            LogService.Info("截图进行中，忽略重复触发");
            return;
        }

        var toHide = new List<Window>();
        if (MainHost is { IsVisible: true })
        {
            toHide.Add(MainHost);
        }

        if (Result is { IsVisible: true })
        {
            toHide.Add(Result);
        }

        NoteActivity();

        if (!CaptureController.Start(toHide))
        {
            MainHost?.SetStatus("无法进入截图模式，请查看日志。", StatusKind.Error);
        }
    }

    private void OnCaptureCompleted(object? sender, CaptureCompletedEventArgs e)
    {
        NoteActivity();

        if (e.Cancelled)
        {
            // 遮罩窗口已销毁：无论主窗口是否可见都裁一次工作集。
            // 遮罩是全屏分层窗口，占用的内存远大于主界面，留在工作集里没有意义。
            TrimMemoryForBackground();
            return;
        }

        if (e.Error is not null)
        {
            MainHost?.SetStatus(e.Error, StatusKind.Error);
            if (MainHost is null || !MainHost.IsVisible)
            {
                MessageBox.Show(e.Error, "QR Quick", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return;
        }

        HandleRecognition(e.Results, QRSource.ScreenCapture);
    }

    /// <summary>
    /// 一次识别任务的统一出口：更新主界面、弹出结果窗口、按设置决定是否自动打开浏览器。
    /// 自动打开只在这里触发，因此复制、查看、重绘都不会重复打开。
    /// </summary>
    private void HandleRecognition(IReadOnlyList<QRResult> results, QRSource source)
    {
        LastResults = results;

        MainHost?.ApplyResults(results);

        if (results.Count == 0)
        {
            MainHost?.SetStatus("没有识别到二维码或条形码，换个角度或重新框选试试。", StatusKind.Warning);
            ReleaseRecognitionBuffers();
            return;
        }

        MainHost?.SetStatus($"识别到 {results.Count} 个码（{DescribeFormats(results)}）。", StatusKind.Success);
        ShowResultWindow(results, source);
        MaybeAutoOpenBrowser(results);

        // 解码用的位图缓冲到这里已经用完，回收一次，避免连续识别时堆内存只涨不落
        ReleaseRecognitionBuffers();
    }

    /// <summary>把结果里的码制去重后连成一句，供状态栏展示（如「QR Code、EAN-13」）。</summary>
    private static string DescribeFormats(IReadOnlyList<QRResult> results)
    {
        var names = new List<string>();

        foreach (QRResult result in results)
        {
            if (!string.IsNullOrEmpty(result.Format) && !names.Contains(result.Format))
            {
                names.Add(result.Format);
            }
        }

        return names.Count == 0 ? "未知码制" : string.Join("、", names);
    }

    private void ShowResultWindow(IReadOnlyList<QRResult> results, QRSource source)
    {
        if (Result is null || !Result.IsLoaded)
        {
            Result = new ResultWindow();
            Result.Closed += (_, _) =>
            {
                Result = null;

                // 结果窗口关掉后通常没有可见窗口了，直接退到后台回收
                if (!HasVisibleWindow())
                {
                    TrimMemoryForBackground();
                }
            };
        }

        NoteActivity();
        Result.ShowResults(results, source);
        Result.Show();
        Result.Activate();
    }

    /// <summary>自动打开浏览器（第九章）。同一识别任务最多触发一次。</summary>
    private void MaybeAutoOpenBrowser(IReadOnlyList<QRResult> results)
    {
        string? url = AutoOpenPolicy.Decide(SettingsService.Current.AutoOpenBrowser, results);

        if (url is null)
        {
            // 开关关闭、没有链接、或存在多个不同链接：只展示，不打开
            return;
        }

        if (BrowserService.TryOpen(url, out string error))
        {
            LogService.Info("已按设置自动打开浏览器");
            MainHost?.SetStatus("已自动打开识别到的网页。", StatusKind.Success);
        }
        else
        {
            MainHost?.SetStatus(error, StatusKind.Warning);
        }
    }

    // ---------------- 设置 ----------------

    /// <summary>应用新的快捷键。返回是否成功与原因。</summary>
    public (bool Ok, string Error) ApplyHotkey(HotkeyDefinition definition)
    {
        var (ok, error) = HotkeyService.Register(definition);
        HotkeyStatusText = ok
            ? $"已生效：{HotkeyConverter.Format(definition)}"
            : error;

        if (ok)
        {
            try
            {
                SettingsService.Update(s => s.CaptureHotkey = definition.Clone());
            }
            catch (Exception ex)
            {
                LogService.Error("保存快捷键失败", ex);
            }
        }

        MainHost?.RefreshHotkeyDisplay();
        return (ok, error);
    }

    private void RegisterConfiguredHotkey()
    {
        var definition = SettingsService.Current.CaptureHotkey;
        var (ok, error) = HotkeyService.Register(definition);

        HotkeyStatusText = ok
            ? $"已生效：{HotkeyConverter.Format(definition)}"
            : $"{error}（可在设置中更换）";

        if (!ok)
        {
            LogService.Warn($"默认快捷键注册失败：{error}");
        }
    }

    /// <summary>保存设置并立即生效。</summary>
    public bool SaveSettings(AppSettings next, out string error)
    {
        error = string.Empty;

        // 右键菜单先落到注册表，失败就不保存开关，避免界面与系统状态不一致
        if (SettingsService.Current.ExplorerContextMenu != next.ExplorerContextMenu)
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                error = "无法获取程序路径，右键菜单未处理。";
                return false;
            }

            var (menuOk, menuError) = ExplorerContextMenu.Sync(next.ExplorerContextMenu, exePath);
            if (!menuOk)
            {
                error = menuError;
                MainHost?.SetStatus(menuError, StatusKind.Error);
                return false;
            }
        }

        try
        {
            SettingsService.Save(next);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            MainHost?.SetStatus($"设置保存失败：{ex.Message}", StatusKind.Error);
            return false;
        }

        UpdateTrayVisibility();
        MainHost?.RefreshHotkeyDisplay();
        return true;
    }

    public void UpdateTrayVisibility()
    {
        if (TrayService is null)
        {
            return;
        }

        bool wanted = SettingsService.Current.ShowTrayIcon ||
                      SettingsService.Current.CloseBehavior == CloseBehavior.MinimizeToTray;

        if (wanted)
        {
            TrayService.Show($"QR Quick Lite　快捷键 {HotkeyConverter.Format(SettingsService.Current.CaptureHotkey)}");
        }
        else
        {
            TrayService.Hide();
        }
    }

    // ---------------- 退出 ----------------

    public void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        LogService.Info("程序退出");

        _signalStop = true;
        try
        {
            _signalThread?.Join(800);
        }
        catch (Exception)
        {
            // 退出路径不因为等待线程异常而中断
        }

        _activateEvent?.Dispose();
        _captureEvent?.Dispose();

        try
        {
            CaptureController?.Dispose();
            HotkeyService?.Dispose();
            TrayService?.Dispose();
            _messageWindow?.Dispose();
        }
        catch (Exception ex)
        {
            LogService.Error("退出清理异常", ex);
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 未持有互斥体时忽略
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Error("未处理的界面异常", e.Exception);

        // 常见操作异常不应让程序直接退出
        e.Handled = true;

        string message = e.Exception is ScreenCaptureException
            ? e.Exception.Message
            : $"发生未预期的错误：{e.Exception.Message}";

        MainHost?.SetStatus(message, StatusKind.Error);
    }

    // ---------------- 诊断入口 ----------------

    private void RunSelfTest(string[] args)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            int exitCode = await SelfTestRunner.RunAsync(args);
            Environment.ExitCode = exitCode;
            Shutdown(exitCode);
        }), DispatcherPriority.ApplicationIdle);
    }

    private void RunOverlayDemo()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            int exitCode = await OverlayDemo.RunAsync();
            Environment.ExitCode = exitCode;
            Shutdown(exitCode);
        }), DispatcherPriority.ApplicationIdle);
    }

}
