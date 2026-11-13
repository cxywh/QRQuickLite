using QRQuick.Helpers;
using System.Diagnostics;

namespace QRQuick.Services;

/// <summary>用系统默认浏览器打开已校验的 HTTP/HTTPS 链接。</summary>
public sealed class BrowserService
{
    /// <summary>
    /// 打开网页。<paramref name="url"/> 必须已经通过 <see cref="UrlValidator"/>；
    /// 这里会再校验一次，避免调用方漏校验。
    /// </summary>
    public bool TryOpen(string? url, out string error)
    {
        error = string.Empty;

        if (!UrlValidator.TryNormalize(url, out string? safe) || safe is null)
        {
            error = "不是有效的网页链接，已阻止打开。";
            LogService.Warn("尝试打开非 HTTP/HTTPS 链接，已拦截");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = safe,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = $"无法启动默认浏览器：{ex.Message}";
            LogService.Error("打开浏览器失败", ex);
            return false;
        }
    }
}
