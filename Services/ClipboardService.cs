using QRQuick.Helpers;
using System.Runtime.InteropServices;
using System.Windows;

namespace QRQuick.Services;

/// <summary>
/// 剪贴板写入。Lite 版只用于复制识别结果，不再读取剪贴板图片。
/// 剪贴板是全局共享资源，随时可能被其他程序占用，因此带重试并吞掉异常。
/// </summary>
public sealed class ClipboardService
{
    private const int RetryCount = 5;
    private const int RetryDelayMs = 60;

    /// <summary>写入文本；失败返回 false。</summary>
    public bool TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (int attempt = 0; attempt < RetryCount; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (COMException ex)
            {
                LogService.Warn($"写剪贴板被占用，重试 {attempt + 1}/{RetryCount}：{ex.Message}");
                Thread.Sleep(RetryDelayMs);
            }
            catch (Exception ex)
            {
                LogService.Error("写剪贴板失败", ex);
                return false;
            }
        }

        return false;
    }
}
