using QRQuick.Models;

namespace QRQuick.Services;

/// <summary>
/// 自动打开浏览器的决策逻辑。抽成纯函数，便于自检覆盖第九章的全部规则，
/// 且调用点唯一——一次识别任务只经过这里一次，不可能重复触发。
/// </summary>
public static class AutoOpenPolicy
{
    /// <summary>
    /// 返回应当自动打开的地址；不该自动打开时返回 null。
    /// 规则：开关关闭 → 不打开；恰好一个不同的合法 HTTP/HTTPS 链接 → 打开；其余（0 个或多个不同）→ 不打开。
    /// </summary>
    public static string? Decide(bool enabled, IReadOnlyList<QRResult>? results)
    {
        if (!enabled || results is null || results.Count == 0)
        {
            return null;
        }

        List<string> urls = results
            .Select(r => r.ValidUrl)
            .Where(u => !string.IsNullOrEmpty(u))
            .Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return urls.Count == 1 ? urls[0] : null;
    }
}
