namespace QRQuick.Services;

/// <summary>
/// 二维码内容 → 可安全打开网址的校验器。
/// 只接受 http / https，且必须有有效主机名；拒绝控制字符、file:/javascript: 等未知协议。
/// </summary>
public static class UrlValidator
{
    private const int MaxLength = 4096;

    /// <summary>
    /// 校验原始内容是否为可打开的网页地址。
    /// 成功时 <paramref name="url"/> 为规范化后的绝对地址。
    /// </summary>
    public static bool TryNormalize(string? raw, out string? url)
    {
        url = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string text = raw.Trim();

        if (text.Length > MaxLength)
        {
            return false;
        }

        // 控制字符（含 \r \n \t）一律拒绝，避免命令/参数注入
        foreach (char c in text)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        // 主机名不允许空白（例如 "http://a b.com"）
        if (uri.Host.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!uri.IsDefaultPort && (uri.Port <= 0 || uri.Port > 65535))
        {
            return false;
        }

        url = uri.AbsoluteUri;
        return true;
    }

    public static bool IsHttpUrl(string? raw) => TryNormalize(raw, out _);
}
