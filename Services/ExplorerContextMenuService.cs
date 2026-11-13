using QRQuick.Helpers;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace QRQuick.Services;

/// <summary>
/// 资源管理器右键菜单集成。
/// 只用 HKCU，不需要管理员权限；卸载时只删自己写入的那几个键。
/// 三处入口：所有文件、文件夹、文件夹空白处（桌面）。
/// </summary>
public sealed class ExplorerContextMenuService
{
    private const string VerbName = "QRQuickLiteCapture";
    private const string MenuText = "二维码识别";
    private const string CommandArgument = "--capture";

    private static readonly string[] VerbRoots =
    {
        $@"Software\Classes\*\shell\{VerbName}",
        $@"Software\Classes\Directory\shell\{VerbName}",
        $@"Software\Classes\Directory\Background\shell\{VerbName}",
        $@"Software\Classes\DesktopBackground\Shell\{VerbName}"
    };

    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>三处入口是否都已存在。</summary>
    public bool IsInstalled()
    {
        foreach (string root in VerbRoots)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(root);
            if (key is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>当前写入的命令行，用于判断是否指向了被移动的 exe；未安装时为 null。</summary>
    public string? GetInstalledCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(VerbRoots[0] + @"\command");
        return key?.GetValue(null) as string;
    }

    /// <summary>写入右键菜单。成功后通知资源管理器刷新。</summary>
    public (bool Ok, string Error) Install(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return (false, "程序路径为空，无法写入右键菜单。");
        }

        string command = $"\"{exePath}\" {CommandArgument}";

        try
        {
            foreach (string root in VerbRoots)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(root, writable: true)
                                        ?? throw new InvalidOperationException("无法创建注册表项。");

                key.SetValue(null, MenuText, RegistryValueKind.String);
                key.SetValue("MUIVerb", MenuText, RegistryValueKind.String);
                key.SetValue("Icon", exePath, RegistryValueKind.String);

                using RegistryKey commandKey = key.CreateSubKey("command", writable: true)
                                               ?? throw new InvalidOperationException("无法创建命令项。");
                commandKey.SetValue(null, command, RegistryValueKind.String);
            }

            NotifyExplorer();
            LogService.Info($"已添加右键菜单：{command}");
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            LogService.Error("写入右键菜单失败", ex);
            return (false, $"写入右键菜单失败：{ex.Message}");
        }
    }

    /// <summary>删除右键菜单。</summary>
    public (bool Ok, string Error) Uninstall()
    {
        try
        {
            foreach (string root in VerbRoots)
            {
                Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            }

            NotifyExplorer();
            LogService.Info("已移除右键菜单");
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            LogService.Error("移除右键菜单失败", ex);
            return (false, $"移除右键菜单失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按设置与当前 exe 路径把右键菜单同步到期望状态。
    /// 程序被移动过（命令行与当前路径不一致）时也会重写。
    /// </summary>
    public (bool Ok, string Error) Sync(bool wanted, string exePath)
    {
        if (!wanted)
        {
            return IsInstalled() ? Uninstall() : (true, string.Empty);
        }

        string expected = $"\"{exePath}\" {CommandArgument}";
        bool upToDate = IsInstalled() &&
                        string.Equals(GetInstalledCommand(), expected, StringComparison.OrdinalIgnoreCase);

        return upToDate ? (true, string.Empty) : Install(exePath);
    }

    private static void NotifyExplorer()
    {
        try
        {
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            LogService.Warn($"通知资源管理器刷新失败：{ex.Message}");
        }
    }
}
