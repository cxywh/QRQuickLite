using QRQuick.Models;
using System.Windows.Input;

namespace QRQuick.Services;

/// <summary>HotkeyDefinition ↔ Win32 修饰位/虚拟键码 的转换与校验。</summary>
public static class HotkeyConverter
{
    /// <summary>Win32 保留键，注册必然失败。</summary>
    private const uint VK_F12 = 0x7B;

    /// <summary>把配置定义转成 RegisterHotKey 需要的参数。</summary>
    public static bool TryParse(HotkeyDefinition? definition, out uint modifiers, out uint virtualKey, out string error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = string.Empty;

        if (definition is null)
        {
            error = "快捷键未配置。";
            return false;
        }

        // 修饰键
        string mods = (definition.Modifiers ?? string.Empty).Trim();
        foreach (string part in mods.Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods_MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= NativeMethods_MOD_ALT;
                    break;
                case "shift":
                    modifiers |= NativeMethods_MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                case "super":
                    modifiers |= NativeMethods_MOD_WIN;
                    break;
                default:
                    error = $"无法识别的修饰键“{part}”，可选 Ctrl、Alt、Shift、Win。";
                    return false;
            }
        }

        if (modifiers == 0)
        {
            error = "快捷键至少需要包含一个修饰键（Ctrl / Alt / Shift / Win），否则会抢占普通按键。";
            return false;
        }

        // 主键
        string keyName = (definition.Key ?? string.Empty).Trim();
        if (keyName.Length == 0)
        {
            error = "快捷键缺少主键。";
            return false;
        }

        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out Key key) ||
            key == Key.None || key == Key.System || IsModifierKey(key))
        {
            error = $"无法识别的主键“{keyName}”。";
            return false;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0)
        {
            error = $"按键“{keyName}”没有对应的虚拟键码。";
            return false;
        }

        if (vk == VK_F12)
        {
            error = "F12 为系统保留键，无法注册为全局快捷键。";
            return false;
        }

        virtualKey = (uint)vk;
        return true;
    }

    /// <summary>显示用文本，例如“Alt+Q”。</summary>
    public static string Format(HotkeyDefinition? definition)
    {
        if (definition is null)
        {
            return "未设置";
        }

        var parts = new List<string>();
        string mods = (definition.Modifiers ?? string.Empty).ToLowerInvariant();

        if (mods.Contains("ctrl") || mods.Contains("control"))
        {
            parts.Add("Ctrl");
        }

        if (mods.Contains("alt"))
        {
            parts.Add("Alt");
        }

        if (mods.Contains("shift"))
        {
            parts.Add("Shift");
        }

        if (mods.Contains("win"))
        {
            parts.Add("Win");
        }

        parts.Add(string.IsNullOrWhiteSpace(definition.Key) ? "?" : definition.Key.Trim().ToUpperInvariant());
        return string.Join("+", parts);
    }

    /// <summary>从一次按键事件构造定义；不合法时返回 null。</summary>
    public static HotkeyDefinition? FromKeyEvent(Key key, ModifierKeys modifiers)
    {
        if (key == Key.System)
        {
            // Alt 组合键在 WPF 中以 Key.System + SystemKey 形式送达，此处由调用方补正
            return null;
        }

        if (key == Key.None || IsModifierKey(key))
        {
            return null;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0 || vk == VK_F12)
        {
            return null;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        return new HotkeyDefinition
        {
            Modifiers = parts.Count == 0 ? "Alt" : string.Join(",", parts),
            Key = key.ToString()
        };
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None;

    // 本地常量，避免 Models/Services 层直接依赖 NativeMethods 的可见性
    private const uint NativeMethods_MOD_ALT = 0x0001;
    private const uint NativeMethods_MOD_CONTROL = 0x0002;
    private const uint NativeMethods_MOD_SHIFT = 0x0004;
    private const uint NativeMethods_MOD_WIN = 0x0008;
}
