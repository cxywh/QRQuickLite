namespace QRQuick.Models;

/// <summary>关闭主窗口后的行为。</summary>
public enum CloseBehavior
{
    /// <summary>完全退出程序。</summary>
    Exit = 0,

    /// <summary>最小化到系统托盘，快捷键继续可用。</summary>
    MinimizeToTray = 1
}

/// <summary>全局快捷键的持久化定义。序列化为可读文本，便于手工修改。</summary>
public sealed class HotkeyDefinition
{
    /// <summary>修饰键，逗号分隔，可选值：Ctrl、Alt、Shift、Win。</summary>
    public string Modifiers { get; set; } = "Alt";

    /// <summary>主键名，使用 WPF <see cref="System.Windows.Input.Key"/> 的枚举名，如 Q、F8、D1。</summary>
    public string Key { get; set; } = "Q";

    public static HotkeyDefinition Default => new() { Modifiers = "Alt", Key = "Q" };

    public HotkeyDefinition Clone() => new() { Modifiers = Modifiers, Key = Key };

    public override string ToString() => $"{Modifiers}+{Key}";
}
