using QRQuick.Helpers;
using QRQuick.Models;

namespace QRQuick.Services;

/// <summary>
/// 全局快捷键。注册在消息专用窗口上，进程生命周期内有效。
/// 重复注册前会先注销旧键，注册失败时保留原绑定并向调用方返回原因。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x5150;
    private const uint ModNoRepeat = 0x4000;

    private readonly AppMessageWindow _sink;
    private HotkeyDefinition _current = HotkeyDefinition.Default;
    private uint _lastModifiers;
    private uint _lastVirtualKey;
    private bool _registered;
    private bool _disposed;

    public HotkeyService(AppMessageWindow sink)
    {
        _sink = sink;
        _sink.Message += OnMessage;
    }

    /// <summary>快捷键被按下（UI 线程）。</summary>
    public event EventHandler? Pressed;

    public bool IsRegistered => _registered;

    public HotkeyDefinition Current => _current;

    /// <summary>注册（或切换）快捷键。返回是否成功与失败原因。</summary>
    public (bool Ok, string Error) Register(HotkeyDefinition definition)
    {
        if (_disposed)
        {
            return (false, "程序正在退出。");
        }

        if (!HotkeyConverter.TryParse(definition, out uint modifiers, out uint vk, out string parseError))
        {
            return (false, parseError);
        }

        // 同一个 id 重复注册会被系统判为占用，因此先注销旧键；
        // 新键注册失败时按原参数回滚，保证用户原有快捷键不丢。
        bool hadOld = _registered;
        HotkeyDefinition oldDefinition = _current.Clone();
        uint oldModifiers = _lastModifiers;
        uint oldVirtualKey = _lastVirtualKey;

        if (hadOld)
        {
            NativeMethods.UnregisterHotKey(_sink.Hwnd, HotkeyId);
            _registered = false;
        }

        bool ok = NativeMethods.RegisterHotKey(_sink.Hwnd, HotkeyId, modifiers | ModNoRepeat, vk);
        int errorCode = 0;

        if (!ok)
        {
            errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            LogService.Warn($"快捷键注册失败 {HotkeyConverter.Format(definition)}，Win32 错误码 {errorCode}");

            if (hadOld && NativeMethods.RegisterHotKey(_sink.Hwnd, HotkeyId, oldModifiers | ModNoRepeat, oldVirtualKey))
            {
                _registered = true;
                _current = oldDefinition;
                _lastModifiers = oldModifiers;
                _lastVirtualKey = oldVirtualKey;
            }

            return (false, DescribeRegisterError(errorCode));
        }

        _registered = true;
        _current = definition.Clone();
        _lastModifiers = modifiers;
        _lastVirtualKey = vk;
        LogService.Info($"快捷键已注册：{HotkeyConverter.Format(_current)}");
        return (true, string.Empty);
    }

    /// <summary>注销当前快捷键。</summary>
    public void Unregister()
    {
        if (!_registered || _disposed)
        {
            return;
        }

        try
        {
            NativeMethods.UnregisterHotKey(_sink.Hwnd, HotkeyId);
        }
        catch (Exception ex)
        {
            LogService.Error("注销快捷键失败", ex);
        }
        finally
        {
            _registered = false;
        }
    }

    private void OnMessage(object? sender, AppWindowMessageEventArgs e)
    {
        if (e.Message == NativeMethods.WM_HOTKEY && e.WParam.ToInt32() == HotkeyId)
        {
            e.Handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string DescribeRegisterError(int errorCode) => errorCode switch
    {
        1409 => "该快捷键已被其他程序占用，请换一个组合。",
        1400 => "快捷键组合无效。",
        _ => $"快捷键注册失败（错误码 {errorCode}），可能已被其他程序占用。"
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unregister();
        _sink.Message -= OnMessage;
        _disposed = true;
    }
}
