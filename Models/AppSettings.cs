namespace QRQuick.Models;

/// <summary>
/// 应用配置。字段缺失或类型不符时由 SettingsService 回落到默认值，
/// 因此所有属性都必须有可用的默认值。
/// </summary>
public sealed class AppSettings
{
    /// <summary>识别到单个合法 HTTP/HTTPS 链接后是否自动用默认浏览器打开。</summary>
    public bool AutoOpenBrowser { get; set; }

    /// <summary>截图识别的全局快捷键。</summary>
    public HotkeyDefinition CaptureHotkey { get; set; } = HotkeyDefinition.Default;

    /// <summary>
    /// 关闭主窗口时的行为。默认最小化到托盘：
    /// 本分支以“右键菜单直接截图”为主要用法，常驻时工作集只有个位数 MB，
    /// 退出后再从菜单冷启动每次要多花约 0.5 秒并重新占内存。
    /// </summary>
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.MinimizeToTray;

    /// <summary>是否启用托盘图标（关闭行为选“最小化到托盘”时强制启用）。</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>是否把“二维码识别”加入资源管理器的右键菜单。</summary>
    public bool ExplorerContextMenu { get; set; }

    public AppSettings Clone() => new()
    {
        AutoOpenBrowser = AutoOpenBrowser,
        CaptureHotkey = CaptureHotkey.Clone(),
        CloseBehavior = CloseBehavior,
        ShowTrayIcon = ShowTrayIcon,
        ExplorerContextMenu = ExplorerContextMenu
    };
}
